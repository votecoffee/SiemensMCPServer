using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SiemensMCPServer.Mcp
{
    // Data-only package Markdown. Caller supplies IDs/pins, never paths or code.
    internal sealed class NativeGuidance
    {
        private readonly string root;
        internal NativeGuidance(string root) { this.root = Path.GetFullPath(root); }
        private (string revision, JsonElement manifest) Snapshot()
        {
            byte[] raw = Read("guidance-manifest.json", 1024 * 1024);
            JsonElement data;
            using (var document = JsonDocument.Parse(raw)) data = document.RootElement.Clone();
            if (data.GetProperty("format").GetString() != "siemens-native-guidance-v1") throw new InvalidDataException("Unsupported native guidance manifest.");
            var files = data.GetProperty("files").EnumerateArray().ToArray();
            if (files.Length < 4 || files.Length > 100 || files.Select(f => f.GetProperty("id").GetString()).Distinct().Count() != files.Length)
                throw new InvalidDataException("Invalid native guidance inventory.");
            return (ResourceOwnership.Hash(raw), data);
        }
        private byte[] Read(string relative, int maximum)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf('\\') >= 0 || relative.IndexOf(':') >= 0 || Path.IsPathRooted(relative)
                || relative.Split('/').Any(p => p == "" || p == "." || p == "..")) throw new InvalidDataException("Guidance paths must remain in their package.");
            string path = root;
            foreach (var part in new[] { "" }.Concat(relative.Split('/'))) {
                if (part.Length != 0) path = Path.Combine(path, part);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked guidance is refused.");
            }
            using (var stream = File.OpenRead(path)) using (var memory = new MemoryStream()) {
                var buffer = new byte[8192]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) {
                    if (memory.Length + read > maximum) throw new InvalidDataException("Guidance exceeds its byte limit.");
                    memory.Write(buffer, 0, read);
                }
                return memory.ToArray();
            }
        }
        public object Catalog()
        {
            var snapshot = Snapshot();
            var rows = snapshot.manifest.GetProperty("files").EnumerateArray().Select(row => new {
                id = row.GetProperty("id").GetString(), kind = row.GetProperty("kind").GetString(), sha256 = row.GetProperty("sha256").GetString(), bytes = row.GetProperty("bytes").GetInt32()
            }).ToArray();
            return new { profile = "native-direct-candidate", catalog_revision = snapshot.revision, workflows = rows.Where(r => r.kind == "workflow").ToArray(),
                references = rows.Where(r => r.kind == "reference").ToArray(),
                session = "One direct stdio host owns an explicit TIA attachment on a bounded STA. No gateway/context acquisition or implicit attachment. Detach/EOF never saves/closes/kills user TIA.",
                writeAuthorization = "Launch permission, current GUI policy, explicit target, matching review/fingerprint/candidate hash and uncertain-outcome guards remain mandatory." };
        }
        public object Get(string id, string expectedRevision, string expectedSha256, string kind)
        {
            var snapshot = Snapshot();
            if (snapshot.revision != expectedRevision) throw new InvalidOperationException("Stale guidance revision; list/review current guidance before proceeding.");
            var matches = snapshot.manifest.GetProperty("files").EnumerateArray().Where(row => row.GetProperty("id").GetString() == id && row.GetProperty("kind").GetString() == kind).ToArray();
            if (matches.Length != 1 || matches[0].GetProperty("sha256").GetString() != expectedSha256) throw new InvalidOperationException("Guidance ID/hash is not in the reviewed catalog.");
            var file = matches[0]; byte[] raw = Read(file.GetProperty("path").GetString()!, kind == "workflow" ? 256 * 1024 : 1024 * 1024);
            if (raw.Length != file.GetProperty("bytes").GetInt32() || ResourceOwnership.Hash(raw) != expectedSha256) throw new InvalidOperationException("Guidance differs from its pinned bytes.");
            return new { id, kind, catalog_revision = snapshot.revision, sha256 = expectedSha256, markdown = new UTF8Encoding(false, true).GetString(raw) };
        }
    }
}
