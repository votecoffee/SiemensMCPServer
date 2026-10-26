using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SiemensMCPServer.Mcp
{
    // The selected launcher hashes and leases the whole archive. Each decoded
    // member also has a bounded, exact hash inventory; no metadata-only cache.
    internal sealed class NativeAssetArchive : IDisposable
    {
        internal const string IndexName = "archive-manifest.json";
        [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
        private sealed class Index
        {
            [JsonRequired] public string Format { get; set; } = "";
            [JsonRequired] public string Purpose { get; set; } = "";
            [JsonRequired] public AssetFile[] Files { get; set; } = Array.Empty<AssetFile>();
        }
        [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
        private sealed class AssetFile
        {
            [JsonRequired] public string Path { get; set; } = "";
            [JsonRequired] public long Bytes { get; set; }
            [JsonRequired] public string Sha256 { get; set; } = "";
        }
        private readonly FileStream stream;
        private readonly ZipArchive zip;
        private readonly Dictionary<string, ZipArchiveEntry> entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssetFile> files = new Dictionary<string, AssetFile>(StringComparer.OrdinalIgnoreCase);
        private readonly object io = new object();
        internal readonly string Sha256;
        internal NativeAssetArchive(string path, string purpose)
        {
            Ordinary(path);
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try {
                if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Native asset archive exceeds its compressed bound.");
                using (var sha = SHA256.Create()) Sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                stream.Position = 0; zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count < 2 || zip.Entries.Count > 513) throw new InvalidDataException("Invalid native asset archive member count.");
                foreach (var entry in zip.Entries) {
                    Relative(entry.FullName);
                    if (entry.Name.Length == 0 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                        || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || entries.ContainsKey(entry.FullName))
                        throw new InvalidDataException("Linked, duplicate or directory archive member.");
                    entries.Add(entry.FullName, entry);
                }
                if (!entries.TryGetValue(IndexName, out var index) || index.FullName != IndexName) throw new InvalidDataException("Native asset archive index missing.");
                byte[] raw = Bytes(index, 1024 * 1024);
                using (var doc = JsonDocument.Parse(raw)) Unique(doc.RootElement);
                var manifest = JsonSerializer.Deserialize<Index>(raw, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                    ?? throw new InvalidDataException("Empty native asset index.");
                if (manifest.Format != "siemens-native-assets-v1" || manifest.Purpose != purpose || manifest.Files == null || manifest.Files.Length == 0 || manifest.Files.Length > 512)
                    throw new InvalidDataException("Unsupported native asset archive.");
                long total = 0;
                foreach (var file in manifest.Files) {
                    if (file == null) throw new InvalidDataException("Null native asset record.");
                    Relative(file.Path);
                    if (file.Path.Equals(IndexName, StringComparison.OrdinalIgnoreCase) || file.Bytes < 0 || file.Bytes > 64 * 1024 * 1024
                        || (total += file.Bytes) > 128 * 1024 * 1024 || !Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$")
                        || files.ContainsKey(file.Path) || !entries.TryGetValue(file.Path, out var actual) || actual.FullName != file.Path || actual.Length != file.Bytes)
                        throw new InvalidDataException("Native asset inventory differs.");
                    files.Add(file.Path, file);
                }
                if (entries.Count != files.Count + 1) throw new InvalidDataException("Unlisted native asset member.");
                foreach (string file in files.Keys)
                    for (string? parent = Path.GetDirectoryName(file); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                        if (files.ContainsKey(parent.Replace('\\', '/'))) throw new InvalidDataException("Archive file/directory collision.");
            } catch { stream.Dispose(); throw; }
        }
        private static void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object) {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var child in value.EnumerateObject()) { if (!seen.Add(child.Name)) throw new InvalidDataException("Duplicate asset index property."); Unique(child.Value); }
            } else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Unique(child);
        }
        internal static void Relative(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Length > 1024 || path.Split('/').Length > 32
                || path.Split('/').Any(p => p == "" || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.Any(c => c < 32 || "<>:\"|?*".IndexOf(c) >= 0)
                    || Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                throw new InvalidDataException("Unsafe native asset path.");
            if (Path.GetFileName(path).StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".whl", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("launch.py", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Vendor assemblies and framework runtimes are forbidden in native asset archives.");
        }
        private static void Ordinary(string path)
        {
            for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked native asset path.");
        }
        private static byte[] Bytes(ZipArchiveEntry entry, int maximum)
        {
            if (entry.Length > maximum) throw new InvalidDataException("Native asset exceeds its decoded bound.");
            using var input = entry.Open(); using var output = new MemoryStream();
            var buffer = new byte[65536]; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
                if (output.Length + count > maximum) throw new InvalidDataException("Native asset decompression exceeded its bound.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        internal byte[] Read(string relative, int maximum)
        {
            Relative(relative);
            lock (io) {
                if (!files.TryGetValue(relative, out var file) || file.Path != relative) throw new InvalidDataException("Unlisted native asset path.");
                byte[] value = Bytes(entries[relative], maximum);
                if (value.LongLength != file.Bytes || TextListDocument.Hash(value) != file.Sha256) throw new InvalidDataException("Native asset content hash differs.");
                return value;
            }
        }
        internal void RequireHash(string relative, string hash)
        {
            Relative(relative);
            if (!files.TryGetValue(relative, out var file) || file.Path != relative || file.Sha256 != hash)
                throw new InvalidDataException("Expanded asset differs from its selected archive.");
        }
        internal void Extract(string directory)
        {
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Asset extraction requires a new private directory.");
            Ordinary(directory); Directory.CreateDirectory(directory);
            foreach (var file in files.Values) {
                string path = ExtensionBundle.SafePath(directory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); Ordinary(path);
                byte[] data = Read(file.Path, 64 * 1024 * 1024);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); output.Write(data, 0, data.Length);
            }
        }
        public void Dispose() { zip.Dispose(); stream.Dispose(); }
    }
}
