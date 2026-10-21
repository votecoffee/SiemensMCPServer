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
        Assert.Equal(NativeXmlValidation.RepairDeclaration(original), File.ReadAllBytes(Path.Combine(bundle, resource.SourceDirectory, "Messages_TL.xml")));
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
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(Destination, "bundle", resource.SourceDirectory, "Messages_TL.xml")));
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
        File.AppendAllText(Path.Combine(bundle, "objects/00000/native/Messages_TL.xml"), " ");
        Assert.Throws<InvalidDataException>(() => TransferBundle.Read(bundle));
    }
    [Fact] public void SelectedZipReadsOnlySelectedResourcesAndCompanionsWithoutExtractingOtherTrees()
    {
        string xml = NativeXmlValidationTests.NativeFixture().Replace("</Document>", "<Asset external='path'>assets/icon.bin</Asset></Document>");
        string deep = "outer/ProjectGraphics/" + string.Join("/", Enumerable.Repeat(new string('g', 55), 5)) + "/image.bin";
        var entries = new System.Collections.Generic.Dictionary<string, byte[]> {
            ["outer/TextLists/Selected.xml"] = NativeXmlValidationTests.Utf8(xml),
            ["outer/TextLists/Unused.xml"] = NativeXmlValidationTests.Utf8(NativeXmlValidationTests.NativeFixture().Replace("Messages_TL", "Unused")),
            ["outer/TextLists/assets/icon.bin"] = new byte[] { 0, 255, 7 },
            [deep] = Encoding.UTF8.GetBytes("unselected-asset"),
            ["outer/Screens/Unrelated.xml"] = Encoding.UTF8.GetBytes("Not parsed by the TextLists profile"),
            ["outer/ExportSummary.txt"] = Encoding.UTF8.GetBytes("OK     TextLists\nFAILED Screens\n") };
        var files = entries.Select(e => new TransferFile { Path = e.Key.Substring("outer/".Length), Bytes = e.Value.Length, Sha256 = TextListDocument.Hash(e.Value) }).ToArray();
        entries["outer/transfer-manifest.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = TransferManifest.Format, files }, JsonDefaults.Options));
        string archive = WriteEntries(entries);
        var result = JsonSerializer.SerializeToElement(TextListBundlePreparation.Prepare(archive, Destination, false, "V15.1", new[] { "Messages_TL" }), JsonDefaults.Options);
        Assert.Equal("verified-zip-stream", result.GetProperty("sourceAccess").GetProperty("mode").GetString());
        Assert.Equal(0, result.GetProperty("sourceAccess").GetProperty("sourceArchiveMembersExtracted").GetInt32());
        Assert.Equal(entries.Count, result.GetProperty("sourceAccess").GetProperty("sourceFilesVerified").GetInt32());
        string bundle = Path.Combine(Destination, "bundle"); var resource = Assert.Single(TransferBundle.Read(bundle));
        Assert.Equal("p:TextLists/n:Messages_TL", resource.Path);
        Assert.Equal(new byte[] { 0, 255, 7 }, File.ReadAllBytes(Path.Combine(bundle, resource.SourceDirectory, "assets/icon.bin")));
        Assert.DoesNotContain(TransferFiles.Inventory(Destination), f => f.Path.Contains("ProjectGraphics") || f.Path.Contains("Unused") || f.Path.Contains("Unrelated"));
        Assert.Contains(result.GetProperty("validation").GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "bundle.source-manifest" && c.GetProperty("status").GetString() == "passed");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnselectedArchiveCorruptionOrManifestMismatchStillBlocksPublication(bool manifestMismatch)
    {
        var entries = new System.Collections.Generic.Dictionary<string, byte[]> {
            ["TextLists/Messages.xml"] = NativeXmlValidationTests.Utf8(NativeXmlValidationTests.NativeFixture()),
            ["ProjectGraphics/asset.bin"] = Encoding.ASCII.GetBytes("unique-unselected-CRC-content") };
        if (manifestMismatch)
        {
            var files = entries.Select(e => new TransferFile { Path = e.Key, Bytes = e.Value.Length, Sha256 = TextListDocument.Hash(e.Value) }).ToArray();
            entries[TransferManifest.FileName] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = TransferManifest.Format, files }, JsonDefaults.Options));
            entries["ProjectGraphics/asset.bin"] = Encoding.ASCII.GetBytes("changed-but-valid-ZIP-member");
        }
        string archive = WriteEntries(entries);
        if (!manifestMismatch)
        {
            byte[] bytes = File.ReadAllBytes(archive), needle = entries["ProjectGraphics/asset.bin"];
            int offset = Enumerable.Range(0, bytes.Length - needle.Length).First(i => bytes.Skip(i).Take(needle.Length).SequenceEqual(needle));
            bytes[offset] ^= 1; File.WriteAllBytes(archive, bytes);
        }
        string hash = TextListDocument.Hash(File.ReadAllBytes(archive));
        Assert.Throws<InvalidDataException>(() => TextListBundlePreparation.Prepare(archive, Destination, false, "V15.1", new[] { "Messages_TL" }));
        Assert.False(Directory.Exists(Destination)); Assert.True(File.Exists(Destination + ".validation-failed.json"));
        Assert.Equal(hash, TextListDocument.Hash(File.ReadAllBytes(archive)));
    }
    [Fact] public void FolderArchiveCheckIsNotApplicableAndRequiredSkippedCheckCannotPublish()
    {
        Add(); var result = JsonSerializer.SerializeToElement(TextListBundlePreparation.Prepare(Source, Destination, false, "V15.1"), JsonDefaults.Options);
        var checks = result.GetProperty("validation").GetProperty("checks").EnumerateArray().ToArray();
        var input = checks.Single(c => c.GetProperty("id").GetString() == "bundle.archive-integrity");
        Assert.Equal("not-run", input.GetProperty("status").GetString()); Assert.Equal("not-applicable-folder-input", input.GetProperty("applicability").GetString());
        Assert.True(result.GetProperty("validation").GetProperty("canPublish").GetBoolean());
        var published = checks.Single(c => c.GetProperty("id").GetString() == "bundle.published-archive");
        Assert.Equal("passed", published.GetProperty("status").GetString()); Assert.Equal("required-for-selected-profile", published.GetProperty("applicability").GetString());
        var report = new HmiValidationReport(); report.Add("required", "not-run", "Required check was skipped", applicability: "required-for-selected-profile");
        Assert.False(report.CanPublish); Assert.Throws<InvalidDataException>(() => report.RequirePassed());
        var optional = new HmiValidationReport(); optional.Add("readback", "not-run", "No supplied native evidence");
        Assert.True(optional.CanPublish); Assert.Equal("optional-evidence-not-supplied", Assert.Single(optional.Checks).Applicability);
    }
    private string WriteEntries(System.Collections.Generic.Dictionary<string, byte[]> entries)
    {
        Directory.CreateDirectory(root); string archive = Path.Combine(root, "input.zip");
        using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create))
            foreach (var item in entries) { using var output = zip.CreateEntry(item.Key, CompressionLevel.NoCompression).Open(); output.Write(item.Value, 0, item.Value.Length); }
        return archive;
    }
    private static void CreateZip(string source, string archive)
    {
        using var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create);
        foreach (var file in TransferFiles.Inventory(source, false))
        { using var target = zip.CreateEntry(file.Path, CompressionLevel.NoCompression).Open(); using var input = File.OpenRead(Path.Combine(source, file.Path)); input.CopyTo(target); }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
