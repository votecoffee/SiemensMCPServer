using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SiemensMCPServer.Mcp
{
    // A lane owns its threads. Cancellation can remove a queued item, but never
    // aborts an operation that has crossed the dispatch boundary.
    internal sealed class RequestLane : IDisposable
    {
        private sealed class Item
        {
            public string Id = Guid.NewGuid().ToString("N"), Operation = "", State = "queued";
            public DateTime Queued = DateTime.UtcNow;
            public DateTime? Started;
            public Func<object> Run = null!;
            public CancellationToken Token;
            public CancellationTokenRegistration Registration;
            public LinkedListNode<Item>? Node;
            public readonly TaskCompletionSource<object> Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly object sync = new object();
        private readonly LinkedList<Item> pending = new LinkedList<Item>();
        private readonly Dictionary<string, Item> running = new Dictionary<string, Item>();
        private readonly Thread[] threads;
        private readonly string name;
        private readonly int capacity;
        private readonly Action? cleanup;
        private bool stopping;
        private Exception? cleanupFailure;
        private long completed, failed, cancelled, rejected;

        public RequestLane(string name, int parallelism, int capacity, bool sta = false, Action? cleanup = null)
        {
            if (parallelism < 1 || parallelism > 8 || capacity < 1 || capacity > 1000 || sta && parallelism != 1) throw new ArgumentException("Invalid request lane limits.");
            this.name = name; this.capacity = capacity; this.cleanup = cleanup;
            threads = Enumerable.Range(0, parallelism).Select(i => new Thread(Loop) { IsBackground = true, Name = name + " " + i }).ToArray();
            foreach (var thread in threads) {
                if (sta) {
#if NET8_0_OR_GREATER
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The TIA STA lane requires Windows.");
#endif
                    thread.SetApartmentState(ApartmentState.STA);
                }
                thread.Start();
            }
        }
        public Task<object> Enqueue(Func<object> operation, CancellationToken token, string operationName)
        {
            if (operation == null || string.IsNullOrEmpty(operationName) || operationName.Length > 256) throw new ArgumentException("A bounded operation name and callback are required.");
            var item = new Item { Run = operation, Token = token, Operation = operationName };
            lock (sync) {
                if (stopping) throw new ObjectDisposedException(name);
                if (token.IsCancellationRequested) { cancelled++; item.State = "cancelled"; item.Completion.SetCanceled(); return item.Completion.Task; }
                if (pending.Count >= capacity) { rejected++; throw new InvalidOperationException(name + " request queue is full; retry after queued work completes."); }
                item.Node = pending.AddLast(item); Monitor.PulseAll(sync);
            }
            // Register outside the lock: Register may synchronously invoke the callback.
            var registration = token.Register(() => CancelQueued(item));
            bool keep;
            lock (sync) { keep = item.State == "queued"; if (keep) item.Registration = registration; }
            if (!keep) registration.Dispose();
            return item.Completion.Task;
        }
        private void CancelQueued(Item item)
        {
            CancellationTokenRegistration registration;
            lock (sync) {
                if (item.State != "queued") return;
                pending.Remove(item.Node!); item.Node = null; item.State = "cancelled"; cancelled++;
                registration = item.Registration; item.Registration = default;
            }
            item.Completion.TrySetCanceled(); registration.Dispose();
        }
        private void Loop()
        {
            try {
                while (true) {
                    Item item; CancellationTokenRegistration registration; bool skip;
                    lock (sync) {
                        while (pending.Count == 0 && !stopping) Monitor.Wait(sync);
                        if (pending.Count == 0 && stopping) return;
                        item = pending.First!.Value; pending.RemoveFirst(); item.Node = null;
                        registration = item.Registration; item.Registration = default;
                        skip = item.Token.IsCancellationRequested;
                        if (skip) { item.State = "cancelled"; cancelled++; }
                        else { item.State = "running"; item.Started = DateTime.UtcNow; running.Add(item.Id, item); }
                    }
                    registration.Dispose();
                    if (skip) { item.Completion.TrySetCanceled(); continue; }
                    try {
                        object result = item.Run();
                        lock (sync) { running.Remove(item.Id); item.State = "completed"; completed++; }
                        item.Completion.TrySetResult(result);
                    } catch (OperationCanceledException) {
                        lock (sync) { running.Remove(item.Id); item.State = "cancelled"; cancelled++; }
                        item.Completion.TrySetCanceled();
                    } catch (Exception ex) {
                        lock (sync) { running.Remove(item.Id); item.State = "failed"; failed++; }
                        item.Completion.TrySetException(ex);
                    }
                }
            } finally {
                try { cleanup?.Invoke(); }
                catch (Exception ex) { lock (sync) cleanupFailure = ex; }
            }
        }
        public object Snapshot()
        {
            lock (sync) {
                object Row(Item item) => new { requestId = item.Id, operation = item.Operation, state = item.State,
                    queuedUtc = item.Queued, startedUtc = item.Started, elapsedMilliseconds = (long)(DateTime.UtcNow - (item.Started ?? item.Queued)).TotalMilliseconds };
                return new { name, parallelism = threads.Length, queueCapacity = capacity, accepting = !stopping,
                    queued = pending.Count, active = running.Count, completed, failed, cancelled, rejected, cleanupFailed = cleanupFailure != null,
                    activeRequests = running.Values.Select(Row).ToArray(), queuedRequests = pending.Take(20).Select(Row).ToArray(), queuedRequestsTruncated = pending.Count > 20 };
            }
        }
        public void BeginStop()
        {
            Item[] abandoned;
            lock (sync) {
                if (stopping) return;
                stopping = true; abandoned = pending.ToArray(); pending.Clear();
                foreach (var item in abandoned) { item.Node = null; item.State = "cancelled"; cancelled++; }
                Monitor.PulseAll(sync);
            }
            foreach (var item in abandoned) { item.Completion.TrySetCanceled(); item.Registration.Dispose(); }
        }
        public void Dispose()
        {
            BeginStop();
            foreach (var thread in threads) if (thread != Thread.CurrentThread) thread.Join();
            if (cleanupFailure != null) throw new InvalidOperationException("Request lane cleanup failed: " + name, cleanupFailure);
        }
    }
}
