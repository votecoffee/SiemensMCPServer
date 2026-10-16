using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SiemensTiaOpenness.Extensions;

namespace SiemensTiaOpenness.Mcp
{
    internal sealed class ExtensionManifest
    {
        public string Format { get; set; } = "";
        public int Protocol { get; set; }
        public string Module { get; set; } = "";
        public string Version { get; set; } = "";
        public string EntryPoint { get; set; } = "";
        public TransferFile[] Files { get; set; } = Array.Empty<TransferFile>();
    }
    internal sealed class ExtensionBundle
    {
        public string Directory = "", ManifestSha256 = "";
        public ExtensionManifest Manifest = null!;
        internal static void ModuleName(string name)
        {
            if (name == null || !Regex.IsMatch(name, "^[a-z][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid extension module name.");
        }
        internal static string SafePath(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(p => p == "" || p == "." || p == ".." || p.Contains(':')))
                throw new ArgumentException("Extension paths must stay inside their root.");
            string current = Path.GetFullPath(root);
            foreach (string part in new[] { "" }.Concat(relative.Split('/')))
            {
                if (part.Length != 0) current = Path.Combine(current, part);
                if ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Linked extension paths are not supported.");
            }
            return current;
        }
        internal static ExtensionBundle Read(string directory, string module)
        {
            ModuleName(module);
            var inventory = TransferFiles.Inventory(directory);
            string json = File.ReadAllText(Path.Combine(directory, "extension.json"), Encoding.UTF8);
            if (json.Length > 1024 * 1024) throw new ArgumentException("Extension manifest is too large.");
            var manifest = JsonSerializer.Deserialize<ExtensionManifest>(json, JsonDefaults.Options) ?? throw new ArgumentException("Missing extension manifest.");
            if (manifest.Format != "tia-extension-v1" || manifest.Protocol != ExtensionWire.Protocol || manifest.Module != module
                || !Version.TryParse(manifest.Version, out var version) || version.ToString(3) != manifest.Version
                || manifest.EntryPoint != "SiemensTiaOpenness.ExtensionWorker.exe") throw new ArgumentException("Incompatible extension manifest.");
            if (manifest.Files == null || manifest.Files.Length == 0 || manifest.Files.Length > 256) throw new ArgumentException("Invalid extension file inventory.");
            var actual = inventory.Where(f => f.Path != "extension.json").ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                SafePath(directory, file.Path);
                if (!seen.Add(file.Path) || file.Path.Equals("extension.json", StringComparison.OrdinalIgnoreCase)
                    || !actual.TryGetValue(file.Path, out var found) || found.Path != file.Path || found.Bytes != file.Bytes || found.Sha256 != file.Sha256)
                    throw new InvalidOperationException("Extension payload differs from its manifest.");
                if (Path.GetFileName(file.Path).StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Extension bundles cannot contain Siemens engineering assemblies.");
            }
            if (actual.Count != seen.Count || !seen.Contains(manifest.EntryPoint)) throw new ArgumentException("Extension has missing or unlisted files.");
            return new ExtensionBundle { Directory = Path.GetFullPath(directory), Manifest = manifest, ManifestSha256 = TextListDocument.Hash(File.ReadAllBytes(Path.Combine(directory, "extension.json"))) };
        }
    }
    internal interface IExtensionWorker : IDisposable
    {
        ExtensionHello Hello { get; }
        int ProcessId { get; }
        JsonElement Call(string operation, object input);
    }
    internal sealed class ExtensionWorker : IExtensionWorker
    {
        private readonly Process process;
        private StreamWriter? input;
        private readonly int timeoutMs;
        private long nextId;
        private bool faulted;
        private bool stopped;
        private readonly StringBuilder errors = new StringBuilder();
        public ExtensionHello Hello { get; private set; } = null!;
        public int ProcessId { get; private set; }
        public ExtensionWorker(ExtensionBundle bundle, int timeoutMs = 30000)
        {
            this.timeoutMs = timeoutMs;
            process = new Process { StartInfo = new ProcessStartInfo {
                FileName = Path.Combine(bundle.Directory, bundle.Manifest.EntryPoint), Arguments = "--module=" + bundle.Manifest.Module,
                WorkingDirectory = bundle.Directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false)
            } };
            try
            {
                process.Start();
                ProcessId = process.Id;
                input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false, true)) { AutoFlush = true };
                // Drain stderr continuously with bounded retained diagnostics.
                Task.Run(() => {
                    try { int c; while ((c = process.StandardError.Read()) >= 0) lock (errors) { if (errors.Length >= 4096) errors.Remove(0, 2048); errors.Append((char)c); } }
                    catch (ObjectDisposedException) { } catch (IOException) { }
                });
                Hello = ExtensionWire.Parse<ExtensionHello>(Call("$hello", new { }));
                if (Hello.Protocol != ExtensionWire.Protocol || Hello.Module != bundle.Manifest.Module || Hello.Version != bundle.Manifest.Version
                    || Hello.Operations == null || Hello.Operations.Length == 0 || Hello.Operations.Length > 100
                    || Hello.Operations.Any(o => string.IsNullOrEmpty(o.Name) || o.Name.Length > 128 || o.InputSchema.ValueKind != JsonValueKind.Object
                        || !new[] { "local", "internal", "classicXml", "unifiedObject", "plcSource" }.Contains(o.Route))
                    || Hello.Operations.GroupBy(o => o.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
                    throw new InvalidOperationException("Extension handshake is incompatible with its manifest or host routes.");
            }
            catch { Dispose(); throw; }
        }
        public JsonElement Call(string operation, object input)
        {
            if (faulted) throw new InvalidOperationException("Extension worker failed. Reload it before proceeding.");
            long id = ++nextId;
            string packet = ExtensionWire.Serialize(new ExtensionRequest { Id = id, Operation = operation, Input = ExtensionWire.Json(input) });
            ExtensionResponse response;
            try
            {
                var io = Task.Run(() => {
                    this.input!.WriteLine(packet); this.input.Flush();
                    string line = ExtensionWire.ReadLine(process.StandardOutput) ?? throw new IOException("Extension worker ended.");
                    return JsonSerializer.Deserialize<ExtensionResponse>(line, ExtensionWire.Options) ?? throw new IOException("Missing extension response.");
                });
                io.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                if (!io.Wait(timeoutMs)) throw new TimeoutException("Extension worker exceeded its response deadline.");
                response = io.GetAwaiter().GetResult();
                if (response.Id != id) throw new InvalidDataException("Extension response ID mismatch.");
            }
            catch (Exception ex)
            {
                faulted = true; Stop(true);
                string stderr; lock (errors) stderr = errors.ToString();
                throw new InvalidOperationException("Extension worker failed; no TIA operation was dispatched by the worker. " + ex.GetBaseException().Message + " " + stderr, ex);
            }
            if (response.Error != null) throw new InvalidOperationException("Extension rejected the request: " + response.Error);
            return response.Result.Clone();
        }
        public void Dispose()
            => Stop(false);
        private void Stop(bool force)
        {
            if (stopped) return;
            stopped = true;
            faulted = true;
            // A timed-out pure worker may be blocked on stdin. Kill it before flushing/closing pipes.
            if (force) try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { input?.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
            try { if (!process.WaitForExit(2000)) { process.Kill(); process.WaitForExit(2000); } } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
    }
    // Accessed only through the same serial STA queue as all broker operations.
    internal sealed class ExtensionManager : IFeatureEngine, IDisposable
    {
        private sealed class Active : IDisposable
        {
            public ExtensionBundle Bundle = null!;
            public IExtensionWorker Worker = null!;
            public string Generation = Guid.NewGuid().ToString("N");
            public void Dispose() => Worker.Dispose();
        }
        private readonly string bundledRoot, externalRoot, cacheRoot;
        private readonly Func<ExtensionBundle, IExtensionWorker> factory;
        private readonly Dictionary<string, Active> active = new Dictionary<string, Active>(StringComparer.Ordinal);
        public ExtensionManager(string bundledRoot, string externalRoot, string cacheRoot, Func<ExtensionBundle, IExtensionWorker>? factory = null)
        { this.bundledRoot = Path.GetFullPath(bundledRoot); this.externalRoot = Path.GetFullPath(externalRoot); this.cacheRoot = Path.GetFullPath(cacheRoot); this.factory = factory ?? (b => new ExtensionWorker(b)); }
        private Dictionary<string, string> Registry(string? root = null)
        {
            root = root ?? externalRoot;
            string file = ExtensionBundle.SafePath(root, "active.json");
            if (!File.Exists(file)) return new Dictionary<string, string>(StringComparer.Ordinal);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 65536) throw new ArgumentException("Invalid extension registry.");
            var registry = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file, Encoding.UTF8), JsonDefaults.Options) ?? throw new ArgumentException("Missing extension registry.");
            if (registry.Count > 16) throw new ArgumentException("At most 16 extension modules may be configured.");
            foreach (var item in registry) { ExtensionBundle.ModuleName(item.Key); ExtensionBundle.SafePath(root, item.Value); }
            return registry;
        }
        private Active Load(string module, IDictionary<string, string> registry)
        {
            ExtensionBundle.ModuleName(module);
            var bundled = Registry(bundledRoot);
            string source = registry.TryGetValue(module, out var relative) ? ExtensionBundle.SafePath(externalRoot, relative)
                : ExtensionBundle.SafePath(bundledRoot, bundled.TryGetValue(module, out var fallback) ? fallback : module);
            var original = ExtensionBundle.Read(source, module);
            string staged = ExtensionBundle.SafePath(cacheRoot, module + "/" + Guid.NewGuid().ToString("N"));
            TransferFiles.Copy(source, staged);
            var bundle = ExtensionBundle.Read(staged, module);
            if (bundle.ManifestSha256 != original.ManifestSha256) throw new InvalidOperationException("Extension changed while staging.");
            var candidate = new Active { Bundle = bundle, Worker = factory(bundle) };
            try
            {
                File.WriteAllText(Path.Combine(staged, "activation.json"), JsonSerializer.Serialize(new { module, bundle.Manifest.Version, candidate.Generation, workerProcessId = candidate.Worker.ProcessId, bundle.ManifestSha256, sourceDirectory = source }, JsonDefaults.Options), new UTF8Encoding(false));
                return candidate;
            }
            catch { candidate.Dispose(); throw; }
        }
        private Active Get(string module)
        {
            ExtensionBundle.ModuleName(module);
            if (!active.TryGetValue(module, out var loaded)) { loaded = Load(module, Registry()); active.Add(module, loaded); }
            return loaded;
        }
        public JsonElement Execute(string module, string operation, object input)
        {
            var loaded = Get(module);
            if (!loaded.Worker.Hello.Operations.Any(o => o.Name == operation)) throw new ArgumentException("Unknown extension operation; inspect tia_list_extensions.");
            return loaded.Worker.Call(operation, input);
        }
        public ExtensionOperation Operation(string module, string name) => Get(module).Worker.Hello.Operations.SingleOrDefault(o => o.Name == name) ?? throw new ArgumentException("Unknown extension operation.");
        public object Identity(string module)
        {
            var loaded = Get(module);
            return new { module, loaded.Bundle.Manifest.Version, loaded.Generation, workerProcessId = loaded.Worker.ProcessId, loaded.Bundle.ManifestSha256 };
        }
        public string Generation(string module) => Get(module).Generation;
        public object List()
        {
            var registry = Registry();
            var modules = new[] { "comfort", "unified", "plc" }.Concat(registry.Keys).Concat(active.Keys).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            return new { hostProcessId = Process.GetCurrentProcess().Id, extensionRoot = externalRoot,
                modules = modules.Select(module => { var a = Get(module); return new { module, a.Bundle.Manifest.Version, a.Generation, workerProcessId = a.Worker.ProcessId, a.Bundle.ManifestSha256, operations = a.Worker.Hello.Operations }; }).ToArray(),
                scope = "Trusted local worker code; hashes detect changes, not publisher identity. Workers receive serialized data, never Siemens objects. Reload does not save or alter the project." };
        }
        public object Reload(string module, Action invalidatePreviews)
        {
            var registry = Registry();
            string[] modules = module == "" ? new[] { "comfort", "unified", "plc" }.Concat(registry.Keys).Concat(active.Keys).Distinct(StringComparer.Ordinal).ToArray() : new[] { module };
            var replacements = new Dictionary<string, Active>(StringComparer.Ordinal);
            try { foreach (string name in modules) replacements.Add(name, Load(name, registry)); }
            catch { foreach (var candidate in replacements.Values) candidate.Dispose(); throw; }
            // Only successful staged activation invalidates plans. Failed reload retains current workers/plans.
            try { invalidatePreviews(); } catch { foreach (var candidate in replacements.Values) candidate.Dispose(); throw; }
            var previous = new List<Active>();
            foreach (var pair in replacements) { if (active.TryGetValue(pair.Key, out var old)) previous.Add(old); active[pair.Key] = pair.Value; }
            foreach (var old in previous) old.Dispose();
            return new { reloaded = modules, hostProcessId = Process.GetCurrentProcess().Id, previewsInvalidated = true, projectChanged = false,
                modules = modules.Select(Identity).ToArray() };
        }
        public void Dispose() { foreach (var worker in active.Values) worker.Dispose(); active.Clear(); }
    }
}
