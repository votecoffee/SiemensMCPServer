using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ClassicReferenceTests
{
    private const string Xml = "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Screen</Name></AttributeList><ObjectList>"
        + "<Hmi.Screen.Button ID='1' CompositionName='ScreenItems'><AttributeList><Name>A</Name><Graphic external='path'>image.bin</Graphic></AttributeList><ObjectList>"
        + "<MultilingualText ID='2' CompositionName='Text'><ObjectList><MultilingualTextItem ID='3'><AttributeList><Culture>en-US</Culture><Text><body><p><b>Start</b> <i>ready</i></p></body></Text></AttributeList></MultilingualTextItem>"
        + "<MultilingualTextItem ID='4'><AttributeList><Culture>de-DE</Culture><Text>Deutsch 😀</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>"
        + "<Hmi.Event.Event ID='5' CompositionName='Events'><AttributeList><Name>Press</Name><Code>Tag_A = Tag_A + 1</Code></AttributeList><LinkList><Owner TargetID='1'/><Tag TargetID='@OpenLink'><Name>&quot;Tag_A&quot;</Name></Tag>"
        + "<DataType TargetID='@OpenLink'><Name>Int</Name></DataType></LinkList></Hmi.Event.Event></ObjectList><LinkList><Tag TargetID='@OpenLink'><Name>Tag_A</Name></Tag></LinkList></Hmi.Screen.Button>"
        + "<Hmi.Screen.Button ID='6' CompositionName='ScreenItems'><AttributeList><Name>B</Name></AttributeList><LinkList><Tag TargetID='@OpenLink'><Name>Tag_A</Name></Tag></LinkList></Hmi.Screen.Button>"
        + "</ObjectList></Hmi.Screen.Screen></Document>";
    private static JsonElement Inspect(string xml = Xml, string rootXPath = "", string linkType = "", int offset = 0, int maxItems = 200) => FeatureOperations.Execute("comfort", "inspect_references", ExtensionWire.Json(new { xml, rootXPath, linkType, offset, maxItems }));
    private static JsonElement[] Rows(string xml = Xml) => Inspect(xml).GetProperty("references").EnumerateArray().ToArray();
    private static ClassicReferenceEdit Edit(JsonElement row, string name = "\"NewTag\"") => new ClassicReferenceEdit {
        Selection = ExtensionWire.Parse<ClassicReferenceSelection>(row.GetProperty("selection")), NewName = name };
    private static JsonElement Change(string xml, params ClassicReferenceEdit[] edits) => FeatureOperations.Execute("comfort", "rebind_references", ExtensionWire.Json(new { snapshot = xml, arguments = new ClassicReferenceRequest { Edits = edits } }));
    private static XElement Control(XDocument doc, string name) => doc.Descendants("Hmi.Screen.Button").Single(e => e.Element("AttributeList")!.Element("Name")!.Value == name);

    [Fact] public void CatalogDistinguishesSymbolicTagsFromNativeOwnershipAndTypeLinks()
    {
        var rows = Rows(); Assert.Equal(5, rows.Length);
        var owner = rows.Single(r => r.GetProperty("linkType").GetString() == "Owner");
        Assert.False(owner.GetProperty("editable").GetBoolean()); Assert.Equal("A", owner.GetProperty("targetName").GetString());
        Assert.Equal(owner.GetProperty("controlXPath").GetString(), owner.GetProperty("targetXPath").GetString());
        Assert.Equal("nativeId", owner.GetProperty("kind").GetString()); Assert.Equal(JsonValueKind.Null, owner.GetProperty("selection").ValueKind);
        Assert.False(rows.Single(r => r.GetProperty("linkType").GetString() == "DataType").GetProperty("editable").GetBoolean());
        Assert.All(rows.Where(r => r.GetProperty("linkType").GetString() == "Tag"), r => {
            Assert.True(r.GetProperty("editable").GetBoolean()); Assert.Equal("openLink", r.GetProperty("kind").GetString()); Assert.NotNull(Edit(r).Selection.ExpectedName);
        });
    }
    [Fact] public void ScopedCatalogFiltersExactOwnerOrControlAndSupportsPaging()
    {
        var first = Rows().First(r => r.GetProperty("editable").GetBoolean());
        var page = Inspect(rootXPath: first.GetProperty("controlXPath").GetString()!, linkType: "Tag", maxItems: 1);
        Assert.Equal(2, page.GetProperty("matchedReferences").GetInt32()); Assert.Equal(1, page.GetProperty("nextOffset").GetInt32()); Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Single(Inspect(rootXPath: first.GetProperty("ownerXPath").GetString()!, linkType: "Tag").GetProperty("references").EnumerateArray());
        Assert.Single(Inspect(rootXPath: first.GetProperty("controlXPath").GetString()!, linkType: "Tag", offset: 1).GetProperty("references").EnumerateArray());
        Assert.Empty(Inspect(linkType: "NoSuchRole").GetProperty("references").EnumerateArray());
    }
    [Fact] public void ExactRebindingPreservesOtherLinksIdsCulturesStylesCodeAndNativeQuoting()
    {
        var row = Rows().First(r => r.GetProperty("editable").GetBoolean()); string next = "\"DB Ü\".\"Value<&😀\"";
        var result = Change(Xml, Edit(row, next)); var doc = XDocument.Parse(result.GetProperty("candidateXml").GetString()!, LoadOptions.PreserveWhitespace);
        var selected = doc.XPathSelectElement(row.GetProperty("xPath").GetString()!)!; Assert.Equal(next, selected.Element("Name")!.Value);
        var original = XDocument.Parse(Xml, LoadOptions.PreserveWhitespace); original.XPathSelectElement(row.GetProperty("xPath").GetString()!)!.Element("Name")!.Value = next;
        Assert.True(XNode.DeepEquals(original, doc)); Assert.Equal("Tag_A", Control(doc, "B").Element("LinkList")!.Element("Tag")!.Element("Name")!.Value);
        var report = result.GetProperty("changes").GetProperty("edits")[0]; Assert.Equal("\"Tag_A\"", report.GetProperty("oldName").GetString()); Assert.Equal(next, report.GetProperty("newName").GetString());
    }
    [Fact] public void CloneThenRebindOnlyTheNewControl()
    {
        var item = FeatureOperations.Execute("comfort", "inspect_screen_items", ExtensionWire.Json(new { xml = Xml })).GetProperty("items")[0];
        var cloned = FeatureOperations.Execute("comfort", "change_screen_items", ExtensionWire.Json(new { snapshot = Xml, arguments = new { changes = new[] { new {
            action = "clone", selection = item.GetProperty("selection"), destinationXPath = item.GetProperty("containerXPath").GetString(),
            names = new[] { new { sourceXPath = item.GetProperty("xPath").GetString(), newName = "Copy" } } } } } }));
        string xml = cloned.GetProperty("candidateXml").GetString()!;
        var selected = Inspect(xml, cloned.GetProperty("changes").GetProperty("operations")[0].GetProperty("newRootXPath").GetString()!, "Tag").GetProperty("references").EnumerateArray().ToArray();
        Assert.Equal(2, selected.Length); var doc = XDocument.Parse(Change(xml, selected.Select(r => Edit(r, "CopyTag")).ToArray()).GetProperty("candidateXml").GetString()!);
        Assert.All(Control(doc, "Copy").Descendants("Tag"), t => Assert.Equal("CopyTag", t.Element("Name")!.Value));
        Assert.Equal(new[] { "\"Tag_A\"", "Tag_A" }, Control(doc, "A").Descendants("Tag").Select(t => t.Element("Name")!.Value));
    }
    [Fact] public void TagTableSupportsConnectionAndPlcSymbolNamesWithoutChangingTypes()
    {
        string xml = "<Document><Hmi.Tag.TagTable ID='0'><AttributeList><Name>Tags</Name></AttributeList><ObjectList><Hmi.Tag.Tag ID='1'><AttributeList><Name>Value</Name></AttributeList><LinkList>"
            + "<Connection TargetID='@OpenLink'><Name>PLC</Name></Connection><ControllerTag TargetID='@OpenLink'><Name>&quot;DB&quot;.&quot;Value&quot;</Name></ControllerTag>"
            + "<HmiDataType TargetID='@OpenLink'><Name>Int</Name></HmiDataType></LinkList></Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>";
        var rows = Rows(xml); Assert.Equal(2, rows.Count(r => r.GetProperty("editable").GetBoolean()));
        var doc = XDocument.Parse(Change(xml, rows.Where(r => r.GetProperty("editable").GetBoolean()).Select(r => Edit(r, r.GetProperty("linkType").GetString() == "Connection" ? "PLC2" : "\"DB2\".\"Value\"")).ToArray()).GetProperty("candidateXml").GetString()!);
        Assert.Equal("PLC2", doc.Descendants("Connection").Single().Element("Name")!.Value); Assert.Equal("Int", doc.Descendants("HmiDataType").Single().Element("Name")!.Value);
    }
    [Fact] public void FingerprintIgnoresExportIdRenumberingAndFormattingButDetectsLinkChanges()
    {
        var row = Rows().First(r => r.GetProperty("editable").GetBoolean()); var edit = Edit(row); var doc = XDocument.Parse(Xml);
        foreach (var id in doc.Descendants().Attributes("ID")) id.Value = "A" + id.Value;
        foreach (var id in doc.Descendants().Attributes("TargetID").Where(a => a.Value != "@OpenLink")) id.Value = "A" + id.Value;
        Assert.NotNull(Change(doc.ToString(), edit).GetProperty("candidateXml").GetString());
        Assert.Throws<InvalidOperationException>(() => Change(Xml.Replace("&quot;Tag_A&quot;", "&quot;Different&quot;"), edit));
        edit.Selection.ExpectedName = "wrong"; Assert.Throws<InvalidOperationException>(() => Change(Xml, edit));
    }
    [Fact] public void DuplicateOrStaleSelectionsDoNotMutateSource()
    {
        var rows = Rows().Where(r => r.GetProperty("editable").GetBoolean()).ToArray(); var first = Edit(rows[0]); var stale = Edit(rows[1]); stale.Selection.ExpectedFingerprint = "stale";
        var source = new ClassicXmlDocument(Xml); string before = JsonSerializer.Serialize(source.ReferenceCatalog(new ClassicReferenceInspection()));
        Assert.Throws<InvalidOperationException>(() => source.RebindReferences(new ClassicReferenceRequest { Edits = new[] { first, stale } }));
        Assert.Equal(before, JsonSerializer.Serialize(source.ReferenceCatalog(new ClassicReferenceInspection())));
        Assert.Throws<ArgumentException>(() => Change(Xml, first, first));
        first.Selection.XPath = "//Tag[1]"; Assert.Throws<ArgumentException>(() => Change(Xml, first));
    }
    [Theory]
    [InlineData("<Name>A</Name><Name>B</Name>")]
    [InlineData("<Name><Part>A</Part></Name>")]
    [InlineData("<Name mode='dynamic'>A</Name>")]
    [InlineData("<Name>A<!--comment--></Name>")]
    [InlineData("<Name>A</Name><Metadata>extra</Metadata>")]
    [InlineData("<Name> </Name>")]
    public void AmbiguousOrStructuredNamesStayInspectionOnly(string name)
    {
        string xml = Xml.Replace("<Name>&quot;Tag_A&quot;</Name>", name); var row = Rows(xml)[1]; Assert.False(row.GetProperty("editable").GetBoolean());
        var forged = new ClassicReferenceEdit { Selection = new ClassicReferenceSelection { XPath = row.GetProperty("xPath").GetString()!, ExpectedFingerprint = "forged", ExpectedName = "A" }, NewName = "B" };
        Assert.ThrowsAny<Exception>(() => Change(xml, forged));
    }
    [Fact] public void NativeIdTagLinkAndAdditionalLinkAttributesAreProtected()
    {
        foreach (string xml in new[] { Xml.Replace("<Tag TargetID='@OpenLink'><Name>&quot;Tag_A&quot;</Name></Tag>", "<Tag TargetID='1'/>"),
            Xml.Replace("<Tag TargetID='@OpenLink'><Name>&quot;Tag_A&quot;", "<Tag TargetID='@OpenLink' mode='unknown'><Name>&quot;Tag_A&quot;") })
            Assert.False(Rows(xml)[1].GetProperty("editable").GetBoolean());
    }
    [Fact] public void UnresolvedNativeLinksBlockSymbolicEdits()
    {
        string xml = Xml.Replace("Owner TargetID='1'", "Owner TargetID='missing'"); Assert.All(Rows(xml), r => Assert.False(r.GetProperty("editable").GetBoolean()));
        Assert.Throws<ArgumentException>(() => Change(xml, Edit(Rows().First(r => r.GetProperty("editable").GetBoolean()))));
    }
    [Theory]
    [InlineData("")][InlineData("   ")][InlineData("Tag\u0001")]
    public void RejectsBlankOrInvalidNewNames(string name)
    { Assert.ThrowsAny<Exception>(() => Change(Xml, Edit(Rows().First(r => r.GetProperty("editable").GetBoolean()), name))); }
    [Fact] public void RejectsNoopOverLimitAndUnknownArguments()
    {
        var row = Rows().First(r => r.GetProperty("editable").GetBoolean()); Assert.Throws<ArgumentException>(() => Change(Xml, Edit(row, row.GetProperty("name").GetString()!)));
        Assert.Throws<ArgumentException>(() => Change(Xml, Edit(row, new string('x', 4097)))); Assert.Throws<ArgumentException>(() => Change(Xml, Array.Empty<ClassicReferenceEdit>()));
        Assert.Throws<ArgumentException>(() => Change(Xml, Enumerable.Repeat(Edit(row), 201).ToArray()));
        Assert.Throws<ArgumentException>(() => Inspect(rootXPath: "//Hmi.Screen.Button")); Assert.Throws<ArgumentException>(() => Inspect(offset: -1)); Assert.Throws<ArgumentException>(() => Inspect(maxItems: 201));
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "inspect_references", ExtensionWire.Json(new { xml = Xml, unknown = true })));
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "rebind_references", ExtensionWire.Json(new { snapshot = Xml, arguments = new { edits = new[] { new { selection = row.GetProperty("selection"), newName = "B", destinationXPath = "forbidden" } } } })));
    }
    [Fact] public void CatalogBoundsEscapedPayloadAndProgressesPastLongMetadata()
    {
        string xml = Xml.Replace("<Name>A</Name>", "<Name>" + new string('中', 10000) + "</Name>")
            .Replace("<Name>&quot;Tag_A&quot;</Name>", "<Name>" + new string('中', 4096) + "</Name>");
        var doc = XDocument.Parse(xml); var list = doc.Descendants("Hmi.Event.Event").Single().Element("LinkList")!; var tag = list.Element("Tag")!;
        for (int i = 0; i < 20; i++) list.Add(new XElement(tag));
        var page = Inspect(doc.ToString(SaveOptions.DisableFormatting), linkType: "Tag"); Assert.True(page.GetRawText().Length < 262144); Assert.True(page.GetProperty("payloadLimited").GetBoolean());
        int next = page.GetProperty("nextOffset").GetInt32(); Assert.InRange(next, 1, 21);
        Assert.True(Inspect(doc.ToString(), linkType: "Tag", offset: next).GetProperty("nextOffset").GetInt32() > next);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("references")[0].GetProperty("controlName").ValueKind);
    }
    [Fact] public void WorkerKeepsExistingHostContractAndPreviewsRetainSidecars()
    {
        var operations = FeatureOperations.Describe("comfort"); Assert.All(operations, op => Assert.True(op.IsCompatible("comfort")));
        Assert.Equal("local", operations.Single(op => op.Name == "inspect_references").Route);
        var preview = operations.Single(op => op.Name == "rebind_references"); Assert.True(preview.Preview); Assert.Equal("classicXml", preview.Route); Assert.Equal("classicReferences", preview.SnapshotKind);
        using var backend = new Backend(); var workflow = new ClassicXmlWorkflow(backend, Path.Combine(backend.Root, "plans")); var before = backend.ReadClassicResource("hmi", "screen");
        var plan = ExtensionWire.Json(workflow.PreviewTransformed("hmi", "screen", before.Fingerprint, xml => Change(xml, Edit(Rows(xml).First(r => r.GetProperty("editable").GetBoolean()))).GetProperty("candidateXml").GetString()!, new { action = "rebind" }));
        string directory = Path.Combine(plan.GetProperty("artifactDirectory").GetString()!, "candidate-files"); Assert.Equal(new byte[] { 0, 255, 1 }, File.ReadAllBytes(Path.Combine(directory, "image.bin")));
        File.AppendAllText(Path.Combine(directory, "payload.xml"), " "); Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
        Assert.Equal(0, backend.Attempts);
    }
    private sealed class Backend : IClassicResourceBackend, IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "classic-references-" + Guid.NewGuid().ToString("N")); public int Attempts;
        public Backend() { Directory.CreateDirectory(Path.Combine(Root, "native")); File.WriteAllText(Path.Combine(Root, "native", "payload.xml"), Xml); File.WriteAllBytes(Path.Combine(Root, "native", "image.bin"), new byte[] { 0, 255, 1 }); }
        public ClassicResource ReadClassicResource(string hmiId, string path) => new ClassicResource { HmiId = hmiId, Path = path, Directory = Path.Combine(Root, "native"), XmlFile = "payload.xml" };
        public void ImportClassicResource(ClassicResource before, string candidateDirectory, string expectedFingerprint) { Attempts++; throw new NotSupportedException("Native import is outside this fixture."); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
