using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SiemensMCPServer.Launcher;

internal sealed record Identity(uint Pid, long CreationFileTime, string Image, string ImageFileId);
internal sealed record Claim(int SchemaVersion, string Generation, Identity Owner, string SnapshotSha256, string Version, string State, int? ExitCode, Identity? Parent = null);

internal sealed class Generation : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Info {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    private readonly string root, path;
    private Claim claim;

    internal static Identity Capture(uint pid)
    {
        IntPtr process = OpenProcess(0x100000 | 0x1000, false, pid);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var image = new StringBuilder(32768); uint size = (uint)image.Capacity;
            if (!GetProcessTimes(process, out long created, out _, out _, out _) || !QueryFullProcessImageName(process, 0, image, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var file = CreateFile(image.ToString(), 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (file.IsInvalid || !GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var final = new StringBuilder(32768);
            uint count = GetFinalPathNameByHandle(file, final, (uint)final.Capacity, 0);
            if (count == 0 || count >= final.Capacity) throw new InvalidDataException("Image identity is unavailable.");
            return new(pid, created, final.ToString(), $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}");
        } finally { CloseHandle(process); }
    }
    // Never compare logical MSIX image spelling to a physical image spelling.
    // File identity + creation time establish an exact live generation.
    internal static string Observe(Identity owner)
    {
        IntPtr handle = OpenProcess(0x100000 | 0x1000, false, owner.Pid);
        if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error() == 87 ? "dead" : "uncertain";
        try {
            if (!GetProcessTimes(handle, out long created, out _, out _, out _)) return "uncertain";
            if (created != owner.CreationFileTime || WaitForSingleObject(handle, 0) == 0) return "dead";
            try { return Capture(owner.Pid).ImageFileId == owner.ImageFileId ? "active" : "uncertain"; }
            catch { return "uncertain"; }
        } finally { CloseHandle(handle); }
    }
    internal static void Write(string path, object value)
    {
        Selection.NoLinks(path);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Selection.Json);
        string stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(path)) File.Move(stage, path, true); else File.Move(stage, path);
    }
    private static void Retire(string root, string path, Claim claim)
    {
        // Keep the original claim and a separate outcome; never erase history.
        string receipt = Selection.Safe(root, "state/receipts/" + claim.Generation);
        Directory.CreateDirectory(receipt);
        File.Move(path, Path.Combine(receipt, "claim.json"));
        Write(Path.Combine(receipt, "outcome.json"), claim);
    }
    internal Generation(string root, Selection selection)
    {
        this.root = root;
        Directory.CreateDirectory(Selection.Safe(root, "state/active"));
        using var mutex = new Mutex(false, "Local\\SiemensOfflineLedger-" + Selection.Hash(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        bool owned; try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new InvalidOperationException("Offline ownership ledger is busy.");
        try {
            var records = Directory.GetFiles(Selection.Safe(root, "state/active"), "*.json");
            if (records.Length > 128) throw new InvalidDataException("Offline active claim bound exceeded.");
            foreach (string file in records) {
                var old = Selection.Decode<Claim>(Selection.Read(file, 16384));
                if (old.SchemaVersion != 1 || !Guid.TryParseExact(old.Generation, "N", out _) || Path.GetFileName(file) != old.Generation + ".json")
                    throw new InvalidDataException("Invalid ownership claim; retained.");
                if (Observe(old.Owner) == "dead") Retire(root, file, old with { State = "owner-exited-reconciled" });
            }
            string generation = Guid.NewGuid().ToString("N");
            Identity? parent = null;
            if (AppContext.GetData("SiemensOfflineFence-v1") is string fence) {
                using var recorded = JsonDocument.Parse(fence);
                parent = Capture(recorded.RootElement.GetProperty("parent").GetProperty("pid").GetUInt32());
                if (parent.CreationFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture) != recorded.RootElement.GetProperty("parent").GetProperty("creationFileTime").GetString())
                    throw new InvalidDataException("Parent generation changed before ownership publication.");
            }
            claim = new(1, generation, Capture((uint)Environment.ProcessId), selection.SnapshotHash, selection.Data.Version, "active", null, parent);
            path = Selection.Safe(root, "state/active/" + generation + ".json");
            Write(path, claim);
        } finally { mutex.ReleaseMutex(); }
    }
    internal void Complete(int code) { claim = claim with { State = code == 0 ? "closed" : "failed", ExitCode = code }; }
    public void Dispose() { Retire(root, path, claim); }
}
