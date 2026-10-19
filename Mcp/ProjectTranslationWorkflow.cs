using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    internal interface IProjectTranslationBackend : IProjectTextBackend
    {
        object ImportProjectTranslations(string projectPath, ProjectTextCapture capture, byte[] candidate,
            string beforeFingerprint, string afterFingerprint, bool updateSourceLanguage);
    }
    internal sealed class ProjectTranslationWorkflow
    {
        private readonly IProjectTranslationBackend backend;
        private readonly string root;
        private readonly IFeatureEngine features;
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>();
        private sealed class Plan
        {
            public string Project = "", Directory = "", Hash = "", Before = "", After = "";
            public bool UpdateSource;
            public ProjectTextCapture Capture = null!;
            public DateTime Created;
        }
        public ProjectTranslationWorkflow(IProjectTranslationBackend backend, string root, IFeatureEngine features) { this.backend = backend; this.root = root; this.features = features; }
        public object Preview(string expectedFingerprint, ProjectTranslationEdits request)
        {
            request.Validate();
            features.Execute("project", "edit_project_texts", new { phase = "validate", arguments = request });
            if (plans.Count >= 100) throw new InvalidOperationException("Too many project-text previews; reattach to clear them.");
            var before = backend.CaptureProjectTexts(request.ExpectedProjectPath, request.Capture);
            var book = new ProjectTextWorkbook(ProjectTextFiles.Decode(before));
            if (book.Fingerprint != expectedFingerprint) throw new InvalidOperationException("Project-text snapshot changed; read translation units again.");
            var generated = features.Execute("project", "edit_project_texts", new { phase = "snapshot", snapshot = before, arguments = request });
            var candidateSnapshot = ExtensionWire.Parse<ProjectTextSnapshot>(generated.GetProperty("candidate"));
            byte[] candidateBytes = ProjectTextFiles.Decode(candidateSnapshot);
            string candidateFingerprint = new ProjectTextWorkbook(candidateBytes).Fingerprint;
            if (generated.GetProperty("beforeFingerprint").GetString() != book.Fingerprint || generated.GetProperty("afterFingerprint").GetString() != candidateFingerprint
                || candidateSnapshot.SourceLanguage != request.Capture.SourceLanguage || candidateSnapshot.TargetLanguage != request.Capture.TargetLanguage)
                throw new InvalidOperationException("Extension candidate language scope or fingerprints differ.");
            string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(root, id); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "before.xlsx"), ProjectTextFiles.Decode(before));
            File.WriteAllBytes(Path.Combine(directory, "candidate.xlsx"), candidateBytes);
            var plan = new Plan { Project = request.ExpectedProjectPath, Capture = new ProjectTextCapture { SourceLanguage = request.Capture.SourceLanguage, TargetLanguage = request.Capture.TargetLanguage }, Directory = directory,
                Hash = SiemensMCPServer.TextListDocument.Hash(candidateBytes), Before = book.Fingerprint, After = candidateFingerprint,
                UpdateSource = request.UpdateSourceLanguage, Created = DateTime.UtcNow };
            var report = new { planId = id, candidateSha256 = plan.Hash, beforeFingerprint = plan.Before, candidateFingerprint = plan.After,
                request.ExpectedProjectPath, request.Capture, request.UpdateSourceLanguage, edits = request.Edits,
                beforeNativeExport = before.Evidence, artifactDirectory = directory, expiresUtc = plan.Created.AddMinutes(30), projectSaved = false,
                scope = "Existing project-text translations only. Import processes the native workbook; metadata, substitutions, other rows and language slots are preserved. Source text updates require explicit opt-in." };
            File.WriteAllText(Path.Combine(directory, "preview.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
            plans.Add(id, plan); return report;
        }
        public object Apply(string id, string hash)
        {
            if (!plans.TryGetValue(id, out var plan)) throw new InvalidOperationException("Unknown or already attempted project-text plan.");
            if (DateTime.UtcNow - plan.Created > TimeSpan.FromMinutes(30)) { plans.Remove(id); throw new InvalidOperationException("Project-text preview expired."); }
            string candidatePath = Path.Combine(plan.Directory, "candidate.xlsx");
            var info = new FileInfo(candidatePath);
            if (!info.Exists || info.Length > ProjectTextFiles.MaxBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Missing, linked or oversized candidate workbook.");
            byte[] bytes = File.ReadAllBytes(candidatePath);
            if (hash != plan.Hash || SiemensMCPServer.TextListDocument.Hash(bytes) != hash || new ProjectTextWorkbook(bytes).Fingerprint != plan.After)
                throw new InvalidOperationException("Candidate workbook changed; preview again.");
            plans.Remove(id);
            string log = Path.Combine(plan.Directory, "apply.json");
            File.WriteAllText(log, "{\"state\":\"started\"}", new UTF8Encoding(false));
            try {
                object native = backend.ImportProjectTranslations(plan.Project, plan.Capture, bytes, plan.Before, plan.After, plan.UpdateSource);
                var report = new { planId = id, state = "applied", native, verifiedFingerprint = plan.After,
                    projectSaved = false, compileRequired = true, artifactDirectory = plan.Directory };
                File.WriteAllText(log, JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false)); return report;
            } catch (Exception ex) {
                File.WriteAllText(log, JsonSerializer.Serialize(new { state = "failed", error = ex.GetBaseException().Message,
                    projectSaved = false, inspectCurrentProjectBeforeRetry = true }, JsonDefaults.Options), new UTF8Encoding(false)); throw;
            }
        }
    }
}
