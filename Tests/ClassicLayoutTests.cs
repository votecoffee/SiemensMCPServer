using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ClassicLayoutTests
{
    private static string Item(string name, int left, int top, int width, int height) =>
        $"<Hmi.Screen.Button CompositionName='ScreenItems'><AttributeList><Name>{name}</Name><Left>{left}</Left><Top>{top}</Top><Width>{width}</Width><Height>{height}</Height><ForeColor>1, 2, 3</ForeColor></AttributeList><ObjectList><MultilingualText CompositionName='Text'><ObjectList><MultilingualTextItem><AttributeList><Culture>de-DE</Culture><Text>Ü &amp; 😀</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Screen.Button>";
    private static string Screen(string items) => "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Test</Name><Width>800</Width><Height>480</Height></AttributeList><ObjectList><Hmi.Screen.ScreenLayer CompositionName='Layers'><AttributeList><Name>Layer</Name></AttributeList><ObjectList>" + items + "</ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>";
    private static string Xml => Screen(Item("A", 10, 20, 20, 10) + Item("B", 51, 60, 10, 30) + Item("C", 110, 120, 30, 20));
    private static JsonElement Catalog(string xml) => ExtensionWire.Json(new ClassicXmlDocument(xml).LayoutCatalog(0, 200));
    private static ClassicLayoutSelection[] Selections(string xml) => Catalog(xml).GetProperty("items").EnumerateArray()
        .Select(i => ExtensionWire.Parse<ClassicLayoutSelection>(i.GetProperty("selection"))).ToArray();
    private static ClassicLayoutRequest Move(string xml, int x = 5, int y = -7) => new ClassicLayoutRequest { Action = "move", Items = Selections(xml), DeltaX = x, DeltaY = y };
    private static JsonElement Arrange(string xml, ClassicLayoutRequest request) => ExtensionWire.Json(new ClassicXmlDocument(xml).ArrangeLayout(request));
    private static XDocument Candidate(JsonElement result) => XDocument.Parse(result.GetProperty("candidateXml").GetString()!);
    private static int[] Values(XDocument doc, string field) => doc.Descendants("Hmi.Screen.Button").Select(i => int.Parse(i.Element("AttributeList")!.Element(field)!.Value)).ToArray();

    [Fact] public void CatalogReturnsReusableExactSelectionsAndPages()
    {
        var catalog = Catalog(Xml); Assert.Equal(3, catalog.GetProperty("totalItems").GetInt32());
        var rows = catalog.GetProperty("items").EnumerateArray().ToArray();
        Assert.All(rows, i => { Assert.True(i.GetProperty("geometryAvailable").GetBoolean()); Assert.DoesNotContain("@ID", i.GetProperty("xPath").GetString()); });
        Assert.Equal(3, rows.Select(i => i.GetProperty("xPath").GetString()).Distinct().Count());
        var page = ExtensionWire.Json(new ClassicXmlDocument(Xml).LayoutCatalog(1, 1));
        Assert.Equal("B", page.GetProperty("items")[0].GetProperty("name").GetString()); Assert.True(page.GetProperty("hasMore").GetBoolean()); Assert.Equal(2, page.GetProperty("nextOffset").GetInt32());
        Assert.Empty(ExtensionWire.Json(new ClassicXmlDocument(Xml).LayoutCatalog(10, 1)).GetProperty("items").EnumerateArray());
    }
    [Fact] public void MoveChangesOnlySelectedGeometryAndPreservesEveryOtherNode()
    {
        var request = Move(Xml); request.Items = request.Items.Take(2).ToArray(); var result = Arrange(Xml, request);
        var expected = XDocument.Parse(Xml);
        foreach (var item in expected.Descendants("Hmi.Screen.Button").Take(2))
        { var a = item.Element("AttributeList")!; a.Element("Left")!.Value = (int.Parse(a.Element("Left")!.Value) + 5).ToString(); a.Element("Top")!.Value = (int.Parse(a.Element("Top")!.Value) - 7).ToString(); }
        Assert.True(XNode.DeepEquals(expected, Candidate(result)));
        Assert.Equal(4, result.GetProperty("changes").GetProperty("edits").GetArrayLength());
        Assert.Equal(10, result.GetProperty("changes").GetProperty("items")[0].GetProperty("before").GetProperty("left").GetInt32());
        Assert.Equal(15, result.GetProperty("changes").GetProperty("items")[0].GetProperty("after").GetProperty("left").GetInt32());
    }
    [Theory]
    [InlineData("left", "Left", 10)][InlineData("right", "Left", 20)][InlineData("horizontalCenter", "Left", 15)]
    [InlineData("top", "Top", 20)][InlineData("bottom", "Top", 0)][InlineData("verticalCenter", "Top", 10)]
    public void AlignUsesExplicitSelectedAnchor(string alignment, string field, int value)
    {
        var items = Selections(Xml); var result = Arrange(Xml, new ClassicLayoutRequest { Action = "align", Items = items, AnchorXPath = items[0].XPath, Alignment = alignment });
        var doc = Candidate(result); Assert.Equal(value, Values(doc, field)[1]);
        Assert.True(XNode.DeepEquals(XDocument.Parse(Xml).Descendants("Hmi.Screen.Button").First(), doc.Descendants("Hmi.Screen.Button").First()));
    }
    [Theory]
    [InlineData(0, 1)][InlineData(-1, -1)]
    public void CenterAlignmentRoundsHalfPixelsAwayFromZero(int anchorLeft, int expected)
    {
        string xml = Screen(Item("A", anchorLeft, 0, 11, 10) + Item("B", 10, 0, 10, 10)); var items = Selections(xml);
        var result = Arrange(xml, new ClassicLayoutRequest { Action = "align", Items = items, AnchorXPath = items[0].XPath, Alignment = "horizontalCenter" });
        Assert.Equal(expected, Values(Candidate(result), "Left")[1]);
    }
    [Fact] public void DistributionUsesEqualGapsWithUnequalSizesAndFixedOuterEdges()
    {
        var result = Arrange(Xml, new ClassicLayoutRequest { Action = "distribute", Items = Selections(Xml).Reverse().ToArray(), Axis = "horizontal" });
        Assert.Equal(new[] { 10, 65, 110 }, Values(Candidate(result), "Left"));
        Assert.Equal(new[] { 20, 60, 120 }, Values(Candidate(result), "Top"));
    }
    [Fact] public void VerticalDistributionRoundsFractionalGapsDeterministically()
    {
        string xml = Screen(Item("A", 0, 0, 10, 10) + Item("B", 0, 12, 10, 10) + Item("C", 0, 31, 10, 10));
        var result = Arrange(xml, new ClassicLayoutRequest { Action = "distribute", Items = Selections(xml), Axis = "vertical" });
        Assert.Equal(new[] { 0, 16, 31 }, Values(Candidate(result), "Top"));
    }
    [Fact] public void ResizeCanChangeOneDimensionAndRetainsPositions()
    {
        var result = Arrange(Xml, new ClassicLayoutRequest { Action = "resize", Items = Selections(Xml), Width = 80 });
        Assert.Equal(new[] { 80, 80, 80 }, Values(Candidate(result), "Width")); Assert.Equal(new[] { 10, 30, 20 }, Values(Candidate(result), "Height"));
        Assert.Equal(new[] { 10, 51, 110 }, Values(Candidate(result), "Left"));
    }
    [Fact] public void StaleDuplicateMissingAndEquivalentButUnreturnedPathsFailBeforeMutation()
    {
        var doc = new ClassicXmlDocument(Xml); var stale = Move(Xml); stale.Items[2].ExpectedBounds.Width++;
        Assert.Throws<InvalidOperationException>(() => doc.ArrangeLayout(stale));
        Assert.Equal(new[] { 15, 56, 115 }, Values(Candidate(ExtensionWire.Json(doc.ArrangeLayout(Move(Xml)))), "Left"));
        var duplicate = Move(Xml); duplicate.Items[1] = duplicate.Items[0]; Assert.Throws<ArgumentException>(() => Arrange(Xml, duplicate));
        var missing = Move(Xml); missing.Items[0].XPath = "//Hmi.Screen.Button[1]"; Assert.Throws<ArgumentException>(() => Arrange(Xml, missing));
    }
    [Fact] public void RejectsDifferentCoordinateContainersAndAncestorDescendantSelections()
    {
        string xml = Xml.Replace("</Hmi.Screen.Screen>", "<ObjectList>" + Item("OtherLayer", 10, 10, 10, 10) + "</ObjectList></Hmi.Screen.Screen>");
        var items = Selections(xml); Assert.Throws<ArgumentException>(() => Arrange(xml, new ClassicLayoutRequest { Action = "align", Items = items, AnchorXPath = items[0].XPath, Alignment = "left" }));
        Assert.Throws<ArgumentException>(() => Arrange(xml, new ClassicLayoutRequest { Action = "distribute", Items = items, Axis = "horizontal" }));
        xml = Screen(Item("Outer", 1, 1, 10, 10).Replace("</Hmi.Screen.Button>", "<ObjectList>" + Item("Inner", 1, 1, 10, 10) + "</ObjectList></Hmi.Screen.Button>"));
        Assert.Throws<ArgumentException>(() => Arrange(xml, Move(xml)));
    }
    [Fact] public void UnsupportedGeometryIsReportedAndCannotBeSelected()
    {
        foreach (string xml in new[] { Xml.Replace("<Width>20</Width>", ""), Xml.Replace("<Left>10</Left>", "<Left>1.5</Left>"), Xml.Replace("<Height>10</Height>", "<Height>0</Height>"), Xml.Replace("<Left>10</Left>", "<Left><Value>10</Value></Left>"), Xml.Replace("<Left>10</Left>", "<Left>10</Left><Left>10</Left>") })
        {
            var row = Catalog(xml).GetProperty("items")[0]; Assert.False(row.GetProperty("geometryAvailable").GetBoolean()); Assert.Equal(JsonValueKind.Null, row.GetProperty("selection").ValueKind);
            var request = Move(Xml); Assert.Throws<ArgumentException>(() => Arrange(xml, request));
        }
    }
    [Fact] public void RejectsNoOpsOverflowInvalidDimensionsAndInsufficientDistributionSpace()
    {
        Assert.Throws<ArgumentException>(() => Arrange(Xml, Move(Xml, 0, 0)));
        Assert.Throws<ArgumentException>(() => Arrange(Xml, Move(Xml, 1000000, 0)));
        Assert.Throws<ArgumentException>(() => Arrange(Xml, Move(Xml, int.MinValue, 0)));
        foreach (int width in new[] { 0, -1, 1000001 }) Assert.Throws<ArgumentException>(() => Arrange(Xml, new ClassicLayoutRequest { Action = "resize", Items = Selections(Xml), Width = width }));
        string crowded = Screen(Item("A", 0, 0, 20, 10) + Item("B", 5, 0, 20, 10) + Item("C", 10, 0, 20, 10));
        Assert.Throws<ArgumentException>(() => Arrange(crowded, new ClassicLayoutRequest { Action = "distribute", Items = Selections(crowded), Axis = "horizontal" }));
    }
    [Fact] public void WrongActionParametersCountsAndUnselectedAnchorFail()
    {
        var request = Move(Xml); request.Width = 50; Assert.Throws<ArgumentException>(() => Arrange(Xml, request));
        request = Move(Xml); request.DeltaX = null; Assert.Throws<ArgumentException>(() => Arrange(Xml, request));
        Assert.Throws<ArgumentException>(() => Arrange(Xml, new ClassicLayoutRequest { Action = "align", Items = Selections(Xml), Alignment = "left", AnchorXPath = "missing" }));
        Assert.Throws<ArgumentException>(() => Arrange(Xml, new ClassicLayoutRequest { Action = "distribute", Items = Selections(Xml).Take(2).ToArray(), Axis = "horizontal" }));
        Assert.Throws<ArgumentException>(() => Arrange(Xml, new ClassicLayoutRequest { Action = "resize", Items = Selections(Xml) }));
        request = Move(Xml); request.Items = Enumerable.Repeat(request.Items[0], 101).ToArray(); Assert.Throws<ArgumentException>(() => Arrange(Xml, request));
    }
    [Fact] public void WorkerRoutesAndStrictInputsAreCompatibleWithExistingHostContract()
    {
        var descriptors = FeatureOperations.Describe("comfort"); Assert.All(descriptors, d => Assert.True(d.IsCompatible("comfort")));
        var inspection = descriptors.Single(o => o.Name == "inspect_screen_layout"); Assert.Equal("local", inspection.Route); Assert.False(inspection.Preview);
        var arrangement = descriptors.Single(o => o.Name == "arrange_screen_items"); Assert.Equal("classicXml", arrangement.Route); Assert.True(arrangement.Preview);
        var catalog = FeatureOperations.Execute("comfort", "inspect_screen_layout", ExtensionWire.Json(new { xml = Xml, maxItems = 1 })); Assert.True(catalog.GetProperty("hasMore").GetBoolean());
        var output = FeatureOperations.Execute("comfort", "arrange_screen_items", ExtensionWire.Json(new { snapshot = Xml, arguments = Move(Xml) })); Assert.Equal(15, Values(Candidate(output), "Left")[0]);
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "inspect_screen_layout", ExtensionWire.Json(new { xml = Xml, unexpected = true })));
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "arrange_screen_items", ExtensionWire.Json(new { snapshot = Xml, arguments = new { action = "move", items = new[] { new { xPath = "x", expectedBounds = new { left = 1 } } }, deltaX = 1, deltaY = 1 } })));
    }
    [Fact] public void RejectsNonScreensInvalidPagingAndOverDeepXml()
    {
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument("<Document><Hmi.Tag.TagTable/></Document>").LayoutCatalog(0, 1));
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(Xml).LayoutCatalog(-1, 1)); Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(Xml).LayoutCatalog(0, 201));
        string deep = Screen(string.Concat(Enumerable.Repeat("<ObjectList>", 65)) + Item("Deep", 1, 1, 10, 10) + string.Concat(Enumerable.Repeat("</ObjectList>", 65)));
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(deep).LayoutCatalog(0, 1));
    }
    [Fact] public void LayoutPreviewStagesSidecarsAndDetectsCandidateTampering()
    {
        using var backend = new Backend(); var resource = backend.ReadClassicResource("hmi", "screen"); var workflow = new ClassicXmlWorkflow(backend, Path.Combine(backend.Root, "plans"));
        var plan = ExtensionWire.Json(workflow.PreviewTransformed("hmi", "screen", resource.Fingerprint,
            xml => FeatureOperations.Execute("comfort", "arrange_screen_items", ExtensionWire.Json(new { snapshot = xml, arguments = Move(xml) })).GetProperty("candidateXml").GetString()!, new { action = "move" }));
        string candidate = Path.Combine(plan.GetProperty("artifactDirectory").GetString()!, "candidate-files");
        Assert.Equal(new byte[] { 0, 255, 1 }, File.ReadAllBytes(Path.Combine(candidate, "image.bin")));
        Assert.Equal(new[] { 15, 56, 115 }, Values(XDocument.Load(Path.Combine(candidate, "payload.xml")), "Left"));
        File.AppendAllText(Path.Combine(candidate, "payload.xml"), " ");
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!)); Assert.Equal(0, backend.Attempts);
        Assert.Equal(new[] { 10, 51, 110 }, Values(XDocument.Load(Path.Combine(resource.Directory, "payload.xml")), "Left"));
    }
    [Fact] public void CatalogBoundsEscapedUnicodePayloadAndStillMakesPagingProgress()
    {
        string xml = Screen(string.Concat(Enumerable.Repeat(Item(new string('中', 500), 1, 1, 10, 10), 200)));
        var page = ExtensionWire.Json(new ClassicXmlDocument(xml).LayoutCatalog(0, 200));
        Assert.True(page.GetProperty("hasMore").GetBoolean()); Assert.True(page.GetProperty("payloadLimited").GetBoolean());
        Assert.InRange(page.GetProperty("nextOffset").GetInt32(), 1, 199); Assert.True(page.GetRawText().Length < 262144);
        var next = ExtensionWire.Json(new ClassicXmlDocument(xml).LayoutCatalog(page.GetProperty("nextOffset").GetInt32(), 200));
        Assert.True(next.GetProperty("nextOffset").GetInt32() > page.GetProperty("nextOffset").GetInt32());
        Assert.Throws<ArgumentException>(() => Catalog(Xml.Replace("<Name>Test</Name>", "<Name>" + new string('x', 513) + "</Name>")));
    }
    private sealed class Backend : IClassicResourceBackend, IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "comfort-layout-" + Guid.NewGuid().ToString("N")); public int Attempts;
        public Backend() { Directory.CreateDirectory(Path.Combine(Root, "native")); File.WriteAllText(Path.Combine(Root, "native", "payload.xml"), Xml); File.WriteAllBytes(Path.Combine(Root, "native", "image.bin"), new byte[] { 0, 255, 1 }); }
        public ClassicResource ReadClassicResource(string hmiId, string path) => new ClassicResource { HmiId = hmiId, Path = path, Directory = Path.Combine(Root, "native"), XmlFile = "payload.xml" };
        public void ImportClassicResource(ClassicResource before, string candidateDirectory, string expectedFingerprint) { Attempts++; throw new NotImplementedException("Native import is outside this fixture."); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
