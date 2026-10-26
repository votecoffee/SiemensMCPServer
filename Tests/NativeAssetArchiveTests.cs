using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class NativeAssetArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "native-archive-test-" + Guid.NewGuid().ToString("N"));
    public NativeAssetArchiveTests() => Directory.CreateDirectory(root);
    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(root).StartsWith("native-archive-test-", StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup");
        Directory.Delete(root, true);
    }
    internal static void WriteArchive(string path, IDictionary<string, byte[]> files, IDictionary<string, byte[]>? actual = null, string? header = null, string purpose = "extensions")
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var output = zip.CreateEntry(NativeAssetArchive.IndexName).Open()) {
            byte[] raw = Encoding.UTF8.GetBytes(header ?? JsonSerializer.Serialize(new { format = "siemens-native-assets-v1", purpose,
                files = files.Select(p => new { path = p.Key, bytes = p.Value.Length, sha256 = TextListDocument.Hash(p.Value) }).ToArray() }));
            output.Write(raw, 0, raw.Length);
        }
        foreach (var item in actual ?? files) { using var output = zip.CreateEntry(item.Key).Open(); output.Write(item.Value, 0, item.Value.Length); }
    }
    private Dictionary<string, byte[]> Files() => new Dictionary<string, byte[]> { ["nested/item.txt"] = Encoding.UTF8.GetBytes("Español 😀"), ["active.json"] = Encoding.UTF8.GetBytes("{}") };
    [Fact] public void ExactBodiesExtractToANewDirectoryAndArchiveLeasePreventsReplacement()
    {
        string path = Path.Combine(root, "assets.zip"); var files = Files(); WriteArchive(path, files);
        using (var archive = new NativeAssetArchive(path, "extensions")) {
            Assert.Equal(files["nested/item.txt"], archive.Read("nested/item.txt", 100));
            archive.RequireHash("nested/item.txt", TextListDocument.Hash(files["nested/item.txt"]));
            Assert.Throws<InvalidDataException>(() => archive.Read("nested/item.txt", 1));
            Assert.Throws<InvalidDataException>(() => archive.RequireHash("nested/item.txt", new string('0', 64)));
            string destination = Path.Combine(root, "expanded"); archive.Extract(destination);
            Assert.All(files, item => Assert.Equal(item.Value, File.ReadAllBytes(Path.Combine(destination, item.Key))));
            Assert.Throws<IOException>(() => archive.Extract(destination));
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        }
        using var writable = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
    }
    [Theory]
    [InlineData("../escape")][InlineData("/absolute")][InlineData("C:/escape")][InlineData("folder\\escape")]
    [InlineData("folder/CON.txt")][InlineData("trailing. ")][InlineData("Siemens.Engineering.dll")][InlineData("runtime.whl")]
    public void UnsafeMembersRefuseBeforeExtraction(string name)
    {
        string path = Path.Combine(root, "unsafe.zip"); WriteArchive(path, new Dictionary<string, byte[]> { [name] = new byte[] { 1 } });
        Assert.Throws<InvalidDataException>(() => new NativeAssetArchive(path, "extensions"));
        Assert.Single(Directory.GetFiles(root)); Assert.Empty(Directory.GetDirectories(root));
    }
    [Fact] public void ChangedBodyMissingExtraAndCaseAliasesAreRejected()
    {
        var files = Files();
        string body = Path.Combine(root, "body.zip"); var changed = new Dictionary<string, byte[]>(files) { ["active.json"] = Encoding.UTF8.GetBytes("[]") }; WriteArchive(body, files, changed);
        using (var archive = new NativeAssetArchive(body, "extensions")) Assert.Throws<InvalidDataException>(() => archive.Read("active.json", 100));
        foreach (string mode in new[] { "missing", "extra", "alias" }) {
            var actual = new Dictionary<string, byte[]>(files);
            if (mode == "missing") actual.Remove("active.json");
            else actual.Add(mode == "alias" ? "ACTIVE.JSON" : "extra.txt", new byte[] { 1 });
            string path = Path.Combine(root, mode + ".zip"); WriteArchive(path, files, actual);
            Assert.Throws<InvalidDataException>(() => new NativeAssetArchive(path, "extensions"));
        }
    }
    [Fact] public void WrongPurposeDuplicateHeaderAndSymlinkAreRejected()
    {
        string purpose = Path.Combine(root, "purpose.zip"); WriteArchive(purpose, Files(), purpose: "native-guidance");
        Assert.Throws<InvalidDataException>(() => new NativeAssetArchive(purpose, "extensions"));
        string duplicate = Path.Combine(root, "duplicate.zip"); WriteArchive(duplicate, Files(), header: "{\"format\":\"siemens-native-assets-v1\",\"format\":\"other\",\"purpose\":\"extensions\",\"files\":[]}");
        Assert.Throws<InvalidDataException>(() => new NativeAssetArchive(duplicate, "extensions"));
        string link = Path.Combine(root, "link.zip"); WriteArchive(link, Files());
        using (var zip = ZipFile.Open(link, ZipArchiveMode.Update)) zip.GetEntry("active.json")!.ExternalAttributes = unchecked((int)0xa1ff0000);
        Assert.Throws<InvalidDataException>(() => new NativeAssetArchive(link, "extensions"));
    }
    [Fact] public void DecodeBoundsFileCollisionsAndTruncatedArchivesReleaseTheirHandles()
    {
        var files = new Dictionary<string, byte[]> { ["large.txt"] = new byte[256 * 1024 + 1] };
        string bounded = Path.Combine(root, "bounded.zip"); WriteArchive(bounded, files);
        using (var archive = new NativeAssetArchive(bounded, "extensions"))
            Assert.Throws<InvalidDataException>(() => archive.Read("large.txt", 256 * 1024));
        string collision = Path.Combine(root, "collision.zip");
        WriteArchive(collision, new Dictionary<string, byte[]> { ["folder"] = new byte[] { 1 }, ["folder/item"] = new byte[] { 2 } });
        Assert.Throws<InvalidDataException>(() => new NativeAssetArchive(collision, "extensions"));
        using (File.Open(collision, FileMode.Open, FileAccess.Write, FileShare.None)) { }
        string truncated = Path.Combine(root, "truncated.zip"); File.WriteAllBytes(truncated, new byte[] { 0x50, 0x4b, 3, 4 });
        Assert.Throws<InvalidDataException>(() => new NativeAssetArchive(truncated, "extensions"));
        using (File.Open(truncated, FileMode.Open, FileAccess.Write, FileShare.None)) { }
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{\"path\":\"a\",\"bytes\":-1,\"sha256\":\"bad\"}")]
    [InlineData("{\"path\":\"a\",\"bytes\":67108865,\"sha256\":\"bad\"}")]
    [InlineData("{\"path\":\"a\",\"bytes\":1,\"sha256\":\"bad\",\"extra\":true}")]
    public void InvalidIndexRecordsRefuseBeforeAnyExtraction(string record)
    {
        string path = Path.Combine(root, "index.zip");
        WriteArchive(path, Files(), header: "{\"format\":\"siemens-native-assets-v1\",\"purpose\":\"extensions\",\"files\":[" + record + "]}");
        Assert.ThrowsAny<Exception>(() => new NativeAssetArchive(path, "extensions"));
        Assert.Empty(Directory.GetDirectories(root));
        using var writable = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
    }
}
