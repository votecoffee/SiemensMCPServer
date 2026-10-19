using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace SiemensMCPServer.Mcp
{
    // One request per worker process. The manager holds a generation read lease
    // for this entire call, so reload cannot retire an in-use process.
    internal sealed class ExtensionWorkerPool : IDisposable
    {
        private readonly object sync = new object();
        private readonly Stack<IExtensionWorker> idle = new Stack<IExtensionWorker>();
        private readonly HashSet<IExtensionWorker> workers = new HashSet<IExtensionWorker>();
        private readonly SemaphoreSlim slots;
        private readonly Func<IExtensionWorker> factory;
        private bool disposed;
        public ExtensionWorkerPool(int size, Func<IExtensionWorker> factory) { slots = new SemaphoreSlim(size, size); this.factory = factory; }
        public int[] ProcessIds { get { lock (sync) return workers.Select(w => w.ProcessId).Distinct().OrderBy(p => p).ToArray(); } }
        public JsonElement Call(string operation, object input)
        {
            slots.Wait(); IExtensionWorker? worker = null;
            try {
                lock (sync) {
                    if (disposed) throw new ObjectDisposedException(nameof(ExtensionWorkerPool));
                    if (idle.Count != 0) worker = idle.Pop();
                }
                if (worker == null) { worker = factory(); lock (sync) workers.Add(worker); }
                return worker.Call(operation, input);
            } finally {
                try { if (worker != null) {
                    bool retire;
                    lock (sync) { retire = disposed || !worker.Healthy; if (retire) workers.Remove(worker); else idle.Push(worker); }
                    if (retire) worker.Dispose();
                } } finally { slots.Release(); }
            }
        }
        public void Dispose()
        {
            // The manager's exclusive generation lease guarantees calls have drained.
            lock (sync) { if (disposed) return; disposed = true; foreach (var worker in workers) worker.Dispose(); workers.Clear(); idle.Clear(); }
            slots.Dispose();
        }
    }
}
