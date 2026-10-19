using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiemensMCPServer.Mcp
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ProjectTranslationFileRequest
    {
        [JsonRequired] public string WorkbookPath { get; set; } = "";
        [JsonRequired] public ProjectTextCapture Capture { get; set; } = null!;
        public string ExpectedWorkbookFingerprint { get; set; } = "";
        public bool UpdateSourceLanguage { get; set; }
        public ProjectTranslationEdit[] Edits { get; set; } = Array.Empty<ProjectTranslationEdit>();
    }
    internal static class ProjectTranslationFiles
    {
        private static string Absolute(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.Length > 4096 || path.Any(char.IsControl)) throw new ArgumentException("Use explicit absolute file/output paths.");
            string full = Path.GetFullPath(path);
            for (string? current = full; current != null; current = Path.GetDirectoryName(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked file/output paths are unsupported.");
                if (Path.GetDirectoryName(current) == current) break;
            }
            return full;
        }
        public static object Run(string requestPath, string outputDirectory)
        {
            requestPath = Absolute(requestPath); outputDirectory = Absolute(outputDirectory);
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new IOException("Choose a new output directory; existing files are preserved.");
            if (new FileInfo(requestPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Request exceeds 2 MiB.");
            byte[] requestBytes = File.ReadAllBytes(requestPath);
            if (requestBytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Request grew beyond 2 MiB.");
            var request = JsonSerializer.Deserialize<ProjectTranslationFileRequest>(requestBytes, JsonDefaults.Options) ?? throw new ArgumentException("Missing request.");
            if (request.Capture == null || request.Edits == null) throw new ArgumentException("Supply capture and an optional edits array.");
            request.Capture.Validate(); string source = Absolute(request.WorkbookPath);
            if (new FileInfo(source).Length > ProjectTextFiles.MaxBytes) throw new InvalidDataException("Workbook exceeds 8 MiB.");
            byte[] bytes = File.ReadAllBytes(source); var book = new ProjectTextWorkbook(bytes); var units = book.TranslationUnits(request.Capture);
            ProjectTranslationCandidate? candidate = null;
            if (request.Edits.Length != 0) {
                if (request.ExpectedWorkbookFingerprint != book.Fingerprint) throw new InvalidOperationException("Workbook fingerprint changed; extract and review this snapshot again.");
                candidate = book.EditTranslations(new ProjectTranslationEdits { ExpectedProjectPath = source, Capture = request.Capture, UpdateSourceLanguage = request.UpdateSourceLanguage, Edits = request.Edits });
            }
            string workbookHash = SiemensMCPServer.TextListDocument.Hash(bytes), requestHash = SiemensMCPServer.TextListDocument.Hash(requestBytes);
            if (SiemensMCPServer.TextListDocument.Hash(File.ReadAllBytes(source)) != workbookHash || SiemensMCPServer.TextListDocument.Hash(File.ReadAllBytes(requestPath)) != requestHash)
                throw new IOException("Input files changed during extraction; retry with a stable snapshot.");
            Directory.CreateDirectory(outputDirectory);
            void Json(string name, object value) => File.WriteAllText(Path.Combine(outputDirectory, name), JsonSerializer.Serialize(value, JsonDefaults.Options), new UTF8Encoding(false));
            Json("translation-units.json", units.Select(u => new { u.Sheet, u.Row, u.Category, u.ViewPath, u.SourceLanguage, u.TargetLanguage, u.SourceText, u.TargetText, u.TargetEmpty, u.Editable, u.ReadOnlyReason,
                targetSelection = u.Editable ? u.Select() : null, sourceSelection = u.Editable ? u.Select(true) : null }).ToArray());
            string Csv(string value) => "\"" + ((value.TrimStart().StartsWith("=", StringComparison.Ordinal) || value.TrimStart().StartsWith("+", StringComparison.Ordinal)
                || value.TrimStart().StartsWith("-", StringComparison.Ordinal) || value.TrimStart().StartsWith("@", StringComparison.Ordinal)) ? "'" + value : value).Replace("\"", "\"\"") + "\"";
            using (var csv = new StreamWriter(Path.Combine(outputDirectory, "translation-units.csv"), false, new UTF8Encoding(true))) {
                csv.WriteLine("Sheet,Row,Category,Object path,Source culture,Target culture,Source text,Target text,Editable,Reason");
                foreach (var unit in units) csv.WriteLine(string.Join(",", new[] { unit.Sheet, unit.Row.ToString(System.Globalization.CultureInfo.InvariantCulture), unit.Category, unit.ViewPath,
                    unit.SourceLanguage, unit.TargetLanguage, unit.SourceText, unit.TargetText, unit.Editable.ToString(), unit.ReadOnlyReason ?? "" }.Select(Csv)));
            }
            if (candidate != null) { File.WriteAllBytes(Path.Combine(outputDirectory, "before.xlsx"), bytes); File.WriteAllBytes(Path.Combine(outputDirectory, "candidate.xlsx"), candidate.Bytes); Json("changes.json", request.Edits); }
            var summary = new { workbookFingerprint = book.Fingerprint, request.Capture, units = units.Length, editableUnits = units.Count(u => u.Editable), missingTranslations = units.Count(u => u.TargetEmpty),
                sourceWorkbook = source, sourceSha256 = workbookHash, requestSha256 = requestHash, editedCells = request.Edits.Length,
                candidateSha256 = candidate == null ? null : SiemensMCPServer.TextListDocument.Hash(candidate.Bytes), candidateFingerprint = candidate?.AfterFingerprint,
                projectChanged = false, scope = "Disk-only native project-text extraction/preview. Input workbook and request are preserved; native import remains a separate reviewed operation against the original project." };
            Json("summary.json", summary);
            if (SiemensMCPServer.TextListDocument.Hash(File.ReadAllBytes(source)) != workbookHash || SiemensMCPServer.TextListDocument.Hash(File.ReadAllBytes(requestPath)) != requestHash)
                throw new IOException("Inputs changed before completion; output has no completion manifest.");
            Json("manifest.json", new { format = "tia-project-text-files-v1", files = Directory.GetFiles(outputDirectory).OrderBy(p => p, StringComparer.Ordinal).Select(p => new { path = Path.GetFileName(p), bytes = new FileInfo(p).Length, sha256 = SiemensMCPServer.TextListDocument.Hash(File.ReadAllBytes(p)) }).ToArray() });
            return new { outputDirectory, summary };
        }
    }
}
