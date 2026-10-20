using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    internal interface ITextListBackend
    {
        string ReadTextList(string hmiId, string listName);
        void ImportTextList(string hmiId, string listName, string expectedFingerprint, string candidateXml);
    }

    internal interface ITextListStagingEvidence
    {
        object? TextListImportStaging { get; }
        object? TextListExportStaging { get; }
    }

    internal sealed class TextListWorkflow
    {
        private readonly ITextListBackend backend;
        private readonly string root;
        private readonly PreviewPolicy policy;
        private readonly IFeatureEngine features;
        private readonly Func<object?> identity;
        private JsonElement? lastAttempt;
        private readonly Dictionary<string, Plan> pending = new Dictionary<string, Plan>();
        public TextListWorkflow(ITextListBackend backend, string root, IFeatureEngine? features = null, Func<SafeguardSettings>? settings = null, Func<DateTime>? utcNow = null, Func<object?>? identity = null) { policy = new PreviewPolicy(settings, utcNow); this.backend = backend; this.root = root; this.features = features ?? new LocalFeatureEngine(); this.identity = identity ?? (() => null); }

        public object Preview(string hmiId, string listName, string expectedFingerprint, TextEdit[] edits)
        {
            policy.RequireCapacity(pending.Count);
            string originalXml = backend.ReadTextList(hmiId, listName);
            var before = TextListDocuments.Parse(originalXml);
            RequireFingerprint(before, expectedFingerprint);
            var intended = before.Edit(edits);
            var reply = features.Execute(before is TextListDocument ? "comfort" : "unified", "edit_text_list", new { snapshot = originalXml, arguments = new { edits } });
            var after = TextListDocuments.Parse(reply.GetProperty("candidate").GetString()!);
            RequireCandidate(before, after, intended);
            var diff = edits.Select(e => new { entry = e.Entry, culture = e.Culture, before = e.ExpectedText, after = e.NewText }).ToArray();
            JsonElement? validation = before is TextListDocument ? ValidateDelta(originalXml, after.Serialize(), expectedFingerprint, edits, Array.Empty<ClassicTextListChange>(), Array.Empty<string>()) : (JsonElement?)null;
            return Stage(hmiId, listName, originalXml, before, after, diff, validation);
        }

        public object PreviewStructure(string hmiId, string listName, string expectedFingerprint, ClassicTextListChange[] changes, string[] activeCultures)
        {
            policy.RequireCapacity(pending.Count);
            string originalXml = backend.ReadTextList(hmiId, listName);
            if (!(TextListDocuments.Parse(originalXml) is TextListDocument before)) throw new NotSupportedException("Structured entry/translation operations require a classic HMI text list.");
            RequireFingerprint(before, expectedFingerprint);
            var intended = before.ChangeStructure(changes, activeCultures);
            var reply = features.Execute("comfort", "change_text_list", new { snapshot = originalXml, arguments = new { changes, activeCultures } });
            var after = new TextListDocument(reply.GetProperty("candidate").GetString()!);
            RequireCandidate(before, after, intended);
            return Stage(hmiId, listName, originalXml, before, after, new { operations = changes, before = before.Structure(), after = after.Structure(), beforeValues = before.Read(), afterValues = after.Read(), activeProjectCultures = activeCultures },
                ValidateDelta(originalXml, after.Serialize(), expectedFingerprint, Array.Empty<TextEdit>(), changes, activeCultures));
        }

        private static JsonElement ValidateDelta(string before, string after, string fingerprint, TextEdit[] edits, ClassicTextListChange[] changes, string[] cultures)
        {
            var result = ClassicTextListValidation.Delta(new ClassicDeltaValidation { BeforeXml = before, CandidateXml = after,
                ExpectedBeforeFingerprint = fingerprint, Edits = edits, Changes = changes, ActiveCultures = cultures, TargetVersion = "" });
            if (!result.GetProperty("validation").GetProperty("canPublish").GetBoolean()) throw new InvalidOperationException("Required text-list delta validation failed; no preview was published.");
            return result;
        }

        public object Status() => new { pendingPlans = pending.Values.Select(p => new { planId = p.Id, hmiId = p.HmiId, listName = p.ListName,
            beforeFingerprint = p.Before, candidateSha256 = p.CandidateHash, artifactDirectory = p.Directory, expiresUtc = p.Expires }).ToArray(), lastAttempt,
            scope = "This native session only. Retained receipts describe observations, not permission to retry uncertain effects." };

        private static void RequireCandidate(ITextListDocument before, ITextListDocument after, ITextListDocument intended)
        {
            if (before.Name != after.Name || before.ArtifactExtension != after.ArtifactExtension) throw new InvalidOperationException("Extension cannot change text-list identity or format.");
            if (after.Fingerprint != intended.Fingerprint) throw new InvalidOperationException("Worker candidate differs from the exact requested text-list changes. No preview was created.");
        }

        private object Stage(string hmiId, string listName, string originalXml, ITextListDocument before, ITextListDocument after, object diff, JsonElement? validation)
        {
            if (before.Name != after.Name || before.ArtifactExtension != after.ArtifactExtension) throw new InvalidOperationException("Extension cannot change text-list identity or format.");
            if (after.Fingerprint == before.Fingerprint) throw new ArgumentException("The preview contains no changes.");
            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(root, id);
            Directory.CreateDirectory(directory);
            var plan = new Plan { Id = id, HmiId = hmiId, ListName = listName, Before = before.Fingerprint,
                After = after.Fingerprint, Candidate = after.Serialize(), Extension = after.ArtifactExtension, Directory = directory, Created = policy.Now, Expires = policy.Expires,
                Identity = JsonSerializer.SerializeToElement(identity(), JsonDefaults.Options) };
            // Original exports are retained separately; no source bytes are overwritten.
            string beforeFile = Path.Combine(directory, "before." + plan.Extension);
            File.WriteAllBytes(beforeFile, before.ArtifactExtension == "xml" ? NativeXmlValidation.SnapshotBytes(originalXml) : new UTF8Encoding(false).GetBytes(originalXml));
            File.WriteAllText(Path.Combine(directory, "candidate." + plan.Extension), plan.Candidate, new UTF8Encoding(false));
            plan.CandidateHash = TextListDocument.Hash(plan.Candidate);
            var report = new { planId = id, hmiId, listName, beforeFingerprint = plan.Before, afterFingerprint = plan.After,
                beforeSnapshotSha256 = TextListDocument.Hash(File.ReadAllBytes(beforeFile)), sourceSnapshotUtf8Sha256 = TextListDocument.Hash(originalXml),
                nativeExport = (backend as ITextListStagingEvidence)?.TextListExportStaging,
                snapshotFormat = before.ArtifactExtension, nativeImportScope = before.NativeImportScope, candidateSha256 = plan.CandidateHash, changes = diff,
                artifactDirectory = directory, expiresUtc = plan.Expires, projectIdentity = plan.Identity, validation };
            File.WriteAllText(Path.Combine(directory, "preview.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
            pending.Add(id, plan);
            return report;
        }

        public object Apply(string planId, string expectedCandidateSha256)
        {
            policy.RequireApply();
            Plan plan;
            if (!pending.TryGetValue(planId, out plan)) throw new InvalidOperationException("Unknown or already attempted plan. Create a new preview.");
            if (policy.Expired(plan.Created, plan.Expires))
            { pending.Remove(planId); throw new InvalidOperationException("Plan expired. Create a new preview."); }
            if (!string.Equals(expectedCandidateSha256, plan.CandidateHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Candidate hash does not match the preview.");
            string candidatePath = Path.Combine(plan.Directory, "candidate." + plan.Extension);
            if (TextListDocument.Hash(File.ReadAllBytes(candidatePath)) != plan.CandidateHash)
                throw new InvalidOperationException("Candidate artifact changed after preview. Create a new preview.");
            // Consume before calling the backend: a client retry can never repeat an uncertain import.
            pending.Remove(planId);
            string attempt = Path.Combine(plan.Directory, "apply.json");
            bool importReturned = false;
            lastAttempt = JsonSerializer.SerializeToElement(new { planId, state = "started", hmiId = plan.HmiId, listName = plan.ListName,
                projectIdentity = plan.Identity, beforeFingerprint = plan.Before, candidateSha256 = plan.CandidateHash, projectSaved = false, artifactDirectory = plan.Directory }, JsonDefaults.Options);
            File.WriteAllText(attempt, lastAttempt.Value.GetRawText(), new UTF8Encoding(false));
            try
            {
                backend.ImportTextList(plan.HmiId, plan.ListName, plan.Before, plan.Candidate);
                importReturned = true;
                string snapshot = backend.ReadTextList(plan.HmiId, plan.ListName);
                string afterFile = Path.Combine(plan.Directory, "native-after." + plan.Extension);
                File.WriteAllBytes(afterFile, plan.Extension == "xml" ? NativeXmlValidation.SnapshotBytes(snapshot) : new UTF8Encoding(false).GetBytes(snapshot));
                string readback = TextListDocuments.Parse(snapshot).Fingerprint;
                if (readback != plan.After) throw new InvalidOperationException("Post-import export differs from the reviewed candidate. Inspect current native state; no retry or automatic rollback was performed.");
                var report = new { planId, state = "applied", verifiedFingerprint = readback, projectSaved = false,
                    compileRequired = true, artifactDirectory = plan.Directory, hmiId = plan.HmiId, listName = plan.ListName, projectIdentity = plan.Identity,
                    beforeFingerprint = plan.Before, candidateSha256 = plan.CandidateHash, nativeImportReturned = true, nativeReadback = "matched",
                    readbackSnapshotSha256 = TextListDocument.Hash(File.ReadAllBytes(afterFile)), readbackFile = afterFile,
                    nativeStaging = (backend as ITextListStagingEvidence)?.TextListImportStaging, nativeExport = (backend as ITextListStagingEvidence)?.TextListExportStaging,
                    snapshotArtifactEncoding = "Declared XML encoding (UTF-8 when absent); exact native source bytes are retained separately when supplied by the native backend", downloadPerformed = false };
                lastAttempt = JsonSerializer.SerializeToElement(report, JsonDefaults.Options);
                File.WriteAllText(attempt, JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
                return report;
            }
            catch (Exception ex)
            {
                lastAttempt = JsonSerializer.SerializeToElement(new { planId, state = importReturned ? "native-import-returned-readback-unverified" : "failed", error = ex.Message,
                    hmiId = plan.HmiId, listName = plan.ListName, projectIdentity = plan.Identity, beforeFingerprint = plan.Before, candidateSha256 = plan.CandidateHash,
                    nativeImportReturned = importReturned, nativeOutcome = "needs-inspection", projectSaved = false, inspectCurrentHmiBeforeRetry = true,
                    nativeStaging = (backend as ITextListStagingEvidence)?.TextListImportStaging,
                    artifactDirectory = plan.Directory, downloadPerformed = false }, JsonDefaults.Options);
                File.WriteAllText(attempt, lastAttempt.Value.GetRawText(), new UTF8Encoding(false));
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
            public DateTime Created, Expires;
            public JsonElement Identity;
        }
    }

}
