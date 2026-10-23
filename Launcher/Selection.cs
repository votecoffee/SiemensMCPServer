using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiemensMCPServer.Launcher;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record Pointer(int SchemaVersion, string SnapshotSha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record Payload(string Path, long Bytes, string Sha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record Snapshot(int SchemaVersion, string Profile, string Version, string PackageSha256, string Runtime, Payload[] Files);

internal sealed class Selection : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal readonly string Root, SnapshotHash, Runtime;
    internal readonly Snapshot Data;
    private readonly List<FileStream> leases = new();
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool Digest(string value) => value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string Safe(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Split('/').Any(p =>
            string.IsNullOrEmpty(p) || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("Invalid deployment path.");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Deployment path escaped its root.");
        NoLinks(full);
        return full;
    }
    internal static void NoLinks(string path)
    {
        for (string? value = Path.GetFullPath(path); value != null; value = Path.GetDirectoryName(value))
            if ((File.Exists(value) || Directory.Exists(value)) && (File.GetAttributes(value) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked deployment paths are unavailable.");
    }
    internal static byte[] Read(string path, int limit)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException("Deployment record exceeds its bound.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    internal static T Decode<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        void Check(JsonElement item) {
            if (item.ValueKind == JsonValueKind.Object) {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject()) { if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate deployment key."); Check(property.Value); }
            } else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Check(child);
        }
        Check(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty deployment record.");
    }
    internal Selection(string root)
    {
        Root = Path.GetFullPath(root); NoLinks(Root);
        var pointer = Decode<Pointer>(Read(Safe(Root, "launcher/current.json"), 4096));
        if (pointer.SchemaVersion != 1 || !Digest(pointer.SnapshotSha256)) throw new InvalidDataException("Invalid deployment pointer.");
        SnapshotHash = pointer.SnapshotSha256;
        byte[] snapshot = Read(Safe(Root, "launcher/snapshots/" + SnapshotHash + ".json"), 262144);
        if (Hash(snapshot) != SnapshotHash) throw new InvalidDataException("Snapshot integrity failed.");
        Data = Decode<Snapshot>(snapshot);
        if (Data.SchemaVersion != 1 || Data.Profile != "direct-offline" || !Digest(Data.PackageSha256) ||
            Data.Runtime != "runtimes/" + Data.Version + "-" + Data.PackageSha256[..12] ||
            Data.Files == null || Data.Files.Length is < 4 or > 200) throw new InvalidDataException("Unsupported deployment selection.");
        Runtime = Safe(Root, Data.Runtime);
        try {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var file in Data.Files) {
                if (file.Bytes < 0 || file.Bytes > 67108864 || (total += file.Bytes) > 134217728 || !Digest(file.Sha256) || !names.Add(file.Path))
                    throw new InvalidDataException("Invalid runtime inventory.");
                string path = Safe(Runtime, file.Path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                leases.Add(stream);
                if (stream.Length != file.Bytes || Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != file.Sha256)
                    throw new InvalidDataException("Runtime integrity failed.");
            }
            int observed = 0;
            void CheckDirectory(string directory) {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                    NoLinks(path);
                    if (Directory.Exists(path)) CheckDirectory(path);
                    else if (++observed > 200 || !names.Contains(Path.GetRelativePath(Runtime, path).Replace('\\', '/')))
                        throw new InvalidDataException("Runtime inventory differs.");
                }
            }
            CheckDirectory(Runtime);
            if (!names.Contains("SiemensMCPServer.Offline.dll") || observed != names.Count) throw new InvalidDataException("Runtime inventory differs.");
        } catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
}
