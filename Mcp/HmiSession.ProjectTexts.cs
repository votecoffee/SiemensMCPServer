using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SiemensMCPServer.Mcp
{
    internal sealed partial class HmiSession : IProjectTextBackend
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
                var active = ReadProjectLanguages().Languages.Select(l => l.Culture).ToArray();
                if (!active.Contains(capture.SourceLanguage, StringComparer.Ordinal) || !active.Contains(capture.TargetLanguage, StringComparer.Ordinal))
                    throw new InvalidOperationException("Both languages must already be active in the attached project; use list_project_languages.");
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
        }
    }
    internal static class ProjectTextNative
    {
        public static object Capabilities() => new {
            nativeExportApiPresent = typeof(Siemens.Engineering.Project).GetMethod("ExportProjectTexts", new[] { typeof(FileInfo), typeof(CultureInfo), typeof(CultureInfo) }) != null,
            activeLanguageApiPresent = typeof(Siemens.Engineering.LanguageSettings).GetProperty("ActiveLanguages") != null,
            importImplemented = false, nativeAcceptance = "pending", scope = "Whole-project text XLSX export and read-only cached-cell inspection. No workbook editing, native text import, language activation or runtime control." };
    }
}
