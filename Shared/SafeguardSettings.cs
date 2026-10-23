using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SiemensMCPServer
{
    internal enum GuardedOperation { Apply, Compile, Save, Archive, Reopen, Delete, Overwrite, SourceReplacement, ProjectTextImport, ExtensionReload }

    // One policy model for the desktop and the MCP host. No native handles or project data.
    internal sealed class SafeguardSettings
    {
        [Browsable(false)] public int SchemaVersion { get; set; } = 1;
        [Category("1. Write permissions"), DisplayName("Desktop writes"), Description("Allow desktop imports and explicit Save. Disabling this does not prevent export or browsing.")]
        public bool DesktopWritesEnabled { get; set; } = true;
        [Category("1. Write permissions"), DisplayName("MCP writes"), Description("Allow MCP engineering writes only when the host was also launched with --allow-writes. Client and Codex permissions still apply independently.")]
        public bool McpWritesEnabled { get; set; } = true;
        [Category("1. Write permissions"), DisplayName("Apply edits and imports")]
        public bool AllowApply { get; set; } = true;
        [Category("1. Write permissions"), DisplayName("Compile")]
        public bool AllowCompile { get; set; } = true;
        [Category("1. Write permissions"), DisplayName("Save project")]
        public bool AllowSave { get; set; } = true;
        [Category("1. Write permissions"), DisplayName("Archive project")]
        public bool AllowArchive { get; set; } = true;
        [Category("1. Write permissions"), DisplayName("Reopen project")]
        public bool AllowReopen { get; set; } = true;
        [Category("2. Restricted changes"), DisplayName("Native object Delete"), Description("Permission for the native engineering Delete action. Does not classify removals inside XML, source code or imported resources; disable Apply to block those changes too.")]
        public bool AllowDelete { get; set; } = true;
        [Category("2. Restricted changes"), DisplayName("Overwrite existing objects"), Description("Permission ceiling; each import must still explicitly select overwrite. This does not select overwrite automatically.")]
        public bool AllowOverwrite { get; set; } = true;
        [Category("2. Restricted changes"), DisplayName("PLC source block replacement")]
        public bool AllowSourceReplacement { get; set; } = true;
        [Category("2. Restricted changes"), DisplayName("Project translation import")]
        public bool AllowProjectTextImport { get; set; } = true;
        [Category("2. Restricted changes"), DisplayName("Reload feature extensions")]
        public bool AllowExtensionReload { get; set; } = true;
        [Category("3. Desktop confirmations"), DisplayName("Confirm imports"), Description("Show the concrete import review before applying. Disabling this skips only the dialog; baseline, candidate and native checks still run where the selected workflow provides them.")]
        public bool ConfirmDesktopImports { get; set; } = true;
        [Category("3. Desktop confirmations"), DisplayName("Confirm Save")]
        public bool ConfirmDesktopSave { get; set; } = true;
        [Category("4. Preview limits"), DisplayName("Preview lifetime (minutes)"), Description("1..1440. Applies to new previews. Shortening the policy also expires older previews sooner; increasing it never extends an existing preview.")]
        public int PreviewLifetimeMinutes { get; set; } = 30;
        [Category("4. Preview limits"), DisplayName("Pending previews per workflow"), Description("1..1000. Existing previews are retained when this is lowered; new previews are blocked until below the limit.")]
        public int MaxPendingPreviews { get; set; } = 100;
        [Category("5. Request limits (host restart)"), DisplayName("Parallel local analysis jobs"), Description("1..8. Native operations always use a single STA lane. Restart applies changes; an explicit --analysis-parallelism launch argument overrides this value.")]
        public int AnalysisParallelism { get; set; } = 2;
        [Category("5. Request limits (host restart)"), DisplayName("Maximum connected MCP chats"), Description("1..16. Bounds lightweight frontend processes. New connections read this limit; lowering it never terminates an existing chat. The application service must be started explicitly.")]
        public int MaxMcpFrontends { get; set; } = 4;
        [Category("5. Request limits (host restart)"), DisplayName("Maximum owned software resources"), Description("1..256. Default 32 non-retired resource claims per Siemens app across supervisor generations. Includes private workers and user-owned TIA attachments. Retained or uncertain claims require reviewed recovery; increasing this does not clear them.")]
        public int MaxOwnedResources { get; set; } = 32;
        [Category("5. Request limits (host restart)"), DisplayName("Maximum processes per owned tree"), Description("1..256, default 64. Windows Job limit includes each supervisor and its descendants. Applies to new process trees; lowering it never terminates existing ones.")]
        public int MaxProcessTreeSize { get; set; } = 64;
        [Category("5. Request limits (host restart)"), DisplayName("Owned process Job memory (MiB)"), Description("64..16384, default 1024. Bounds total committed memory of a new direct native host and inherited pure workers; never includes an externally opened TIA process.")]
        public int MaxOwnedJobMemoryMiB { get; set; } = 1024;
        [Category("5. Request limits (host restart)"), DisplayName("MCP stdio EOF drain seconds"), Description("1..600, default 15. Grace for an owned child to finish after client EOF. A timeout closes the supervisor's private tree and retains uncertainty; it never proves a native write rolled back.")]
        public int StdioDrainTimeoutSeconds { get; set; } = 15;
        [Category("5. Request limits (host restart)"), DisplayName("Queued native requests"), Description("1..1000. Overflow is rejected before dispatch.")]
        public int NativeQueueCapacity { get; set; } = 100;
        [Category("5. Request limits (host restart)"), DisplayName("Queued analysis requests"), Description("1..1000.")]
        public int AnalysisQueueCapacity { get; set; } = 32;
        [Category("5. Request limits (host restart)"), DisplayName("Queued control requests"), Description("1..1000.")]
        public int ControlQueueCapacity { get; set; } = 16;
        [Category("5. Request limits (host restart)"), DisplayName("Worker response timeout (seconds)"), Description("1..600. Applies to pure feature workers. Native Siemens operations are never aborted on a timeout.")]
        public int WorkerTimeoutSeconds { get; set; } = 30;
        [Category("6. Project scope"), DisplayName("Required project path"), Description("Optional exact absolute project path. Empty permits any explicitly selected project. Applies to both the desktop and MCP, including already attached sessions.")]
        public string RequiredProjectPath { get; set; } = "";
        [Category("7. Evidence locations"), DisplayName("Desktop import evidence folder"), Description("Empty uses the per-user DesktopTransfers folder. Never changes the original source files.")]
        public string DesktopEvidenceRoot { get; set; } = "";
        [Category("7. Evidence locations"), DisplayName("MCP artifact folder (host restart)"), Description("Empty uses the per-user MCP Sessions folder. An explicit --artifact-root takes precedence. Restart applies the location.")]
        public string McpArtifactRoot { get; set; } = "";

        [JsonIgnore, Category("8. Always enforced"), DisplayName("Target and candidate identity")]
        public string IdentityChecks => "Exact target, baseline fingerprints and candidate hashes";
        [JsonIgnore, Category("8. Always enforced"), DisplayName("Native transaction checks")]
        public string NativeChecks => "Exclusive access, transaction/readback guards and cancellation checks on reviewed routes";
        [JsonIgnore, Category("8. Always enforced"), DisplayName("Recovery and plan ownership")]
        public string RecoveryChecks => "Single-attempt plans, retained evidence and generation binding; no automatic uncertain-write retry";
        [JsonIgnore, Category("8. Always enforced"), DisplayName("PLC and API restrictions")]
        public string NativeScope => "PLC Offline checks, supported API/type limits, protected standard cycles; no online control/download";
        [JsonIgnore, Category("8. Always enforced"), DisplayName("File and parser protections")]
        public string FileChecks => "Bounded files, safe paths, hash-verified packages, XML/YAML/workbook validation";
        [JsonIgnore, Category("8. Always enforced"), DisplayName("External authorization")]
        public string ExternalAuthorization => "Siemens access prompts and Codex/client permissions are controlled by their owners";

        internal void Validate()
        {
            if (SchemaVersion != 1) throw new InvalidDataException("Unsupported safeguards schema version.");
            Range(PreviewLifetimeMinutes, 1, 1440, nameof(PreviewLifetimeMinutes));
            Range(MaxPendingPreviews, 1, 1000, nameof(MaxPendingPreviews));
            Range(AnalysisParallelism, 1, 8, nameof(AnalysisParallelism));
            Range(MaxMcpFrontends, 1, 16, nameof(MaxMcpFrontends));
            Range(MaxOwnedResources, 1, 256, nameof(MaxOwnedResources));
            Range(MaxProcessTreeSize, 1, 256, nameof(MaxProcessTreeSize));
            Range(MaxOwnedJobMemoryMiB, 64, 16384, nameof(MaxOwnedJobMemoryMiB));
            Range(StdioDrainTimeoutSeconds, 1, 600, nameof(StdioDrainTimeoutSeconds));
            Range(NativeQueueCapacity, 1, 1000, nameof(NativeQueueCapacity));
            Range(AnalysisQueueCapacity, 1, 1000, nameof(AnalysisQueueCapacity));
            Range(ControlQueueCapacity, 1, 1000, nameof(ControlQueueCapacity));
            Range(WorkerTimeoutSeconds, 1, 600, nameof(WorkerTimeoutSeconds));
            ValidatePath(RequiredProjectPath); ValidatePath(DesktopEvidenceRoot); ValidatePath(McpArtifactRoot);
        }
        private static void Range(int value, int min, int max, string name)
        { if (value < min || value > max) throw new InvalidDataException(name + " must be " + min + ".." + max + "."); }
        private static void ValidatePath(string path)
        {
            if (path == null || path.Length > 2048 || path.Any(char.IsControl)) throw new InvalidDataException("Invalid safeguards path.");
            if (path.Length != 0 && (!Path.IsPathRooted(path) || path.StartsWith("\\", StringComparison.Ordinal) && !path.StartsWith("\\\\", StringComparison.Ordinal)
                || !string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Safeguards paths must be normalized absolute paths, or empty for the default.");
        }
        internal void Require(GuardedOperation operation, bool mcp = false)
        {
            Validate();
            if (operation != GuardedOperation.ExtensionReload && !(mcp ? McpWritesEnabled : DesktopWritesEnabled))
                throw new InvalidOperationException((mcp ? "MCP" : "Desktop") + " writes are disabled in Safeguards.");
            bool allowed = operation == GuardedOperation.Apply ? AllowApply : operation == GuardedOperation.Compile ? AllowCompile
                : operation == GuardedOperation.Save ? AllowSave : operation == GuardedOperation.Archive ? AllowArchive
                : operation == GuardedOperation.Reopen ? AllowReopen : operation == GuardedOperation.Delete ? AllowDelete
                : operation == GuardedOperation.Overwrite ? AllowOverwrite : operation == GuardedOperation.SourceReplacement ? AllowSourceReplacement
                : operation == GuardedOperation.ProjectTextImport ? AllowProjectTextImport : operation == GuardedOperation.ExtensionReload && AllowExtensionReload;
            if (!allowed) throw new InvalidOperationException(operation + " is disabled in Safeguards.");
        }
        internal void RequireProject(string path)
        {
            Validate();
            if (RequiredProjectPath.Length != 0 && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(RequiredProjectPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected project is outside the required Safeguards project scope.");
        }
    }

    internal sealed class SafeguardSnapshot
    {
        internal SafeguardSettings Settings = new SafeguardSettings();
        internal string Hash = "absent";
    }
    internal sealed class SafeguardStore
    {
        internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SiemensMCPServer", "safeguards.json");
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        internal string FilePath { get; }
        private volatile bool observedFile;
        internal SafeguardStore(string? path = null) { FilePath = Path.GetFullPath(path ?? DefaultPath); }
        internal SafeguardSnapshot Load()
        {
            if (!File.Exists(FilePath)) {
                if (observedFile) throw new InvalidDataException("Safeguards file disappeared. Restore it or restart after reviewing the policy.");
                return new SafeguardSnapshot();
            }
            observedFile = true;
            if (new FileInfo(FilePath).Length > 65536 || (File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Safeguards file is linked or too large.");
            byte[] bytes = File.ReadAllBytes(FilePath);
            if (bytes.Length > 65536) throw new InvalidDataException("Safeguards file is too large.");
            using (var doc = JsonDocument.Parse(bytes)) {
                if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    throw new InvalidDataException("Safeguards must contain one object with unique properties.");
            }
            var settings = JsonSerializer.Deserialize<SafeguardSettings>(bytes, Json) ?? throw new InvalidDataException("Missing safeguards settings.");
            settings.Validate(); return new SafeguardSnapshot { Settings = settings, Hash = Hash(bytes) };
        }
        internal SafeguardSettings Current() => Load().Settings;
        internal SafeguardSnapshot Save(SafeguardSettings settings, string expectedHash)
        {
            settings.Validate();
            using (var mutex = new Mutex(false, "Local\\SiemensMCPServer-Safeguards-" + Hash(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())))) {
                bool owned; try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("Another Safeguards editor is saving. Reload and try again.");
                try {
                    if (Load().Hash != expectedHash) throw new IOException("Safeguards changed in another editor. Reload before saving; your edits have not overwritten them.");
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try {
                        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, Json));
                        if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".previous"); else File.Move(temp, FilePath);
                    } finally { if (File.Exists(temp)) File.Delete(temp); }
                    observedFile = true; return Load();
                } finally { mutex.ReleaseMutex(); }
            }
        }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }

    internal sealed class PreviewPolicy
    {
        private readonly Func<SafeguardSettings> settings;
        private readonly Func<DateTime> utcNow;
        internal PreviewPolicy(Func<SafeguardSettings>? settings = null, Func<DateTime>? utcNow = null)
        { this.settings = settings ?? (() => new SafeguardSettings()); this.utcNow = utcNow ?? (() => DateTime.UtcNow); }
        internal DateTime Now => utcNow();
        internal DateTime Expires => Now.AddMinutes(Current.PreviewLifetimeMinutes);
        internal SafeguardSettings Current { get { var value = settings(); value.Validate(); return value; } }
        internal void RequireApply() { if (!Current.AllowApply) throw new InvalidOperationException("Apply is disabled in Safeguards."); }
        internal void RequireCapacity(int count) { if (count >= Current.MaxPendingPreviews) throw new InvalidOperationException("Too many pending previews for the configured Safeguards limit. Reattach to clear them."); }
        internal bool Expired(DateTime created, DateTime expires) => Now >= expires || Now - created >= TimeSpan.FromMinutes(Current.PreviewLifetimeMinutes);
    }
}
