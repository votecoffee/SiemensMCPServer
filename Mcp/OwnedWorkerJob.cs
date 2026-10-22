using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SiemensMCPServer.Mcp
{
    // One non-inheritable, OS-owned handle lives until the MCP process exits.
    // Unexpected server death must not leave its private feature workers alive.
    // An externally opened TIA GUI is not a descendant and is never a target.
    internal static class OwnedWorkerJob
    {
        private static IntPtr job;
        [StructLayout(LayoutKind.Sequential)] private struct Basic
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Io
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Extended
        {
            public Basic Basic;
            public Io Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr handle, int kind, ref Extended info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr handle, IntPtr process);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        public static void Install()
        {
            if (job != IntPtr.Zero) return;
            IntPtr created = CreateJobObject(IntPtr.Zero, null);
            if (created == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the worker lifetime boundary.");
            var limits = new Extended(); limits.Basic.Flags = 0x2000; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(created, 9, ref limits, (uint)Marshal.SizeOf<Extended>())
                || !AssignProcessToJobObject(created, GetCurrentProcess())) {
                int error = Marshal.GetLastWin32Error(); CloseHandle(created);
                throw new Win32Exception(error, "Cannot contain private workers; startup stopped before creating them.");
            }
            job = created; // Closed by kernel process teardown, never by a finalizer.
        }
    }
}
