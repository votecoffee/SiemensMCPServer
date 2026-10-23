using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SiemensMCPServer.Mcp
{
    // Direct client owns creation of this root. Root owns its inherited worker
    // Job and durable worker/attached-TIA claims; never adopt/kill its parent.
    internal static class NativeHostLifetime
    {
        private static IntPtr parent;
        private static Mutex? slot;
        private static Action? stop;
        private static int requested, draining, exitCode;
        private static int drainSeconds;
        public static object? Identity { get; private set; }
        public static int ExitCode => Volatile.Read(ref exitCode);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry {
            public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, Parent;
            public int Priority; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Image;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder image, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref Entry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref Entry entry);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private static object Capture(IntPtr handle, uint pid, out long creation)
        {
            var image = new StringBuilder(32768); uint length = (uint)image.Capacity;
            if (!GetProcessTimes(handle, out creation, out _, out _, out _) || !QueryFullProcessImageName(handle, 0, image, ref length))
                throw new InvalidOperationException("Cannot prove native host/parent creation identity.");
            return new { pid, creationFileTime = creation.ToString(System.Globalization.CultureInfo.InvariantCulture), image = image.ToString() };
        }
        public static void Install(int maximumSessions, int eofDrainSeconds)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Native direct host requires Windows.");
            if (maximumSessions < 1 || maximumSessions > 16 || eofDrainSeconds < 1 || eofDrainSeconds > 600) throw new ArgumentException("Invalid native session/drain bounds.");
            if (Identity != null) throw new InvalidOperationException("Native host lifetime already installed.");
            drainSeconds = eofDrainSeconds;
            uint ownPid;
            using (var current = Process.GetCurrentProcess()) ownPid = (uint)current.Id;
            uint parentPid = 0;
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) throw new InvalidOperationException("Cannot read native host parent identity.");
            try {
                var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Image = "" };
                bool present = Process32First(snapshot, ref entry); int count = 0;
                while (present && ++count <= 65536) { if (entry.Pid == ownPid) { parentPid = entry.Parent; break; } present = Process32Next(snapshot, ref entry); }
            } finally { CloseHandle(snapshot); }
            if (parentPid == 0 || parentPid == ownPid) throw new InvalidOperationException("Native parent is unknown.");
            parent = OpenProcess(0x100000 | 0x1000, false, parentPid);
            if (parent == IntPtr.Zero) throw new InvalidOperationException("Native parent is not queryable.");
            var ownIdentity = Capture(GetCurrentProcess(), ownPid, out long ownCreation);
            var parentIdentity = Capture(parent, parentPid, out long parentCreation);
            if (parentCreation > ownCreation || WaitForSingleObject(parent, 0) != 258) throw new InvalidOperationException("Native parent exited or its PID was reused.");
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Native user identity unavailable.");
            var admission = new ManualResetEventSlim();
            Exception? admissionError = null;
            {
                new Thread(() => {
                    try {
                        for (int index = 0; index < maximumSessions; index++) {
                            var candidate = new Mutex(false, "Local\\SiemensMCPServer-NativeDirect-v1-" + sid + "-" + index);
                            bool owned; try { owned = candidate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                            if (owned) { slot = candidate; break; } candidate.Dispose();
                        }
                    } catch (Exception ex) { admissionError = ex; }
                    finally { admission.Set(); }
                    if (slot == null) return;
                    uint result = WaitForSingleObject(parent, uint.MaxValue);
                    RequestStop(result == 0 ? 125 : 126, "parent-handle-signaled-or-unverifiable");
                    Thread.Sleep(Timeout.Infinite); // retain admission through the entire bounded drain
                }) { IsBackground = true, Name = "SiemensNativeParentLifetime" }.Start();
                if (!admission.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Native admission exceeded its deadline.");
                admission.Dispose();
                if (admissionError != null || slot == null) throw new InvalidOperationException("Native direct session capacity unavailable; no SDK/native session started.", admissionError);
            }
            Identity = new { root = ownIdentity, parent = parentIdentity, maximumSessions, drainSeconds, parentRights = "query-limited-and-synchronize" };
            Console.Error.WriteLine("Siemens native direct lifetime: " + System.Text.Json.JsonSerializer.Serialize(Identity));
        }
        public static void BindStop(Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            stop = callback; if (Volatile.Read(ref requested) != 0) callback();
        }
        private static void RequestStop(int code, string reason)
        {
            Interlocked.CompareExchange(ref exitCode, code, 0); Interlocked.Exchange(ref requested, 1);
            BeginDrain(reason);
            try { stop?.Invoke(); } catch (Exception ex) { Console.Error.WriteLine("Native stop callback failed: " + ex.Message); }
        }
        public static void InputEnded() => RequestStop(0, "input-pipe-eof");
        public static void BeginDrain(string reason)
        {
            if (Interlocked.Exchange(ref draining, 1) != 0) return;
            Console.Error.WriteLine("Siemens native direct drain: " + reason + "; retained native/worker evidence is never replayed or cleared.");
            new Thread(() => {
                Thread.Sleep(TimeSpan.FromSeconds(drainSeconds));
                Console.Error.WriteLine("Siemens native direct drain expired; private Job closes with root. Native outcome remains subject to retained journal/attachment inspection.");
                Environment.Exit(ExitCode == 0 ? 124 : 127);
            }) { IsBackground = true, Name = "SiemensNativeDrainFence" }.Start();
        }
    }
}
