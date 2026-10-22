using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

// File/driver fixtures only. No worker, MCP transport or native application is started.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ResourceOwnershipTests
{
    private sealed class Driver : IResourceProcessDriver
    {
        internal readonly Dictionary<int, ResourceIdentity> Processes = new();
        internal Action? Capturing;
        internal Driver() => Add(Process.GetCurrentProcess().Id, "100", @"c:\fixture\supervisor.exe");
        internal void Add(int pid = 100001, string created = "200", string executable = @"c:\fixture\worker.exe") => Processes[pid] = new ResourceIdentity { HostId = new string('f', 64), Pid = pid, Created = created, Executable = executable };
        public ResourceIdentity Capture(int pid) { Capturing?.Invoke(); return Processes[pid]; }
        public string State(ResourceIdentity identity) => !Processes.TryGetValue(identity.Pid, out var current) ? "absent" : current.Same(identity) ? "alive" : "different";
    }
    private sealed class Scratch : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "siemens-resource-contract-" + Guid.NewGuid().ToString("N"));
        internal string Resources => Path.Combine(Root, "resources");
        internal string FilePath(string id) => Path.Combine(Resources, id + ".json");
        internal JsonElement Read(string id) => JsonDocument.Parse(File.ReadAllBytes(FilePath(id))).RootElement.Clone();
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    [Fact] public void ReservationPrecedesWorkAndCleanupIntentPrecedesConfirmedExit()
    {
        using var temp = new Scratch(); var driver = new Driver(); driver.Add();
        var store = new ResourceOwnership(temp.Root, driver);
        string id = store.Begin("Pure worker ñ 😀 \u007f");
        Assert.Equal("launching", temp.Read(id).GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, temp.Read(id).GetProperty("process").ValueKind);
        store.Bind(id, 100001);
        byte[] before = File.ReadAllBytes(temp.FilePath(id));
        string request = store.BeginCleanup(id);
        Assert.Equal("dispatching", temp.Read(id).GetProperty("receipt").GetProperty("outcome").GetString());
        Assert.Equal("alive", driver.State(driver.Processes[100001]));
        driver.Processes.Remove(100001); store.CompleteCleanup(id, request, confirmedExited: true);
        Assert.Equal("terminated", temp.Read(id).GetProperty("state").GetString());
        string? output = Environment.GetEnvironmentVariable("SIEMENS_RESOURCE_CONTRACT_OUTPUT");
        if (output != null) {
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "private-before.json"), before);
            File.Copy(temp.FilePath(id), Path.Combine(output, "private-after.json"), overwrite: true);
        }
    }
    [Fact] public void AttachedUserSoftwareCanPredateOwnerAndNeverGetsPrivateCleanup()
    {
        using var temp = new Scratch(); var driver = new Driver(); driver.Add(created: "1");
        var store = new ResourceOwnership(temp.Root, driver);
        string id = store.Begin("User-owned application", attached: true); store.Bind(id, 100001);
        store.Barrier(id, "User-owned software/dirty state needs independent inspection.");
        Assert.Equal("report", temp.Read(id).GetProperty("cleanup_mode").GetString());
        Assert.Throws<InvalidOperationException>(() => store.BeginCleanup(id));
        store.ReleaseAttachment(id);
        Assert.Equal("released", temp.Read(id).GetProperty("state").GetString());
        Assert.True(driver.Processes.ContainsKey(100001));
        Assert.Equal(JsonValueKind.Null, temp.Read(id).GetProperty("receipt").ValueKind);
    }
    [Fact] public void UncertainCleanupKeepsBarrierAndRejectsAutomaticRetry()
    {
        using var temp = new Scratch(); var driver = new Driver(); driver.Add();
        var store = new ResourceOwnership(temp.Root, driver); string id = store.Begin("Pure worker"); store.Bind(id, 100001);
        string request = store.BeginCleanup(id); store.CompleteCleanup(id, request, confirmedExited: false);
        byte[] before = File.ReadAllBytes(temp.FilePath(id));
        Assert.Equal("uncertain", temp.Read(id).GetProperty("receipt").GetProperty("outcome").GetString());
        Assert.Equal("uncertain", temp.Read(id).GetProperty("state").GetString());
        Assert.Throws<InvalidOperationException>(() => store.BeginCleanup(id));
        Assert.Throws<InvalidOperationException>(() => store.CompleteCleanup(id, Guid.NewGuid().ToString("N"), true));
        Assert.Equal(before, File.ReadAllBytes(temp.FilePath(id)));
    }
    [Fact] public void NewGenerationCannotMutatePreviousClaimsOrBypassGlobalCapacity()
    {
        using var temp = new Scratch(); var driver = new Driver(); driver.Add();
        var first = new ResourceOwnership(temp.Root, driver, maximum: 1); string id = first.Begin("Reserved"); first.Bind(id, 100001);
        var second = new ResourceOwnership(temp.Root, driver, maximum: 1);
        byte[] before = File.ReadAllBytes(temp.FilePath(id));
        Assert.Throws<InvalidOperationException>(() => second.Begin("Must refuse"));
        Assert.Throws<InvalidOperationException>(() => second.BeginCleanup(id));
        Assert.Equal(before, File.ReadAllBytes(temp.FilePath(id)));
    }
    [Fact] public void PrivateClaimsRejectEarlierProcessesAndExistingForeignClaims()
    {
        using var temp = new Scratch(); var driver = new Driver(); driver.Add(created: "1");
        var store = new ResourceOwnership(temp.Root, driver);
        string early = store.Begin("Invalid earlier process");
        Assert.Throws<InvalidDataException>(() => store.Bind(early, 100001));
        driver.Add(created: "200");
        string attached = store.Begin("Existing attachment", attached: true); store.Bind(attached, 100001);
        string duplicate = store.Begin("Cannot adopt attachment as private");
        Assert.Throws<InvalidOperationException>(() => store.Bind(duplicate, 100001));
        Assert.Equal("attached", temp.Read(attached).GetProperty("ownership").GetString());
    }
    [Fact] public void AnotherRetainedAttachmentBlocksPrivateCleanup()
    {
        using var temp = new Scratch(); var driver = new Driver(); driver.Add();
        var store = new ResourceOwnership(temp.Root, driver);
        string worker = store.Begin("Pure worker"); store.Bind(worker, 100001);
        string attached = store.Begin("Other retained attachment", attached: true); store.Bind(attached, 100001);
        byte[] before = File.ReadAllBytes(temp.FilePath(worker));
        Assert.Throws<InvalidOperationException>(() => store.BeginCleanup(worker));
        Assert.Equal(before, File.ReadAllBytes(temp.FilePath(worker)));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema_version\":1,\"schema_version\":1}")]
    [InlineData("{\"schema_version\":true}")]
    [InlineData("{\"schema_version\":2}")]
    public void InvalidRetainedRecordBlocksAdmissionWithoutReplacingEvidence(string json)
    {
        using var temp = new Scratch(); Directory.CreateDirectory(temp.Resources);
        string path = temp.FilePath(new string('a', 32)); File.WriteAllText(path, json);
        var store = new ResourceOwnership(temp.Root, new Driver());
        Assert.ThrowsAny<Exception>(() => store.Begin("Must refuse")); Assert.Equal(json, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(temp.Resources, "*.json"));
    }
    [Fact] public void ProtocolRevisionHasPythonCompatibleIntegerTimestampAndUnicodeEncoding()
    {
        var identity = new ResourceIdentity { HostId = new string('f', 64), Pid = 1001, Created = "100", Executable = @"c:\fixture\owner.exe" };
        var record = new ResourceRecord { Owner = identity, OwnerId = new string('b', 32), ResourceId = new string('a', 32), Label = "ñ 😀 \u007f", CreatedAt = 0, UpdatedAt = 1700000000.125 };
        string? output = Environment.GetEnvironmentVariable("SIEMENS_RESOURCE_CONTRACT_OUTPUT");
        if (output != null) {
            Directory.CreateDirectory(output);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            File.WriteAllBytes(Path.Combine(output, "canonical.json"), JsonSerializer.SerializeToUtf8Bytes(record, options));
            File.WriteAllText(Path.Combine(output, "canonical.sha256"), ResourceOwnership.Revision(record));
        }
        Assert.Equal(64, ResourceOwnership.Revision(record).Length);
    }
    [Fact] public void CrossRuntimeLedgerLockMatchesFrameworkByteRange()
    {
        string? root = Environment.GetEnvironmentVariable("SIEMENS_RESOURCE_LOCK_REGISTRY");
        if (root == null) return; // run explicitly with the retained Python contract coordinator
        var driver = new Driver(); driver.Add(); var store = new ResourceOwnership(root, driver);
        string id = store.Begin("Cross-runtime lock fixture");
        driver.Capturing = () => {
            File.WriteAllText(Path.Combine(root, "ready"), "locked");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(Path.Combine(root, "release"))) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Contract coordinator did not release the fixture.");
                System.Threading.Thread.Sleep(10);
            }
        };
        store.Bind(id, 100001);
    }
}
