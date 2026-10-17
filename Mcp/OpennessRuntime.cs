using System;
using System.Collections.Concurrent;
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
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>(100);
        private readonly Thread thread;
        private readonly string artifactRoot;
        private readonly bool allowWrites;
        private IHmiSession? session;
        private string? requestedVersion;
        private int disposed;
        private readonly ExtensionManager extensions;

        public OpennessRuntime(string? version, string artifactRoot, bool allowWrites, string? extensionsRoot = null)
        {
            requestedVersion = version; this.artifactRoot = Path.GetFullPath(artifactRoot); this.allowWrites = allowWrites;
            extensions = new ExtensionManager(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extensions"),
                extensionsRoot ?? ExtensionPaths.DefaultRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                Path.Combine(this.artifactRoot, "extension-workers"));
            thread = new Thread(Run) { IsBackground = true, Name = "TIA Openness STA" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public Task<object> ListExtensions(CancellationToken token) => Enqueue(() => extensions.List(), token);
        public Task<object> ReloadExtensions(string module, CancellationToken token) => Enqueue(() => extensions.Reload(module, () => session?.InvalidatePreviews()), token);
        private static JsonElement Arguments(string json)
        {
            if (json == null || json.Length > 16 * 1024 * 1024) throw new ArgumentException("Supply a bounded JSON arguments object.");
            using (var doc = JsonDocument.Parse(json)) { if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Extension arguments must be a JSON object."); return doc.RootElement.Clone(); }
        }
        public Task<object> ReadExtension(string module, string operation, string argumentsJson, CancellationToken token) => Enqueue(() => {
            var descriptor = extensions.Operation(module, operation);
            if (descriptor.Preview || descriptor.Route != "local") throw new ArgumentException("Use a preview for this extension operation.");
            var data = extensions.Execute(module, operation, Arguments(argumentsJson));
            return (object)new { extension = extensions.Identity(module), data };
        }, token);
        public Task<object> PreviewExtension(string module, string operation, string targetId, string path, string fingerprint, string argumentsJson, CancellationToken token)
            => Enqueue(() => EnsureSession().PreviewExtension(module, operation, targetId, path, fingerprint, Arguments(argumentsJson)), token);

        public Task<object> Versions(CancellationToken token) => Enqueue(() => (object)new {
            referencedApi = TiaPortalAssemblyResolver.ReferencedApiVersion,
            compatibleEngineeringVersions = TiaPortalAssemblyResolver.GetCompatibleEngineeringVersions(),
            selectedEngineeringVersion = TiaPortalAssemblyResolver.SelectedEngineeringVersion ?? requestedVersion,
            writesEnabled = allowWrites, artifactRoot
        }, token);

        public Task<object> PrepareClassicBundle(string sourceDirectory, string[] categories, ClassicFolderName[] folderNames, CancellationToken token)
            => Enqueue(() => ClassicExportBundle.Prepare(sourceDirectory, Path.Combine(artifactRoot, "classic-export-bundles", Guid.NewGuid().ToString("N")), categories, folderNames), token);

        public Task<object> SelectVersion(string version, CancellationToken token) => Enqueue(() => {
            string selected = Normalize(version);
            if (!TiaPortalAssemblyResolver.GetCompatibleEngineeringVersions().Contains(selected, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("This build is not compatible with that installed TIA version. Use tia_list_versions.");
            if (session != null && !string.Equals(selected, TiaPortalAssemblyResolver.SelectedEngineeringVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Restart the MCP server before switching engineering versions after loading Siemens assemblies.");
            requestedVersion = selected;
            return (object)new { selectedEngineeringVersion = selected };
        }, token);

        public Task<object> Invoke(Func<IHmiSession, object> operation, CancellationToken token, bool writes = false) => Enqueue(() => {
            if (writes && !allowWrites) throw new InvalidOperationException("Writes are disabled. Start this server with --allow-writes to apply, compile or save.");
            return operation(EnsureSession());
        }, token);

        private IHmiSession EnsureSession()
        {
            if (session != null) return session;
            var versions = TiaPortalAssemblyResolver.GetCompatibleEngineeringVersions();
            string? selected = string.IsNullOrWhiteSpace(requestedVersion) ? (versions.Count == 1 ? versions[0] : null) : Normalize(requestedVersion!);
            if (selected == null) throw new InvalidOperationException("Select a compatible engineering version with tia_select_version first.");
            TiaPortalAssemblyResolver.Initialize(selected);
            session = CreateSession(artifactRoot, extensions);
            return session;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IHmiSession CreateSession(string root, IFeatureEngine features) => new HmiSession(root, features);

        private Task<object> Enqueue(Func<object> operation, CancellationToken token)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(OpennessRuntime));
            if (!queue.TryAdd(() => {
                try { token.ThrowIfCancellationRequested(); completion.SetResult(operation()); }
                catch (OperationCanceledException) { completion.SetCanceled(); }
                catch (Exception ex) { completion.SetException(ex); }
            })) throw new InvalidOperationException("TIA request queue is full; wait for the current operation.");
            return completion.Task;
        }

        private void Run()
        {
            try { foreach (Action action in queue.GetConsumingEnumerable()) action(); }
            finally { try { session?.Dispose(); } finally { extensions.Dispose(); } }
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
            queue.CompleteAdding();
            // Never abort a native import or dispose its session from another thread.
            thread.Join();
            queue.Dispose();
        }
    }
}
