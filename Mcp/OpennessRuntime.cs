using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Mcp
{
    internal interface IHmiSession : IDisposable
    {
        void InvalidatePreviews();
        object PreviewExtension(string module, string operation, string targetId, string path, string fingerprint, JsonElement arguments);
        object ApplyExtension(string id, string hash);
        object ReadPlcExtension(string operation, string kind, JsonElement arguments);
        object ReadProjectExtension(string operation, string kind, JsonElement arguments);
        object ListProcesses();
        object ApiCapabilities();
        object ListScreens(string hmiId);
        object ListScreenItems(string hmiId, string screenPath);
        object ReadUnifiedObject(string hmiId, string screenPath, string itemPath);
        object PreviewUnified(string hmiId, string screenPath, string itemPath, string expectedFingerprint, UnifiedEdit[] edits);
        object ApplyUnified(string planId, string candidateSha256);
        object ReadEngineeringObject(string hmiId, string path);
        object ExportEngineeringObject(string hmiId, string path);
        object PreviewEngineering(string action, string hmiId, string path, string fingerprint, EngineeringEdit[]? edits, string nativeType, CreationArgument[]? arguments, string sourceDirectory, bool overwrite);
        object ApplyEngineeringPlan(string planId, string candidateSha256);
        object ReadClassicXml(string hmiId, string path);
        object PreviewClassicXml(string hmiId, string path, string fingerprint, ClassicXmlEdit[] edits);
        object PreviewClassicBehaviors(string hmiId, string path, string fingerprint, ClassicBehaviorChange[] changes);
        object ApplyClassicXml(string planId, string candidateSha256);
        object ListPlcs();
        object ReadPlcObject(string plcId, string path);
        object ReadPlcResource(string plcId, string path);
        object ExportPlcObjects(string plcId, string path);
        object PreviewPlc(string action, string plcId, string path, string fingerprint, EngineeringEdit[]? edits, string nativeType, CreationArgument[]? arguments, string sourceDirectory, bool overwrite);
        object PreviewPlcXmlImport(string plcId, string path, string fingerprint, string candidateXml, bool overwrite);
        object PreviewPlcSource(string plcId, string fingerprint, string name, string language, string code, bool allowBlockReplacement);
        object CompilePlc(string plcId);
        object Attach(int processId, string expectedProjectPath);
        object Status();
        object ListTextLists(string hmiId);
        object Read(string hmiId, string listName);
        object Preview(string hmiId, string listName, string expectedFingerprint, TextEdit[] edits);
        object PreviewClassicTextStructure(string hmiId, string listName, string expectedFingerprint, ClassicTextListChange[] changes);
        object Apply(string planId, string candidateSha256);
        object Compile(string hmiId);
        object Save(string expectedProjectPath);
        object Archive(string expectedProjectPath);
        object Reopen(string expectedProjectPath);
    }

    internal sealed class OpennessRuntime : IDisposable
    {
        private readonly RequestLane native, local, control;
        private readonly string artifactRoot;
        private readonly bool allowWrites;
        private readonly SafeguardStore safeguards;
        private readonly SafeguardSettings startupSettings;
        private IHmiSession? session;
        private string? requestedVersion;
        private int disposed;
        private readonly ExtensionManager extensions;
        private readonly Func<string, IFeatureEngine, IHmiSession>? sessionFactory;

        public OpennessRuntime(string? version, string artifactRoot, bool allowWrites, string? extensionsRoot = null, int analysisParallelism = 2,
            Func<string, IFeatureEngine, IHmiSession>? sessionFactory = null, SafeguardStore? safeguards = null)
        {
            requestedVersion = version; this.artifactRoot = Path.GetFullPath(artifactRoot); this.allowWrites = allowWrites;
            this.sessionFactory = sessionFactory;
            this.safeguards = safeguards ?? new SafeguardStore();
            startupSettings = this.safeguards.Current();
            extensions = new ExtensionManager(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extensions"),
                extensionsRoot ?? ExtensionPaths.DefaultRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                Path.Combine(this.artifactRoot, "extension-workers"), factory: b => new ExtensionWorker(b, startupSettings.WorkerTimeoutSeconds * 1000), localParallelism: analysisParallelism);
            local = new RequestLane("Local analysis", analysisParallelism, startupSettings.AnalysisQueueCapacity);
            control = new RequestLane("Request control", 1, startupSettings.ControlQueueCapacity);
            native = new RequestLane("TIA Openness STA", 1, startupSettings.NativeQueueCapacity, sta: true, cleanup: () => session?.Dispose());
        }

        public Task<object> ListExtensions(CancellationToken token) => control.Enqueue(() => extensions.List(new {
            native = native.Snapshot(), local = local.Snapshot(), control = control.Snapshot(),
            scope = "Native calls/transactions are FIFO on one STA. Pure local analysis uses separate bounded workers. Queued cancellation removes work before dispatch; running operations finish without thread abort. Reload drains active generation leases." }), token, nameof(ListExtensions));
        public Task<object> ReloadExtensions(string module, CancellationToken token) => Enqueue(() => { safeguards.Current().Require(GuardedOperation.ExtensionReload, mcp: true); return extensions.Reload(module, () => session?.InvalidatePreviews()); }, token);
        private static JsonElement Arguments(string json)
        {
            if (json == null || json.Length > 16 * 1024 * 1024) throw new ArgumentException("Supply a bounded JSON arguments object.");
            using (var doc = JsonDocument.Parse(json)) { if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Extension arguments must be a JSON object."); return doc.RootElement.Clone(); }
        }
        public async Task<object> ReadExtension(string module, string operation, string argumentsJson, CancellationToken token)
        {
            ExtensionBundle.ModuleName(module);
            if (operation == null || operation.Length == 0 || operation.Length > 128 || operation.Any(char.IsControl)) throw new ArgumentException("Use an advertised bounded extension operation name.");
            if (argumentsJson == null || argumentsJson.Length > 16 * 1024 * 1024) throw new ArgumentException("Supply a bounded JSON arguments object.");
            var route = (string)await control.Enqueue(() => {
                var descriptor = extensions.Operation(module, operation);
                if (descriptor.Preview) throw new ArgumentException("Use a preview for this extension operation.");
                return descriptor.Route;
            }, token, "Route extension read").ConfigureAwait(false);
            if (route == "local") return await local.Enqueue(() => extensions.ExecuteLocal(module, operation, Arguments(argumentsJson)), token, module + "/" + operation).ConfigureAwait(false);
            return await Enqueue(() => {
                // Reload can happen after classification. Revalidate before native capture.
                var descriptor = extensions.Operation(module, operation);
                if (descriptor.Route != route || descriptor.Preview) throw new InvalidOperationException("Extension route changed; inspect its current schema before retrying.");
                var arguments = Arguments(argumentsJson);
                object data;
                if (route == "plcRead" && module == "plc") data = EnsureSession().ReadPlcExtension(operation, descriptor.SnapshotKind, arguments);
                else if (route == "projectRead" && module == "project") data = EnsureSession().ReadProjectExtension(operation, descriptor.SnapshotKind, arguments);
                else throw new ArgumentException("This extension operation has no read route.");
                return (object)new { extension = extensions.Identity(module), data };
            }, token, module + "/" + operation).ConfigureAwait(false);
        }
        public Task<object> PreviewExtension(string module, string operation, string targetId, string path, string fingerprint, string argumentsJson, CancellationToken token)
            => Enqueue(() => EnsureSession().PreviewExtension(module, operation, targetId, path, fingerprint, Arguments(argumentsJson)), token);

        public Task<object> Versions(CancellationToken token) => Enqueue(() => (object)new {
            referencedApi = TiaPortalAssemblyResolver.ReferencedApiVersion,
            compatibleEngineeringVersions = TiaPortalAssemblyResolver.GetCompatibleEngineeringVersions(),
            selectedEngineeringVersion = TiaPortalAssemblyResolver.SelectedEngineeringVersion ?? requestedVersion,
            writesEnabled = allowWrites && safeguards.Current().McpWritesEnabled, artifactRoot,
            safeguardsFile = safeguards.FilePath, safeguards = safeguards.Current(),
            startupRequestLimits = new { startupSettings.NativeQueueCapacity, startupSettings.AnalysisQueueCapacity, startupSettings.ControlQueueCapacity, startupSettings.WorkerTimeoutSeconds },
            launchWritePermission = allowWrites
        }, token);

        public Task<object> PrepareClassicBundle(string sourceDirectory, string[] categories, ClassicFolderName[] folderNames, CancellationToken token, bool repairXmlDeclaration = false, string targetVersion = "V15.1", string[]? selectedTextLists = null)
            => local.Enqueue(() => {
                string destination = Path.Combine(artifactRoot, "classic-export-bundles", Guid.NewGuid().ToString("N"));
                if (categories != null && categories.Length == 1 && categories[0] == "TextLists") {
                    if (folderNames == null || folderNames.Length != 0) throw new ArgumentException("TextLists have no native folder-name overrides.");
                    return TextListBundlePreparation.Prepare(sourceDirectory, destination, repairXmlDeclaration, targetVersion, selectedTextLists);
                }
                if (repairXmlDeclaration || selectedTextLists != null) throw new ArgumentException("Declaration repair and selectedTextLists require categories=['TextLists'].");
                return ClassicExportBundle.Prepare(sourceDirectory, destination, categories, folderNames);
            }, token, nameof(PrepareClassicBundle));

        public Task<object> SelectVersion(string version, CancellationToken token) => Enqueue(() => {
            string selected = Normalize(version);
            if (!TiaPortalAssemblyResolver.GetCompatibleEngineeringVersions().Contains(selected, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("This build is not compatible with that installed TIA version. Use tia_list_versions.");
            if (session != null && !string.Equals(selected, TiaPortalAssemblyResolver.SelectedEngineeringVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Restart the MCP server before switching engineering versions after loading Siemens assemblies.");
            requestedVersion = selected;
            return (object)new { selectedEngineeringVersion = selected };
        }, token);

        public Task<object> Invoke(Func<IHmiSession, object> operation, CancellationToken token, bool writes = false, [CallerMemberName] string operationName = "") => Enqueue(() => {
            if (writes) {
                if (!allowWrites) throw new InvalidOperationException("Writes are disabled. Start this server with --allow-writes to apply, compile or save.");
                GuardedOperation kind = operationName == "Compile" || operationName == "CompilePlc" ? GuardedOperation.Compile
                    : operationName == "Save" ? GuardedOperation.Save : operationName == "Archive" ? GuardedOperation.Archive
                    : operationName == "Reopen" ? GuardedOperation.Reopen : GuardedOperation.Apply;
                safeguards.Current().Require(kind, mcp: true);
            }
            return operation(EnsureSession());
        }, token, operationName);

        private IHmiSession EnsureSession()
        {
            if (session != null) return session;
            // Internal provider seam for the separate adapter acceptance harness.
            // Production startup never supplies a factory or exposes a test option.
            if (sessionFactory != null) return session = sessionFactory(artifactRoot, extensions);
            var versions = TiaPortalAssemblyResolver.GetCompatibleEngineeringVersions();
            string? selected = string.IsNullOrWhiteSpace(requestedVersion) ? (versions.Count == 1 ? versions[0] : null) : Normalize(requestedVersion!);
            if (selected == null) throw new InvalidOperationException("Select a compatible engineering version with tia_select_version first.");
            TiaPortalAssemblyResolver.Initialize(selected);
            session = CreateSession(artifactRoot, extensions, safeguards.Current);
            return session;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IHmiSession CreateSession(string root, IFeatureEngine features, Func<SafeguardSettings> settings) => new HmiSession(root, features, settings);

        private Task<object> Enqueue(Func<object> operation, CancellationToken token, [CallerMemberName] string operationName = "")
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(OpennessRuntime));
            // Materialize deferred native enumerations on the owning thread. Only
            // immutable JSON crosses the native queue's response boundary.
            return native.EnqueueSnapshot(operation, token, operationName);
        }

        private static string Normalize(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Engineering version is required.");
            string value = version.Trim();
            return value.StartsWith("V", StringComparison.OrdinalIgnoreCase) ? "V" + value.Substring(1) : "V" + value;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            control.BeginStop(); local.BeginStop(); native.BeginStop();
            // Stop queued work; let dispatched native operations finish, and release
            // their session on its owning STA before disposing pure workers.
            try { control.Dispose(); local.Dispose(); native.Dispose(); }
            finally { extensions.Dispose(); }
        }
    }
}
