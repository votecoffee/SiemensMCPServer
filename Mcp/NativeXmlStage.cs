using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SiemensMCPServer.Mcp
{
    internal sealed class NativeStagingReport
    {
        public string State { get; set; } = "prepared";
        public string LogicalPath { get; set; } = "";
        public string NativePath { get; set; } = "";
        public string PayloadSha256 { get; set; } = "";
        public long PayloadBytes { get; set; }
        public bool NativeDispatched { get; set; }
        public bool NativeCallReturned { get; set; }
        public bool? CleanupSucceeded { get; set; }
        public string? Error { get; set; }
        public string? EvidenceDirectory { get; set; }
        public string? ResourceInventorySha256 { get; set; }
        public object? Preflight { get; set; }
        public string Visibility => "The server verified the resolved physical file and hash. Only the native operation establishes Siemens process access.";
        public bool SettingsChanged => false;
    }

    // Per-operation files only. Never changes TEMP, user safeguards or a shared root's ACL.
    internal sealed class NativeXmlStage : IDisposable
    {
        public string DirectoryPath { get; }
        public string NativeDirectory { get; }
        public NativeStagingReport Report { get; }
        private readonly string root, token;
        private readonly int maximumPath;
        private bool disposed;
        private NativeXmlStage(string root, string token, string fileName, int maximumPath)
        {
            this.root = Path.GetFullPath(root); this.token = token; this.maximumPath = maximumPath;
            DirectoryPath = Path.Combine(this.root, token);
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("A staging directory already exists.");
            RejectLinks(this.root);
            try
            {
                CreatePrivate(DirectoryPath);
                string probe = Path.Combine(DirectoryPath, "visibility.tmp");
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                {
                    stream.WriteByte(0); stream.Flush(true);
                    NativeDirectory = Path.GetDirectoryName(PhysicalPath(stream))!;
                }
                File.Delete(probe);
                string native = Path.Combine(NativeDirectory, fileName);
                if (native.StartsWith("\\\\", StringComparison.Ordinal) || native.Length > maximumPath)
                    throw new IOException("Resolved native staging path exceeds the local short-path profile.");
                Report = new NativeStagingReport { LogicalPath = Path.Combine(DirectoryPath, fileName), NativePath = native };
            }
            catch { DeleteOwned(); throw; }
        }

        public static NativeXmlStage Create(string fileName = "payload.xml", string[]? roots = null, int maximumPath = 140)
        {
            if (fileName != Path.GetFileName(fileName) || string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Use one safe staging filename.");
            string drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            string[] selected = roots ?? new[] { Path.Combine(drive + Path.DirectorySeparatorChar, "Temp"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), Path.GetTempPath() };
            var failures = new System.Collections.Generic.List<string>();
            foreach (string candidate in selected.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Path.IsPathRooted(candidate) || !Directory.Exists(candidate)) continue;
                try { return new NativeXmlStage(candidate, "smcp-" + Guid.NewGuid().ToString("N"), fileName, maximumPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)
                { failures.Add(Path.GetFullPath(candidate) + ": " + ex.Message); }
            }
            throw new IOException("No writable private short staging directory was available. No native operation was dispatched. " + string.Join("; ", failures));
        }

        public void Write(byte[] bytes, string targetVersion = "", bool textListMetadata = false)
        {
            var preflight = NativeXmlValidation.Analyze(bytes, targetVersion, textListMetadata);
            Report.Preflight = preflight; preflight.RequirePassed();
            using (var stream = new FileStream(Report.LogicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            Verify(bytes);
        }

        public byte[] Read()
        {
            if (!File.Exists(Report.NativePath)) throw new FileNotFoundException("Native operation did not produce its resolved staging XML.", Report.NativePath);
            if (new FileInfo(Report.NativePath).Length > NativeXmlValidation.MaximumBytes) throw new IOException("Native XML exceeds its transfer bound.");
            byte[] bytes = File.ReadAllBytes(Report.NativePath);
            NativeXmlValidation.Analyze(bytes).RequirePassed(); Verify(bytes); return bytes;
        }

        public void Verify(byte[] expected)
        {
            RejectLinks(DirectoryPath);
            if (Report.NativePath.Length > maximumPath || !File.Exists(Report.NativePath) || !File.Exists(Report.LogicalPath)) throw new IOException("Prepared native staging XML is unavailable.");
            string hash = TextListDocument.Hash(expected);
            if (TextListDocument.Hash(File.ReadAllBytes(Report.NativePath)) != hash || TextListDocument.Hash(File.ReadAllBytes(Report.LogicalPath)) != hash)
                throw new IOException("Prepared native staging XML changed before dispatch.");
            Report.PayloadSha256 = hash; Report.PayloadBytes = expected.LongLength;
        }

        private static void CreatePrivate(string directory)
        {
#if NETFRAMEWORK
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { Directory.CreateDirectory(directory); return; }
#else
            if (!OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); return; }
#endif
            var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Cannot determine staging owner.");
            var security = new DirectorySecurity(); security.SetOwner(owner); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
#if NETFRAMEWORK
            Directory.CreateDirectory(directory, security);
#else
            new DirectoryInfo(directory).Create(security);
#endif
        }

        private static void RejectLinks(string directory)
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current != null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked native staging paths are not supported.");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        private static string PhysicalPath(FileStream stream)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return Path.GetFullPath(stream.Name);
            var buffer = new StringBuilder(4096);
            uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve native staging path.");
            string value = buffer.ToString();
            if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Native staging must be on a local drive.");
            if (value.StartsWith("\\\\?\\", StringComparison.Ordinal)) value = value.Substring(4);
            return Path.GetFullPath(value);
        }
        private void DeleteOwned()
        {
            string full = Path.GetFullPath(DirectoryPath), parent = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != token) throw new IOException("Refusing cleanup outside this operation's staging directory.");
            if (!Directory.Exists(full)) return;
            RejectLinks(full);
            CheckTree(full, 0);
            Directory.Delete(full, true);
        }
        private static void CheckTree(string directory, int depth)
        {
            if (depth > 32 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked or deeply nested staged files prevent automatic cleanup.");
            foreach (string file in Directory.GetFiles(directory))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked staged files prevent automatic cleanup.");
            foreach (string child in Directory.GetDirectories(directory)) CheckTree(child, depth + 1);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { DeleteOwned(); Report.CleanupSucceeded = true; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Report.CleanupSucceeded = false; Report.Error = ex.Message; }
        }
    }
}
