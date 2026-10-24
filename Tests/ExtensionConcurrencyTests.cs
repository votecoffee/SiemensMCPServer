using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ExtensionConcurrencyTests
{
    sealed class Worker : IExtensionWorker
    {
        public ExtensionHello Hello { get; }
        public int ProcessId { get; }
        public bool Disposed, Fault;
        public bool Healthy => !Disposed && !Fault;
        public Func<JsonElement>? Work;
        public Worker(ExtensionBundle bundle, int pid) { ProcessId = pid; Hello = new ExtensionHello { Module = bundle.Manifest.Module, Version = bundle.Manifest.Version, Protocol = 1, Operations = FeatureOperations.Describe(bundle.Manifest.Module) }; }
        public JsonElement Call(string operation, object input) => Work == null ? ExtensionWire.Json(new { ok = true }) : Work();
        public void Dispose() { Disposed = true; }
    }
    sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "tia-concurrent-" + Guid.NewGuid().ToString("N"));
        public readonly ConcurrentBag<Worker> Workers = new();
        public Func<Worker, Worker>? Configure;
        int next;
        public string Bundled => Path.Combine(Root, "bundled");
        public Fixture() { Bundle("0.13.0"); }
        public void Bundle(string version)
        {
            string directory = Path.Combine(Bundled, "comfort"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SiemensMCPServer.ExtensionWorker.exe"), version);
            var manifest = new ExtensionManifest { Format = "tia-extension-v1", Protocol = 1, Module = "comfort", Version = version, EntryPoint = "SiemensMCPServer.ExtensionWorker.exe",
                Files = TransferFiles.Inventory(directory).Where(f => f.Path != "extension.json").ToArray() };
            File.WriteAllText(Path.Combine(directory, "extension.json"), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        }
        public ExtensionManager Manager() => new(Bundled, Path.Combine(Root, "external"), Path.Combine(Root, "cache"), bundle => {
            var worker = new Worker(bundle, Interlocked.Increment(ref next)); Workers.Add(worker); return Configure?.Invoke(worker) ?? worker;
        }, localParallelism: 2);
        public void Dispose() => Directory.Delete(Root, true);
    }
    [Fact] public async Task LocalRequestsOverlapWithoutUsingThePrimaryWorkerAndReloadDrainsTheirGeneration()
    {
        using var fixture = new Fixture(); using var started = new CountdownEvent(2); using var release = new ManualResetEventSlim(); using var reloadStarted = new ManualResetEventSlim();
        fixture.Configure = worker => {
            if (worker.ProcessId > 1) worker.Work = () => { started.Signal(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return ExtensionWire.Json(new { ok = true }); };
            return worker;
        };
        using var manager = fixture.Manager(); string first = manager.Generation("comfort");
        try {
            var requests = Enumerable.Range(0, 2).Select(_ => Task.Run(() => manager.ExecuteLocal("comfort", "inspect_xml", new { }))).ToArray();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            var identity = ExtensionWire.Json(manager.Identity("comfort")); Assert.Equal(2, identity.GetProperty("localWorkerProcessIds").GetArrayLength());
            Assert.True(manager.Execute("comfort", "inspect_xml", new { }).GetProperty("ok").GetBoolean());
            fixture.Bundle("0.13.1"); int invalidations = 0;
            var reload = Task.Run(() => { reloadStarted.Set(); return manager.Reload("comfort", () => Interlocked.Increment(ref invalidations)); });
            Assert.True(reloadStarted.Wait(TimeSpan.FromSeconds(5))); Assert.False(reload.IsCompleted); Assert.All(fixture.Workers, worker => Assert.False(worker.Disposed));
            release.Set(); var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10)); await reload.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(responses, response => Assert.Equal(first, ExtensionWire.Json(response).GetProperty("extension").GetProperty("generation").GetString()));
            Assert.NotEqual(first, manager.Generation("comfort")); Assert.Equal(1, invalidations);
            Assert.All(fixture.Workers.Where(worker => worker.Hello.Version == "0.13.0"), worker => Assert.True(worker.Disposed));
        } finally { release.Set(); }
    }
    [Fact] public async Task ExplicitWorkerResetDrainsLocalCallsAndRecreatesTheirGeneration()
    {
        using var fixture = new Fixture(); using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        fixture.Configure = worker => {
            if (worker.ProcessId == 2) worker.Work = () => { started.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return ExtensionWire.Json(new { ok = true }); };
            return worker;
        };
        using var manager = fixture.Manager(); string original = manager.Generation("comfort");
        try {
            var request = Task.Run(() => manager.ExecuteLocal("comfort", "inspect_xml", new { }));
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            var reset = Task.Run(() => manager.ResetWorkers());
            Assert.False(reset.IsCompleted); Assert.All(fixture.Workers, worker => Assert.False(worker.Disposed));
            release.Set(); await request.WaitAsync(TimeSpan.FromSeconds(10)); await reset.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(fixture.Workers, worker => Assert.True(worker.Disposed));
            Assert.NotEqual(original, manager.Generation("comfort"));
            Assert.True(ExtensionWire.Json(manager.ExecuteLocal("comfort", "inspect_xml", new { })).GetProperty("data").GetProperty("ok").GetBoolean());
        } finally { release.Set(); }
    }
    [Fact] public void FailedReloadPreservesTheCurrentGenerationAndItsLocalPool()
    {
        using var fixture = new Fixture(); using var manager = fixture.Manager();
        string generation = manager.Generation("comfort"); manager.ExecuteLocal("comfort", "inspect_xml", new { }); var workers = fixture.Workers.ToArray();
        File.AppendAllText(Path.Combine(fixture.Bundled, "comfort", "SiemensMCPServer.ExtensionWorker.exe"), "tamper");
        Assert.Throws<InvalidOperationException>(() => manager.Reload("comfort", () => throw new Exception("must not invalidate")));
        var response = ExtensionWire.Json(manager.ExecuteLocal("comfort", "inspect_xml", new { }));
        Assert.Equal(generation, response.GetProperty("extension").GetProperty("generation").GetString()); Assert.Equal(workers.Length, fixture.Workers.Count);
        Assert.All(workers, worker => Assert.False(worker.Disposed));
    }
    [Fact] public void FaultedPureWorkersAreRetiredWithoutChangingTheGenerationOrPrimaryWorker()
    {
        using var fixture = new Fixture();
        fixture.Configure = worker => {
            if (worker.ProcessId == 2) worker.Work = () => { worker.Fault = true; throw new IOException("synthetic worker crash"); };
            return worker;
        };
        using var manager = fixture.Manager(); string generation = manager.Generation("comfort");
        Assert.Throws<IOException>(() => manager.ExecuteLocal("comfort", "inspect_xml", new { }));
        Assert.True(fixture.Workers.Single(worker => worker.ProcessId == 2).Disposed);
        manager.ExecuteLocal("comfort", "inspect_xml", new { }); Assert.Equal(generation, manager.Generation("comfort")); Assert.Equal(3, fixture.Workers.Count);
        Assert.False(fixture.Workers.Single(worker => worker.ProcessId == 1).Disposed);
    }
    [Fact] public void DomainErrorsRetainHealthyWorkersAndNativeRoutesCannotUseTheLocalPool()
    {
        using var fixture = new Fixture();
        fixture.Configure = worker => { if (worker.ProcessId == 2) worker.Work = () => throw new ArgumentException("bad supplied XML"); return worker; };
        using var manager = fixture.Manager();
        Assert.Throws<ArgumentException>(() => manager.ExecuteLocal("comfort", "inspect_xml", new { }));
        Assert.Throws<ArgumentException>(() => manager.ExecuteLocal("comfort", "inspect_xml", new { })); Assert.Equal(2, fixture.Workers.Count);
        Assert.Throws<InvalidOperationException>(() => manager.ExecuteLocal("comfort", "edit_xml", new { })); Assert.Equal(2, fixture.Workers.Count);
        Assert.All(fixture.Workers, worker => Assert.False(worker.Disposed));
    }
    [Fact] public void LocalWorkerCatalogMustMatchItsImmutableGeneration()
    {
        using var fixture = new Fixture(); fixture.Configure = worker => { if (worker.ProcessId > 1) worker.Hello.Version = "9.9.9"; return worker; };
        using var manager = fixture.Manager(); string generation = manager.Generation("comfort");
        Assert.Throws<InvalidOperationException>(() => manager.ExecuteLocal("comfort", "inspect_xml", new { }));
        Assert.True(fixture.Workers.Single(worker => worker.ProcessId == 2).Disposed); Assert.Equal(generation, manager.Generation("comfort"));
    }
}
