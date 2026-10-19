using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    internal sealed class ClassicResource
    {
        public string HmiId { get; set; } = "";
        public string Path { get; set; } = "";
        public string Directory { get; set; } = "";
        public string XmlFile { get; set; } = "";
        public bool Editable { get; set; } = true;
        public string Fingerprint => TransferFiles.SemanticHash(Directory);
    }
    internal interface IClassicResourceBackend
    {
        ClassicResource ReadClassicResource(string hmiId, string path);
        void ImportClassicResource(ClassicResource before, string candidateDirectory, string expectedFingerprint);
    }
    internal sealed class ClassicXmlWorkflow
    {
        private readonly IClassicResourceBackend backend;
        private readonly string root;
        private readonly PreviewPolicy policy;
        private readonly IFeatureEngine features;
        private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>();
        public ClassicXmlWorkflow(IClassicResourceBackend backend, string root, IFeatureEngine? features = null, Func<SafeguardSettings>? settings = null, Func<DateTime>? utcNow = null) { policy = new PreviewPolicy(settings, utcNow); this.backend = backend; this.root = root; this.features = features ?? new LocalFeatureEngine(); }
        public object Read(string hmiId, string path)
        {
            var resource = backend.ReadClassicResource(hmiId, path);
            string xml = File.ReadAllText(System.IO.Path.Combine(resource.Directory, resource.XmlFile), Encoding.UTF8);
            if (xml.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Resource XML exceeds the 16 MiB read limit; use native export files.");
            var document = ExtensionWire.Parse<ClassicInspection>(features.Execute("comfort", "inspect_xml", new { xml, editable = resource.Editable }));
            return new { hmiId, path, fingerprint = resource.Fingerprint, document.Name, document.NativeType, resource.Editable, xml, scalarCatalog = document.ScalarCatalog, behaviorCatalog = document.BehaviorCatalog,
                artifactDirectory = resource.Directory, files = TransferFiles.Inventory(resource.Directory) };
        }
        public object Preview(string hmiId, string path, string fingerprint, ClassicXmlEdit[] edits)
            => PreviewTransformed(hmiId, path, fingerprint, xml => features.Execute("comfort", "edit_xml", new { snapshot = xml, arguments = new { edits } }).GetProperty("candidateXml").GetString()!, edits);

        public object PreviewTransformed(string hmiId, string path, string fingerprint, Func<string, string> transform, object changes)
        {
            policy.RequireCapacity(plans.Count);
            var before = backend.ReadClassicResource(hmiId, path);
            if (!before.Editable) throw new InvalidOperationException("This classic resource is inspection-only.");
            if (before.Fingerprint != fingerprint) throw new InvalidOperationException("Classic resource changed; read it again.");
            string original = File.ReadAllText(System.IO.Path.Combine(before.Directory, before.XmlFile), Encoding.UTF8);
            string candidate = transform(original);
            if (candidate == null || candidate.Length > 16 * 1024 * 1024) throw new ArgumentException("Extension candidate XML exceeds the resource limit.");
            var baseline = new ClassicXmlDocument(original); var updated = new ClassicXmlDocument(candidate);
            updated.ValidateNativeLineGeometry();
            if (baseline.Name != updated.Name || baseline.NativeType != updated.NativeType) throw new InvalidOperationException("Extension cannot change resource identity.");
            if (baseline.NativeType == "Hmi.Screen.ScreenSlidein"
                && TransferFiles.Xml(original).Root!.Element(baseline.NativeType)?.Element("AttributeList")?.Element("SlideinType")?.Value
                    != TransferFiles.Xml(candidate).Root!.Element(updated.NativeType)?.Element("AttributeList")?.Element("SlideinType")?.Value)
                throw new InvalidOperationException("Extension cannot change slide-in edge identity.");
            return Stage(before, fingerprint, candidate, changes is Func<object> resolve ? resolve() : changes);
        }
        public object PreviewBehaviors(string hmiId, string path, string fingerprint, ClassicBehaviorChange[] changes)
            => PreviewTransformed(hmiId, path, fingerprint, xml => features.Execute("comfort", "change_behaviors", new { snapshot = xml, arguments = new { changes } }).GetProperty("candidateXml").GetString()!, changes);
        private object Stage(ClassicResource before, string fingerprint, string candidate, object changes)
        {
            string id = Guid.NewGuid().ToString("N"), directory = System.IO.Path.Combine(root, id);
            string files = System.IO.Path.Combine(directory, "candidate-files");
            TransferFiles.Copy(before.Directory, files);
            File.WriteAllText(System.IO.Path.Combine(files, before.XmlFile), candidate, new UTF8Encoding(false));
            TransferFiles.ValidateXmlCompanions(files);
            if (TransferFiles.SemanticHash(files) == fingerprint) throw new ArgumentException("The extension preview contains no changes.");
            var plan = new Plan { Before = before, BeforeFingerprint = fingerprint, Directory = directory, Created = policy.Now, Expires = policy.Expires,
                CandidateFingerprint = TransferFiles.SemanticHash(files), Hash = TransferFiles.Hash(TransferFiles.Inventory(files)) };
            var report = new { planId = id, hmiId = before.HmiId, path = before.Path, candidateSha256 = plan.Hash, beforeFingerprint = fingerprint,
                candidateFingerprint = plan.CandidateFingerprint, changes, artifactDirectory = directory,
                beforeArtifactDirectory = before.Directory, expiresUtc = plan.Expires, projectSaved = false };
            File.WriteAllText(System.IO.Path.Combine(directory, "preview.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
            plans.Add(id, plan); return report;
        }
        public object Apply(string id, string hash)
        {
            policy.RequireApply();
            if (!plans.TryGetValue(id, out var plan)) throw new InvalidOperationException("Unknown or already attempted classic resource plan.");
            if (policy.Expired(plan.Created, plan.Expires)) { plans.Remove(id); throw new InvalidOperationException("Classic resource preview expired."); }
            string files = System.IO.Path.Combine(plan.Directory, "candidate-files");
            if (hash != plan.Hash || TransferFiles.Hash(TransferFiles.Inventory(files)) != hash) throw new InvalidOperationException("Candidate files changed; preview again.");
            plans.Remove(id);
            string log = System.IO.Path.Combine(plan.Directory, "apply.json");
            File.WriteAllText(log, "{\"state\":\"started\"}", new UTF8Encoding(false));
            try
            {
                // Baseline fingerprint is stored independently of mutable artifact files.
                backend.ImportClassicResource(new ClassicResource { HmiId = plan.Before.HmiId, Path = plan.Before.Path, Directory = plan.Before.Directory, XmlFile = plan.Before.XmlFile }, files, plan.BeforeFingerprint);
                var report = new { planId = id, state = "applied", verifiedFingerprint = plan.CandidateFingerprint, projectSaved = false, compileRequired = true, artifactDirectory = plan.Directory };
                File.WriteAllText(log, JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false)); return report;
            }
            catch (Exception ex)
            {
                File.WriteAllText(log, JsonSerializer.Serialize(new { state = "failed", error = ex.GetBaseException().Message, projectSaved = false, inspectCurrentHmiBeforeRetry = true }, JsonDefaults.Options), new UTF8Encoding(false)); throw;
            }
        }
        private sealed class Plan
        {
            public ClassicResource Before = null!;
            public string Directory = "", Hash = "", CandidateFingerprint = "", BeforeFingerprint = "";
            public DateTime Created, Expires;
        }
    }
}
