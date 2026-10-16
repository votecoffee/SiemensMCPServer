using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SiemensTiaOpenness.Mcp
{
    internal interface ITextListBackend
    {
        string ReadTextList(string hmiId, string listName);
        void ImportTextList(string hmiId, string listName, string expectedFingerprint, string candidateXml);
    }

    internal sealed class TextListWorkflow
    {
        private readonly ITextListBackend backend;
        private readonly string root;
        private readonly Dictionary<string, Plan> pending = new Dictionary<string, Plan>();
        public TextListWorkflow(ITextListBackend backend, string root) { this.backend = backend; this.root = root; }

        public object Preview(string hmiId, string listName, string expectedFingerprint, TextEdit[] edits)
        {
            if (pending.Count >= 100) throw new InvalidOperationException("Too many pending previews. Reattach to clear them.");
            string originalXml = backend.ReadTextList(hmiId, listName);
            var before = TextListDocuments.Parse(originalXml);
            RequireFingerprint(before, expectedFingerprint);
            var after = before.Edit(edits);
            var diff = edits.Select(e => new { entry = e.Entry, culture = e.Culture, before = e.ExpectedText, after = e.NewText }).ToArray();
            return Stage(hmiId, listName, originalXml, before, after, diff);
        }

        public object PreviewStructure(string hmiId, string listName, string expectedFingerprint, ClassicTextListChange[] changes, string[] activeCultures)
        {
            if (pending.Count >= 100) throw new InvalidOperationException("Too many pending previews. Reattach to clear them.");
            string originalXml = backend.ReadTextList(hmiId, listName);
            if (!(TextListDocuments.Parse(originalXml) is TextListDocument before)) throw new NotSupportedException("Structured entry/translation operations require a classic HMI text list.");
            RequireFingerprint(before, expectedFingerprint);
            var after = before.ChangeStructure(changes, activeCultures);
            return Stage(hmiId, listName, originalXml, before, after, new { operations = changes, before = before.Structure(), after = after.Structure(), beforeValues = before.Read(), afterValues = after.Read(), activeProjectCultures = activeCultures });
        }

        private object Stage(string hmiId, string listName, string originalXml, ITextListDocument before, ITextListDocument after, object diff)
        {
            if (after.Fingerprint == before.Fingerprint) throw new ArgumentException("The preview contains no changes.");
            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(root, id);
            Directory.CreateDirectory(directory);
            var plan = new Plan { Id = id, HmiId = hmiId, ListName = listName, Before = before.Fingerprint,
                After = after.Fingerprint, Candidate = after.Serialize(), Extension = after.ArtifactExtension, Directory = directory, Created = DateTime.UtcNow };
            // Original exports are retained separately; no source bytes are overwritten.
            File.WriteAllText(Path.Combine(directory, "before." + plan.Extension), originalXml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "candidate." + plan.Extension), plan.Candidate, new UTF8Encoding(false));
            plan.CandidateHash = TextListDocument.Hash(plan.Candidate);
            var report = new { planId = id, hmiId, listName, beforeFingerprint = plan.Before, afterFingerprint = plan.After,
                beforeSnapshotSha256 = TextListDocument.Hash(originalXml), snapshotFormat = before.ArtifactExtension, nativeImportScope = before.NativeImportScope, candidateSha256 = plan.CandidateHash, changes = diff,
                artifactDirectory = directory, expiresUtc = plan.Created.AddMinutes(30) };
            File.WriteAllText(Path.Combine(directory, "preview.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
            pending.Add(id, plan);
            return report;
        }

        public object Apply(string planId, string expectedCandidateSha256)
        {
            Plan plan;
            if (!pending.TryGetValue(planId, out plan)) throw new InvalidOperationException("Unknown or already attempted plan. Create a new preview.");
            if (DateTime.UtcNow - plan.Created > TimeSpan.FromMinutes(30))
            { pending.Remove(planId); throw new InvalidOperationException("Plan expired. Create a new preview."); }
            if (!string.Equals(expectedCandidateSha256, plan.CandidateHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Candidate hash does not match the preview.");
            string candidatePath = Path.Combine(plan.Directory, "candidate." + plan.Extension);
            if (TextListDocument.Hash(File.ReadAllBytes(candidatePath)) != plan.CandidateHash)
                throw new InvalidOperationException("Candidate artifact changed after preview. Create a new preview.");
            // Consume before calling the backend: a client retry can never repeat an uncertain import.
            pending.Remove(planId);
            string attempt = Path.Combine(plan.Directory, "apply.json");
            File.WriteAllText(attempt, "{\"state\":\"started\",\"projectSaved\":false}", new UTF8Encoding(false));
            try
            {
                backend.ImportTextList(plan.HmiId, plan.ListName, plan.Before, plan.Candidate);
                var report = new { planId, state = "applied", verifiedFingerprint = plan.After, projectSaved = false,
                    compileRequired = true, artifactDirectory = plan.Directory };
                File.WriteAllText(attempt, JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
                return report;
            }
            catch (Exception ex)
            {
                File.WriteAllText(attempt, JsonSerializer.Serialize(new { planId, state = "failed", error = ex.Message,
                    projectSaved = false, inspectCurrentHmiBeforeRetry = true }, JsonDefaults.Options), new UTF8Encoding(false));
                throw;
            }
        }

        internal static void RequireFingerprint(ITextListDocument actual, string expected)
        {
            if (actual.Fingerprint != expected) throw new InvalidOperationException("Text list changed since it was read. Read it again and create a new preview.");
        }

        private sealed class Plan
        {
            public string Id = "", HmiId = "", ListName = "", Before = "", After = "", Candidate = "", CandidateHash = "", Extension = "xml", Directory = "";
            public DateTime Created;
        }
    }

}
