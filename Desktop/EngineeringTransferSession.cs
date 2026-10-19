using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.Online;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer
{
    internal sealed class DesktopImportPlan
    {
        internal string Path = "";
        internal string Directory = "";
        internal string FilesHash = "";
        internal string BaselineHash = "";
        internal string Fingerprint = "";
        internal bool Overwrite;
        internal bool Bundle;
        internal int ResourceCount;
        internal bool Applied;
        internal DateTime Created, Expires;
    }

    // Runs on the WinForms STA. Native handles remain in this session, never in saved plans.
    internal sealed class EngineeringTransferSession
    {
        private readonly TiaPortal portal;
        private readonly Project project;
        private readonly SoftwareReference reference;
        private readonly SafeguardStore safeguards;
        internal EngineeringTransferSession(TiaPortal portal, Project project, SoftwareReference reference, SafeguardStore safeguards)
        { this.portal = portal; this.project = project; this.reference = reference; this.safeguards = safeguards; }

        internal string Family => reference.Family;
        internal string TargetLabel => reference.Label;
        internal string ProjectPath => project.Path == null ? project.Name : project.Path.FullName;

        private void Check(bool transfer)
        {
            safeguards.Current().RequireProject(ProjectPath);
            if (portal.Projects.Count != 1 || !portal.Projects[0].Equals(project)) throw new InvalidOperationException("The attached project changed. Attach again.");
            var current = SoftwareCatalog.Discover(project).SingleOrDefault(s => s.Id == reference.Id);
            if (current == null || !current.Software.Equals(reference.Software)) throw new InvalidOperationException("The selected target changed. Attach again.");
            if (transfer && Family == "plc")
            {
                string? state;
                try { state = current.Owner.GetService<OnlineProvider>()?.State.ToString(); } catch { state = null; }
                PlcPolicy.RequireOffline(state);
            }
        }
        private object Resolve(string path)
        {
            DesktopTransferSelection.ValidatePath(Family, path);
            return EngineeringNative.Resolve(reference.Software, path);
        }
        internal EngineeringSnapshot Read(string path)
        {
            Check(false);
            return EngineeringNative.Read(Resolve(path), reference.Id, path);
        }

        internal string Export(string path, string parentDirectory)
        {
            Check(true);
            string directory = System.IO.Path.Combine(parentDirectory, "TIA-" + Family + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            using (var exclusive = portal.ExclusiveAccess("Desktop engineering export"))
            {
                Check(true);
                ExportCore(path, directory, false);
                if (exclusive.IsCancellationRequested) throw new OperationCanceledException("Export cancelled. Inspect the partial export directory: " + directory);
            }
            return directory;
        }

        private void ExportCore(string path, string directory, bool requireComplete)
        {
            Directory.CreateDirectory(directory);
            var entries = new List<TransferResource>(); var failures = new List<object>(); int count = 0;
            ExportNode(Resolve(path), path, directory, entries, failures, ref count, 0);
            File.WriteAllText(System.IO.Path.Combine(directory, "export-index.json"), JsonSerializer.Serialize(new {
                format = "tia-engineering-export-v1", family = Family, targetId = reference.Id, path, entries, failures
            }, JsonDefaults.Options), new UTF8Encoding(false));
            TransferFiles.Inventory(directory);
            if (requireComplete && failures.Count != 0) throw new InvalidOperationException("A complete baseline export is required. Inspect export-index.json in " + directory);
        }
        private static void ExportNode(object node, string path, string directory, List<TransferResource> entries, List<object> failures, ref int count, int depth)
        {
            if (depth > 64 || count >= 20000) throw new InvalidOperationException("Export traversal limit reached.");
            string relative = "objects/" + (count++).ToString("D5", CultureInfo.InvariantCulture);
            string target = System.IO.Path.Combine(directory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)); Directory.CreateDirectory(target);
            var state = EngineeringNative.Read(node, "", path);
            File.WriteAllText(System.IO.Path.Combine(target, "object.json"), JsonSerializer.Serialize(state, JsonDefaults.Options), new UTF8Encoding(false));
            if (state.Errors.Length != 0) failures.Add(new { path, errors = state.Errors });
            if (state.CanExport)
            {
                try
                {
                    string native = System.IO.Path.Combine(target, "native"); TransferNative.Export(node, native);
                    if (Directory.GetFiles(native, "*", SearchOption.AllDirectories).Length == 0) throw new InvalidOperationException("Native export produced no files.");
                    entries.Add(new TransferResource { Path = path, NativeType = state.NativeType, SourceDirectory = relative + "/native", ImportPath = state.CanImport ? path : ParentPath(path) });
                }
                catch (Exception ex) { failures.Add(new { path, error = ex.GetBaseException().Message }); }
                return;
            }
            foreach (var child in state.Children)
            {
                try { ExportNode(EngineeringNative.Resolve(node, child.Path.Substring(path.Length == 0 ? 0 : path.Length + 1)), child.Path, directory, entries, failures, ref count, depth + 1); }
                catch (Exception ex) { failures.Add(new { path = child.Path, error = ex.GetBaseException().Message }); }
            }
        }

        internal DesktopImportPlan PrepareImport(string path, string sourceDirectory, bool overwrite, bool bundle)
        {
            Check(true);
            var policy = safeguards.Current(); policy.Require(GuardedOperation.Apply);
            if (overwrite) policy.Require(GuardedOperation.Overwrite);
            string root = policy.DesktopEvidenceRoot.Length == 0 ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SiemensMCPServer", "DesktopTransfers") : policy.DesktopEvidenceRoot;
            string directory = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string candidate = System.IO.Path.Combine(directory, "candidate-files");
            TransferFiles.Copy(sourceDirectory, candidate);
            var plan = new DesktopImportPlan { Path = path, Directory = directory, Overwrite = overwrite, Bundle = bundle,
                Created = DateTime.UtcNow, Expires = DateTime.UtcNow.AddMinutes(policy.PreviewLifetimeMinutes),
                FilesHash = TransferFiles.Hash(TransferFiles.Inventory(candidate)) };
            ValidateCandidate(plan);
            using (var exclusive = portal.ExclusiveAccess("Desktop import baseline"))
            {
                Check(true);
                plan.Fingerprint = Read(path).Fingerprint;
                ExportCore(path, System.IO.Path.Combine(directory, "baseline"), true);
                plan.BaselineHash = TransferFiles.SemanticHash(System.IO.Path.Combine(directory, "baseline"));
                if (exclusive.IsCancellationRequested) throw new OperationCanceledException("Import preparation cancelled.");
            }
            return plan;
        }
        private void ValidateCandidate(DesktopImportPlan plan)
        {
            string candidate = System.IO.Path.Combine(plan.Directory, "candidate-files");
            if (!plan.Bundle)
            {
                TransferNative.ValidateImport(Resolve(plan.Path), candidate, plan.Overwrite);
                plan.ResourceCount = 1; return;
            }
            var resources = DesktopTransferSelection.ReadBundle(candidate, Family, plan.Path, plan.Overwrite);
            var snapshots = TransferBundle.Snapshots(candidate);
            foreach (var resource in resources)
            {
                object? target = null;
                try { target = Resolve(resource.ImportPath); }
                catch (InvalidOperationException) { ValidateMissingFolders(resource.ImportPath, snapshots); }
                if (target != null) TransferNative.ValidateImport(target, TransferBundle.SafePath(candidate, resource.SourceDirectory), plan.Overwrite);
            }
            plan.ResourceCount = resources.Length;
        }

        internal void Apply(DesktopImportPlan plan)
        {
            var policy = safeguards.Current(); policy.Require(GuardedOperation.Apply);
            if (plan.Overwrite) policy.Require(GuardedOperation.Overwrite);
            if (new PreviewPolicy(safeguards.Current).Expired(plan.Created, plan.Expires)) throw new InvalidOperationException("Desktop import preview expired. Prepare it again.");
            if (plan.Applied) throw new InvalidOperationException("This import plan has already been used. Prepare another import.");
            string candidate = System.IO.Path.Combine(plan.Directory, "candidate-files");
            if (TransferFiles.Hash(TransferFiles.Inventory(candidate)) != plan.FilesHash) throw new InvalidOperationException("Staged files changed after review. Prepare another import.");
            using (var exclusive = portal.ExclusiveAccess("Desktop engineering import"))
            {
                Check(true); ValidateCandidate(plan);
                if (Read(plan.Path).Fingerprint != plan.Fingerprint) throw new InvalidOperationException("Target changed after review. Prepare another import.");
                string baseline = System.IO.Path.Combine(plan.Directory, "apply-baseline");
                ExportCore(plan.Path, baseline, true);
                if (TransferFiles.SemanticHash(baseline) != plan.BaselineHash) throw new InvalidOperationException("Target resources changed after review. Prepare another import.");
                plan.Applied = true; // No retry after an uncertain native result.
                using (var transaction = exclusive.Transaction(project, "Desktop engineering import"))
                {
                    if (plan.Bundle)
                    {
                        var snapshots = TransferBundle.Snapshots(candidate);
                        foreach (var resource in DesktopTransferSelection.ReadBundle(candidate, Family, plan.Path, plan.Overwrite))
                            TransferNative.Import(EnsureFolder(resource.ImportPath, snapshots), TransferBundle.SafePath(candidate, resource.SourceDirectory), plan.Overwrite, Family == "plc");
                    }
                    else TransferNative.Import(Resolve(plan.Path), candidate, plan.Overwrite);
                    var state = Read(plan.Path);
                    if (state.Errors.Length != 0) throw new InvalidOperationException("Import readback failed: " + string.Join("; ", state.Errors));
                    ExportCore(plan.Path, System.IO.Path.Combine(plan.Directory, "after-export"), true);
                    if (exclusive.IsCancellationRequested || !transaction.CanCommit) throw new InvalidOperationException("Import cancelled or transaction cannot commit.");
                    transaction.CommitOnDispose();
                }
            }
        }

        private static bool IsFolder(EngineeringSnapshot state) => state.NativeType.Contains("Folder") || state.NativeType.EndsWith("HmiScreenGroup", StringComparison.Ordinal)
            || state.NativeType.EndsWith("HmiTagTableGroup", StringComparison.Ordinal) || state.NativeType.StartsWith("Siemens.Engineering.SW.", StringComparison.Ordinal) && state.NativeType.EndsWith("UserGroup", StringComparison.Ordinal);
        private void ValidateMissingFolders(string path, IDictionary<string, EngineeringSnapshot> snapshots)
        {
            string current = ""; bool missing = false;
            foreach (string part in path.Split('/'))
            {
                current = EngineeringNative.Join(current, part);
                if (!missing) { try { Resolve(current); continue; } catch (InvalidOperationException) { missing = true; } }
                // A missing named folder may have navigator properties/compositions underneath it.
                if (part.StartsWith("n:", StringComparison.Ordinal))
                {
                    if (!snapshots.TryGetValue(current, out var state) || !IsFolder(state)) throw new InvalidOperationException("Bundle can recreate folder hierarchy only: " + current);
                }
                else if (!part.StartsWith("p:", StringComparison.Ordinal)) throw new InvalidOperationException("Unsupported missing destination: " + current);
            }
        }
        private object EnsureFolder(string path, IDictionary<string, EngineeringSnapshot> snapshots)
        {
            object node = reference.Software; string current = "";
            foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string childPath = EngineeringNative.Join(current, part);
                var found = EngineeringNative.Children(node, "").SingleOrDefault(c => c.Key == part);
                if (found.Value != null) node = found.Value;
                else
                {
                    if (!part.StartsWith("n:", StringComparison.Ordinal) || !snapshots.TryGetValue(childPath, out var source) || !IsFolder(source)) throw new InvalidOperationException("Required folder is unavailable: " + childPath);
                    var state = EngineeringNative.Read(node, reference.Id, current);
                    var creation = state.Creations.SingleOrDefault(c => c.NativeType == source.NativeType) ?? throw new InvalidOperationException("This API cannot recreate the folder: " + childPath);
                    var name = creation.Parameters.Single(p => p.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
                    node = EngineeringNative.Create(node, state, source.NativeType, new[] { new CreationArgument { Name = name.Name, Value = Uri.UnescapeDataString(part.Substring(2)) } });
                }
                current = childPath;
            }
            return node;
        }
        private static string ParentPath(string path) { int i = path.LastIndexOf('/'); return i < 0 ? "" : path.Substring(0, i); }
    }
}
