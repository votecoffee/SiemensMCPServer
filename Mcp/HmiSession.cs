using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Hmi;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    // All methods run on the runtime's single STA thread. No UI objects are required.
    internal sealed partial class HmiSession : IHmiSession, ITextListBackend, IUnifiedBackend, IEngineeringBackend, IClassicResourceBackend
    {
        private TiaPortal? portal;
        private Project? project;
        private string? projectPath;
        private IList<HmiReference> hmis = new List<HmiReference>();
        private TextListWorkflow? workflow;
        private UnifiedEditWorkflow? unifiedWorkflow;
        private EngineeringWorkflow? engineeringWorkflow;
        private ClassicXmlWorkflow? classicXmlWorkflow;
        private ProjectTranslationWorkflow? projectTranslationWorkflow;
        private readonly string artifactRoot;
        private readonly IFeatureEngine features;
        private readonly Func<SafeguardSettings> safeguards;
        private int? processId;

        public HmiSession(string artifactRoot, IFeatureEngine features, Func<SafeguardSettings>? safeguards = null) { this.artifactRoot = artifactRoot; this.features = features; this.safeguards = safeguards ?? (() => new SafeguardSettings()); }

        public object ListProcesses()
        {
            return TiaPortal.GetProcesses().Select(p => new { processId = p.Id, projectPath = p.ProjectPath?.FullName }).ToArray();
        }

        public object ApiCapabilities() { return UnifiedNative.ApiCapabilities(); }

        public object ListScreens(string hmiId)
        {
            object software = FindUnifiedSoftware(hmiId);
            return new { hmiId, family = "unified", screens = UnifiedNative.Screens(software).Select(s => new { screenPath = s.Path, name = s.Name }).ToArray() };
        }

        public object ListScreenItems(string hmiId, string screenPath)
        {
            object screen = UnifiedNative.FindScreen(FindUnifiedSoftware(hmiId), screenPath);
            return new { hmiId, screenPath, items = UnifiedNative.Items(screen).Select(i => new { itemPath = i.Path, name = i.Name, nativeType = i.Native.GetType().FullName }).ToArray() };
        }

        public object ReadUnifiedObject(string hmiId, string screenPath, string itemPath)
        {
            var state = ReadUnified(hmiId, screenPath, itemPath);
            return new { fingerprint = state.Fingerprint, state };
        }

        public UnifiedObjectState ReadUnified(string hmiId, string screenPath, string itemPath)
        {
            object target = UnifiedNative.FindObject(FindUnifiedSoftware(hmiId), screenPath, itemPath);
            return UnifiedNative.Read(target, hmiId, screenPath, itemPath);
        }

        public object PreviewUnified(string hmiId, string screenPath, string itemPath, string expectedFingerprint, UnifiedEdit[] edits)
        {
            CheckProject();
            return unifiedWorkflow!.Preview(hmiId, screenPath, itemPath, expectedFingerprint, edits);
        }

        public object ApplyUnified(string planId, string candidateSha256)
        {
            CheckProject();
            return unifiedWorkflow!.Apply(planId, candidateSha256);
        }

        public void WriteUnified(UnifiedObjectState before, UnifiedObjectState candidate)
        {
            CheckProject();
            using (var exclusive = portal!.ExclusiveAccess("Codex Unified screen edit"))
            {
                CheckProject();
                object native = UnifiedNative.FindObject(FindUnifiedSoftware(before.HmiId), before.ScreenPath, before.ItemPath);
                var current = UnifiedNative.Read(native, before.HmiId, before.ScreenPath, before.ItemPath);
                if (current.Fingerprint != before.Fingerprint) throw new InvalidOperationException("Unified object changed since preview. Read it again.");
                using (var transaction = exclusive.Transaction(project!, "Edit Unified screen object"))
                {
                    UnifiedNative.WriteChanged(native, current, candidate);
                    var readback = UnifiedNative.Read(native, before.HmiId, before.ScreenPath, before.ItemPath);
                    if (readback.Fingerprint != candidate.Fingerprint) throw new InvalidOperationException("Unified native readback differs from the preview. The transaction was not committed.");
                    if (exclusive.IsCancellationRequested || !transaction.CanCommit) throw new InvalidOperationException("Unified transaction cancelled or cannot commit.");
                    transaction.CommitOnDispose();
                }
            }
        }

        public object Attach(int pid, string expectedProjectPath)
        {
            if (string.IsNullOrWhiteSpace(expectedProjectPath) || !Path.IsPathRooted(expectedProjectPath))
                throw new ArgumentException("Supply the absolute project path returned by tia_list_processes.");
            string expected = Path.GetFullPath(expectedProjectPath);
            safeguards().RequireProject(expected);
            var selected = TiaPortal.GetProcesses().SingleOrDefault(p => p.Id == pid);
            if (selected == null || selected.ProjectPath == null || !SamePath(selected.ProjectPath.FullName, expected))
                throw new InvalidOperationException("Process ID and expected project path do not match a running project.");
            Dispose();
            try
            {
                portal = selected.Attach();
                if (portal.Projects.Count != 1) throw new InvalidOperationException("Exactly one open project is required.");
                project = portal.Projects[0];
                if (project.Path == null || !SamePath(project.Path.FullName, expected))
                    throw new InvalidOperationException("The project changed during attach.");
                projectPath = expected; processId = pid;
                hmis = HmiCatalog.Discover(project, includeUnified: true);
                ResetWorkflows();
                return Status();
            }
            catch { Dispose(); throw; }
        }

        public object Status()
        {
            if (project != null) CheckProject();
            return new { attached = project != null, processId, projectPath, projectModified = project?.IsModified,
                plcTargets = project == null ? Array.Empty<object>() : SoftwareCatalog.Discover(project).Where(s => s.Family == "plc")
                    .Select(s => (object)new { plcId = s.Id, label = s.Label, name = s.Software.Name }).ToArray(),
                hmis = hmis.Select(h => new { hmiId = h.Id, label = h.Label, name = h.Software.Name, family = h.Family,
                    capabilities = new { textListEditing = h.Family == "classic" || UnifiedTextListNative.Supports(h.Software.GetType()),
                        engineeringObjectTools = true,
                        unifiedScreenEditing = h.Family == "unified" && UnifiedNative.CanInspect(h.Software) } }).ToArray() };
        }

        public object ListTextLists(string hmiId)
        {
            var reference = FindReference(hmiId);
            if (reference.Family == "unified") return new { hmiId, family = "unified", textLists = UnifiedTextListNative.Names(reference.Software) };
            dynamic target = FindClassicHmi(hmiId);
            var names = new List<string>();
            foreach (dynamic list in target.TextLists) names.Add((string)list.Name);
            return new { hmiId, textLists = names.OrderBy(n => n, StringComparer.Ordinal).ToArray() };
        }

        public object Read(string hmiId, string listName)
        {
            var document = TextListDocuments.Parse(ReadTextList(hmiId, listName));
            return new { hmiId, listName, fingerprint = document.Fingerprint, values = document.Read(), nativeImportScope = document.NativeImportScope,
                structure = (document as TextListDocument)?.Structure(), activeProjectCultures = project!.LanguageSettings.ActiveLanguages.Select(l => l.Culture.Name).ToArray() };
        }

        public object Preview(string hmiId, string listName, string expectedFingerprint, TextEdit[] edits)
        {
            CheckProject();
            return workflow!.Preview(hmiId, listName, expectedFingerprint, edits);
        }

        public object Apply(string planId, string candidateSha256)
        {
            CheckProject();
            return workflow!.Apply(planId, candidateSha256);
        }

        public object PreviewClassicTextStructure(string hmiId, string listName, string expectedFingerprint, ClassicTextListChange[] changes)
        {
            FindClassicHmi(hmiId);
            TextListDocument.RequireNativeStructureSupport(typeof(Project).Assembly.GetName().Version, changes);
            return workflow!.PreviewStructure(hmiId, listName, expectedFingerprint, changes, project!.LanguageSettings.ActiveLanguages.Select(l => l.Culture.Name).ToArray());
        }

        public string ReadTextList(string hmiId, string listName)
        {
            var reference = FindReference(hmiId);
            if (reference.Family == "unified") return UnifiedTextListNative.Export(reference.Software, listName, artifactRoot);
            dynamic list = FindTextList(hmiId, listName);
            string temp = Path.Combine(artifactRoot, "exports", Guid.NewGuid().ToString("N") + ".xml");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            try
            {
                list.Export(new FileInfo(temp), ExportOptions.WithDefaults);
                return File.ReadAllText(temp, Encoding.UTF8);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public void ImportTextList(string hmiId, string listName, string expectedFingerprint, string candidateXml)
        {
            if (FindReference(hmiId).Family == "unified")
            {
                ImportUnifiedTextList(hmiId, listName, expectedFingerprint, candidateXml);
                return;
            }
            var target = FindClassicHmi(hmiId);
            var candidate = new TextListDocument(candidateXml);
            if (candidate.Name != listName) throw new InvalidOperationException("Candidate text-list name does not match the target.");
            string temp = Path.Combine(artifactRoot, "imports", Guid.NewGuid().ToString("N") + ".xml");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            File.WriteAllText(temp, candidateXml, new UTF8Encoding(false));
            try
            {
                using (var exclusive = portal!.ExclusiveAccess("Codex HMI text-list edit"))
                {
                    CheckProject();
                    TextListWorkflow.RequireFingerprint(new TextListDocument(ReadTextList(hmiId, listName)), expectedFingerprint);
                    using (var transaction = exclusive.Transaction(project!, "Update HMI text list " + listName))
                    {
                        dynamic native = target;
                        native.TextLists.Import(new FileInfo(temp), ImportOptions.Override);
                        var readback = new TextListDocument(ReadTextList(hmiId, listName));
                        if (readback.Fingerprint != candidate.Fingerprint)
                            throw new InvalidOperationException("Native import readback differs from the preview. The transaction was not committed.");
                        if (exclusive.IsCancellationRequested || !transaction.CanCommit)
                            throw new InvalidOperationException("Native transaction was cancelled or cannot commit.");
                        transaction.CommitOnDispose();
                    }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private void ImportUnifiedTextList(string hmiId, string listName, string expectedFingerprint, string snapshot)
        {
            var candidate = new UnifiedTextListDocument(snapshot);
            if (candidate.Name != listName) throw new InvalidOperationException("Candidate Unified list does not match the target.");
            using (var exclusive = portal!.ExclusiveAccess("Codex Unified text-list edit"))
            {
                CheckProject();
                object software = FindUnifiedSoftware(hmiId);
                var before = new UnifiedTextListDocument(ReadTextList(hmiId, listName));
                TextListWorkflow.RequireFingerprint(before, expectedFingerprint);
                if (before.Bundle.Configuration != candidate.Bundle.Configuration) throw new InvalidOperationException("Unified list configuration cannot be changed by this text-only workflow.");
                using (var transaction = exclusive.Transaction(project!, "Edit Unified text-list translations"))
                {
                    UnifiedTextListNative.Import(software, candidate, artifactRoot);
                    var readback = new UnifiedTextListDocument(ReadTextList(hmiId, listName));
                    if (readback.Fingerprint != candidate.Fingerprint) throw new InvalidOperationException("Unified paired-YAML native readback differs from the preview. The transaction was not committed.");
                    if (exclusive.IsCancellationRequested || !transaction.CanCommit) throw new InvalidOperationException("Unified transaction cancelled or cannot commit.");
                    transaction.CommitOnDispose();
                }
            }
        }

        public object Compile(string hmiId)
        {
            CheckProject();
            using (var exclusive = portal!.ExclusiveAccess("Codex HMI compile"))
            {
                var reference = FindReference(hmiId);
                var selection = CompileServiceResolver.Resolve(reference.Software, reference.Id, reference.Family == "unified",
                    CompileService, () => reference.CompileOwners);
                if (selection.OwnerFallback)
                {
                    // A native device compiler can include sibling PLC software. Keep
                    // the PLC offline policy even when compilation starts at an HMI.
                    foreach (var plc in SoftwareCatalog.Discover(project!).Where(s => s.Family == "plc"
                        && (s.Id == selection.Scope.Path || s.Id.StartsWith(selection.Scope.Path + "/", StringComparison.Ordinal))))
                        PlcPolicy.RequireOffline(OnlineStateOf(plc));
                }
                return CompileSelected(hmiId, selection);
            }
        }
        private static ICompilable CompileService(object target) => (target as IEngineeringServiceProvider)?.GetService<ICompilable>();
        private static object CompileSoftware(string targetId, Siemens.Engineering.HW.Software target)
        {
            return CompileSelected(targetId, CompileServiceResolver.Resolve(target, targetId, false, CompileService, () => Array.Empty<CompileScope>()));
        }
        private static object CompileSelected(string targetId, CompileSelection<ICompilable> selected)
        {
            CompilerResult result = selected.Service.Compile();
            var messages = new List<object>();
            int count = 0;
            AddMessages(result.Messages, messages, ref count);
            return new { targetId, state = result.State.ToString(), errors = result.ErrorCount, warnings = result.WarningCount,
                messages, messagesTruncated = count > messages.Count, totalMessages = count, projectSaved = false,
                compileScope = new { kind = selected.Scope.Kind, path = selected.Scope.Path, nativeType = selected.Scope.Target.GetType().FullName,
                    ownerFallback = selected.OwnerFallback, includesOwningScope = selected.OwnerFallback } };
        }

        private static void AddMessages(IEnumerable<CompilerResultMessage> source, List<object> destination, ref int count)
        {
            foreach (var message in source)
            {
                count++;
                if (destination.Count < 5000) destination.Add(new { state = message.State.ToString(), message.Description, message.Path, message.ErrorCount, message.WarningCount });
                AddMessages(message.Messages, destination, ref count);
            }
        }

        public object Save(string expectedProjectPath)
        {
            CheckProject();
            if (!SamePath(expectedProjectPath, projectPath!)) throw new InvalidOperationException("Expected project path does not match the attached project.");
            using (var exclusive = portal!.ExclusiveAccess("Codex save project"))
            {
                CheckProject();
                project!.Save();
            }
            return new { projectPath, projectSaved = true };
        }

        public object Archive(string expectedProjectPath)
        {
            CheckProject();
            RequireExpectedPath(expectedProjectPath);
            string directory = Path.Combine(artifactRoot, "project-archives", Guid.NewGuid().ToString("N"));
            string fileName = "project-backup.z" + Path.GetExtension(projectPath!).Substring(1);
            using (var exclusive = portal!.ExclusiveAccess("Codex archive saved project"))
            {
                CheckProject();
                if (project!.IsModified) throw new InvalidOperationException("Project has unsaved changes. Save within the user's authorization before archiving.");
                Directory.CreateDirectory(directory);
                // V15.1 limits the native archive path to 143 characters. Stage in
                // a short temporary directory, then retain it under the artifact root.
                string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string staging = Path.GetFullPath(Path.Combine(tempRoot, "tia-archive-" + Guid.NewGuid().ToString("N")));
                if (!staging.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid archive staging path.");
                if (Path.Combine(staging, fileName).Length > 143) throw new IOException("TIA V15.1 needs an archive path no longer than 143 characters. Configure a shorter Windows TEMP directory.");
                Directory.CreateDirectory(staging);
                try
                {
                    project.Archive(new DirectoryInfo(staging), fileName, ProjectArchivationMode.Compressed);
                    File.Move(Path.Combine(staging, fileName), Path.Combine(directory, fileName));
                }
                finally { Directory.Delete(staging, true); }
            }
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) throw new IOException("Siemens did not create the expected project archive: " + path);
            string hash;
            using (var stream = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return new { projectPath, archivePath = path, sha256 = hash, bytes = new FileInfo(path).Length, projectSaved = false,
                scope = "Native compressed archive of the saved project; no save or project-location change." };
        }

        public object Reopen(string expectedProjectPath)
        {
            CheckProject();
            RequireExpectedPath(expectedProjectPath);
            string expected = projectPath!;
            using (var exclusive = portal!.ExclusiveAccess("Codex reopen saved project"))
            {
                CheckProject();
                if (project!.IsModified) throw new InvalidOperationException("Project has unsaved changes. Reopen refuses to discard them; save within the user's authorization first.");
                if (!File.Exists(expected)) throw new FileNotFoundException("The saved project file is missing.", expected);
                try
                {
                    // Invalidate every preview before closing, even if reopening fails.
                    workflow = null; unifiedWorkflow = null; engineeringWorkflow = null; classicXmlWorkflow = null;
                    project.Close();
                    project = null;
                    if (portal.Projects.Count != 0) throw new InvalidOperationException("Another project is open; the saved project was not reopened.");
                    project = portal.Projects.Open(new FileInfo(expected));
                    CheckProject();
                    hmis = HmiCatalog.Discover(project, includeUnified: true);
                    ResetWorkflows();
                }
                catch
                {
                    // Keep the TIA process alive. Reattach explicitly after inspecting its state.
                    project = null; projectPath = null; processId = null; hmis = new List<HmiReference>();
                    throw;
                }
            }
            return new { projectReopened = true, status = Status() };
        }

        private void RequireExpectedPath(string expectedProjectPath)
        {
            if (string.IsNullOrWhiteSpace(expectedProjectPath) || !Path.IsPathRooted(expectedProjectPath) || !SamePath(expectedProjectPath, projectPath!))
                throw new InvalidOperationException("Expected absolute project path does not match the attached project.");
        }

        private void ResetWorkflows()
        {
            string sessionRoot = Path.Combine(artifactRoot, Guid.NewGuid().ToString("N"));
            workflow = new TextListWorkflow(this, Path.Combine(sessionRoot, "classic"), features, safeguards);
            unifiedWorkflow = new UnifiedEditWorkflow(this, Path.Combine(sessionRoot, "unified"), features, safeguards);
            engineeringWorkflow = new EngineeringWorkflow(this, Path.Combine(sessionRoot, "engineering"), safeguards);
            classicXmlWorkflow = new ClassicXmlWorkflow(this, Path.Combine(sessionRoot, "comfort-xml"), features, safeguards);
            projectTranslationWorkflow = new ProjectTranslationWorkflow(this, Path.Combine(sessionRoot, "project-texts"), features, safeguards);
            extensionPlans.Clear();
        }

        public void InvalidatePreviews() { if (project != null) ResetWorkflows(); else extensionPlans.Clear(); }

        private HmiReference FindReference(string hmiId)
        {
            CheckProject();
            // Rediscover to avoid trusting a cached device reference after a project edit.
            hmis = HmiCatalog.Discover(project!, includeUnified: true);
            var reference = hmis.SingleOrDefault(h => h.Id == hmiId);
            if (reference == null) throw new InvalidOperationException("HMI ID is not present in the attached project.");
            return reference;
        }

        private HmiTarget FindClassicHmi(string hmiId)
        {
            var reference = FindReference(hmiId);
            if (reference.Family != "classic") throw new NotSupportedException("This is a Unified HMI. The installed API has no supported classic XML text-list route. Use API capabilities and the Unified tools.");
            return reference.Target;
        }

        private object FindUnifiedSoftware(string hmiId)
        {
            var reference = FindReference(hmiId);
            if (reference.Family != "unified") throw new NotSupportedException("This operation requires a Unified HMI.");
            return reference.Software;
        }

        private object FindTextList(string hmiId, string listName)
        {
            dynamic target = FindClassicHmi(hmiId);
            object? match = null;
            foreach (dynamic list in target.TextLists)
                if (string.Equals((string)list.Name, listName, StringComparison.Ordinal))
                {
                    if (match != null) throw new InvalidOperationException("Ambiguous text-list name.");
                    match = list;
                }
            return match ?? throw new InvalidOperationException("Text list does not exist: " + listName);
        }

        private void CheckProject()
        {
            if (projectPath != null) safeguards().RequireProject(projectPath);
            if (portal == null || project == null || projectPath == null) throw new InvalidOperationException("Attach to a project first.");
            if (portal.Projects.Count != 1 || !portal.Projects[0].Equals(project) || project.Path == null || !SamePath(project.Path.FullName, projectPath))
                throw new InvalidOperationException("The attached project changed. Reattach before continuing.");
        }
        private static bool SamePath(string left, string right) { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }

        public void Dispose()
        {
            workflow = null; unifiedWorkflow = null; engineeringWorkflow = null; classicXmlWorkflow = null; hmis = new List<HmiReference>(); project = null; projectPath = null; processId = null;
            if (portal != null) { portal.Dispose(); portal = null; }
        }
    }
}
