using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensTiaOpenness.Extensions;

namespace SiemensTiaOpenness.Mcp
{
    internal interface IUnifiedBackend
    {
        UnifiedObjectState ReadUnified(string hmiId, string screenPath, string itemPath);
        void WriteUnified(UnifiedObjectState before, UnifiedObjectState candidate);
    }

    internal sealed class UnifiedEditWorkflow
    {
        private readonly IUnifiedBackend backend;
        private readonly string root;
        private readonly IFeatureEngine features;
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>();
        public UnifiedEditWorkflow(IUnifiedBackend backend, string root, IFeatureEngine? features = null) { this.backend = backend; this.root = root; this.features = features ?? new LocalFeatureEngine(); }

        public object Preview(string hmiId, string screenPath, string itemPath, string expectedFingerprint, UnifiedEdit[] edits)
            => PreviewTransformed(hmiId, screenPath, itemPath, expectedFingerprint,
                before => ExtensionWire.Parse<UnifiedObjectState>(features.Execute("unified", "edit_object", new { snapshot = before, arguments = new { edits } }).GetProperty("state")),
                edits.Select(e => new { property = e.Property, culture = e.Culture, before = e.ExpectedValue, after = e.NewValue }).ToArray());

        public object PreviewTransformed(string hmiId, string screenPath, string itemPath, string expectedFingerprint, Func<UnifiedObjectState, UnifiedObjectState> transform, object changes)
        {
            if (plans.Count >= 100) throw new InvalidOperationException("Too many previews; reattach to clear them.");
            var before = backend.ReadUnified(hmiId, screenPath, itemPath);
            if (before.Fingerprint != expectedFingerprint) throw new InvalidOperationException("Unified object changed. Read it again before previewing.");
            var after = transform(before);
            before.RequireAllowedCandidate(after);
            if (before.Fingerprint == after.Fingerprint) throw new ArgumentException("The preview contains no changes.");
            string id = Guid.NewGuid().ToString("N");
            var plan = new Plan { Before = before, After = after, Directory = Path.Combine(root, id), Created = DateTime.UtcNow };
            Directory.CreateDirectory(plan.Directory);
            string candidate = JsonSerializer.Serialize(after, JsonDefaults.Options);
            plan.Hash = TextListDocument.Hash(candidate);
            File.WriteAllText(Path.Combine(plan.Directory, "before.json"), JsonSerializer.Serialize(before, JsonDefaults.Options), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(plan.Directory, "candidate.json"), candidate, new UTF8Encoding(false));
            var report = new { planId = id, hmiId, screenPath, itemPath, beforeFingerprint = before.Fingerprint, afterFingerprint = after.Fingerprint,
                candidateSha256 = plan.Hash, changes,
                artifactDirectory = plan.Directory, expiresUtc = plan.Created.AddMinutes(30), projectSaved = false };
            File.WriteAllText(Path.Combine(plan.Directory, "preview.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
            plans.Add(id, plan);
            return report;
        }

        public object Apply(string planId, string candidateSha256)
        {
            Plan plan;
            if (!plans.TryGetValue(planId, out plan)) throw new InvalidOperationException("Unknown or already attempted Unified plan.");
            if (DateTime.UtcNow - plan.Created > TimeSpan.FromMinutes(30))
            { plans.Remove(planId); throw new InvalidOperationException("Unified plan expired. Create a new preview."); }
            if (candidateSha256 != plan.Hash || TextListDocument.Hash(File.ReadAllBytes(Path.Combine(plan.Directory, "candidate.json"))) != plan.Hash)
                throw new InvalidOperationException("Unified candidate hash does not match the preview.");
            plans.Remove(planId);
            string log = Path.Combine(plan.Directory, "apply.json");
            File.WriteAllText(log, "{\"state\":\"started\",\"projectSaved\":false}", new UTF8Encoding(false));
            try
            {
                backend.WriteUnified(plan.Before, plan.After);
                var report = new { planId, state = "applied", verifiedFingerprint = plan.After.Fingerprint, projectSaved = false, compileRequired = true, artifactDirectory = plan.Directory };
                File.WriteAllText(log, JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
                return report;
            }
            catch (Exception ex)
            {
                File.WriteAllText(log, JsonSerializer.Serialize(new { planId, state = "failed", error = ex.Message, projectSaved = false, inspectCurrentHmiBeforeRetry = true }, JsonDefaults.Options), new UTF8Encoding(false));
                throw;
            }
        }
        private sealed class Plan
        {
            public UnifiedObjectState Before = null!, After = null!;
            public string Hash = "", Directory = "";
            public DateTime Created;
        }
    }
}
