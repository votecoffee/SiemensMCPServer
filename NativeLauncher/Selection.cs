using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiemensMCPServer.NativeLauncher
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class Pointer {
        [JsonRequired] public int SchemaVersion { get; set; }
        [JsonRequired] public string SnapshotSha256 { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class Payload {
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public long Bytes { get; set; }
        [JsonRequired] public string Sha256 { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class Snapshot {
        [JsonRequired] public int SchemaVersion { get; set; }
        [JsonRequired] public string Profile { get; set; } = "";
        [JsonRequired] public string Version { get; set; } = "";
        [JsonRequired] public string PackageSha256 { get; set; } = "";
        [JsonRequired] public string Runtime { get; set; } = "";
        [JsonRequired] public string TiaVersion { get; set; } = "";
        [JsonRequired] public bool AllowWrites { get; set; }
        [JsonRequired] public Payload[] Files { get; set; } = Array.Empty<Payload>();
    }
    internal sealed class Selection : IDisposable
    {
        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        internal readonly string Root, SnapshotHash, Runtime;
        internal readonly Snapshot Data;
        private readonly List<FileStream> leases = new List<FileStream>();
        internal static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static bool Digest(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        internal static void NoLinks(string path) {
            for (string? p = System.IO.Path.GetFullPath(path); p != null; p = System.IO.Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked deployment path.");
        }
        internal static string Safe(string root, string relative) {
            if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Split('/').Any(p => string.IsNullOrEmpty(p) || p == "." || p == ".." || p.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith(".") || p.EndsWith(" ")))
                throw new InvalidDataException("Invalid deployment path.");
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            if (!full.StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Deployment path escaped root.");
            NoLinks(full); return full;
        }
        internal static byte[] Read(string path, int maximum) {
            NoLinks(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximum) throw new InvalidDataException("Deployment record exceeds bound.");
            using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
        }
        internal static T Decode<T>(byte[] raw) {
            using var document = JsonDocument.Parse(raw);
            void Check(JsonElement item) {
                if (item.ValueKind == JsonValueKind.Object) {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in item.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate deployment key."); Check(p.Value); }
                } else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Check(child);
            }
            Check(document.RootElement); return JsonSerializer.Deserialize<T>(raw, Json) ?? throw new InvalidDataException("Empty deployment record.");
        }
        internal Selection(string root) {
            Root = System.IO.Path.GetFullPath(root); NoLinks(Root);
            var pointer = Decode<Pointer>(Read(Safe(Root, "launcher/current.json"), 4096));
            if (pointer.SchemaVersion != 1 || !Digest(pointer.SnapshotSha256)) throw new InvalidDataException("Invalid native pointer.");
            SnapshotHash = pointer.SnapshotSha256;
            byte[] raw = Read(Safe(Root, "launcher/snapshots/" + SnapshotHash + ".json"), 262144);
            if (Hash(raw) != SnapshotHash) throw new InvalidDataException("Native snapshot hash differs.");
            Data = Decode<Snapshot>(raw);
            if (Data.SchemaVersion != 1 || Data.Profile != "direct-native" || !Digest(Data.PackageSha256) || Data.Version.Length > 64 ||
                Data.Runtime != "r/" + Data.PackageSha256.Substring(0,16) || !new[] { "V15.1", "V16", "V17", "V18", "V19", "V20", "V21" }.Contains(Data.TiaVersion) ||
                Data.Files == null || Data.Files.Length < 4 || Data.Files.Length > 250) throw new InvalidDataException("Unsupported native selection.");
            Runtime = Safe(Root, Data.Runtime);
            try {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (var file in Data.Files) {
                    if (file.Bytes < 0 || file.Bytes > 67108864 || (total += file.Bytes) > 134217728 || !Digest(file.Sha256) || !names.Add(file.Path)) throw new InvalidDataException("Invalid native inventory.");
                    var stream = new FileStream(Safe(Runtime, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read); leases.Add(stream);
                    using var sha = SHA256.Create();
                    if (stream.Length != file.Bytes || BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != file.Sha256) throw new InvalidDataException("Native payload hash differs.");
                }
                int count = 0;
                void CheckDirectory(string directory) {
                    foreach (string p in Directory.EnumerateFileSystemEntries(directory)) {
                        NoLinks(p); if (Directory.Exists(p)) CheckDirectory(p);
                        else if (++count > 250 || !names.Contains(p.Substring(Runtime.Length + 1).Replace('\\','/'))) throw new InvalidDataException("Native inventory differs.");
                    }
                }
                CheckDirectory(Runtime);
                if (count != names.Count || !names.Contains("SiemensMCPServer.Mcp.exe") || !names.Contains("SiemensMCPServer.Mcp.exe.config")) throw new InvalidDataException("Native entry/config missing.");
            } catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var stream in leases) stream.Dispose(); leases.Clear(); }
    }
}
