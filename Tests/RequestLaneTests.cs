using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class RequestLaneTests
{
    static void Wait(ManualResetEventSlim gate) { if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test gate did not open."); }
    static int Count(RequestLane lane, string property) => ExtensionWire.Json(lane.Snapshot()).GetProperty(property).GetInt32();

    [Fact] public async Task NativeSnapshotMaterializesDeferredReadsBeforeCrossingTheOwnerThreadBoundary()
    {
        using var lane = new RequestLane("snapshot", 1, 2); int owner = 0, reads = 0;
        var response = await lane.EnqueueSnapshot(() => {
            owner = Environment.CurrentManagedThreadId;
            return new { values = Enumerable.Range(0, 5).Select(value => {
                Assert.Equal(owner, Environment.CurrentManagedThreadId); Interlocked.Increment(ref reads); return value;
            }) };
        }, default, "deferred native read");
        Assert.Equal(5, reads); var json = Assert.IsType<System.Text.Json.JsonElement>(response);
        Assert.Equal(5, json.GetProperty("values").GetArrayLength());
        Assert.Contains("values", System.Text.Json.JsonSerializer.Serialize(json)); Assert.Equal(5, reads);
    }

    [Fact] public async Task SerialLanePreservesFifoThreadOwnershipAndContinuesAfterErrors()
    {
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var lane = new RequestLane("native-test", 1, 10);
        var order = new ConcurrentQueue<int>(); var owners = new ConcurrentBag<int>();
        try {
            var first = lane.Enqueue(() => { owners.Add(Environment.CurrentManagedThreadId); started.Set(); Wait(release); order.Enqueue(0); return 0; }, default, "first"); Wait(started);
            var next = Enumerable.Range(1, 5).Select(i => lane.Enqueue(() => { owners.Add(Environment.CurrentManagedThreadId); order.Enqueue(i); return i; }, default, "next")).ToArray();
            Assert.Equal(5, Count(lane, "queued")); release.Set(); await first; await Task.WhenAll(next).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Enumerable.Range(0, 6), order); Assert.Single(owners.Distinct());
            var bad = lane.Enqueue(() => throw new InvalidOperationException("synthetic"), default, "failure");
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await bad);
            Assert.Equal(9, await lane.Enqueue(() => 9, default, "after failure")); Assert.Equal(1, Count(lane, "failed"));
        } finally { release.Set(); }
    }
    [Fact] public async Task QueuedCancellationReleasesCapacityImmediatelyAndNeverDispatches()
    {
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        using var lane = new RequestLane("cancel-test", 1, 1); int called = 0;
        try {
            var first = lane.Enqueue(() => { started.Set(); Wait(release); return 0; }, default, "active"); Wait(started);
            var queued = lane.Enqueue(() => Interlocked.Increment(ref called), cancellation.Token, "cancelled queued");
            Assert.Throws<InvalidOperationException>(() => { _ = lane.Enqueue(() => 1, default, "full"); }); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, Count(lane, "queued"));
            var replacement = lane.Enqueue(() => 2, default, "replacement"); release.Set(); await first; Assert.Equal(2, await replacement);
            Assert.Equal(0, called); Assert.Equal(1, Count(lane, "cancelled")); Assert.Equal(1, Count(lane, "rejected"));
        } finally { release.Set(); }
    }
    [Fact] public async Task CancellationAfterDispatchDoesNotAbortOrHideTheOperationResult()
    {
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource(); using var lane = new RequestLane("running-test", 1, 1);
        try {
            var task = lane.Enqueue(() => { started.Set(); Wait(release); return "committed"; }, cancellation.Token, "transaction"); Wait(started);
            cancellation.Cancel(); Assert.False(task.IsCompleted); release.Set(); Assert.Equal("committed", await task); Assert.Equal(0, Count(lane, "cancelled"));
        } finally { release.Set(); }
    }
    [Fact] public async Task SeparateAnalysisAndControlLanesContinueWhileNativeWorkIsBlocked()
    {
        using var nativeStarted = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var parallelStarted = new CountdownEvent(2);
        using var native = new RequestLane("native", 1, 4); using var local = new RequestLane("analysis", 2, 4); using var control = new RequestLane("control", 1, 4);
        try {
            var transaction = native.Enqueue(() => { nativeStarted.Set(); Wait(release); return 0; }, default, "native"); Wait(nativeStarted);
            var analysis = Enumerable.Range(0, 2).Select(i => local.Enqueue(() => { parallelStarted.Signal(); Wait(release); return i; }, default, "analysis")).ToArray();
            Assert.True(parallelStarted.Wait(TimeSpan.FromSeconds(5))); Assert.Equal(2, Count(local, "active"));
            Assert.Equal(1, await control.Enqueue(() => Count(native, "active"), default, "status").WaitAsync(TimeSpan.FromSeconds(2)));
            release.Set(); await transaction; await Task.WhenAll(analysis);
        } finally { release.Set(); }
    }
    [Fact] public async Task OverlappingPreviewsRejectAStaleCommitAndDistinctObjectsCanBothCommit()
    {
        using var lane = new RequestLane("transactions", 1, 10); var fingerprints = new Dictionary<string, int> { ["A"] = 0, ["B"] = 0 };
        Task<object> Apply(string target, int expected) => lane.Enqueue(() => {
            if (fingerprints[target] != expected) throw new InvalidOperationException("stale preview");
            fingerprints[target]++; return target;
        }, default, "apply " + target);
        var first = Apply("A", 0); var overlap = Apply("A", 0); var separate = Apply("B", 0);
        Assert.Equal("A", await first); await Assert.ThrowsAsync<InvalidOperationException>(async () => await overlap); Assert.Equal("B", await separate);
        Assert.Equal(1, fingerprints["A"]); Assert.Equal(1, fingerprints["B"]);
    }
    [Fact] public async Task ShutdownCancelsQueuedWorkAndWaitsForDispatchedWorkAndOwnerCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int owner = 0, cleanupOwner = 0, queuedCalls = 0;
        using var lane = new RequestLane("shutdown", 1, 2, sta: true, cleanup: () => cleanupOwner = Environment.CurrentManagedThreadId);
        try {
            var active = lane.Enqueue(() => { owner = Environment.CurrentManagedThreadId; Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState()); started.Set(); Wait(release); return 1; }, default, "active"); Wait(started);
            var queued = lane.Enqueue(() => Interlocked.Increment(ref queuedCalls), default, "queued"); lane.BeginStop();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
            Assert.Throws<ObjectDisposedException>(() => { _ = lane.Enqueue(() => 0, default, "late"); }); Assert.False(active.IsCompleted);
            release.Set(); Assert.Equal(1, await active); lane.Dispose(); Assert.Equal(owner, cleanupOwner); Assert.Equal(0, queuedCalls);
        } finally { release.Set(); }
    }
    [Fact] public async Task CancellationAdmissionDispatchRacesNeverExecuteAnItemTwiceOrLeakPendingWork()
    {
        using var lane = new RequestLane("race", 4, 100); var tasks = new List<Task<object>>(); var sources = new List<CancellationTokenSource>(); var counts = new int[100];
        try {
            for (int i = 0; i < counts.Length; i++) {
                int index = i; var cancellation = new CancellationTokenSource(); sources.Add(cancellation);
                tasks.Add(lane.Enqueue(() => Interlocked.Increment(ref counts[index]), cancellation.Token, "race")); cancellation.Cancel();
            }
            foreach (var task in tasks) try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            Assert.All(counts, count => Assert.InRange(count, 0, 1)); Assert.Equal(0, Count(lane, "queued")); Assert.Equal(0, Count(lane, "active"));
            Assert.Equal(100, Count(lane, "completed") + Count(lane, "cancelled"));
        } finally { foreach (var source in sources) source.Dispose(); }
    }
}
