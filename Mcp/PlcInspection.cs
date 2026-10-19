using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class PlcInspectionInput
    {
        [JsonRequired] public string PlcId { get; set; } = "";
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public JsonElement Capture { get; set; }
        [JsonRequired] public JsonElement Options { get; set; }
        public void Validate(string kind)
        {
            if (string.IsNullOrWhiteSpace(PlcId) || PlcId.Length > 2048 || PlcId.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(Path) || Path.Length > 4096 || Path.Any(char.IsControl)
                || Capture.ValueKind != JsonValueKind.Object || Options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Supply an explicit PLC ID, object path, capture and options objects.");
            var parts = Path.Split('/');
            if (parts.Length > 128 || parts.Any(p => p.Length == 0)
                || !parts[0].StartsWith("p:", StringComparison.Ordinal) || !PlcPolicy.AllowsRootNavigator(parts[0].Substring(2))
                || !parts.Last().StartsWith("n:", StringComparison.Ordinal) && !parts.Last().StartsWith("i:", StringComparison.Ordinal))
                throw new ArgumentException("Select one PLC software object using a path returned by plc_read_object.");
            if (kind == "source") ExtensionWire.Parse<PlcSourceCapture>(Capture);
            else if (kind == "crossReferences") ExtensionWire.Parse<PlcReferenceCapture>(Capture).Validate();
            else throw new NotSupportedException("Unsupported PLC inspection snapshot kind.");
        }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class PlcSourceCapture
    {
        [JsonRequired] public bool WithDependencies { get; set; }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class PlcReferenceCapture
    {
        [JsonRequired] public string Filter { get; set; } = "";
        [JsonRequired] public int MaxRows { get; set; }
        public void Validate()
        {
            if (!new[] { "AllObjects", "ObjectsWithReferences", "ObjectsWithoutReferences", "UnusedObjects" }.Contains(Filter, StringComparer.Ordinal)
                || MaxRows < 1 || MaxRows > 1000) throw new ArgumentException("Use an advertised cross-reference filter and 1 to 1000 rows.");
        }
    }
    internal sealed class PlcSourceSnapshot
    {
        public string PlcId { get; set; } = "";
        public string Path { get; set; } = "";
        public string NativeType { get; set; } = "";
        public string Language { get; set; } = "";
        public bool WithDependencies { get; set; }
        public string ArtifactDirectory { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FileSha256 { get; set; } = "";
        public long Bytes { get; set; }
        public string Text { get; set; } = "";
        public object Evidence => new { PlcId, Path, NativeType, Language, WithDependencies, ArtifactDirectory, FileName, FileSha256, Bytes };
    }
    internal sealed class PlcReferenceSnapshot
    {
        public string PlcId { get; set; } = "";
        public string Path { get; set; } = "";
        public string Filter { get; set; } = "";
        public PlcReferenceRow[] Rows { get; set; } = Array.Empty<PlcReferenceRow>();
        public bool Truncated { get; set; }
        public bool FieldsTruncated { get; set; }
        public object Evidence => new { PlcId, Path, Filter, rows = Rows.Length, Truncated, FieldsTruncated,
            scope = "Native cross-reference view at read time; not proof of compile freshness or absence of dynamic references. Native display paths are not plc_read_object navigation paths." };
    }
    internal sealed class PlcReferenceRow
    {
        public string[] SourceAncestry { get; set; } = Array.Empty<string>();
        public System.Collections.Generic.Dictionary<string, string> Source { get; set; } = new System.Collections.Generic.Dictionary<string, string>();
        public System.Collections.Generic.Dictionary<string, string>? Reference { get; set; }
        public System.Collections.Generic.Dictionary<string, string>? Location { get; set; }
    }
    internal interface IPlcInspectionBackend
    {
        PlcSourceSnapshot CapturePlcSource(string plcId, string path, bool withDependencies);
        PlcReferenceSnapshot CapturePlcReferences(string plcId, string path, PlcReferenceCapture capture);
    }
    internal sealed class PlcInspectionWorkflow
    {
        private readonly IPlcInspectionBackend backend;
        private readonly IFeatureEngine features;
        public PlcInspectionWorkflow(IPlcInspectionBackend backend, IFeatureEngine features) { this.backend = backend; this.features = features; }
        public object Read(string operation, string kind, JsonElement arguments)
        {
            var request = ExtensionWire.Parse<PlcInspectionInput>(arguments); request.Validate(kind);
            features.Execute("plc", operation, new { phase = "validate", arguments });
            object snapshot, evidence;
            if (kind == "source")
            {
                var source = backend.CapturePlcSource(request.PlcId, request.Path, ExtensionWire.Parse<PlcSourceCapture>(request.Capture).WithDependencies);
                snapshot = source; evidence = source.Evidence;
            }
            else
            {
                var references = backend.CapturePlcReferences(request.PlcId, request.Path, ExtensionWire.Parse<PlcReferenceCapture>(request.Capture));
                snapshot = references; evidence = references.Evidence;
            }
            return new { snapshotKind = kind, evidence, capturedUtc = DateTime.UtcNow.ToString("o"), data = features.Execute("plc", operation, new { phase = "snapshot", snapshot, arguments }),
                projectSaved = false, sourceGeneratedIntoProject = false };
        }
    }
    internal static class PlcInspectionFiles
    {
        internal const int MaxBytes = 4 * 1024 * 1024;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static PlcSourceSnapshot Read(string directory, string fileName)
        {
            string path = System.IO.Path.Combine(directory, fileName);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > MaxBytes) throw new InvalidDataException("Native source is empty, missing or exceeds the 4 MiB inspection limit. Retain artifacts and use the native resource export workflow.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("Native source size changed during read.");
            int bom = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
            string text = Utf8.GetString(bytes, bom, bytes.Length - bom);
            if (text.Length == 0 || text.Contains('\0')) throw new InvalidDataException("Native source must be nonempty UTF-8 text.");
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return new PlcSourceSnapshot { ArtifactDirectory = directory, FileName = fileName, Bytes = bytes.Length, Text = text,
                    FileSha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() };
        }
        internal static string Prefix(string text, int count)
        {
            int length = Math.Min(count, text.Length);
            if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length);
        }
    }
}
