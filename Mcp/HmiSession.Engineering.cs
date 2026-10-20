using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Siemens.Engineering;

namespace SiemensMCPServer.Mcp
{
    internal sealed partial class HmiSession
    {
        private object ResolveEngineering(string targetId, string path)
        {
            var reference = FindEngineeringReference(targetId);
            const string graphics = "$projectGraphics";
            if (path == graphics || path.StartsWith(graphics + "/", StringComparison.Ordinal))
            {
                if (reference.Family == "plc") throw new NotSupportedException("Project graphics are an HMI resource.");
                object root = NativeProjectResources.Graphics(project!);
                return EngineeringNative.Resolve(root, path == graphics ? "" : path.Substring(graphics.Length + 1));
            }
            return EngineeringNative.Resolve(reference.Software, path);
        }
        public EngineeringSnapshot ReadEngineering(string targetId, string path)
        {
            var state = EngineeringNative.Read(ResolveEngineering(targetId, path), targetId, path);
            if (!path.StartsWith("$projectGraphics", StringComparison.Ordinal)
                && EngineeringNative.HasDynamicAncestor(FindEngineeringReference(targetId).Software, path)) state.ProtectInheritedDynamization();
            return state;
        }
        public object ReadEngineeringObject(string targetId, string path)
        {
            var state = ReadEngineering(targetId, path);
            return new { fingerprint = state.Fingerprint, state, sharedProjectGraphicsPath = FindEngineeringReference(targetId).Family == "plc" ? null : "$projectGraphics", fingerprintScope = "Returned attributes, child identities, and advertised operations; child content requires its own read." };
        }
        public object PreviewEngineering(string action, string targetId, string path, string fingerprint, EngineeringEdit[]? edits,
            string nativeType, CreationArgument[]? arguments, string sourceDirectory, bool overwrite)
        {
            CheckProject(); return engineeringWorkflow!.Preview(action, targetId, path, fingerprint, edits, nativeType, arguments, sourceDirectory, overwrite);
        }
        public object ApplyEngineeringPlan(string planId, string candidateSha256) { CheckProject(); return engineeringWorkflow!.Apply(planId, candidateSha256); }
        public object ValidateImport(EngineeringPlan plan)
        {
            string directory = Path.Combine(plan.Directory, "candidate-files");
            if (plan.Action == "source") return ValidatePlcSource(plan, directory);
            if (plan.Action == "import")
            {
                object target = ResolveEngineering(plan.Before.TargetId, plan.Before.Path);
                TransferNative.ValidateImport(target, directory, plan.Overwrite);
                return new[] { new { importPath = plan.Before.Path, sourceDirectory = "candidate-files" } };
            }
            var resources = TransferBundle.Read(directory);
            foreach (var resource in resources)
            {
                if (!plan.Overwrite && resource.NativeType.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal))
                    throw new ArgumentException("Unified bundle imports require overwrite=true to acknowledge native replacement scope.");
                if (plan.Before.Path.Length != 0 && resource.ImportPath != plan.Before.Path && !resource.ImportPath.StartsWith(plan.Before.Path + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Bundle imports outside the selected subtree.");
                object? target = null;
                try { target = ResolveEngineering(plan.Before.TargetId, resource.ImportPath); }
                catch (InvalidOperationException) { /* Missing folder hierarchy is checked and created inside the transaction. */ }
                if (target != null) TransferNative.ValidateImport(target, TransferBundle.SafePath(directory, resource.SourceDirectory), plan.Overwrite, skipExistingCycles: true);
            }
            return resources;
        }
        public void ValidateCreation(EngineeringSnapshot state, string type, CreationArgument[] args)
        {
            var creation = state.Creations.SingleOrDefault(c => c.NativeType == type) ?? throw new NotSupportedException("Creation type is not advertised by this composition.");
            EngineeringNative.CreationValues(creation, args);
        }
        public object ExportEngineeringObject(string targetId, string path)
        {
            CheckProject(); RequireOfflineIfPlc(targetId);
            string directory = Path.Combine(artifactRoot, "engineering-exports", Guid.NewGuid().ToString("N"));
            using (var exclusive = portal!.ExclusiveAccess("Codex HMI export"))
            {
                CheckProject(); RequireOfflineIfPlc(targetId); return ExportEngineering(targetId, path, directory, false);
            }
        }
        public object ExportEngineering(string targetId, string path, string directory, bool requireComplete)
        {
            RequireOfflineIfPlc(targetId);
            object node = ResolveEngineering(targetId, path);
            Directory.CreateDirectory(directory);
            var entries = new List<object>(); var failures = new List<object>(); int count = 0;
            ExportNode(node, path, directory, entries, failures, ref count, 0);
            if (path == "" && FindEngineeringReference(targetId).Family != "plc")
            {
                try { ExportNode(ResolveEngineering(targetId, "$projectGraphics"), "$projectGraphics", directory, entries, failures, ref count, 0); }
                catch (Exception ex) { failures.Add(new { path = "$projectGraphics", error = ex.GetBaseException().Message }); }
            }
            File.WriteAllText(Path.Combine(directory, "export-index.json"), JsonSerializer.Serialize(new { format = "tia-engineering-export-v1", family = FindEngineeringReference(targetId).Family, targetId, path, entries, failures }, JsonDefaults.Options), new UTF8Encoding(false));
            var files = TransferFiles.Inventory(directory);
            if (requireComplete && failures.Count != 0) throw new InvalidOperationException("A complete baseline export is required for import. Inspect export-index.json for unsupported or failed objects.");
            return new { targetId, path, artifactDirectory = directory, files, sha256 = TransferFiles.Hash(files), exportedResources = entries.Count,
                failures, complete = failures.Count == 0, scope = "All API-visible descendants with metadata snapshots; native XML/YAML/JS and sidecars where export exists. This is not a complete TIA project backup." };
        }
        private static void ExportNode(object node, string path, string directory, List<object> entries, List<object> failures, ref int count, int depth)
        {
            if (depth > 64 || count >= 20000) throw new InvalidOperationException("Export traversal limit reached.");
            string relative = "objects/" + (count++).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            string target = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(target);
            var state = EngineeringNative.Read(node, "", path);
            File.WriteAllText(Path.Combine(target, "object.json"), JsonSerializer.Serialize(state, JsonDefaults.Options), new UTF8Encoding(false));
            if (state.Errors.Length != 0) failures.Add(new { path, errors = state.Errors });
            if (state.CanExport)
            {
                try
                {
                    TransferNative.Export(node, Path.Combine(target, "native"));
                    if (Directory.GetFiles(Path.Combine(target, "native"), "*", SearchOption.AllDirectories).Length != 0)
                        entries.Add(new { path, nativeType = state.NativeType, sourceDirectory = relative + "/native", importPath = state.CanImport ? path : ParentPath(path) });
                }
                catch (Exception ex) { failures.Add(new { path, error = ex.GetBaseException().Message }); }
                return;
            }
            foreach (var child in state.Children)
            {
                try
                {
                    string key = child.Path.Substring(path.Length == 0 ? 0 : path.Length + 1);
                    object native = EngineeringNative.Children(node, "").Single(c => c.Key == key).Value;
                    ExportNode(native, child.Path, directory, entries, failures, ref count, depth + 1);
                }
                catch (Exception ex) { failures.Add(new { path = child.Path, error = ex.GetBaseException().Message }); }
            }
        }
        private static string ParentPath(string path) { int i = path.LastIndexOf('/'); return i < 0 ? "" : path.Substring(0, i); }
        public object ApplyEngineering(EngineeringPlan plan)
        {
            using (var exclusive = portal!.ExclusiveAccess("Codex HMI " + plan.Action))
            {
                CheckProject(); RequireOfflineIfPlc(plan.Before.TargetId); object node = ResolveEngineering(plan.Before.TargetId, plan.Before.Path);
                var current = ReadEngineering(plan.Before.TargetId, plan.Before.Path);
                if (current.Fingerprint != plan.Before.Fingerprint) throw new InvalidOperationException("Object changed since preview; read it again.");
                if (plan.Action == "import" || plan.Action == "bundle" || plan.Action == "delete" || plan.Action == "source")
                {
                    string baseline = Path.Combine(plan.Directory, "apply-baseline");
                    ExportEngineering(plan.Before.TargetId, plan.Before.Path, baseline, true);
                    if (TransferFiles.SemanticHash(baseline) != plan.BaselineHash) throw new InvalidOperationException("Native resource content changed since preview.");
                }
                object result;
                using (var transaction = exclusive.Transaction(project!, "HMI " + plan.Action))
                {
                    switch (plan.Action)
                    {
                        case "edit":
                            EngineeringNative.Write(node, current, plan.Candidate!);
                            var after = EngineeringNative.Read(node, current.TargetId, current.Path);
                            if (after.ReadbackFingerprint != plan.Candidate!.ReadbackFingerprint) throw new InvalidOperationException("Native authored-field readback differs from preview; transaction not committed.");
                            if (current.Path.Split('/').Last().StartsWith("n:", StringComparison.Ordinal))
                            {
                                object editParent = ResolveEngineering(current.TargetId, ParentPath(current.Path));
                                string updatedKey = EngineeringNative.Children(editParent, "").Single(c => c.Value.Equals(node)).Key;
                                after.Path = EngineeringNative.Join(ParentPath(current.Path), updatedKey);
                            }
                            result = new { fingerprint = after.Fingerprint, state = after }; break;
                        case "create":
                            object created = EngineeringNative.Create(node, current, plan.NativeType, plan.Arguments);
                            var children = EngineeringNative.Children(node, "");
                            if (children.Count != current.Children.Length + 1) throw new InvalidOperationException("Creation did not add exactly one child.");
                            string key = children.Single(c => c.Value.Equals(created)).Key;
                            var createdState = ReadEngineering(current.TargetId, EngineeringNative.Join(current.Path, key));
                            if (createdState.Errors.Length != 0) throw new InvalidOperationException("Created object could not be fully inspected.");
                            result = new { createdPath = createdState.Path, state = createdState, fingerprint = createdState.Fingerprint }; break;
                        case "delete":
                            if (!current.CanDelete) throw new InvalidOperationException("Object is not deletable.");
                            object parent = ResolveEngineering(current.TargetId, ParentPath(current.Path));
                            var composition = parent as IEngineeringComposition ?? throw new InvalidOperationException("Deletion requires a composition member.");
                            int oldCount = composition.Count;
                            node.GetType().GetMethod("Delete", Type.EmptyTypes)!.Invoke(node, null);
                            // Do not pass a destroyed native handle back into Contains().
                            string deletedKey = current.Path.Split('/').Last();
                            var remaining = EngineeringNative.Children(composition, "");
                            if (composition.Count != oldCount - 1 || (!deletedKey.StartsWith("i:", StringComparison.Ordinal) && remaining.Any(c => c.Key == deletedKey)))
                                throw new InvalidOperationException("Deletion readback failed.");
                            result = new { deletedPath = current.Path, remainingMembers = composition.Count }; break;
                        case "import":
                            TransferNative.Import(node, Path.Combine(plan.Directory, "candidate-files"), plan.Overwrite, evidenceRoot: plan.Directory);
                            var imported = ReadEngineering(current.TargetId, current.Path);
                            if (imported.Errors.Length != 0) throw new InvalidOperationException("Import readback could not be inspected.");
                            ExportEngineering(current.TargetId, current.Path, Path.Combine(plan.Directory, "after-export"), true);
                            result = new { nativeImportAccepted = true, contentEquality = "not asserted; inspect after-export and compile diagnostics", state = imported, fingerprint = imported.Fingerprint }; break;
                        case "bundle":
                            result = ImportBundle(plan);
                            ExportEngineering(current.TargetId, current.Path, Path.Combine(plan.Directory, "after-export"), true);
                            break;
                        case "source":
                            result = GeneratePlcSource(plan);
                            ExportEngineering(current.TargetId, current.Path, Path.Combine(plan.Directory, "after-export"), true);
                            break;
                        default: throw new ArgumentException("Unknown engineering operation.");
                    }
                    if (exclusive.IsCancellationRequested || !transaction.CanCommit) throw new InvalidOperationException("Transaction cancelled or cannot commit.");
                    transaction.CommitOnDispose();
                }
                return result;
            }
        }
        private object ImportBundle(EngineeringPlan plan)
        {
            string directory = Path.Combine(plan.Directory, "candidate-files");
            var resources = TransferBundle.Read(directory);
            var snapshots = TransferBundle.Snapshots(directory);
            var outcomes = new List<object>();
            foreach (var resource in resources)
            {
                // A category export may be imported only within its selected subtree.
                if (plan.Before.Path.Length != 0 && resource.ImportPath != plan.Before.Path && !resource.ImportPath.StartsWith(plan.Before.Path + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Bundle import path is outside the selected subtree.");
                object target = EnsureBundleFolder(plan.Before.TargetId, resource.ImportPath, snapshots);
                string source = TransferBundle.SafePath(directory, resource.SourceDirectory);
                if (target.GetType().FullName!.StartsWith("Siemens.Engineering.Hmi.Cycle.", StringComparison.Ordinal))
                {
                    string name = TransferFiles.XmlObjectName(File.ReadAllText(Directory.GetFiles(source, "*.xml").Single()));
                    if (EngineeringNative.Children(target, "").Any(c => c.Key == "n:" + Uri.EscapeDataString(name)))
                    { outcomes.Add(new { resource.Path, state = "skipped-existing-cycle" }); continue; }
                }
                TransferNative.Import(target, source, plan.Overwrite, allowMissingReferences: FindEngineeringReference(plan.Before.TargetId).Family == "plc", evidenceRoot: plan.Directory);
                outcomes.Add(new { resource.Path, state = "native-import-accepted" });
            }
            return new { resources = outcomes, contentEquality = "not asserted; inspect after-export and compile diagnostics" };
        }
        private object EnsureBundleFolder(string targetId, string path, IDictionary<string, EngineeringSnapshot> snapshots)
        {
            object node = path.StartsWith("$projectGraphics", StringComparison.Ordinal) ? ResolveEngineering(targetId, "$projectGraphics") : FindEngineeringReference(targetId).Software;
            string current = path.StartsWith("$projectGraphics", StringComparison.Ordinal) ? "$projectGraphics" : "";
            var parts = (current.Length == 0 ? path : path.Substring(current.Length).TrimStart('/')).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string childPath = EngineeringNative.Join(current, part);
                var found = EngineeringNative.Children(node, "").SingleOrDefault(c => c.Key == part);
                if (found.Value == null)
                {
                    if (!part.StartsWith("n:", StringComparison.Ordinal) || !snapshots.TryGetValue(childPath, out var source)) throw new InvalidOperationException("Required target path does not exist: " + childPath);
                    bool folder = source.NativeType.Contains("Folder") || source.NativeType.EndsWith("HmiScreenGroup", StringComparison.Ordinal) || source.NativeType.EndsWith("HmiTagTableGroup", StringComparison.Ordinal)
                        || source.NativeType.StartsWith("Siemens.Engineering.SW.", StringComparison.Ordinal) && source.NativeType.EndsWith("UserGroup", StringComparison.Ordinal);
                    if (!folder) throw new InvalidOperationException("Bundle may create folder hierarchy only: " + childPath);
                    var state = EngineeringNative.Read(node, targetId, current);
                    var creation = state.Creations.SingleOrDefault(c => c.NativeType == source.NativeType) ?? throw new InvalidOperationException("Target API cannot create folder: " + childPath);
                    var nameParameter = creation.Parameters.Single(p => p.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
                    node = EngineeringNative.Create(node, state, source.NativeType, new[] { new CreationArgument { Name = nameParameter.Name, Value = Uri.UnescapeDataString(part.Substring(2)) } });
                }
                else node = found.Value;
                current = childPath;
            }
            return node;
        }
    }
}
