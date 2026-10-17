using System;
using System.IO;
using System.Text.Json;
using SiemensMCPServer;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class DesktopTransferTests
{
    [Theory]
    [InlineData("plc", "p:BlockGroup/p:Blocks")]
    [InlineData("plc", "p:TypeGroup/p:Groups/n:Folder/p:Types")]
    [InlineData("plc", "p:TagTableGroup/p:TagTables")]
    [InlineData("plc", "p:ExternalSourceGroup")]
    [InlineData("unified", "p:ScreenFolder/p:Screens/n:Screen%2FOne")]
    public void AllowsAdvertisedOfflineResourcePaths(string family, string path) => DesktopTransferSelection.ValidatePath(family, path);

    [Theory]
    [InlineData("plc", "p:Parent")]
    [InlineData("plc", "p:OnlineProvider")]
    [InlineData("plc", "$projectGraphics")]
    [InlineData("plc", "p:BlockGroup//p:Blocks")]
    [InlineData("unified", "../p:Scripts")]
    [InlineData("classic", "")]
    public void RejectsInvalidFamiliesPathsAndPlcRootNavigators(string family, string path) => Assert.Throws<ArgumentException>(() => DesktopTransferSelection.ValidatePath(family, path));

    [Fact] public void BundleRequiresMatchingFamilyAndResourceType()
    {
        using var bundle = new Bundle("plc", "Siemens.Engineering.SW.Blocks.FC", "p:BlockGroup/p:Blocks");
        Assert.Single(DesktopTransferSelection.ReadBundle(bundle.Path, "plc", "", false));
        Assert.Throws<ArgumentException>(() => DesktopTransferSelection.ReadBundle(bundle.Path, "unified", "", true));
        bundle.Index("plc", "Siemens.Engineering.HmiUnified.HmiTags.HmiTag", "p:BlockGroup/p:Blocks");
        Assert.Throws<ArgumentException>(() => DesktopTransferSelection.ReadBundle(bundle.Path, "plc", "", true));
    }
    [Fact] public void BundleCannotImportOutsideSelectedSubtreeOrIntoSimilarPrefix()
    {
        using var bundle = new Bundle("plc", "Siemens.Engineering.SW.Blocks.FC", "p:BlockGroup/p:Blocks");
        Assert.Single(DesktopTransferSelection.ReadBundle(bundle.Path, "plc", "p:BlockGroup", true));
        Assert.Single(DesktopTransferSelection.ReadBundle(bundle.Path, "plc", "p:BlockGroup/p:Blocks", true));
        Assert.Throws<ArgumentException>(() => DesktopTransferSelection.ReadBundle(bundle.Path, "plc", "p:TypeGroup", true));
        Assert.Throws<ArgumentException>(() => DesktopTransferSelection.ReadBundle(bundle.Path, "plc", "p:BlockGroup/p:Block", true));
    }
    [Fact] public void UnifiedRequiresExplicitReplacementAcknowledgment()
    {
        using var bundle = new Bundle("unified", "Siemens.Engineering.HmiUnified.HmiTags.HmiTagComposition", "p:Tags");
        Assert.Throws<ArgumentException>(() => DesktopTransferSelection.ReadBundle(bundle.Path, "unified", "", false));
        Assert.Single(DesktopTransferSelection.ReadBundle(bundle.Path, "unified", "", true));
    }
    [Fact] public void FailedExportsAndMissingNativeCompanionsCannotBeImported()
    {
        using var bundle = new Bundle("plc", "Siemens.Engineering.SW.Blocks.FC", "p:BlockGroup/p:Blocks");
        bundle.Index("plc", "Siemens.Engineering.SW.Blocks.FC", "p:BlockGroup/p:Blocks", true);
        Assert.Throws<ArgumentException>(() => DesktopTransferSelection.ReadBundle(bundle.Path, "plc", "", false));
        bundle.Index("plc", "Siemens.Engineering.SW.Blocks.FC", "p:BlockGroup/p:Blocks");
        File.WriteAllText(System.IO.Path.Combine(bundle.Path, "native", "payload.xml"), "<Document><File external=\"path\">missing.bin</File></Document>");
        Assert.Throws<FileNotFoundException>(() => DesktopTransferSelection.ReadBundle(bundle.Path, "plc", "", false));
    }
    private sealed class Bundle : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "desktop-transfer-" + Guid.NewGuid().ToString("N"));
        public Bundle(string family, string type, string destination)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "native"));
            File.WriteAllText(System.IO.Path.Combine(Path, "native", "payload.xml"), "<Document />");
            Index(family, type, destination);
        }
        public void Index(string family, string type, string destination, bool failed = false) => File.WriteAllText(System.IO.Path.Combine(Path, "export-index.json"), JsonSerializer.Serialize(new {
            format = "tia-engineering-export-v1", family,
            entries = new[] { new TransferResource { Path = destination + "/n:Resource", ImportPath = destination, NativeType = type, SourceDirectory = "native" } },
            failures = failed ? new[] { "export failed" } : Array.Empty<string>()
        }, JsonDefaults.Options));
        public void Dispose() { Directory.Delete(Path, true); }
    }
}
