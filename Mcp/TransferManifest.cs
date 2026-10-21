using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SiemensMCPServer.Mcp
{
    internal static class TransferManifest
    {
        internal const string FileName = "transfer-manifest.json";
        internal const string Format = "siemens-transfer-files-v1";
        internal static void Write(string directory)
        {
            var files = TransferFiles.Inventory(directory).Where(f => f.Path != FileName).ToArray();
            File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(new { format = Format, files }, JsonDefaults.Options));
        }
        internal static bool Verify(string directory, TransferFile[] inventory)
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Transfer manifest exceeds its bound.");
            return VerifyBytes(File.ReadAllBytes(path), inventory);
        }
        internal static bool VerifyBytes(byte[] bytes, TransferFile[] inventory)
        {
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Transfer manifest exceeds its bound.");
            using (var doc = JsonDocument.Parse(bytes))
            {
                if (doc.RootElement.GetProperty("format").GetString() != Format) throw new NotSupportedException("Unsupported transfer manifest format.");
                var declared = JsonSerializer.Deserialize<TransferFile[]>(doc.RootElement.GetProperty("files").GetRawText(), JsonDefaults.Options)
                    ?? throw new InvalidDataException("Manifest has no files.");
                if (declared.Length == 0 || declared.Length > 10000 || declared.Any(f => f == null || f.Path == null || f.Path == FileName || f.Sha256 == null || f.Bytes < 0)
                    || declared.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != declared.Length)
                    throw new InvalidDataException("Manifest has incomplete or duplicate members.");
                foreach (var file in declared) TransferBundle.ValidateRelativePath(file.Path);
                var actual = inventory.Where(f => f.Path != FileName).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
                if (TransferFiles.Hash(declared.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray()) != TransferFiles.Hash(actual))
                    throw new InvalidDataException("Manifest member set, byte counts or SHA-256 hashes do not match the source.");
                return true;
            }
        }
    }
}
