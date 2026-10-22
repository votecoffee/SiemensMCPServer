using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SiemensMCPServer.Offline;

// No process is launched or adopted here. The direct client creates this root.
// Retain parent and Job handles until kernel teardown; never act on a later PID.
internal static class OfflineLifetime
{
    private static IntPtr job, parent;
    private static Mutex? slot;
    public static object? Identity { get; private set; }
    [StructLayout(LayoutKind.Sequential)] private struct Basic {
        public long ProcessTime, JobTime; public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Extended {
        public Basic Basic; public Io Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry {
        public uint Size, Usage, ProcessId; public UIntPtr Heap; public uint ModuleId, Threads, ParentProcessId;
        public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr handle, int kind, ref Extended info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr handle, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private static object Capture(IntPtr handle, uint pid, out long creation)
    {
        var image = new StringBuilder(32768); uint size = (uint)image.Capacity;
        if (!GetProcessTimes(handle, out creation, out _, out _, out _) || !QueryFullProcessImageName(handle, 0, image, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot establish direct offline process identity.");
        return new { pid, creationFileTime = creation.ToString(System.Globalization.CultureInfo.InvariantCulture), image = image.ToString() };
    }

    public static void Install()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Direct offline containment requires Windows.");
        if (job != IntPtr.Zero) throw new InvalidOperationException("Offline lifetime already installed.");
        uint ownPid = (uint)Environment.ProcessId, parentPid = 0;
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
            bool present = Process32First(snapshot, ref entry); int seen = 0;
            while (present && ++seen <= 65536) {
                if (entry.ProcessId == ownPid) { parentPid = entry.ParentProcessId; break; }
                present = Process32Next(snapshot, ref entry);
            }
        } finally { CloseHandle(snapshot); }
        if (parentPid == 0 || parentPid == ownPid) throw new InvalidOperationException("Cannot establish direct offline parent.");
        parent = OpenProcess(0x100000 | 0x1000, false, parentPid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION only
        if (parent == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Offline parent is unavailable.");
        var ownIdentity = Capture(GetCurrentProcess(), ownPid, out long ownCreation);
        var parentIdentity = Capture(parent, parentPid, out long parentCreation);
        if (parentCreation > ownCreation || WaitForSingleObject(parent, 0) != 258)
            throw new InvalidOperationException("Offline parent exited or its PID was reused.");
        IntPtr created = CreateJobObject(IntPtr.Zero, null);
        if (created == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new Extended();
        limits.Basic.Flags = 0x2000 | 0x8 | 0x200; // KILL_ON_CLOSE | ACTIVE_PROCESS | JOB_MEMORY
        limits.Basic.ActiveProcessLimit = 1;
        limits.JobMemoryLimit = (UIntPtr)(512u * 1024 * 1024);
        if (!SetInformationJobObject(created, 9, ref limits, (uint)Marshal.SizeOf<Extended>()) || !AssignProcessToJobObject(created, GetCurrentProcess())) {
            int error = Marshal.GetLastWin32Error(); CloseHandle(created);
            throw new Win32Exception(error, "Cannot contain direct offline root; no MCP host was started.");
        }
        job = created;
        using var admission = new ManualResetEventSlim();
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Offline user identity is unavailable.");
        // A dedicated retained thread owns the Mutex. Async continuations cannot
        // accidentally abandon it; kernel teardown releases it after any crash.
        new Thread(() => {
            for (int index = 0; index < 4; index++) {
                var candidate = new Mutex(false, "Local\\SiemensMCPServer-DirectOffline-v1-" + sid + "-" + index);
                bool owned;
                try { owned = candidate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (owned) { slot = candidate; break; }
                candidate.Dispose();
            }
            admission.Set();
            if (slot == null) return;
            uint state = WaitForSingleObject(parent, uint.MaxValue);
            Environment.Exit(state == 0 ? 125 : 126);
        }) { IsBackground = true, Name = "SiemensOfflineParentLifetime" }.Start();
        if (!admission.Wait(TimeSpan.FromSeconds(5)) || slot == null)
            throw new InvalidOperationException("Direct offline capacity is unavailable (four sessions per user/logon session). No MCP host was started.");
        Identity = new { owner = ownIdentity, parent = parentIdentity, activeProcessLimit = 1, jobMemoryBytes = 512u * 1024 * 1024, maxSessions = 4 };
        Console.Error.WriteLine("Siemens direct offline lifetime: " + System.Text.Json.JsonSerializer.Serialize(Identity));
    }
}
