using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace SiemensMCPServer.Mcp
{
    // One non-inheritable, OS-owned handle lives until the MCP process exits.
    // Unexpected server death must not leave its private feature workers alive.
    // An externally opened TIA GUI is not a descendant and is never a target.
    internal static class OwnedWorkerJob
    {
        private static IntPtr job;
        private static readonly object budget = new object();
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
        [StructLayout(LayoutKind.Sequential)] private struct Accounting
        {
            public long TotalUser, TotalKernel, PeriodUser, PeriodKernel;
            public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr handle, int kind, ref Extended info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr handle, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr handle, int kind, out Accounting info, uint size, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        public static void Install(int maximum, int memoryMiB = 1024)
        {
            if (maximum < 1 || maximum > 256) throw new ArgumentException("Invalid owned process-tree limit.");
            if (memoryMiB < 64 || memoryMiB > 16384) throw new ArgumentException("Invalid owned Job memory limit.");
            if (job != IntPtr.Zero) return;
            IntPtr created = CreateJobObject(IntPtr.Zero, null);
            if (created == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the worker lifetime boundary.");
            var limits = new Extended(); limits.Basic.Flags = 0x2000 | 0x8 | 0x200; // KILL_ON_JOB_CLOSE | ACTIVE_PROCESS | JOB_MEMORY
            limits.Basic.ActiveProcessLimit = (uint)maximum;
            limits.JobMemoryLimit = (UIntPtr)checked((ulong)memoryMiB * 1024 * 1024);
            if (!SetInformationJobObject(created, 9, ref limits, (uint)Marshal.SizeOf<Extended>())
                || !AssignProcessToJobObject(created, GetCurrentProcess())) {
                int error = Marshal.GetLastWin32Error(); CloseHandle(created);
                throw new Win32Exception(error, "Cannot contain private workers; startup stopped before creating them.");
            }
            job = created; // Closed by kernel process teardown, never by a finalizer.
        }

        internal static void Configure(int maximum, int memoryMiB)
        {
            if (maximum < 1 || maximum > 256 || memoryMiB < 64 || memoryMiB > 16384) throw new ArgumentException("Invalid owned Job budget.");
            lock (budget) {
                if (job == IntPtr.Zero) throw new InvalidOperationException("Owned Job is not installed.");
                var limits = new Extended(); limits.Basic.Flags = 0x2000 | 0x8 | 0x200;
                limits.Basic.ActiveProcessLimit = (uint)maximum;
                limits.JobMemoryLimit = (UIntPtr)checked((ulong)memoryMiB * 1024 * 1024);
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Extended>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot change this root's owned Job budget.");
            }
        }

        internal static bool TryMetadataBudget(int memoryMiB)
        {
            // Native/worker callers must be drained before this method. Leave
            // execution admitted if the root still retains substantial memory;
            // never force the limit below observed committed memory.
            if (!QueryInformationJobObject(job, 1, out Accounting current, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot confirm this root's remaining Job members.");
            if (current.ActiveProcesses != 1) return false;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using (var process = Process.GetCurrentProcess())
                if (process.PrivateMemorySize64 > ((long)memoryMiB - 16) * 1024 * 1024) return false;
            Configure(1, memoryMiB);
            return true;
        }
    }
}
