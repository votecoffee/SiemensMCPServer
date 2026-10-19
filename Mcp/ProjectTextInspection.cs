using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTextInput
    {
        [JsonRequired] public string ExpectedProjectPath { get; set; } = "";
        [JsonRequired] public JsonElement Capture { get; set; }
        [JsonRequired] public JsonElement Options { get; set; }
        public void Validate(string kind)
        {
            if (string.IsNullOrWhiteSpace(ExpectedProjectPath) || ExpectedProjectPath.Length > 4096 || ExpectedProjectPath.Any(char.IsControl)
                || !Path.IsPathRooted(ExpectedProjectPath) || Capture.ValueKind != JsonValueKind.Object || Options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Supply the exact absolute attached-project path, capture and options objects.");
            if (kind == "projectLanguages") {
                if (Capture.EnumerateObject().Any()) throw new ArgumentException("Language capture must be empty.");
            } else if (kind == "projectTexts") ExtensionWire.Parse<ProjectTextCapture>(Capture).Validate();
            else throw new NotSupportedException("Unsupported project-text snapshot kind.");
        }
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTextCapture
    {
        [JsonRequired] public string SourceLanguage { get; set; } = "";
        [JsonRequired] public string TargetLanguage { get; set; } = "";
        public void Validate()
        {
            Culture(SourceLanguage); Culture(TargetLanguage);
            if (SourceLanguage == TargetLanguage) throw new ArgumentException("Choose distinct active source and target languages.");
        }
        private static void Culture(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || CultureInfo.GetCultureInfo(name).Name != name)
                throw new ArgumentException("Use exact canonical culture names returned by list_project_languages.");
        }
    }
    internal sealed class ProjectLanguage
    {
        public string Culture { get; set; } = "";
        public string EnglishName { get; set; } = "";
        public string NativeName { get; set; } = "";
        public bool IsReferenceLanguage { get; set; }
        public bool IsEditingLanguage { get; set; }
    }
    internal sealed class ProjectLanguageSnapshot { public ProjectLanguage[] Languages { get; set; } = Array.Empty<ProjectLanguage>(); }
    internal sealed class ProjectTextSnapshot
    {
        public string SourceLanguage { get; set; } = "";
        public string TargetLanguage { get; set; } = "";
        public string ArtifactDirectory { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FileSha256 { get; set; } = "";
        public long Bytes { get; set; }
        public string WorkbookBase64Url { get; set; } = "";
        public object Evidence => new { SourceLanguage, TargetLanguage, ArtifactDirectory, FileName, FileSha256, Bytes };
    }
    internal interface IProjectTextBackend
    {
        ProjectLanguageSnapshot CaptureProjectLanguages(string expectedProjectPath);
        ProjectTextSnapshot CaptureProjectTexts(string expectedProjectPath, ProjectTextCapture capture);
    }
    internal sealed class ProjectTextWorkflow
    {
        private readonly IProjectTextBackend backend;
        private readonly IFeatureEngine features;
        public ProjectTextWorkflow(IProjectTextBackend backend, IFeatureEngine features) { this.backend = backend; this.features = features; }
        public object Read(string operation, string kind, JsonElement arguments)
        {
            var request = ExtensionWire.Parse<ProjectTextInput>(arguments); request.Validate(kind);
            features.Execute("project", operation, new { phase = "validate", arguments });
            object snapshot, evidence; ProjectTextSnapshot? workbook = null;
            if (kind == "projectLanguages") {
                var languages = backend.CaptureProjectLanguages(request.ExpectedProjectPath);
                snapshot = languages; evidence = new { request.ExpectedProjectPath, activeLanguages = languages.Languages.Length };
            } else {
                var texts = backend.CaptureProjectTexts(request.ExpectedProjectPath, ExtensionWire.Parse<ProjectTextCapture>(request.Capture));
                workbook = texts; snapshot = texts; evidence = new { request.ExpectedProjectPath, workbook = texts.Evidence };
            }
            JsonElement data;
            try { data = features.Execute("project", operation, new { phase = "snapshot", snapshot, arguments }); }
            catch (Exception ex) when (workbook != null) {
                throw new InvalidOperationException("Project-text inspection failed; native XLSX retained at " + Path.Combine(workbook.ArtifactDirectory, workbook.FileName)
                    + " (SHA-256 " + workbook.FileSha256 + "). " + ex.GetBaseException().Message);
            }
            return new { snapshotKind = kind, evidence, capturedUtc = DateTime.UtcNow.ToString("o"),
                data,
                projectSaved = false, projectTextsImported = false,
                scope = "Fresh project-text export per call. Cells include metadata/headers and are not classified as translatable. No workbook edits, import, compile or runtime control." };
        }
    }
    internal static class ProjectTextFiles
    {
        public const int MaxBytes = 8 * 1024 * 1024;
        public static ProjectTextSnapshot FromBytes(byte[] bytes, ProjectTextCapture capture)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("Project-text workbook exceeds its file limit.");
            return new ProjectTextSnapshot { SourceLanguage = capture.SourceLanguage, TargetLanguage = capture.TargetLanguage, Bytes = bytes.Length,
                FileSha256 = TextListDocument.Hash(bytes), WorkbookBase64Url = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') };
        }
        public static ProjectTextSnapshot Read(string directory)
        {
            string path = Path.Combine(directory, "project-texts.xlsx");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > MaxBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Native project-text XLSX is missing, linked, empty or exceeds 8 MiB; inspect the retained export directory.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("Native workbook size changed while reading.");
            return new ProjectTextSnapshot { ArtifactDirectory = directory, FileName = file.Name, Bytes = bytes.Length,
                FileSha256 = TextListDocument.Hash(bytes), WorkbookBase64Url = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') };
        }
        public static byte[] Decode(ProjectTextSnapshot snapshot)
        {
            string value = snapshot.WorkbookBase64Url;
            if (value == null || value.Length == 0 || value.Length > (MaxBytes + 2) / 3 * 4 || value.Any(c => !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_')))
                throw new ArgumentException("Invalid bounded workbook snapshot.");
            byte[] bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
            if (bytes.Length == 0 || bytes.Length > MaxBytes || snapshot.Bytes != bytes.Length || TextListDocument.Hash(bytes) != snapshot.FileSha256)
                throw new InvalidDataException("Workbook snapshot length/hash mismatch.");
            return bytes;
        }
    }
}
