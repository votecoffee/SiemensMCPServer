using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer.NativeLauncher;
using Xunit;

public sealed class NativeLauncherSelectionTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "SiemensNativeSelection-" + Guid.NewGuid().ToString("N"));
        internal string Runtime = "";
        internal void Publish(string packageHash, string profile = "direct-native", string tia = "V15.1") {
            Runtime = Path.Combine(Root, "r", packageHash.Substring(0,16)); Directory.CreateDirectory(Runtime);
            string[] names = { "SiemensMCPServer.Mcp.exe", "SiemensMCPServer.Mcp.exe.config", "dependency.dll", "native-guidance/catalog.json" };
            foreach (string name in names) { string path = Path.Combine(Runtime, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, name + packageHash); }
            var files = names.Select(name => { byte[] raw = File.ReadAllBytes(Path.Combine(Runtime,name)); return new { path = name, bytes = raw.Length, sha256 = Selection.Hash(raw) }; }).ToArray();
            byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, profile, version = "26.1024.100420", packageSha256 = packageHash, runtime = "r/" + packageHash.Substring(0,16), tiaVersion = tia, allowWrites = false, files });
            string hash = Selection.Hash(snapshot); Directory.CreateDirectory(Path.Combine(Root,"launcher","snapshots"));
            File.WriteAllBytes(Path.Combine(Root,"launcher","snapshots",hash + ".json"), snapshot);
            File.WriteAllText(Path.Combine(Root,"launcher","current.json"), JsonSerializer.Serialize(new { schemaVersion = 1, snapshotSha256 = hash }));
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root,true); }
    }
    [Fact] public void NativeSelectionLeasesOldClosureAcrossAtomicPointerChange()
    {
        using var f = new Fixture(); f.Publish(new string('a',64));
        using var old = new Selection(f.Root); string oldRuntime = old.Runtime;
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(oldRuntime,"dependency.dll"),"changed"));
        f.Publish(new string('b',64)); using var next = new Selection(f.Root);
        Assert.NotEqual(old.SnapshotHash,next.SnapshotHash); Assert.Equal(oldRuntime,old.Runtime);
        Assert.Equal("V15.1",next.Data.TiaVersion); Assert.False(next.Data.AllowWrites);
    }
    [Fact] public void NativeSelectionRejectsExtraTamperedDuplicateAndOfflineInputs()
    {
        using var f = new Fixture(); f.Publish(new string('a',64),profile:"direct-offline");
        Assert.Throws<InvalidDataException>(() => new Selection(f.Root));
        f.Publish(new string('b',64)); File.WriteAllText(Path.Combine(f.Runtime,"extra.dll"),"unlisted");
        Assert.Throws<InvalidDataException>(() => new Selection(f.Root));
        f.Publish(new string('c',64)); File.WriteAllText(Path.Combine(f.Runtime,"dependency.dll"),"tampered");
        Assert.Throws<InvalidDataException>(() => new Selection(f.Root));
        Assert.Throws<InvalidDataException>(() => Selection.Decode<Pointer>(System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1,\"snapshotSha256\":\"x\"}")));
        Assert.ThrowsAny<Exception>(() => Selection.Decode<Pointer>(System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"SnapshotSha256\":\"x\"}")));
        Assert.Throws<InvalidDataException>(() => Selection.Safe(f.Root,"../outside"));
        Assert.Throws<InvalidDataException>(() => Selection.Safe(f.Root,"r\\outside"));
    }
}
