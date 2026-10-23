using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Launcher;
using Xunit;

public sealed class SelectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Siemens deployment 日本語 " + Guid.NewGuid().ToString("N"));
    public SelectionTests()
    {
        string package = new('a', 64), version = "26.1023.164730", relative = "runtimes/" + version + "-" + package[..12];
        string runtime = Path.Combine(root, relative); Directory.CreateDirectory(runtime);
        var files = new[] { "SiemensMCPServer.Offline.dll", "SiemensMCPServer.Offline.exe", "SiemensMCPServer.Offline.deps.json", "SiemensMCPServer.Offline.runtimeconfig.json" }
            .Select(name => { byte[] data = Encoding.UTF8.GetBytes(name); File.WriteAllBytes(Path.Combine(runtime, name), data); return new Payload(name, data.Length, Selection.Hash(data)); }).ToArray();
        byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, "direct-offline", version, package, relative, files), Selection.Json);
        string hash = Selection.Hash(snapshot); Directory.CreateDirectory(Path.Combine(root, "launcher/snapshots"));
        File.WriteAllBytes(Path.Combine(root, "launcher/snapshots/" + hash + ".json"), snapshot);
        Generation.Write(Path.Combine(root, "launcher/current.json"), new Pointer(1, hash));
    }
    public void Dispose() { Directory.Delete(root, true); }

    [Fact] public void VerifiedPayloadLeasesRefuseWritesUntilSelectionCloses()
    {
        string runtime;
        using (var selection = new Selection(root)) {
            runtime = selection.Runtime;
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(runtime, "SiemensMCPServer.Offline.dll"), "tampered"));
        }
        File.WriteAllText(Path.Combine(runtime, "SiemensMCPServer.Offline.dll"), "tampered");
        Assert.Throws<InvalidDataException>(() => new Selection(root));
    }
    [Theory][InlineData("{}")] [InlineData("{\"schemaVersion\":1,\"snapshotSha256\":\"../escape\"}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"snapshotSha256\":\"x\"}")]
    public void MalformedPointersNeverSelectAnAlternative(string text)
    {
        File.WriteAllText(Path.Combine(root, "launcher/current.json"), text);
        Assert.ThrowsAny<Exception>(() => new Selection(root));
    }
    [Fact] public void ExtraRuntimeFileRefusesEvenWhenAllListedHashesMatch()
    {
        using (var selection = new Selection(root)) File.WriteAllText(Path.Combine(selection.Runtime, "extra.dll"), "unreviewed");
        Assert.Throws<InvalidDataException>(() => new Selection(root));
    }
    [Fact] public void ActualImageIdentitySurvivesDifferentLogicalImageSpelling()
    {
        uint pid = uint.TryParse(Environment.GetEnvironmentVariable("SIEMENS_TEST_MSIX_PID"), out var supplied) ? supplied : (uint)Environment.ProcessId;
        var identity = Generation.Capture(pid);
        Assert.Equal("active", Generation.Observe(identity with { Image = @"C:\logical-MSIX-alias\Codex.exe" }));
        Assert.Equal("uncertain", Generation.Observe(identity with { ImageFileId = "wrong-file-identity" }));
        Assert.Equal("dead", Generation.Observe(identity with { CreationFileTime = identity.CreationFileTime - 1 }));
    }
    [Fact] public void ReconciliationRetainsLiveAndUncertainClaimsAndRetiresOnlyDeadGeneration()
    {
        using var selection = new Selection(root);
        Directory.CreateDirectory(Path.Combine(root, "state/active"));
        var owner = Generation.Capture((uint)Environment.ProcessId);
        string Put(Identity identity) {
            string id = Guid.NewGuid().ToString("N");
            Generation.Write(Path.Combine(root, "state/active/" + id + ".json"), new Claim(1, id, identity, selection.SnapshotHash, selection.Data.Version, "active", null));
            return id;
        }
        string live = Put(owner with { Image = @"C:\logical\testhost.exe" });
        string uncertain = Put(owner with { ImageFileId = "not-the-live-image" });
        string dead = Put(owner with { CreationFileTime = owner.CreationFileTime - 1 });
        using (var generation = new Generation(root, selection)) { generation.Complete(0); }
        Assert.True(File.Exists(Path.Combine(root, "state/active/" + live + ".json")));
        Assert.True(File.Exists(Path.Combine(root, "state/active/" + uncertain + ".json")));
        Assert.False(File.Exists(Path.Combine(root, "state/active/" + dead + ".json")));
        Assert.True(File.Exists(Path.Combine(root, "state/receipts/" + dead + "/claim.json")));
        Assert.Contains("owner-exited-reconciled", File.ReadAllText(Path.Combine(root, "state/receipts/" + dead + "/outcome.json")));
        Assert.False(Process.GetCurrentProcess().HasExited);
    }
}
