using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Siemens.Engineering;

namespace SiemensMCPServer.Mcp
{
    internal sealed partial class HmiSession : IProjectTranslationBackend
    {
        public ProjectLanguageSnapshot CaptureProjectLanguages(string expectedProjectPath)
        {
            CheckProject(); RequireExpectedPath(expectedProjectPath);
            using (var exclusive = portal!.ExclusiveAccess("Codex project-language inspection")) {
                CheckProject(); RequireExpectedPath(expectedProjectPath);
                return ReadProjectLanguages();
            }
        }
        private ProjectLanguageSnapshot ReadProjectLanguages()
        {
            var settings = project!.LanguageSettings;
            string? reference = settings.ReferenceLanguage?.Culture?.Name, editing = settings.EditingLanguage?.Culture?.Name;
            var result = new List<ProjectLanguage>();
            foreach (var language in settings.ActiveLanguages) {
                if (result.Count >= 256) throw new NotSupportedException("Project language count exceeds 256.");
                var culture = language.Culture;
                result.Add(new ProjectLanguage { Culture = culture.Name, EnglishName = culture.EnglishName, NativeName = culture.NativeName,
                    IsReferenceLanguage = culture.Name == reference, IsEditingLanguage = culture.Name == editing });
            }
            if (result.Select(l => l.Culture).Distinct(StringComparer.Ordinal).Count() != result.Count) throw new InvalidOperationException("Ambiguous active project languages.");
            return new ProjectLanguageSnapshot { Languages = result.OrderBy(l => l.Culture, StringComparer.Ordinal).ToArray() };
        }
        public ProjectTextSnapshot CaptureProjectTexts(string expectedProjectPath, ProjectTextCapture capture)
        {
            capture.Validate(); CheckProject(); RequireExpectedPath(expectedProjectPath);
            using (var exclusive = portal!.ExclusiveAccess("Codex project-text XLSX export")) {
                CheckProject(); RequireExpectedPath(expectedProjectPath);
                RequireProjectTextLanguages(capture);
                return ExportProjectTextSnapshot(capture);
            }
        }
        private void RequireProjectTextLanguages(ProjectTextCapture capture)
        {
            capture.Validate();
            var active = ReadProjectLanguages().Languages.Select(l => l.Culture).ToArray();
            if (!active.Contains(capture.SourceLanguage, StringComparer.Ordinal) || !active.Contains(capture.TargetLanguage, StringComparer.Ordinal))
                throw new InvalidOperationException("Both languages must already be active in the attached project; use list_project_languages.");
        }
        private ProjectTextSnapshot ExportProjectTextSnapshot(ProjectTextCapture capture)
        {
                string directory = Path.Combine(artifactRoot, "project-text-exports", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                try {
                    project!.ExportProjectTexts(new FileInfo(Path.Combine(directory, "project-texts.xlsx")), CultureInfo.GetCultureInfo(capture.SourceLanguage), CultureInfo.GetCultureInfo(capture.TargetLanguage));
                    CheckProject();
                    var snapshot = ProjectTextFiles.Read(directory);
                    snapshot.SourceLanguage = capture.SourceLanguage; snapshot.TargetLanguage = capture.TargetLanguage; return snapshot;
                } catch (Exception ex) {
                    // The tool reports GetBaseException().Message, so keep the artifact
                    // location in this exception rather than losing it to an inner one.
                    throw new InvalidOperationException("Project-text export failed; retained artifacts: " + directory + ". " + ex.GetBaseException().Message);
                }
        }
        public object ImportProjectTranslations(string expectedProjectPath, ProjectTextCapture capture, byte[] candidate,
            string beforeFingerprint, string afterFingerprint, bool updateSourceLanguage)
        {
            CheckProject(); RequireExpectedPath(expectedProjectPath);
            using (var exclusive = portal!.ExclusiveAccess("Codex reviewed project translations")) {
                CheckProject(); RequireExpectedPath(expectedProjectPath); RequireProjectTextLanguages(capture);
                foreach (var plc in SoftwareCatalog.Discover(project!).Where(s => s.Family == "plc")) RequireOfflineIfPlc(plc.Id);
                var before = ExportProjectTextSnapshot(capture);
                if (new ProjectTextWorkbook(ProjectTextFiles.Decode(before)).Fingerprint != beforeFingerprint) throw new InvalidOperationException("Project texts changed since preview; read and preview again.");
                string directory = Path.Combine(artifactRoot, "project-text-imports", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "candidate.xlsx"); File.WriteAllBytes(path, candidate);
                if (new ProjectTextWorkbook(candidate).Fingerprint != afterFingerprint) throw new InvalidOperationException("Candidate text fingerprint changed.");
                try {
                    using (var transaction = exclusive.Transaction(project!, "Update project text translations")) {
                        var result = project!.ImportProjectTexts(new FileInfo(path), updateSourceLanguage);
                        if (result.State == ProjectTextResultState.Error) throw new InvalidOperationException("Siemens project-text import reported Error. Native log: " + result.Path?.FullName);
                        var after = ExportProjectTextSnapshot(capture);
                        if (new ProjectTextWorkbook(ProjectTextFiles.Decode(after)).Fingerprint != afterFingerprint)
                            throw new InvalidOperationException("Native project-text readback differs from the preview; the transaction was not committed.");
                        if (exclusive.IsCancellationRequested || !transaction.CanCommit) throw new InvalidOperationException("Project-text transaction cancelled or cannot commit.");
                        transaction.CommitOnDispose();
                        return new { importState = result.State.ToString(), nativeLog = result.Path?.FullName, before = before.Evidence, after = after.Evidence,
                            candidateSha256 = SiemensMCPServer.TextListDocument.Hash(candidate), artifactDirectory = directory, projectSaved = false };
                    }
                } catch (Exception ex) {
                    throw new InvalidOperationException("Project-text import/readback failed; inspect the current project before retrying. Retained artifacts: " + directory + ". " + ex.GetBaseException().Message);
                }
            }
        }
    }
    internal static class ProjectTextNative
    {
        public static object Capabilities() => new {
            nativeExportApiPresent = typeof(Siemens.Engineering.Project).GetMethod("ExportProjectTexts", new[] { typeof(FileInfo), typeof(CultureInfo), typeof(CultureInfo) }) != null,
            activeLanguageApiPresent = typeof(Siemens.Engineering.LanguageSettings).GetProperty("ActiveLanguages") != null,
            nativeImportApiPresent = typeof(Siemens.Engineering.Project).GetMethod("ImportProjectTexts", new[] { typeof(FileInfo), typeof(bool) }) != null,
            importImplemented = true, nativeAcceptance = "pending", scope = "Whole-project text XLSX export and exact schema-1.3 translation previews/imports. Active languages only; IDs, metadata, substitutions and other rows are preserved. Source updates require opt-in. No language activation or runtime control." };
    }
}
