using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SiemensMCPServer.Mcp
{
    internal interface IEngineeringBackend
    {
        EngineeringSnapshot ReadEngineering(string targetId, string path);
        void ValidateCreation(EngineeringSnapshot state, string type, CreationArgument[] args);
        object ExportEngineering(string targetId, string path, string directory, bool requireComplete);
        object ValidateImport(EngineeringPlan plan);
        object ApplyEngineering(EngineeringPlan plan);
    }
    internal sealed class EngineeringPlan
    {
        public string Action { get; set; } = "";
        public EngineeringSnapshot Before { get; set; } = null!;
        public EngineeringSnapshot? Candidate { get; set; }
        public string NativeType { get; set; } = "";
        public CreationArgument[] Arguments { get; set; } = Array.Empty<CreationArgument>();
        public bool Overwrite { get; set; }
        public string Directory { get; set; } = "";
        public string BaselineHash { get; set; } = "";
    }
    internal sealed class EngineeringWorkflow
    {
        private readonly IEngineeringBackend backend;
        private readonly string root;
        private readonly PreviewPolicy policy;
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        public EngineeringWorkflow(IEngineeringBackend backend, string root, Func<SafeguardSettings>? settings = null, Func<DateTime>? utcNow = null) { this.backend = backend; this.root = root; policy = new PreviewPolicy(settings, utcNow); }
        public object Preview(string action, string targetId, string path, string fingerprint, EngineeringEdit[]? edits = null,
            string nativeType = "", CreationArgument[]? arguments = null, string sourceDirectory = "", bool overwrite = false)
        {
            policy.RequireCapacity(pending.Count);
            var before = backend.ReadEngineering(targetId, path);
            if (before.Fingerprint != fingerprint) throw new InvalidOperationException("Object changed; read it again.");
            if (before.Errors.Length != 0) throw new InvalidOperationException("Object inspection is incomplete: " + string.Join("; ", before.Errors));
            var plan = new EngineeringPlan { Action = action, Before = before, NativeType = nativeType, Arguments = arguments ?? Array.Empty<CreationArgument>(), Overwrite = overwrite };
            switch (action)
            {
                case "edit": plan.Candidate = before.Edit(edits!); break;
                case "create": backend.ValidateCreation(before, nativeType, plan.Arguments); break;
                case "delete": if (!before.CanDelete) throw new InvalidOperationException("This object cannot be deleted."); break;
                case "import": if (!before.CanImport) throw new NotSupportedException("This object has no supported native import."); break;
                case "bundle": TransferBundle.Read(sourceDirectory); break;
                case "source": PlcSourceFiles.Read(sourceDirectory); break;
                default: throw new ArgumentException("Unknown engineering action.");
            }
            string id = Guid.NewGuid().ToString("N");
            plan.Directory = Path.Combine(root, id); Directory.CreateDirectory(plan.Directory);
            object? importOperations = null;
            if (HasFiles(action))
            {
                TransferFiles.Copy(sourceDirectory, Path.Combine(plan.Directory, "candidate-files"));
                importOperations = backend.ValidateImport(plan);
                backend.ExportEngineering(targetId, path, Path.Combine(plan.Directory, "before-export"), requireComplete: true);
                plan.BaselineHash = TransferFiles.SemanticHash(Path.Combine(plan.Directory, "before-export"));
            }
            if (action == "delete")
            {
                backend.ExportEngineering(targetId, path, Path.Combine(plan.Directory, "before-export"), requireComplete: true);
                plan.BaselineHash = TransferFiles.SemanticHash(Path.Combine(plan.Directory, "before-export"));
            }
            // Exact plan bytes and the staged file inventory are bound together. Apply uses the in-memory plan.
            string json = JsonSerializer.Serialize(plan, JsonDefaults.Options);
            File.WriteAllText(Path.Combine(plan.Directory, "candidate.json"), json, new UTF8Encoding(false));
            var files = HasFiles(action) ? TransferFiles.Inventory(Path.Combine(plan.Directory, "candidate-files")) : Array.Empty<TransferFile>();
            string hash = TextListDocument.Hash(json + "\n" + TransferFiles.Hash(files));
            var item = new Pending { Plan = plan, Hash = hash, Created = policy.Now, Expires = policy.Expires };
            var report = new { planId = id, action, targetId, path, candidateSha256 = hash, beforeFingerprint = before.Fingerprint,
                changes = edits, nativeType, arguments = plan.Arguments, overwrite, files, importOperations, artifactDirectory = plan.Directory,
                nativeImportScope = action == "source" ? "Offline native source generation. May replace the blocks declared in the reviewed source; retains the external source in the project."
                    : HasFiles(action) ? "Native resource import. Unified directory imports can replace the entire exported composition. Review all candidate files and native scope." : null,
                expiresUtc = item.Expires, projectSaved = false, nativeWriteAcceptance = "pending" };
            File.WriteAllText(Path.Combine(plan.Directory, "preview.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), new UTF8Encoding(false));
            pending.Add(id, item); return report;
        }
        public object Apply(string id, string hash)
        {
            if (!pending.TryGetValue(id, out var item)) throw new InvalidOperationException("Unknown or already attempted engineering plan.");
            if (policy.Expired(item.Created, item.Expires)) { pending.Remove(id); throw new InvalidOperationException("Preview expired."); }
            var plan = item.Plan;
            var guards = policy.Current;
            if (!guards.AllowApply) throw new InvalidOperationException("Apply is disabled in Safeguards.");
            if (plan.Action == "delete" && !guards.AllowDelete) throw new InvalidOperationException("Delete is disabled in Safeguards.");
            if (plan.Overwrite && !guards.AllowOverwrite) throw new InvalidOperationException("Overwrite is disabled in Safeguards.");
            if (plan.Action == "source" && PlcSourceFiles.Read(Path.Combine(plan.Directory, "candidate-files")).AllowBlockReplacement && !guards.AllowSourceReplacement)
                throw new InvalidOperationException("PLC source block replacement is disabled in Safeguards.");
            string json = File.ReadAllText(Path.Combine(plan.Directory, "candidate.json"), Encoding.UTF8);
            var files = HasFiles(plan.Action) ? TransferFiles.Inventory(Path.Combine(plan.Directory, "candidate-files")) : Array.Empty<TransferFile>();
            if (hash != item.Hash || TextListDocument.Hash(json + "\n" + TransferFiles.Hash(files)) != hash
                || TextListDocument.Hash(File.ReadAllBytes(Path.Combine(plan.Directory, "candidate.json"))) != TextListDocument.Hash(JsonSerializer.Serialize(plan, JsonDefaults.Options)))
                throw new InvalidOperationException("Plan or staged files changed; preview again.");
            pending.Remove(id);
            string log = Path.Combine(plan.Directory, "apply.json");
            File.WriteAllText(log, "{\"state\":\"started\",\"projectSaved\":false}", new UTF8Encoding(false));
            try
            {
                object nativeResult = backend.ApplyEngineering(plan);
                var result = new { planId = id, state = "applied", nativeResult, projectSaved = false, compileRequired = true, artifactDirectory = plan.Directory };
                File.WriteAllText(log, JsonSerializer.Serialize(result, JsonDefaults.Options), new UTF8Encoding(false)); return result;
            }
            catch (Exception ex)
            {
                File.WriteAllText(log, JsonSerializer.Serialize(new { state = "failed", error = ex.GetBaseException().Message, projectSaved = false, inspectCurrentTargetBeforeRetry = true }, JsonDefaults.Options), new UTF8Encoding(false)); throw;
            }
        }
        private sealed class Pending { public EngineeringPlan Plan = null!; public string Hash = ""; public DateTime Created, Expires; }
        private static bool HasFiles(string action) => action == "import" || action == "bundle" || action == "source";
    }
}
