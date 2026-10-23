using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SiemensMCPServer.Mcp
{
    // App-owned implementation of framework 0.20's language-neutral record contract.
    // The native host is the surviving supervisor of its feature workers and TIA
    // attachments. No temporary Python helper is allowed to own those records.
    internal sealed class ResourceIdentity
    {
        public string HostId { get; set; } = "";
        public int Pid { get; set; }
        public string Created { get; set; } = "";
        public string Executable { get; set; } = "";
        public bool Same(ResourceIdentity other) => other != null && HostId == other.HostId && Pid == other.Pid && Created == other.Created && Executable == other.Executable;
    }
    internal sealed class ResourceReceipt
    {
        public string RequestId { get; set; } = "";
        public string ExpectedRevision { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string Message { get; set; } = "";
    }
    internal sealed class ResourceAttestation
    {
        public string ActorOwnerId { get; set; } = "";
        public ResourceIdentity Actor { get; set; } = null!;
        public string PreviousRevision { get; set; } = ""; public string Evidence { get; set; } = "";
        public double RecordedAt { get; set; }
    }
    internal sealed class ResourceRecord
    {
        public int SchemaVersion { get; set; } = 1;
        public string ResourceId { get; set; } = "";
        public string AppId { get; set; } = "siemens-mcp-server";
        public string OwnerId { get; set; } = "";
        public ResourceIdentity Owner { get; set; } = null!;
        public string Label { get; set; } = "";
        public string Kind { get; set; } = "process";
        public string Ownership { get; set; } = "private";
        public string CleanupMode { get; set; } = "report";
        public string State { get; set; } = "launching";
        public ResourceIdentity? Process { get; set; }
        public ResourceIdentity[] Members { get; set; } = Array.Empty<ResourceIdentity>();
        public string Containment { get; set; } = "app_owned";
        public string? RecoveryBarrier { get; set; }
        public ResourceReceipt? Receipt { get; set; }
        public ResourceAttestation? Reconciliation { get; set; }
        public ResourceAttestation? BarrierResolution { get; set; }
        public double CreatedAt { get; set; }
        public double UpdatedAt { get; set; }
    }
    internal interface IResourceProcessDriver
    {
        ResourceIdentity Capture(int pid);
        string State(ResourceIdentity identity);
    }
#if NET8_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    internal sealed class WindowsResourceProcessDriver : IResourceProcessDriver
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        private readonly string host;
        public WindowsResourceProcessDriver()
        {
            using (var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")) {
                if (!Guid.TryParse(key?.GetValue("MachineGuid") as string, out var id)) throw new InvalidOperationException("Cannot verify the resource host identity.");
                host = ResourceOwnership.Hash(Encoding.UTF8.GetBytes(id.ToString("D").ToLowerInvariant()));
            }
        }
        public ResourceIdentity Capture(int pid)
        {
            IntPtr handle = OpenProcess(0x00101000, false, pid); // synchronize/query only; no authority to terminate TIA
            if (handle == IntPtr.Zero) {
                int error = Marshal.GetLastWin32Error();
                if (error == 87) throw new ArgumentException("Resource process is gone.");
                throw new System.ComponentModel.Win32Exception(error);
            }
            try { return CaptureHandle(handle, pid); } finally { CloseHandle(handle); }
        }
        internal ResourceIdentity Capture(Process process)
        {
            return CaptureHandle(process.Handle, process.Id); // exact original CreateProcess handle for private workers
        }
        private ResourceIdentity CaptureHandle(IntPtr handle, int pid)
        {
                if (WaitForSingleObject(handle, 0) == 0) throw new ArgumentException("Original resource process exited.");
                if (!GetProcessTimes(handle, out long created, out _, out _, out _)) throw new IOException("Resource process is unavailable.");
                var image = new StringBuilder(32768); int length = image.Capacity;
                if (!QueryFullProcessImageName(handle, 0, image, ref length)) throw new IOException("Cannot verify the resource image.");
                return new ResourceIdentity { HostId = host, Pid = pid, Created = created.ToString(CultureInfo.InvariantCulture), Executable = Path.GetFullPath(image.ToString()).ToLowerInvariant() };
        }
        public string State(ResourceIdentity identity)
        {
            if (identity.HostId != host) return "unknown"; // foreign host rejected before PID lookup
            try {
                var actual = Capture(identity.Pid);
                if (actual.Created != identity.Created) return "different";
                return actual.Same(identity) ? "alive" : "unknown";
            } catch (ArgumentException) { return "absent"; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is IOException) { return "unknown"; }
        }
    }
    internal sealed class ResourceOwnership
    {
        private static readonly object Gate = new object();
        private static ResourceOwnership? current;
        internal static ResourceOwnership Current => current ?? throw new InvalidOperationException("Resource ownership is not initialized.");
        internal static void Initialize(int maximum)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native resource ownership requires Windows.");
#endif
            lock (Gate) {
                if (current != null) return;
                string path = Environment.GetEnvironmentVariable("SIEMENS_MCP_MANAGEMENT_REGISTRY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMCPManager", "Registry");
                current = new ResourceOwnership(path, new WindowsResourceProcessDriver(), maximum, privateDirectory: Protect);
            }
        }
        private readonly string directory, generation = Guid.NewGuid().ToString("N");
        private readonly IResourceProcessDriver driver;
        private readonly ResourceIdentity owner;
        private readonly int maximum;
        private readonly Action<string> privateDirectory;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        internal ResourceOwnership(string registry, IResourceProcessDriver driver, int maximum = 32, Action<string>? privateDirectory = null)
        {
            if (!Path.IsPathRooted(registry) || maximum < 1 || maximum > 256) throw new ArgumentException("Invalid resource registry or limit.");
            directory = Path.Combine(Path.GetFullPath(registry), "resources"); this.driver = driver; this.maximum = maximum;
            this.privateDirectory = privateDirectory ?? (path => Directory.CreateDirectory(path));
            owner = driver.Capture(Process.GetCurrentProcess().Id);
        }
        private static void Protect(string path)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native resource privacy requires Windows.");
#endif
            Ordinary(path); Directory.CreateDirectory(path);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        private static void Ordinary(string path)
        {
            for (string? currentPath = Path.GetFullPath(path); currentPath != null; currentPath = Path.GetDirectoryName(currentPath))
                if ((File.Exists(currentPath) || Directory.Exists(currentPath)) && (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked resource paths are unsupported.");
        }
        private T Locked<T>(Func<T> action)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native byte-range resource locking requires Windows.");
#endif
            lock (Gate) {
                privateDirectory(Path.GetDirectoryName(directory)!); privateDirectory(directory);
                string path = Path.Combine(directory, "ledger.lock"); Ordinary(path);
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) {
                    if (stream.Length == 0) { stream.WriteByte(48); stream.Flush(true); }
                    stream.Lock(0, 1);
                    try { return action(); } finally { stream.Unlock(0, 1); }
                }
            }
        }
        private string FilePath(string id)
        {
            if (!Hex(id, 32)) throw new ArgumentException("Invalid resource ID.");
            string path = Path.Combine(directory, id + ".json"); Ordinary(path); return path;
        }
        private static bool Hex(string value, int length) => value != null && value.Length == length && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        private static bool Terminal(ResourceRecord record) => record.State == "released" || record.State == "terminated" || record.State == "failed";
        private static void Identity(ResourceIdentity identity)
        {
            if (identity == null || !Hex(identity.HostId, 64) || identity.Pid < 1 || identity.Created.Length < 1 || identity.Created.Length > 24 || identity.Created.Any(c => c < '0' || c > '9') || (identity.Created.Length > 1 && identity.Created[0] == '0') || !Path.IsPathRooted(identity.Executable) || identity.Executable.Length > 32768)
                throw new InvalidDataException("Invalid resource process identity.");
        }
        private static void Validate(ResourceRecord record)
        {
            if (record.SchemaVersion != 1 || !Hex(record.ResourceId, 32) || !Hex(record.OwnerId, 32) || record.AppId == null || !Regex.IsMatch(record.AppId, "^[a-z][a-z0-9_-]{0,63}\\z") || record.Kind == null || !Regex.IsMatch(record.Kind, "^[a-z][a-z0-9_-]{0,63}\\z") || record.Label.Length < 1 || record.Label.Length > 256 || !new[] { "private", "attached", "shared" }.Contains(record.Ownership) || !new[] { "report", "terminate" }.Contains(record.CleanupMode) || !new[] { "launching", "active", "released", "terminated", "failed", "uncertain" }.Contains(record.State) || !new[] { "app_owned", "windows_job" }.Contains(record.Containment) || record.Members == null || record.Members.Length > 256 || record.RecoveryBarrier?.Length > 512)
                throw new InvalidDataException("Invalid resource record.");
            Identity(record.Owner);
            if (record.Ownership != "private" && record.CleanupMode != "report") throw new InvalidDataException("Attachments/shared resources are report-only.");
            foreach (var identity in record.Members.Concat(record.Process == null ? Array.Empty<ResourceIdentity>() : new[] { record.Process })) {
                Identity(identity);
                if (identity.HostId != record.Owner.HostId || identity.Same(record.Owner) || (record.Ownership == "private" && decimal.Parse(identity.Created, CultureInfo.InvariantCulture) < decimal.Parse(record.Owner.Created, CultureInfo.InvariantCulture))) throw new InvalidDataException("Resource identity/ownership conflict.");
            }
            if (double.IsNaN(record.CreatedAt) || double.IsInfinity(record.CreatedAt) || double.IsNaN(record.UpdatedAt) || double.IsInfinity(record.UpdatedAt)) throw new InvalidDataException("Invalid resource timestamps.");
            if (record.Receipt != null && (!Hex(record.Receipt.RequestId, 32) || !Hex(record.Receipt.ExpectedRevision, 64) || record.Receipt.Message.Length > 512 || !new[] { "dispatching", "terminated", "already_exited", "uncertain" }.Contains(record.Receipt.Outcome))) throw new InvalidDataException("Invalid cleanup receipt.");
            foreach (var attestation in new[] { record.Reconciliation, record.BarrierResolution }) if (attestation != null) {
                Identity(attestation.Actor);
                if (!Hex(attestation.ActorOwnerId, 32) || !Hex(attestation.PreviousRevision, 64) || string.IsNullOrWhiteSpace(attestation.Evidence) || attestation.Evidence.Length > 1024 || double.IsNaN(attestation.RecordedAt) || double.IsInfinity(attestation.RecordedAt)) throw new InvalidDataException("Invalid resource attestation.");
            }
        }
        private ResourceRecord Read(string id)
        {
            string path = FilePath(id);
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Oversized resource record.");
            byte[] bytes = File.ReadAllBytes(path); if (bytes.Length > 32768) throw new InvalidDataException("Oversized resource record.");
            using (var document = JsonDocument.Parse(bytes)) {
                Unique(document.RootElement);
                foreach (string field in new[] { "schema_version", "resource_id", "app_id", "owner_id", "owner", "label", "ownership", "state", "created_at", "updated_at" })
                    if (!document.RootElement.TryGetProperty(field, out _)) throw new InvalidDataException("Missing required resource field: " + field);
                if (document.RootElement.TryGetProperty("receipt", out var receipt) && receipt.ValueKind != JsonValueKind.Null)
                    Required(receipt, "request_id", "expected_revision", "outcome", "message");
                foreach (string key in new[] { "reconciliation", "barrier_resolution" })
                    if (document.RootElement.TryGetProperty(key, out var attestation) && attestation.ValueKind != JsonValueKind.Null)
                        Required(attestation, "actor_owner_id", "actor", "previous_revision", "evidence", "recorded_at");
            }
            var record = JsonSerializer.Deserialize<ResourceRecord>(bytes, Json) ?? throw new InvalidDataException("Missing resource record.");
            Validate(record); if (record.ResourceId != id) throw new InvalidDataException("Resource ID differs from filename."); return record;
        }
        private static void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object) {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate resource property."); Unique(property.Value); }
            } else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
        }
        private static void Required(JsonElement element, params string[] fields)
        {
            foreach (string field in fields) if (!element.TryGetProperty(field, out _)) throw new InvalidDataException("Missing required nested resource field: " + field);
        }
        private List<ResourceRecord> Records()
        {
            var paths = Directory.EnumerateFiles(directory, "*.json").Take(4097).ToArray();
            if (paths.Length > 4096) throw new InvalidDataException("Resource history capacity exceeded.");
            return paths.Select(path => Read(Path.GetFileNameWithoutExtension(path))).ToList();
        }
        private void Write(ResourceRecord record)
        {
            Validate(record); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            if (bytes.Length + 1 > 32768) throw new InvalidDataException("Oversized resource record.");
            string path = FilePath(record.ResourceId), temp = Path.Combine(directory, ".resource-" + Guid.NewGuid().ToString("N"));
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.WriteByte(10); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private void Mine(ResourceRecord record)
        {
            if (record.AppId != "siemens-mcp-server" || record.OwnerId != generation || !record.Owner.Same(owner) || !driver.Capture(owner.Pid).Same(owner)) throw new InvalidOperationException("Resource requires its exact live supervisor generation.");
        }
        private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        internal string Begin(string label, bool attached = false)
        {
            return Locked(() => {
                var all = Records();
                if (all.Count >= 4096 || all.Count(r => r.AppId == "siemens-mcp-server" && !Terminal(r)) >= maximum) throw new InvalidOperationException("Resource capacity reached. Preserve/reconcile earlier claims before starting more software.");
                var record = new ResourceRecord { ResourceId = Guid.NewGuid().ToString("N"), OwnerId = generation, Owner = owner, Label = label, Kind = attached ? "native-app" : "process", Ownership = attached ? "attached" : "private", CleanupMode = attached ? "report" : "terminate", CreatedAt = Now(), UpdatedAt = Now() };
                Write(record); return record.ResourceId;
            });
        }
        internal void Bind(string id, int pid)
            => BindIdentity(id, () => driver.Capture(pid));
        internal void BindStartedWorker(string id, Process original)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native process handles require Windows.");
#endif
            BindIdentity(id, () => {
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native process handles require Windows.");
#endif
                return (driver as WindowsResourceProcessDriver ?? throw new InvalidOperationException("Started-worker identity requires the Windows handle driver.")).Capture(original);
            });
        }
        private void BindIdentity(string id, Func<ResourceIdentity> capture)
        {
            Locked(() => {
                var record = Read(id); Mine(record);
                if (record.State != "launching" || record.Process != null) throw new InvalidOperationException("Resource already bound.");
                var identity = capture();
                if (record.Ownership == "private" && Records().Any(r => r.ResourceId != id && !Terminal(r) && ((r.Process?.Same(identity) ?? false) || r.Members.Any(m => m.Same(identity))))) throw new InvalidOperationException("Another retained claim references the process.");
                if (record.Ownership == "attached" && Records().Any(r => r.ResourceId != id && !Terminal(r) && r.Ownership == "attached" && (r.Process?.Same(identity) ?? false)))
                    throw new InvalidOperationException("Another native attachment owns or retains uncertainty for this exact TIA process. Inspect that claim before attaching again.");
                record.Process = identity; record.State = "active"; record.UpdatedAt = Now(); Write(record); return true;
            });
        }
        internal void FailedLaunch(string id, bool confirmedNotRunning)
        {
            Locked(() => {
                var record = Read(id); Mine(record);
                if (record.Ownership != "private" || record.State != "launching" || record.Process != null || record.Receipt != null) throw new InvalidOperationException("Not an unbound launch reservation.");
                record.State = confirmedNotRunning ? "failed" : "uncertain";
                if (!confirmedNotRunning) record.RecoveryBarrier = "Worker launch/binding failed; exit was not confirmed through the original handle. No PID-based cleanup or automatic retry.";
                record.UpdatedAt = Now(); Write(record); return true;
            });
        }
        internal string BeginCleanup(string id)
        {
            return Locked(() => {
                var record = Read(id); Mine(record);
                if (record.Ownership != "private" || record.CleanupMode != "terminate" || record.State != "active" || record.Receipt != null || record.RecoveryBarrier != null) throw new InvalidOperationException("Resource cleanup is blocked.");
                var identities = record.Members.Concat(record.Process == null ? Array.Empty<ResourceIdentity>() : new[] { record.Process }).ToArray();
                if (Records().Any(r => r.ResourceId != id && !Terminal(r) && identities.Any(identity => (r.Process?.Same(identity) ?? false) || r.Members.Any(member => member.Same(identity))))) throw new InvalidOperationException("Another retained resource claim blocks cleanup.");
                string revision = Revision(record), request = Guid.NewGuid().ToString("N");
                record.Receipt = new ResourceReceipt { RequestId = request, ExpectedRevision = revision, Outcome = "dispatching", Message = "Native supervisor closing its private pure feature worker." };
                record.UpdatedAt = Now(); Write(record); return request;
            });
        }
        internal void CompleteCleanup(string id, string request, bool confirmedExited)
        {
            Locked(() => {
                var record = Read(id); Mine(record);
                if (record.Receipt?.RequestId != request || record.Receipt.Outcome != "dispatching") throw new InvalidOperationException("Cleanup receipt mismatch; no retry is permitted.");
                // The app retains the original Process handle until this call. The
                // driver's absent/different check alone never authorizes a kill.
                bool gone = confirmedExited || (record.Process != null && new[] { "absent", "different" }.Contains(driver.State(record.Process)));
                record.Receipt.Outcome = gone ? "terminated" : "uncertain"; record.Receipt.Message = gone ? "Original private worker process handle confirmed exit." : "Worker exit is unverified; inspect the original cleanup receipt.";
                record.State = gone ? "terminated" : "uncertain"; if (!gone) record.RecoveryBarrier = "Private worker teardown needs independent inspection; no automatic retry.";
                record.UpdatedAt = Now(); Write(record); return true;
            });
        }
        internal void ReleaseAttachment(string id)
        {
            Locked(() => {
                var record = Read(id); Mine(record);
                if (record.Ownership != "attached" && record.Ownership != "shared") throw new InvalidOperationException("A private process cannot be forgotten.");
                record.State = "released"; record.UpdatedAt = Now(); Write(record); return true;
            });
        }
        internal void Barrier(string id, string reason)
        {
            Locked(() => { var record = Read(id); Mine(record); if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) throw new ArgumentException("Invalid resource recovery barrier."); record.RecoveryBarrier = reason; record.UpdatedAt = Now(); Write(record); return true; });
        }
        internal static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static string Revision(ResourceRecord record)
        {
            using (var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(record, Json))) {
                var output = new StringBuilder(); Canonical(document.RootElement, output, null); return Hash(Encoding.UTF8.GetBytes(output.ToString()));
            }
        }
        private static void Quoted(string text, StringBuilder output)
        {
            output.Append('"');
            foreach (char c in text) {
                switch (c) { case '"': output.Append("\\\""); break; case '\\': output.Append("\\\\"); break; case '\b': output.Append("\\b"); break; case '\f': output.Append("\\f"); break; case '\n': output.Append("\\n"); break; case '\r': output.Append("\\r"); break; case '\t': output.Append("\\t"); break;
                    default: if (c < 32 || c > 126) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else output.Append(c); break; }
            }
            output.Append('"');
        }
        private static void Canonical(JsonElement value, StringBuilder output, string? property)
        {
            if (value.ValueKind == JsonValueKind.Object) {
                output.Append('{'); bool first = true;
                foreach (var item in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { if (!first) output.Append(','); first = false; Quoted(item.Name, output); output.Append(':'); Canonical(item.Value, output, item.Name); } output.Append('}');
            } else if (value.ValueKind == JsonValueKind.Array) {
                output.Append('['); bool first = true; foreach (var item in value.EnumerateArray()) { if (!first) output.Append(','); first = false; Canonical(item, output, null); } output.Append(']');
            } else if (value.ValueKind == JsonValueKind.String) Quoted(value.GetString()!, output);
            else if (value.ValueKind == JsonValueKind.Number && (property == "created_at" || property == "updated_at" || property == "recorded_at")) {
                string number = value.GetDouble().ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant(); if (!number.Contains('.') && !number.Contains('e')) number += ".0"; output.Append(number);
            } else output.Append(value.GetRawText());
        }
    }
}
