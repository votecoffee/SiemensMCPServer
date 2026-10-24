using System;
using System.Security.Principal;
using System.Threading;

namespace SiemensMCPServer.Mcp
{
    // Metadata roots never reserve an execution slot. The old native slot
    // namespace remains in use so already-running roots count during transition.
    internal static class NativeWorkAdmission
    {
        private static readonly object sync = new object();
        private static int maximum, processes, memory, metadataMemory;
        private static string prefix = "";
        private static Slot? slot;
        internal static void Configure(int max, int processLimit, int memoryMiB, int metadataMiB, string? testPrefix = null)
        {
            lock (sync) {
                if (maximum != 0) throw new InvalidOperationException("Native work admission already configured.");
                if (max < 1 || max > 16 || processLimit < 1 || processLimit > 256 || memoryMiB < 64 || memoryMiB > 16384 || metadataMiB < 64 || metadataMiB > memoryMiB)
                    throw new ArgumentException("Invalid native work budget.");
                maximum = max; processes = processLimit; memory = memoryMiB; metadataMemory = metadataMiB;
                string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Native user identity unavailable.");
                prefix = testPrefix ?? "Local\\SiemensMCPServer-NativeDirect-v1-" + sid + "-";
            }
        }
        internal static void EnsureActive()
        {
            lock (sync) {
                if (maximum == 0) return; // separate model harness has no OS budget
                if (slot != null) {
                    if (!slot.Active) throw new InvalidOperationException("Previous execution release is unconfirmed. End this root after inspecting its evidence; no work dispatched.");
                    return;
                }
                var candidate = new Slot(prefix, maximum); slot = candidate;
                try { OwnedWorkerJob.Configure(processes, memory); }
                catch { candidate.Dispose(); slot = null; throw; }
                Console.Error.WriteLine("Siemens native work admitted: slot=" + slot.Index + "; processes=" + processes + "; memoryMiB=" + memory);
            }
        }
        internal static bool ReleaseAfterDrain()
        {
            lock (sync) {
                if (slot == null) return true;
                if (!OwnedWorkerJob.TryMetadataBudget(metadataMemory)) return false;
                slot.Dispose(); slot = null;
                Console.Error.WriteLine("Siemens native work released after explicit detach; discovery budget restored.");
                return true;
            }
        }
        internal static object Snapshot()
        {
            lock (sync) return new { active = slot?.Active == true, releasePending = slot != null && !slot.Active, slot = slot?.Index, maximumWorkspaces = maximum,
                metadataMemoryMiB = metadataMemory, executionMemoryMiB = memory, executionProcesses = processes,
                release = "Explicit tia_detach drains workers before releasing execution. Retained memory may keep the work budget until EOF; peers are never stopped." };
        }
        private sealed class Slot : IDisposable
        {
            private readonly ManualResetEventSlim release = new ManualResetEventSlim();
            private readonly ManualResetEventSlim closed = new ManualResetEventSlim();
            private int releaseRequested;
            internal int Index { get; private set; } = -1;
            internal bool Active => Index >= 0 && Volatile.Read(ref releaseRequested) == 0;
            internal Slot(string name, int count)
            {
                var ready = new ManualResetEventSlim(); Exception? error = null;
                new Thread(() => {
                    Mutex? held = null;
                    try {
                        for (int i = 0; i < count; i++) {
                            var candidate = new Mutex(false, name + i);
                            bool acquired; try { acquired = candidate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                            if (acquired) { held = candidate; Index = i; break; }
                            candidate.Dispose();
                        }
                    } catch (Exception ex) { error = ex; }
                    finally { ready.Set(); }
                    try { if (held != null) release.Wait(); }
                    finally { if (held != null) { held.ReleaseMutex(); held.Dispose(); } closed.Set(); }
                }) { IsBackground = true, Name = "SiemensNativeWorkLease" }.Start();
                if (!ready.Wait(TimeSpan.FromSeconds(5))) { release.Set(); throw new InvalidOperationException("Native execution admission exceeded its deadline; no work dispatched."); }
                ready.Dispose();
                if (error != null || Index < 0) { release.Set(); throw new InvalidOperationException("Native execution capacity unavailable; discovery remains connected. Detach an idle workspace or wait for its owner to release it. No worker or native operation was dispatched.", error); }
            }
            public void Dispose()
            {
                Interlocked.Exchange(ref releaseRequested, 1);
                release.Set();
                if (!closed.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Native work lease release is unconfirmed; retain the root and inspect it.");
            }
        }
    }
}
