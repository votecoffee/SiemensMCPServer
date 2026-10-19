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
    private static string Line(string name, int left, int width, int height) => Item("Hmi.Screen.Line", name, left, width, height).Replace("</AttributeList><ObjectList>", $"<StartLeft>{left}</StartLeft><StartTop>20</StartTop><EndLeft>{(long)left + width}</EndLeft><EndTop>{20L + height}</EndTop></AttributeList><ObjectList>");
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
        expected.Descendants("StartLeft").Single().Value = "5"; expected.Descendants("EndLeft").Single().Value = (5 + width).ToString();
        expected.Descendants("StartTop").Single().Value = "27"; expected.Descendants("EndTop").Single().Value = (27 + height).ToString();
        Assert.True(XNode.DeepEquals(expected, result));
    }
    [Theory] [InlineData(0, 80)] [InlineData(40, 0)]
    public void ResizeCanExplicitlyMakeADiagonalLineHorizontalOrVertical(int width, int height)
    {
        string xml = Screen(Line("Line", 10, 40, 80)); var result = Arrange(xml, new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Width = width, Height = height });
        Assert.Equal(width.ToString(), result.Descendants("Width").Single().Value); Assert.Equal(height.ToString(), result.Descendants("Height").Single().Value);
        Assert.Equal("10", result.Descendants("Left").Single().Value); Assert.Equal("20", result.Descendants("Top").Single().Value);
        Assert.Equal((10 + width).ToString(), result.Descendants("EndLeft").Single().Value); Assert.Equal((20 + height).ToString(), result.Descendants("EndTop").Single().Value);
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
    [Theory]
    [InlineData("<StartLeft>10</StartLeft>", "")]
    [InlineData("<StartLeft>10</StartLeft>", "<StartLeft>10</StartLeft><StartLeft>10</StartLeft>")]
    [InlineData("<StartLeft>10</StartLeft>", "<StartLeft><Value>10</Value></StartLeft>")]
    [InlineData("<StartLeft>10</StartLeft>", "<StartLeft unit='px'>10</StartLeft>")]
    [InlineData("<StartLeft>10</StartLeft>", "<StartLeft>2147483648</StartLeft>")]
    [InlineData("<StartLeft>10</StartLeft>", "<StartLeft>1000001</StartLeft>")]
    [InlineData("<StartLeft>10</StartLeft>", "<StartLeft>11</StartLeft>")]
    [InlineData("<EndTop>20</EndTop>", "<EndTop>21</EndTop>")]
    public void IncompleteMalformedOrInconsistentEndpointsAreInspectionOnly(string from, string to)
    {
        string xml = Screen(Line("Line", 10, 80, 0)).Replace(from, to); var row = Catalog(xml).GetProperty("items")[0];
        Assert.False(row.GetProperty("geometryAvailable").GetBoolean()); Assert.Equal(JsonValueKind.Null, row.GetProperty("selection").ValueKind);
        var selection = new ClassicLayoutSelection { XPath = row.GetProperty("xPath").GetString()!, ExpectedBounds = new ClassicLayoutBounds { Left = 10, Top = 20, Width = 80, Height = 0 } };
        Assert.Throws<ArgumentException>(() => Arrange(xml, new ClassicLayoutRequest { Action = "move", Items = new[] { selection }, DeltaX = 1, DeltaY = 0 }));
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void MoveAndResizeRetainEachEndpointDirection(bool reverseX, bool reverseY)
    {
        var document = XDocument.Parse(Screen(Line("Line", 10, 40, 80)));
        if (reverseX) { document.Descendants("StartLeft").Single().Value = "50"; document.Descendants("EndLeft").Single().Value = "10"; }
        if (reverseY) { document.Descendants("StartTop").Single().Value = "100"; document.Descendants("EndTop").Single().Value = "20"; }
        string xml = document.ToString();
        var moved = Arrange(xml, new ClassicLayoutRequest { Action = "move", Items = Select(xml), DeltaX = 3, DeltaY = -4 });
        Assert.Equal(reverseX ? "53" : "13", moved.Descendants("StartLeft").Single().Value);
        Assert.Equal(reverseY ? "96" : "16", moved.Descendants("StartTop").Single().Value);
        var resized = Arrange(xml, new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Width = 60, Height = 30 });
        Assert.Equal(reverseX ? "70" : "10", resized.Descendants("StartLeft").Single().Value);
        Assert.Equal(reverseX ? "10" : "70", resized.Descendants("EndLeft").Single().Value);
        Assert.Equal(reverseY ? "50" : "20", resized.Descendants("StartTop").Single().Value);
        Assert.Equal(reverseY ? "20" : "50", resized.Descendants("EndTop").Single().Value);
        Assert.True(Catalog(moved.ToString()).GetProperty("items")[0].GetProperty("geometryAvailable").GetBoolean());
        Assert.True(Catalog(resized.ToString()).GetProperty("items")[0].GetProperty("geometryAvailable").GetBoolean());
    }
    [Fact] public void ExpandingZeroAxesUsesExplicitAscendingEndpointConvention()
    {
        string xml = Screen(Line("Line", 10, 0, 80));
        var result = Arrange(xml, new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Width = 40 });
        Assert.Equal("10", result.Descendants("StartLeft").Single().Value); Assert.Equal("50", result.Descendants("EndLeft").Single().Value);
    }
    [Fact] public void EndpointOverflowIsRejectedBeforeAnyEdits()
    {
        string xml = Screen(Line("Line", 999990, 10, 0)); var document = new ClassicXmlDocument(xml); string before = JsonSerializer.Serialize(document.LayoutCatalog(0, 200));
        Assert.Throws<ArgumentException>(() => document.ArrangeLayout(new ClassicLayoutRequest { Action = "move", Items = Select(xml), DeltaX = 1, DeltaY = 0 }));
        Assert.Throws<ArgumentException>(() => document.ArrangeLayout(new ClassicLayoutRequest { Action = "resize", Items = Select(xml), Width = 11 }));
        Assert.Equal(before, JsonSerializer.Serialize(document.LayoutCatalog(0, 200)));
    }
    [Fact] public void ScalarEditsCannotAuthorInconsistentLineBoundsAndRestoreRejectedChanges()
    {
        string xml = Screen(Line("Line", 10, 80, 0)); var document = new ClassicXmlDocument(xml);
        string before = JsonSerializer.Serialize(document.LayoutCatalog(0, 200));
        var edit = new ClassicXmlEdit { XPath = "//Hmi.Screen.Line/AttributeList/Left", ExpectedValue = "10", NewValue = "11" };
        Assert.Throws<ArgumentException>(() => document.Edit(new[] { edit }));
        Assert.Equal(before, JsonSerializer.Serialize(document.LayoutCatalog(0, 200)));
        var candidate = document.Edit(new[] { edit,
            new ClassicXmlEdit { XPath = "//Hmi.Screen.Line/AttributeList/StartLeft", ExpectedValue = "10", NewValue = "11" },
            new ClassicXmlEdit { XPath = "//Hmi.Screen.Line/AttributeList/EndLeft", ExpectedValue = "90", NewValue = "91" } });
        Assert.True(Catalog(candidate).GetProperty("items")[0].GetProperty("geometryAvailable").GetBoolean());
    }
}
