using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class LibraryInspectionInput
    {
        [JsonRequired] public string ExpectedProjectPath { get; set; } = "";
        [JsonRequired] public LibraryCapture Capture { get; set; } = new LibraryCapture();
        [JsonRequired] public JsonElement Options { get; set; }
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ExpectedProjectPath) || ExpectedProjectPath.Length > 4096
                || ExpectedProjectPath.Any(char.IsControl) || !System.IO.Path.IsPathRooted(ExpectedProjectPath)
                || Capture == null || Options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Supply the exact absolute attached-project path, capture and options objects.");
            Capture.Validate();
        }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class LibraryCapture
    {
        public string LibraryId { get; set; } = "";
        public string Path { get; set; } = "";
        public int Offset { get; set; }
        public int MaxRows { get; set; } = 100;
        public void Validate()
        {
            if (LibraryId == null || Path == null || Offset < 0 || Offset > 10000 || MaxRows < 1 || MaxRows > 200
                || LibraryId != "" && LibraryId != "project" && !System.Text.RegularExpressions.Regex.IsMatch(LibraryId, "\\Aglobal:[0-9a-f]{64}\\z"))
                throw new ArgumentException("Use a returned library ID, offset 0 to 10000 and 1 to 200 rows.");
            if (LibraryId == "" && Path != "") throw new ArgumentException("Library listing requires an empty path.");
            LibraryCatalogReader.ValidatePath(Path);
        }
    }
    internal sealed class LibraryRow
    {
        public string LibraryId { get; set; } = "";
        public string Path { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string NativeType { get; set; } = "";
        public bool CanBrowse { get; set; }
        public System.Collections.Generic.Dictionary<string, string?> Fields { get; set; } = new System.Collections.Generic.Dictionary<string, string?>();
        public string[] UnsupportedFields { get; set; } = Array.Empty<string>();
        public string[] TruncatedFields { get; set; } = Array.Empty<string>();
    }
    internal sealed class LibrarySnapshot
    {
        public string LibraryId { get; set; } = "";
        public string Path { get; set; } = "";
        public LibraryRow? Selected { get; set; }
        public LibraryRow[] Rows { get; set; } = Array.Empty<LibraryRow>();
        public int Offset { get; set; }
        public int? NextOffset { get; set; }
        public bool HasMore { get; set; }
        public bool PayloadLimited { get; set; }
        public bool OffsetLimitReached { get; set; }
    }
    internal interface ILibraryInspectionBackend
    {
        LibrarySnapshot CaptureLibraries(string expectedProjectPath, LibraryCapture capture);
    }
    internal sealed class LibraryInspectionWorkflow
    {
        private readonly ILibraryInspectionBackend backend;
        private readonly IFeatureEngine features;
        public LibraryInspectionWorkflow(ILibraryInspectionBackend backend, IFeatureEngine features) { this.backend = backend; this.features = features; }
        public object Read(string operation, string kind, JsonElement arguments)
        {
            if (kind != "libraryCatalog") throw new NotSupportedException("Unsupported project inspection snapshot kind.");
            var request = ExtensionWire.Parse<LibraryInspectionInput>(arguments); request.Validate();
            features.Execute("project", operation, new { phase = "validate", arguments });
            var snapshot = backend.CaptureLibraries(request.ExpectedProjectPath, request.Capture);
            return new { snapshotKind = kind, capturedUtc = DateTime.UtcNow.ToString("o"),
                evidence = new { request.ExpectedProjectPath, request.Capture.LibraryId, request.Capture.Path, snapshot.Offset, snapshot.NextOffset, snapshot.HasMore, snapshot.PayloadLimited, snapshot.OffsetLimitReached,
                    scope = "Metadata page in native composition order at read time. Paging is not a stable snapshot; rediscover after library changes. IDs identify open libraries, not content hashes." },
                data = features.Execute("project", operation, new { phase = "snapshot", snapshot, arguments }),
                projectSaved = false, librariesOpened = false, libraryInstancesCreated = false };
        }
    }
}
