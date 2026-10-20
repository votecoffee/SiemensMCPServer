using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer;
using SiemensMCPServer.Mcp;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class TextListBundleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tia-bundle-test-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Destination => Path.Combine(root, "candidate");
    private string Add(string name = "Messages_TL", bool alias = false, bool utf16 = false, string wrapper = "")
    {
        string path = Path.Combine(Source, wrapper, "TextLists", name + ".xml"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string xml = NativeXmlValidationTests.NativeFixture().Replace("Messages_TL", name);
        byte[] bytes = utf16 ? Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<?xml version='1.0' encoding='utf-16'?>\r\n" + xml)).ToArray()
            : NativeXmlValidationTests.Utf8(xml, alias ? "<?xml version='1.0' encoding='utf8'?>" : "<?xml version='1.0' encoding='utf-8'?>", true);
        File.WriteAllBytes(path, bytes); return path;
    }
    [Fact] public void RepairsOnlyDeclarationAndPublishesMatchingFolderZipWithSeparateOriginals()
    {
        string path = Add(alias: true, wrapper: "outer/inner"); byte[] original = File.ReadAllBytes(path);
        var receipt = JsonSerializer.SerializeToElement(TextListBundlePreparation.Prepare(Source, Destination, true, "V15.1"), JsonDefaults.Options);
        Assert.True(receipt.GetProperty("validation").GetProperty("canPublish").GetBoolean());
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(original, File.ReadAllBytes(Path.Combine(Destination, "originals/00000/resource.xml")));
        string bundle = receipt.GetProperty("artifactDirectory").GetString()!;
        var resource = Assert.Single(TransferBundle.Read(bundle));
        Assert.Equal(NativeXmlValidation.RepairDeclaration(original), File.ReadAllBytes(Path.Combine(bundle, resource.SourceDirectory, "resource.xml")));
        string extracted = Path.Combine(root, "zip-check"); TransferZip.Extract(Path.Combine(Destination, "bundle.zip"), extracted);
        Assert.Equal(TransferFiles.Hash(TransferFiles.Inventory(bundle)), TransferFiles.Hash(TransferFiles.Inventory(extracted)));
        Assert.DoesNotContain(TransferFiles.Inventory(bundle), f => f.Path.Contains("originals") || f.Path.Contains("backup"));
    }
    [Fact] public void KeepsNativeUtf16AndExactSelectedNamesWithoutSuffixInference()
    {
        string path = Add(utf16: true); Add("Messages_TL_1");
        TextListBundlePreparation.Prepare(Source, Destination, true, "V15.1", new[] { "Messages_TL" });
        var resource = Assert.Single(TransferBundle.Read(Path.Combine(Destination, "bundle")));
        Assert.Equal("p:TextLists/n:Messages_TL", resource.Path);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(Destination, "bundle", resource.SourceDirectory, "resource.xml")));
    }
    [Theory] [InlineData("alias")] [InlineData("duplicate")] [InlineData("companion")] [InlineData("truncated")] [InlineData("manifest")] [InlineData("target")]
    public void RequiredFailureBlocksPublicationAndRetainsReceipt(string problem)
    {
        string path = Add(alias: problem == "alias");
        if (problem == "duplicate") File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, "Other.xml"));
        if (problem == "companion") File.WriteAllText(path, NativeXmlValidationTests.NativeFixture().Replace("</Document>", "<Payload external='path'>missing.bin</Payload></Document>"));
        if (problem == "truncated") File.WriteAllText(path, "<Document>");
        if (problem == "manifest") { TransferManifest.Write(Source); File.WriteAllText(Path.Combine(Source, "extra.txt"), "undeclared"); }
        string before = TransferFiles.Hash(TransferFiles.Inventory(Source, false));
        Assert.Throws<InvalidDataException>(() => TextListBundlePreparation.Prepare(Source, Destination, false, problem == "target" ? "V19" : "V15.1"));
        Assert.False(Directory.Exists(Destination)); Assert.True(File.Exists(Destination + ".validation-failed.json"));
        Assert.Equal(before, TransferFiles.Hash(TransferFiles.Inventory(Source, false)));
    }
    [Fact] public void SupportsNestedZipAndChecksSourceManifestBeforeRepair()
    {
        Add(alias: true, wrapper: "wrapper");
        // The original alias is legitimate source evidence for a declaration repair.
        var files = TransferFiles.Inventory(Source, false);
        File.WriteAllText(Path.Combine(Source, TransferManifest.FileName), JsonSerializer.Serialize(new { format = TransferManifest.Format, files }, JsonDefaults.Options));
        string archive = Path.Combine(root, "source.zip"); CreateZip(Source, archive);
        string before = TextListDocument.Hash(File.ReadAllBytes(archive));
        TextListBundlePreparation.Prepare(archive, Destination, true, "V15.1");
        Assert.Equal(before, TextListDocument.Hash(File.ReadAllBytes(archive)));
    }
    [Fact] public void RejectsArchiveCrcCorruptionAndUnsafeOrCaseCollidingNames()
    {
        Add(); string archive = Path.Combine(root, "source.zip"); CreateZip(Source, archive);
        byte[] bytes = File.ReadAllBytes(archive); byte[] needle = Encoding.UTF8.GetBytes("Messages_TL");
        int position = Enumerable.Range(0, bytes.Length - needle.Length).First(i => bytes.Skip(i).Take(needle.Length).SequenceEqual(needle));
        // First occurrence is in a local header filename; corrupt body instead.
        position = Enumerable.Range(position + needle.Length, bytes.Length - needle.Length - position - needle.Length).First(i => bytes.Skip(i).Take(needle.Length).SequenceEqual(needle));
        bytes[position] = (byte)'X'; File.WriteAllBytes(archive, bytes);
        Assert.Throws<InvalidDataException>(() => TransferZip.Extract(archive, Path.Combine(root, "bad-crc")));
        foreach (var names in new[] { new[] { "../escape.xml" }, new[] { "TextLists/A.xml", "TextLists/a.xml" } })
        {
            string zipPath = Path.Combine(root, Guid.NewGuid() + ".zip");
            using (var zip = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create)) foreach (var name in names) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("x"); }
            var error = Record.Exception(() => TransferZip.Extract(zipPath, Path.Combine(root, Guid.NewGuid().ToString("N"))));
            Assert.True(error is ArgumentException || error is InvalidDataException);
        }
    }
    [Fact] public void ManifestIsAutomaticallyCheckedWhenPreviewReadsPublishedBundle()
    {
        Add(); TextListBundlePreparation.Prepare(Source, Destination, false, "V15.1"); string bundle = Path.Combine(Destination, "bundle");
        File.AppendAllText(Path.Combine(bundle, "objects/00000/native/resource.xml"), " ");
        Assert.Throws<InvalidDataException>(() => TransferBundle.Read(bundle));
    }
    private static void CreateZip(string source, string archive)
    {
        using var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create);
        foreach (var file in TransferFiles.Inventory(source, false))
        { using var target = zip.CreateEntry(file.Path, CompressionLevel.NoCompression).Open(); using var input = File.OpenRead(Path.Combine(source, file.Path)); input.CopyTo(target); }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
