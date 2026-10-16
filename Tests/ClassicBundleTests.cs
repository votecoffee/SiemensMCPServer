using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SiemensTiaOpenness.Mcp;
using Xunit;

public sealed class ClassicBundleTests
{
    [Fact] public void ConvertsAllLegacyCategoriesInDependencyOrderAndKeepsSourceBytes()
    {
        using var fixture = new Fixture();
        var categories = new[] { "ProjectGraphics", "Connections", "Cycles", "VBScripts", "TagTables", "TextLists", "GraphicLists", "ScreenTemplates", "PopupScreens", "SlideInScreens", "PermanentArea", "Screens" };
        fixture.Resource("ProjectGraphics/Graphic/Image.xml", "Hmi.Globalization.MultiLingualGraphic", "Image");
        fixture.Resource("Connections/Conn.xml", "Hmi.Communication.Connection", "Conn");
        fixture.Resource("Cycles/Cycle.xml", "Hmi.Cycle.Cycle", "Cycle");
        fixture.Resource("VBScripts/Script.xml", "Hmi.RuntimeScripting.VBScript", "Script");
        fixture.Resource("TagTables/Table.xml", "Hmi.Tag.TagTable", "Table");
        fixture.Resource("TextLists/List.xml", "Hmi.TextGraphicList.TextList", "List");
        fixture.Resource("GraphicLists/List.xml", "Hmi.TextGraphicList.GraphicList", "List");
        fixture.Resource("ScreenTemplates/Template.xml", "Hmi.Screen.ScreenTemplate", "Template");
        fixture.Resource("PopupScreens/Popup.xml", "Hmi.Screen.ScreenPopup", "Popup");
        fixture.Resource("SlideInScreens/Left.xml", "Hmi.Screen.ScreenSlidein", null, "<SlideinType>Left</SlideinType>");
        fixture.Resource("PermanentArea/PermanentArea.xml", "Hmi.Screen.ScreenOverview", null);
        fixture.Resource("Screens/Screen.xml", "Hmi.Screen.Screen", "Screen");
        fixture.Summary(categories); string before = TransferFiles.Hash(TransferFiles.Inventory(fixture.Source));
        fixture.Prepare(categories);
        var entries = TransferBundle.Read(fixture.Destination); Assert.Equal(12, entries.Length);
        Assert.Equal("$projectGraphics", entries.First().ImportPath); Assert.Equal("p:ScreenFolder/p:Screens", entries.Last().ImportPath);
        Assert.Contains(entries, e => e.Path == "p:ScreenSlideinFolder/p:ScreenSlideins/s:Left");
        Assert.Contains(entries, e => e.ImportPath == "p:ScreenOverview" && e.Path == e.ImportPath);
        Assert.Equal(before, TransferFiles.Hash(TransferFiles.Inventory(fixture.Source)));
        Assert.All(entries, e => Assert.Single(Directory.GetFiles(TransferBundle.SafePath(fixture.Destination, e.SourceDirectory), "*.xml")));
    }
    [Fact] public void NestedFoldersUseNativeNamesAndSnapshotsWithoutRelocatingSidecars()
    {
        using var fixture = new Fixture(); fixture.Resource("Screens/A_B/Inner/S.xml", "Hmi.Screen.Screen", "S Ü"); fixture.Summary("Screens");
        fixture.Bytes("Screens/A_B/Inner/assets/image.png", new byte[] { 0, 255, 13, 10 });
        fixture.Prepare(new[] { "Screens" }, new[] { new ClassicFolderName { ExportedPath = "Screens/A_B", Name = "A/B" } });
        var entry = Assert.Single(TransferBundle.Read(fixture.Destination));
        Assert.Equal("p:ScreenFolder/p:Folders/n:A%2FB/p:Folders/n:Inner/p:Screens/n:S%20%C3%9C", entry.Path);
        var snapshots = TransferBundle.Snapshots(fixture.Destination); Assert.Equal(2, snapshots.Count);
        Assert.All(snapshots.Values, s => Assert.Equal("Siemens.Engineering.Hmi.Screen.ScreenUserFolder", s.NativeType));
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, File.ReadAllBytes(Path.Combine(TransferBundle.SafePath(fixture.Destination, entry.SourceDirectory), "assets/image.png")));
    }
    [Fact] public void SiblingResourcesAndChildFoldersDoNotGetCopiedAsXmlCompanions()
    {
        using var fixture = new Fixture(); fixture.Resource("Screens/A.xml", "Hmi.Screen.Screen", "A"); fixture.Resource("Screens/B.xml", "Hmi.Screen.Screen", "B");
        fixture.Resource("Screens/Child/C.xml", "Hmi.Screen.Screen", "C"); fixture.Bytes("Screens/Child/C.png", new byte[] { 1 }); fixture.Bytes("Screens/root.png", new byte[] { 2 }); fixture.Summary("Screens");
        fixture.Prepare(new[] { "Screens" });
        var entries = TransferBundle.Read(fixture.Destination);
        foreach (var entry in entries.Where(e => e.ImportPath == "p:ScreenFolder/p:Screens"))
        {
            string native = TransferBundle.SafePath(fixture.Destination, entry.SourceDirectory);
            Assert.Single(Directory.GetFiles(native, "*.xml", SearchOption.AllDirectories)); Assert.True(File.Exists(Path.Combine(native, "root.png"))); Assert.False(Directory.Exists(Path.Combine(native, "Child")));
        }
    }
    [Theory]
    [InlineData("FAILED Screens - device failed")][InlineData("OK     Screens\nFAILED Screens - ambiguous")][InlineData("OK     TextLists")]
    public void RejectsFailedMissingOrAmbiguousSelectedCategory(string summary)
    {
        using var fixture = new Fixture(); fixture.Resource("Screens/A.xml", "Hmi.Screen.Screen", "A"); fixture.SummaryText(summary);
        Assert.Throws<ArgumentException>(() => fixture.Prepare(new[] { "Screens" })); Assert.False(Directory.Exists(fixture.Destination));
    }
    [Fact] public void CanSelectSuccessfulCategoryFromPartiallyFailedExport()
    {
        using var fixture = new Fixture(); fixture.Resource("TextLists/A.xml", "Hmi.TextGraphicList.TextList", "A"); fixture.SummaryText("OK     TextLists\nFAILED Screens - unsupported");
        fixture.Prepare(new[] { "TextLists" }); Assert.Single(TransferBundle.Read(fixture.Destination));
    }
    [Fact] public void RejectsWrongTypeUnknownCategoryDuplicateNamesAndUnusedMappings()
    {
        using var fixture = new Fixture(); fixture.Resource("Screens/A.xml", "Hmi.Tag.TagTable", "A"); fixture.Summary("Screens");
        Assert.Throws<ArgumentException>(() => fixture.Prepare(new[] { "Unknown" }));
        Assert.Throws<ArgumentException>(() => fixture.Prepare(new[] { "Screens", "Screens" }));
        Assert.Throws<ArgumentException>(() => fixture.Prepare(new[] { "Screens" }));
        fixture.Resource("Screens/A.xml", "Hmi.Screen.Screen", "A");
        Assert.Throws<ArgumentException>(() => fixture.Prepare(new[] { "Screens" }, new[] { new ClassicFolderName { ExportedPath = "Screens/Missing", Name = "A" } }));
        fixture.Resource("Screens/B.xml", "Hmi.Screen.Screen", "A"); Assert.Throws<ArgumentException>(() => fixture.Prepare(new[] { "Screens" }));
        Assert.False(Directory.Exists(fixture.Destination));
    }
    [Fact] public void RejectsInPlaceOutputAndUnsafeOverrideBeforeCreatingArtifacts()
    {
        using var fixture = new Fixture(); fixture.Resource("Screens/A.xml", "Hmi.Screen.Screen", "A"); fixture.Summary("Screens");
        Assert.Throws<ArgumentException>(() => ClassicExportBundle.Prepare(fixture.Source, Path.Combine(fixture.Source, "nested"), new[] { "Screens" }, Array.Empty<ClassicFolderName>()));
        Assert.Throws<ArgumentException>(() => fixture.Prepare(new[] { "Screens" }, new[] { new ClassicFolderName { ExportedPath = "Screens/../other", Name = "A" } }));
        Assert.False(Directory.Exists(fixture.Destination));
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "classic-bundle-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(root, "source"); public string Destination => Path.Combine(root, "converted");
        public Fixture() { Directory.CreateDirectory(Source); }
        public void Resource(string path, string type, string? name, string extra = "") => Bytes(path, System.Text.Encoding.UTF8.GetBytes("<Document><Engineering version='V15.1'/><" + type + " ID='0'><AttributeList>" + (name == null ? "" : "<Name>" + name + "</Name>") + extra + "</AttributeList></" + type + "></Document>"));
        public void Bytes(string path, byte[] bytes) { string full = Path.Combine(Source, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes); }
        public void Summary(params string[] categories) => SummaryText(string.Join("\n", categories.Select(c => "OK     " + c)));
        public void SummaryText(string summary) => File.WriteAllText(Path.Combine(Source, "ExportSummary.txt"), summary);
        public void Prepare(string[] categories, ClassicFolderName[]? mappings = null) => ClassicExportBundle.Prepare(Source, Destination, categories, mappings ?? Array.Empty<ClassicFolderName>());
        public void Dispose() { Directory.Delete(root, true); }
    }
}
