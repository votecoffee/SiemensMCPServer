using System;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ClassicLineLayoutTests
{
    private static string Item(string type, string name, int left, int width, int height) => $"<{type} CompositionName='ScreenItems'><AttributeList><ObjectName>{name}</ObjectName><Left>{left}</Left><Top>20</Top><Width>{width}</Width><Height>{height}</Height><LineColor>1, 2, 3</LineColor></AttributeList><ObjectList><MultilingualText CompositionName='ToolTipText'><ObjectList><MultilingualTextItem><AttributeList><Culture>de-DE</Culture><Text>Linie</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></{type}>";
    private static string Line(string name, int left, int width, int height) => Item("Hmi.Screen.Line", name, left, width, height);
    private static string Screen(string items) => "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Screen</Name></AttributeList><ObjectList>" + items + "</ObjectList></Hmi.Screen.Screen></Document>";
    private static JsonElement Catalog(string xml) => FeatureOperations.Execute("comfort", "inspect_screen_layout", ExtensionWire.Json(new { xml }));
    private static ClassicLayoutSelection[] Select(string xml) => Catalog(xml).GetProperty("items").EnumerateArray().Select(r => ExtensionWire.Parse<ClassicLayoutSelection>(r.GetProperty("selection"))).ToArray();
    private static XDocument Arrange(string xml, ClassicLayoutRequest request) => XDocument.Parse(FeatureOperations.Execute("comfort", "arrange_screen_items", ExtensionWire.Json(new { snapshot = xml, arguments = request })).GetProperty("candidateXml").GetString()!);

    [Theory] [InlineData(80, 0)] [InlineData(0, 80)] [InlineData(40, 80)]
    public void NativeLinesCanMoveWithoutChangingExtentsStylesOrTranslations(int width, int height)
    {
        string xml = Screen(Line("Line", 10, width, height)); var row = Catalog(xml).GetProperty("items")[0];
        Assert.True(row.GetProperty("geometryAvailable").GetBoolean()); Assert.Equal("Line", row.GetProperty("name").GetString());
        var result = Arrange(xml, new ClassicLayoutRequest { Action = "move", Items = Select(xml), DeltaX = -5, DeltaY = 7 });
        var expected = XDocument.Parse(xml); expected.Descendants("Left").Single().Value = "5"; expected.Descendants("Top").Single().Value = "27";
        Assert.True(XNode.DeepEquals(expected, result));
    }
    [Theory] [InlineData(0, 80)] [InlineData(40, 0)]
    public void ResizeCanExplicitlyMakeADiagonalLineHorizontalOrVertical(int width, int height)
    {
        string xml = Screen(Line("Line", 10, 40, 80)); var result = Arrange(xml, new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Width = width, Height = height });
        Assert.Equal(width.ToString(), result.Descendants("Width").Single().Value); Assert.Equal(height.ToString(), result.Descendants("Height").Single().Value);
        Assert.Equal("10", result.Descendants("Left").Single().Value); Assert.Equal("20", result.Descendants("Top").Single().Value);
        Assert.True(Catalog(result.ToString()).GetProperty("items")[0].GetProperty("geometryAvailable").GetBoolean());
    }
    [Theory] [InlineData(0, 0)] [InlineData(-1, 80)] [InlineData(80, -1)] [InlineData(1000001, 0)]
    public void InvalidLineDimensionsRemainInspectionOnly(int width, int height)
    {
        var row = Catalog(Screen(Line("Line", 10, width, height))).GetProperty("items")[0];
        Assert.False(row.GetProperty("geometryAvailable").GetBoolean()); Assert.Equal(JsonValueKind.Null, row.GetProperty("selection").ValueKind);
    }
    [Theory] [InlineData("Hmi.Screen.Button")] [InlineData("Hmi.Screen.Rectangle")] [InlineData("Hmi.Screen.UnknownLine")]
    public void OnlyTheExactNativeLineTypePermitsZeroExtents(string type)
    {
        Assert.False(Catalog(Screen(Item(type, "Control", 10, 80, 0))).GetProperty("items")[0].GetProperty("geometryAvailable").GetBoolean());
        string xml = Screen(Item(type, "Control", 10, 80, 30));
        Assert.Throws<ArgumentException>(() => Arrange(xml, new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Height = 0 }));
    }
    [Fact] public void ZeroLengthResultsAreRejectedBeforeAnyScalarChanges()
    {
        string xml = Screen(Line("Line", 10, 80, 0)); var document = new ClassicXmlDocument(xml); string before = JsonSerializer.Serialize(document.LayoutCatalog(0, 200));
        Assert.Throws<ArgumentException>(() => document.ArrangeLayout(new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Width = 0 }));
        Assert.Equal(before, JsonSerializer.Serialize(document.LayoutCatalog(0, 200)));
        Assert.Throws<ArgumentException>(() => Arrange(xml, new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Width = 0, Height = 0 }));
    }
    [Fact] public void MixedBatchCannotApplyZeroDimensionsToANonLineControl()
    {
        string xml = Screen(Line("Line", 10, 40, 80) + Item("Hmi.Screen.Button", "Button", 100, 80, 30));
        var document = new ClassicXmlDocument(xml); string before = JsonSerializer.Serialize(document.LayoutCatalog(0, 200));
        Assert.Throws<ArgumentException>(() => document.ArrangeLayout(new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Width = 0 }));
        Assert.Equal(before, JsonSerializer.Serialize(document.LayoutCatalog(0, 200)));
    }
    [Fact] public void AlignmentAccountsForZeroWidthAnchorWithoutChangingLineDimensions()
    {
        string xml = Screen(Line("Vertical", 10, 0, 80) + Line("Horizontal", 100, 80, 0)); var items = Select(xml);
        var result = Arrange(xml, new ClassicLayoutRequest { Action = "align", Items = items, AnchorXPath = items[0].XPath, Alignment = "horizontalCenter" });
        Assert.Equal(new[] { "10", "-30" }, result.Descendants("Left").Select(e => e.Value)); Assert.Equal(new[] { "0", "80" }, result.Descendants("Width").Select(e => e.Value));
    }
    [Fact] public void DistributionPreservesOuterPositionsAndEqualGapsForZeroWidthLines()
    {
        string xml = Screen(Line("First", 10, 0, 80) + Line("Middle", 31, 0, 80) + Line("Last", 80, 0, 80));
        var result = Arrange(xml, new ClassicLayoutRequest { Action = "distribute", Items = Select(xml), Axis = "horizontal" });
        Assert.Equal(new[] { "10", "45", "80" }, result.Descendants("Left").Select(e => e.Value)); Assert.All(result.Descendants("Width"), e => Assert.Equal("0", e.Value));
    }
    [Fact] public void ZeroExtentSelectionsStillRequireExactFreshBounds()
    {
        string xml = Screen(Line("Line", 10, 80, 0)); var items = Select(xml); items[0].ExpectedBounds.Height = 1;
        Assert.Throws<InvalidOperationException>(() => Arrange(xml, new ClassicLayoutRequest { Action = "move", Items = items, DeltaX = 1, DeltaY = 0 }));
    }
}
