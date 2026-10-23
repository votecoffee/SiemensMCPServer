using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

// Package bytes and streams only: no MCP/worker/native process startup.
public sealed class NativeGuidanceTests
{
    private sealed class Scratch : IDisposable
    {
        internal string Root = Path.Combine(Path.GetTempPath(), "siemens-native-guidance-" + Guid.NewGuid().ToString("N"));
        internal readonly byte[] Text = Encoding.UTF8.GetBytes("# Español 😀\n");
        internal Scratch() { Directory.CreateDirectory(Root); File.WriteAllBytes(Path.Combine(Root, "guide.md"), Text); Manifest(); }
        internal void Manifest(string path = "guide.md", int? length = null) => File.WriteAllBytes(Path.Combine(Root, "guidance-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            format = "siemens-native-guidance-v1", files = Enumerable.Range(0, 4).Select(i => new { id = "guide-" + i, kind = i == 3 ? "reference" : "workflow", path, sha256 = ResourceOwnership.Hash(Text), bytes = length ?? Text.Length })
        }));
        internal string Revision => ResourceOwnership.Hash(File.ReadAllBytes(Path.Combine(Root, "guidance-manifest.json")));
        public void Dispose() => Directory.Delete(Root, true);
    }
    [Fact] public void ExactRevisionAndContentPinsPreserveUnicode()
    {
        using var temp = new Scratch(); var reader = new NativeGuidance(temp.Root);
        var catalog = JsonSerializer.SerializeToElement(reader.Catalog());
        Assert.Equal(3, catalog.GetProperty("workflows").GetArrayLength());
        var result = JsonSerializer.SerializeToElement(reader.Get("guide-0", temp.Revision, ResourceOwnership.Hash(temp.Text), "workflow"));
        Assert.Equal(Encoding.UTF8.GetString(temp.Text), result.GetProperty("markdown").GetString());
        Assert.Throws<InvalidOperationException>(() => reader.Get("guide-0", temp.Revision, ResourceOwnership.Hash(temp.Text), "reference"));
    }
    [Fact] public void StaleRevisionHashAndTamperedContentAreRefused()
    {
        using var temp = new Scratch(); var reader = new NativeGuidance(temp.Root); string revision = temp.Revision, hash = ResourceOwnership.Hash(temp.Text);
        Assert.Throws<InvalidOperationException>(() => reader.Get("guide-0", new string('0',64), hash, "workflow"));
        Assert.Throws<InvalidOperationException>(() => reader.Get("guide-0", revision, new string('0',64), "workflow"));
        File.AppendAllText(Path.Combine(temp.Root,"guide.md"), "changed");
        Assert.Throws<InvalidOperationException>(() => reader.Get("guide-0", revision, hash, "workflow"));
        temp.Manifest(length: 999);
        Assert.Throws<InvalidOperationException>(() => reader.Get("guide-0", revision, hash, "workflow"));
    }
    [Theory] [InlineData("../outside.md")] [InlineData("/outside.md")] [InlineData("folder\\guide.md")] [InlineData("guide.md:stream")] [InlineData("./guide.md")]
    public void PathsCannotEscapeThePackage(string path)
    {
        using var temp = new Scratch(); temp.Manifest(path); var reader = new NativeGuidance(temp.Root);
        Assert.Throws<InvalidDataException>(() => reader.Get("guide-0", temp.Revision, ResourceOwnership.Hash(temp.Text), "workflow"));
    }
    [Fact] public void OversizedMarkdownIsRejectedDuringBoundedRead()
    {
        using var temp = new Scratch(); File.WriteAllBytes(Path.Combine(temp.Root,"guide.md"),new byte[256*1024+1]);
        Assert.Throws<InvalidDataException>(() => new NativeGuidance(temp.Root).Get("guide-0", temp.Revision, ResourceOwnership.Hash(temp.Text), "workflow"));
    }
    [Fact] public async Task InputEofIsObservedOnceBeforeSdkDrainAndZeroReadsAreIgnored()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Español 😀"); int ended = 0;
        var stream = new NativeInputStream(new MemoryStream(bytes), () => ended++); var buffer = new byte[64];
        Assert.Equal(0, stream.Read(buffer,0,0)); Assert.Equal(0,ended);
        int count = await stream.ReadAsync(buffer,0,buffer.Length);
        Assert.Equal(bytes, buffer.Take(count).ToArray()); Assert.Equal(0,ended);
        Assert.Equal(0, await stream.ReadAsync(buffer,0,buffer.Length)); Assert.Equal(1,ended);
        Assert.Equal(0, stream.Read(buffer,0,buffer.Length)); Assert.Equal(1,ended);
    }
}
