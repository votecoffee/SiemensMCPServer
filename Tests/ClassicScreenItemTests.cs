using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ClassicScreenItemTests
{
    private static string Button(string name, string id) => $"<Hmi.Screen.Button ID='{id}' CompositionName='ScreenItems'><AttributeList><Name>{name}</Name><Left>10</Left></AttributeList></Hmi.Screen.Button>";
    private const string Complex = "<Hmi.Screen.Button ID='2' CompositionName='ScreenItems'><AttributeList><Name>A</Name><Left>10</Left><Graphic external='path'>image.bin</Graphic></AttributeList><ObjectList><MultilingualText ID='3' CompositionName='Text'><ObjectList><MultilingualTextItem ID='4'><AttributeList><Culture>en-US</Culture><Text><body><p><b>Start</b> <i>ready</i></p></body></Text></AttributeList></MultilingualTextItem><MultilingualTextItem ID='5'><AttributeList><Culture>de-DE</Culture><Text>Deutsch 😀</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><Hmi.Event.Event ID='6' CompositionName='Events'><AttributeList><Name>Press</Name></AttributeList><ObjectList><Hmi.Event.Function ID='7' CompositionName='Functions'><AttributeList><Name>SetTag</Name></AttributeList></Hmi.Event.Function></ObjectList><LinkList><Function TargetID='7'/><Owner TargetID='2'/><Layer TargetID='1'/><Tag TargetID='@OpenLink'><Name>Tag_X</Name></Tag></LinkList></Hmi.Event.Event></ObjectList></Hmi.Screen.Button>";
    private static string Screen(string? first = null) => "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Screen</Name></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID='1' CompositionName='Layers'><AttributeList><Name>Layer1</Name></AttributeList><ObjectList>" + (first ?? Complex + Button("B", "8")) + "</ObjectList></Hmi.Screen.ScreenLayer><Hmi.Screen.ScreenLayer ID='9' CompositionName='Layers'><AttributeList><Name>Layer2</Name></AttributeList><ObjectList>" + Button("C", "A") + "</ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>";
    private static string Group => "<Hmi.Screen.Group ID='B' CompositionName='ScreenItems'><AttributeList><Name>Group</Name></AttributeList><ObjectList>" + Button("G", "C") + Button("H", "D") + "</ObjectList></Hmi.Screen.Group>";
    private static JsonElement Inspect(string xml, string rootXPath = "") => FeatureOperations.Execute("comfort", "inspect_screen_items", ExtensionWire.Json(new { xml, rootXPath, maxItems = 200 }));
    private static JsonElement Row(string xml, string name) => Inspect(xml).GetProperty("items").EnumerateArray().Single(r => r.GetProperty("name").GetString() == name);
    private static ClassicScreenItemSelection Selection(JsonElement row) => ExtensionWire.Parse<ClassicScreenItemSelection>(row.GetProperty("selection"));
    private static ClassicScreenItemChange Clone(string xml, string source = "A", string name = "New Ü", string? destination = null)
    {
        var row = Row(xml, source); return new ClassicScreenItemChange { Action = "clone", Selection = Selection(row), DestinationXPath = destination ?? row.GetProperty("containerXPath").GetString(),
            Names = new[] { new ClassicScreenItemName { SourceXPath = row.GetProperty("xPath").GetString()!, NewName = name } } };
    }
    private static ClassicScreenItemChange Remove(string xml, string name) => new ClassicScreenItemChange { Action = "remove", Selection = Selection(Row(xml, name)) };
    private static JsonElement Change(string xml, params ClassicScreenItemChange[] changes) => FeatureOperations.Execute("comfort", "change_screen_items", ExtensionWire.Json(new { snapshot = xml, arguments = new ClassicScreenItemRequest { Changes = changes } }));
    private static XDocument Candidate(JsonElement result) => XDocument.Parse(result.GetProperty("candidateXml").GetString()!, LoadOptions.PreserveWhitespace);
    private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => e.Element("AttributeList")?.Element("Name")?.Value == name);

    [Fact] public void CatalogReturnsExactSelectionsAndExistingDestinationContainers()
    {
        string xml = Screen(); var catalog = Inspect(xml); Assert.Equal(3, catalog.GetProperty("matchedItems").GetInt32());
        foreach (var row in catalog.GetProperty("items").EnumerateArray())
        {
            Assert.True(row.GetProperty("cloneAvailable").GetBoolean()); Assert.True(row.GetProperty("standaloneRemovalAvailable").GetBoolean());
            Assert.DoesNotContain("@ID", Selection(row).XPath); Assert.Equal("ObjectList", XDocument.Parse(xml).XPathSelectElement(row.GetProperty("containerXPath").GetString()!)!.Name.LocalName);
        }
        var page = FeatureOperations.Execute("comfort", "inspect_screen_items", ExtensionWire.Json(new { xml, offset = 1, maxItems = 1 }));
        Assert.Equal("B", page.GetProperty("items")[0].GetProperty("name").GetString()); Assert.Equal(2, page.GetProperty("nextOffset").GetInt32()); Assert.True(page.GetProperty("hasMore").GetBoolean());
    }
    [Fact] public void CloneRemapsInternalIdsAndPreservesExternalBindingsStylesLanguagesAndOriginal()
    {
        string xml = Screen(); var result = Change(xml, Clone(xml)); var doc = Candidate(result); var copy = Named(doc, "New Ü");
        Assert.True(XNode.DeepEquals(Named(XDocument.Parse(xml), "A"), Named(XDocument.Parse(result.GetProperty("candidateXml").GetString()!), "A")));
        Assert.Equal((string?)copy.Descendants("Hmi.Event.Function").Single().Attribute("ID"), (string?)copy.Descendants("Function").Single().Attribute("TargetID"));
        Assert.Equal((string?)copy.Attribute("ID"), (string?)copy.Descendants("Owner").Single().Attribute("TargetID"));
        Assert.Equal("1", (string?)copy.Descendants("Layer").Single().Attribute("TargetID")); Assert.Equal("Tag_X", copy.Descendants("Tag").Single().Element("Name")!.Value);
        Assert.Contains("</b> <i>", copy.ToString(SaveOptions.DisableFormatting)); Assert.Contains("Deutsch 😀", copy.Value);
        var ids = doc.Descendants().Attributes("ID").Select(a => a.Value).ToArray(); Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(1, result.GetProperty("changes").GetProperty("operations")[0].GetProperty("retainedOpenLinks").GetInt32());
        Assert.Equal(1, result.GetProperty("changes").GetProperty("operations")[0].GetProperty("retainedExternalNativeIdLinks").GetInt32());
    }
    [Fact] public void CloneToReturnedOtherLayerKeepsLocalGeometryAndCorrectReportedPathAfterEdits()
    {
        string xml = Screen(); var change = Clone(xml, destination: Row(xml, "C").GetProperty("containerXPath").GetString());
        change.Edits = new[] { new ClassicXmlEdit { XPath = "/Document/Hmi.Screen.Button/AttributeList/Left", ExpectedValue = "10", NewValue = "70" } };
        var result = Change(xml, change); var doc = Candidate(result); var copy = Named(doc, "New Ü");
        Assert.Equal("Layer2", copy.Parent!.Parent!.Element("AttributeList")!.Element("Name")!.Value); Assert.Equal("70", copy.Element("AttributeList")!.Element("Left")!.Value);
        Assert.Same(copy, doc.XPathSelectElement(result.GetProperty("changes").GetProperty("operations")[0].GetProperty("newRootXPath").GetString()!));
        Assert.Equal("10", Named(doc, "A").Element("AttributeList")!.Element("Left")!.Value);
    }
    [Fact] public void GroupCloneRequiresExplicitNamesForEveryDescendantAndSupportsSubtreePaging()
    {
        string xml = Screen(Complex + Group); var group = Row(xml, "Group"); var rows = Inspect(xml, group.GetProperty("xPath").GetString()!).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length); Assert.Equal(3, group.GetProperty("subtreeItems").GetInt32());
        var change = new ClassicScreenItemChange { Action = "clone", Selection = Selection(group), DestinationXPath = Row(xml, "C").GetProperty("containerXPath").GetString(),
            Names = rows.Select(r => new ClassicScreenItemName { SourceXPath = r.GetProperty("xPath").GetString()!, NewName = "New" + r.GetProperty("name").GetString() }).ToArray() };
        var doc = Candidate(Change(xml, change)); Assert.Equal(2, Named(doc, "NewGroup").Descendants("Hmi.Screen.Button").Count()); Assert.NotNull(Named(doc, "NewG")); Assert.NotNull(Named(doc, "NewH"));
        change.Names = change.Names.Take(1).ToArray(); Assert.Throws<ArgumentException>(() => Change(xml, change));
    }
    [Fact] public void RepeatedPrototypeCopiesUseUniqueIdsAndExplicitNames()
    {
        string xml = Screen(); var doc = Candidate(Change(xml, Clone(xml, name: "First"), Clone(xml, name: "Second")));
        Assert.NotEqual((string?)Named(doc, "First").Attribute("ID"), (string?)Named(doc, "Second").Attribute("ID"));
        var ids = doc.Descendants().Attributes("ID").Select(a => a.Value).ToArray(); Assert.Equal(ids.Length, ids.Distinct().Count());
    }
    [Fact] public void BatchClonesAlwaysUseFrozenOriginalPrototypes()
    {
        string xml = Screen(Complex + Group); var group = Row(xml, "Group"); var groupRows = Inspect(xml, group.GetProperty("xPath").GetString()!).GetProperty("items").EnumerateArray().ToArray();
        var copyGroup = new ClassicScreenItemChange { Action = "clone", Selection = Selection(group), DestinationXPath = Row(xml, "C").GetProperty("containerXPath").GetString(),
            Names = groupRows.Select(r => new ClassicScreenItemName { SourceXPath = r.GetProperty("xPath").GetString()!, NewName = "Copy" + r.GetProperty("name").GetString() }).ToArray() };
        var insert = Clone(xml, name: "Inserted", destination: Row(xml, "G").GetProperty("containerXPath").GetString());
        var doc = Candidate(Change(xml, insert, copyGroup)); Assert.Equal(3, Named(doc, "Group").Descendants("Hmi.Screen.Button").Count()); Assert.Equal(2, Named(doc, "CopyGroup").Descendants("Hmi.Screen.Button").Count());
    }
    [Fact] public void RemovePreservesUnselectedControlsAndBlocksRetainedNativeIdReferences()
    {
        string xml = Screen(); var doc = Candidate(Change(xml, Remove(xml, "A"))); Assert.DoesNotContain(doc.Descendants("Name"), n => n.Value == "A"); Assert.NotNull(Named(doc, "B"));
        string linked = xml.Replace("<Name>B</Name>", "<Name>B</Name><Dependency TargetID='2'/>");
        Assert.False(Row(linked, "A").GetProperty("standaloneRemovalAvailable").GetBoolean()); Assert.Throws<InvalidOperationException>(() => Change(linked, Remove(linked, "A")));
        Assert.Single(Candidate(Change(linked, Remove(linked, "A"), Remove(linked, "B"))).Descendants("Hmi.Screen.Button"));
    }
    [Fact] public void ExactSymbolicOpenLinksBlockRemovalAndAreNotAutomaticallyRewritten()
    {
        string xml = Screen().Replace("<Name>B</Name>", "<Name>B</Name><LinkList><ScreenItem TargetID='@OpenLink'><Name>\"A\"</Name></ScreenItem></LinkList>");
        Assert.Throws<InvalidOperationException>(() => Change(xml, Remove(xml, "A")));
        var doc = Candidate(Change(xml, Clone(xml))); Assert.Equal("\"A\"", Named(doc, "B").Descendants("ScreenItem").Single().Element("Name")!.Value);
    }
    [Fact] public void RemovedIdsStayReservedAndCloneNamesCanReplaceAnUnreferencedRemovedControl()
    {
        string xml = Screen(); var doc = Candidate(Change(xml, Remove(xml, "C"), Clone(xml, name: "C")));
        Assert.DoesNotContain(doc.Descendants().Attributes("ID"), a => a.Value == "A"); Assert.NotNull(Named(doc, "C"));
        Assert.NotEqual("A", (string?)Named(doc, "C").Attribute("ID"));
    }
    [Fact] public void RejectsStaleDuplicateAndOverlappingSourcesBeforeMutation()
    {
        string xml = Screen(Complex + Group); var stale = Clone(xml); stale.Selection.ExpectedFingerprint = "stale";
        Assert.Throws<InvalidOperationException>(() => Change(xml, stale)); Assert.Throws<ArgumentException>(() => Change(xml, Remove(xml, "A"), Remove(xml, "A")));
        Assert.Throws<ArgumentException>(() => Change(xml, Remove(xml, "Group"), Remove(xml, "G")));
        Assert.Throws<ArgumentException>(() => Change(xml, Remove(xml, "A"), Clone(xml)));
        var source = new ClassicXmlDocument(xml); var before = JsonSerializer.Serialize(source.ScreenItemCatalog(new ClassicScreenItemInspection()));
        Assert.Throws<InvalidOperationException>(() => source.ChangeScreenItems(new ClassicScreenItemRequest { Changes = new[] { Clone(xml), stale } }));
        Assert.Equal(before, JsonSerializer.Serialize(source.ScreenItemCatalog(new ClassicScreenItemInspection())));
    }
    [Fact] public void FingerprintIgnoresVolatileIdRenumberingButDetectsStyleAndSpaceChanges()
    {
        string xml = Screen(); var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        foreach (var id in doc.Descendants().Attributes("ID")) id.Value = "A" + id.Value;
        foreach (var id in doc.Descendants().Attributes("TargetID").Where(a => a.Value != "@OpenLink")) id.Value = "A" + id.Value;
        Assert.Equal(Selection(Row(xml, "A")).ExpectedFingerprint, Selection(Row(doc.ToString(SaveOptions.DisableFormatting), "A")).ExpectedFingerprint);
        Assert.Throws<InvalidOperationException>(() => Change(xml.Replace("</b> <i>", "</b>  <i>"), Clone(xml)));
    }
    [Theory]
    [InlineData("A")][InlineData("a")][InlineData("")][InlineData("   ")]
    public void RejectsDuplicateOrEmptyNewNames(string name)
    { string xml = Screen(); Assert.Throws<ArgumentException>(() => Change(xml, Clone(xml, name: name))); }
    [Fact] public void RejectsUnknownContainersSourceSelectorsAndUnsafeCloneEdits()
    {
        string xml = Screen(); var clone = Clone(xml); clone.DestinationXPath = "/Document"; Assert.Throws<ArgumentException>(() => Change(xml, clone));
        clone = Clone(xml); clone.Selection.XPath = "//Hmi.Screen.Button[AttributeList/Name='A']"; Assert.Throws<ArgumentException>(() => Change(xml, clone));
        clone = Clone(xml); clone.Edits = new[] { new ClassicXmlEdit { XPath = "//AttributeList/Name", ExpectedValue = "New Ü", NewValue = "Overridden" } }; Assert.Throws<ArgumentException>(() => Change(xml, clone));
        clone = Clone(xml); clone.Edits = new[] { new ClassicXmlEdit { XPath = "//Layer/@TargetID", ExpectedValue = "1", NewValue = "missing" } }; Assert.Throws<ArgumentException>(() => Change(xml, clone));
        clone = Clone(xml); clone.Edits = new[] { new ClassicXmlEdit { XPath = "//Layer/@TargetID", ExpectedValue = "1", NewValue = "9" } }; Assert.Throws<ArgumentException>(() => Change(xml, clone));
        clone = Clone(xml); clone.Edits = new[] { new ClassicXmlEdit { XPath = "//MultilingualTextItem[AttributeList/Culture='en-US']/AttributeList/Culture", ExpectedValue = "en-US", NewValue = "fr-FR" } }; Assert.Throws<ArgumentException>(() => Change(xml, clone));
        clone = Clone(xml); clone.Edits = new[] { new ClassicXmlEdit { XPath = "//Hmi.Screen.Button/@ID", ExpectedValue = "B", NewValue = "C" } }; Assert.Throws<ArgumentException>(() => Change(xml, clone));
    }
    [Fact] public void UnsupportedIdentityAndNamesAreReportedAsUnavailable()
    {
        foreach (string xml in new[] { Screen().Replace(" ID='2'", " ID='2' UId='opaque'"), Screen().Replace("<Name>B</Name>", "<Name>A</Name>"), Screen().Replace(" ID='2'", ""), Screen().Replace("<Name>A</Name>", "<Name/>"), Screen().Replace("TargetID='7'", "TargetID='missing'") })
        {
            var row = Inspect(xml).GetProperty("items")[0]; Assert.False(row.GetProperty("cloneAvailable").GetBoolean());
            Assert.Throws<NotSupportedException>(() => Change(xml, new ClassicScreenItemChange { Action = "remove", Selection = Selection(row) }));
        }
    }
    [Fact] public void RejectsSelfOrRemovedDestinationAndExtraRemoveParameters()
    {
        string xml = Screen(Complex + Group); var clone = Clone(xml, source: "A", destination: Row(xml, "G").GetProperty("containerXPath").GetString());
        Assert.Throws<ArgumentException>(() => Change(xml, Remove(xml, "Group"), clone));
        var group = Clone(xml, source: "Group", destination: Row(xml, "G").GetProperty("containerXPath").GetString()); Assert.Throws<ArgumentException>(() => Change(xml, group));
        var remove = Remove(xml, "A"); remove.Names = Array.Empty<ClassicScreenItemName>(); Assert.Throws<ArgumentException>(() => Change(xml, remove));
    }
    [Fact] public void CatalogBoundsPayloadAndRejectsInvalidInputs()
    {
        string type = "Hmi.Screen." + new string('X', 1000);
        string xml = Screen(string.Concat(Enumerable.Range(100, 200).Select(i => Button(new string('中', 120) + i, i.ToString("X")).Replace("Hmi.Screen.Button", type))));
        var page = Inspect(xml); Assert.True(page.GetProperty("payloadLimited").GetBoolean()); Assert.True(page.GetRawText().Length < 262144); Assert.InRange(page.GetProperty("nextOffset").GetInt32(), 1, 199);
        Assert.Throws<ArgumentException>(() => FeatureOperations.Execute("comfort", "inspect_screen_items", ExtensionWire.Json(new { xml, maxItems = 201 })));
        Assert.Throws<ArgumentException>(() => Inspect(xml, "//Missing")); Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "inspect_screen_items", ExtensionWire.Json(new { xml, extra = true })));
        Assert.Throws<ArgumentException>(() => Inspect("<Document><Hmi.Tag.TagTable/></Document>"));
    }
    [Fact] public void WorkerOperationsKeepTheExistingWhitespaceSafeContract()
    {
        var descriptors = FeatureOperations.Describe("comfort"); Assert.All(descriptors, d => Assert.True(d.IsCompatible("comfort")));
        Assert.Equal("local", descriptors.Single(d => d.Name == "inspect_screen_items").Route);
        var preview = descriptors.Single(d => d.Name == "change_screen_items"); Assert.Equal("classicXml", preview.Route); Assert.Equal("classicReferences", preview.SnapshotKind);
    }
    [Fact] public void NativePreviewRetainsCompanionBytesAndRejectsTamperedCandidates()
    {
        using var backend = new Backend(); var workflow = new ClassicXmlWorkflow(backend, Path.Combine(backend.Root, "plans")); var before = backend.ReadClassicResource("hmi", "screen");
        var plan = ExtensionWire.Json(workflow.PreviewTransformed("hmi", "screen", before.Fingerprint, xml => Change(xml, Clone(xml)).GetProperty("candidateXml").GetString()!, new { action = "clone" }));
        string directory = Path.Combine(plan.GetProperty("artifactDirectory").GetString()!, "candidate-files"); Assert.Equal(new byte[] { 0, 255, 1 }, File.ReadAllBytes(Path.Combine(directory, "image.bin")));
        Assert.NotNull(Named(XDocument.Load(Path.Combine(directory, "payload.xml")), "New Ü"));
        File.AppendAllText(Path.Combine(directory, "payload.xml"), " "); Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!)); Assert.Equal(0, backend.Attempts);
    }
    private sealed class Backend : IClassicResourceBackend, IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "screen-items-" + Guid.NewGuid().ToString("N")); public int Attempts;
        public Backend() { Directory.CreateDirectory(Path.Combine(Root, "native")); File.WriteAllText(Path.Combine(Root, "native", "payload.xml"), Screen()); File.WriteAllBytes(Path.Combine(Root, "native", "image.bin"), new byte[] { 0, 255, 1 }); }
        public ClassicResource ReadClassicResource(string hmiId, string path) => new ClassicResource { HmiId = hmiId, Path = path, Directory = Path.Combine(Root, "native"), XmlFile = "payload.xml" };
        public void ImportClassicResource(ClassicResource before, string candidateDirectory, string expectedFingerprint) { Attempts++; throw new NotSupportedException("Native import is outside this fixture."); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
