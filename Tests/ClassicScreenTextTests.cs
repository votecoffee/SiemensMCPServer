using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ClassicScreenTextTests
{
    private static XElement Translation(string id, string culture, object content) => new XElement("MultilingualTextItem", new XAttribute("ID", id), new XAttribute("CompositionName", "Items"),
        new XElement("AttributeList", new XElement("Culture", culture), new XElement("Text", content)));
    private static XElement Property(string name, params XElement[] translations) => new XElement("ObjectList", new XElement("MultilingualText", new XAttribute("CompositionName", name), new XElement("ObjectList", translations)));
    private static string Screen(object english)
    {
        var button = new XElement("Hmi.Screen.Button", new XAttribute("ID", "1"), new XAttribute("CompositionName", "ScreenItems"),
            new XElement("AttributeList", new XElement("Name", "Button Ü"), new XElement("Left", "20")),
            Property("Text", Translation("3", "en-US", english), Translation("4", "de-DE", new XElement("body", new XElement("p", "Deutsch 😀")))),
            Property("ToolTipText", Translation("5", "en-US", "Tooltip")));
        var screen = new XElement("Hmi.Screen.Screen", new XAttribute("ID", "0"), new XElement("AttributeList", new XElement("Name", "Screen"), new XElement("Width", "800")),
            new XElement("ObjectList", button, new XElement("Hmi.Event.Event", new XAttribute("CompositionName", "Events"), new XElement("AttributeList", new XElement("Name", "Press")))));
        return new XDocument(new XElement("Document", new XElement("Engineering", new XAttribute("version", "V15.1")), screen)).ToString(SaveOptions.DisableFormatting);
    }
    private static XElement Rich() => XElement.Parse("<body><p style='text-align:center'>Ready <span style='font-weight:bold;color:red'>ON</span> now<br/><span style='font-style:italic'/></p><p><b>Next</b></p></body>", LoadOptions.PreserveWhitespace);
    private static JsonElement Inspect(string xml, int maxCharacters = 4096, string culture = "", string query = "") => FeatureOperations.Execute("comfort", "inspect_screen_texts", ExtensionWire.Json(new { xml, maxCharacters, culture, query }));
    private static JsonElement[] Runs(string xml) => Inspect(xml).GetProperty("runs").EnumerateArray().Where(r => r.GetProperty("culture").GetString() == "en-US" && r.GetProperty("property").GetString() == "Text").ToArray();
    private static ClassicScreenTextSelection Selection(JsonElement row) => ExtensionWire.Parse<ClassicScreenTextSelection>(row.GetProperty("selection"));
    private static ClassicScreenTextEdit Edit(JsonElement row, string value) => new ClassicScreenTextEdit { Selection = Selection(row), NewText = value };
    private static JsonElement Apply(string xml, params ClassicScreenTextEdit[] edits) => FeatureOperations.Execute("comfort", "edit_screen_texts", ExtensionWire.Json(new { snapshot = xml, arguments = new ClassicScreenTextRequest { Edits = edits } }));
    private static string Candidate(JsonElement result) => result.GetProperty("candidateXml").GetString()!;
    private static XElement EnglishText(XDocument doc) => doc.Descendants("MultilingualTextItem").First().Element("AttributeList")!.Element("Text")!;

    [Fact] public void CatalogExposesDistinctStyledRunsEmptySlotsAndExactCultureSelections()
    {
        var rows = Runs(Screen(Rich())); Assert.Equal(new[] { "Ready ", "ON", " now", "", "Next" }, rows.Select(r => r.GetProperty("text").GetString()));
        Assert.All(rows, r => { Assert.True(r.GetProperty("canPreview").GetBoolean()); Assert.Equal("embeddedBody", r.GetProperty("storage").GetString()); Assert.DoesNotContain("@ID", Selection(r).XPath); });
        Assert.Single(rows.Select(r => Selection(r).ExpectedTextFingerprint).Distinct()); Assert.Equal(Enumerable.Range(0, 5), rows.Select(r => Selection(r).RunIndex));
        Assert.Equal("Button Ü", rows[0].GetProperty("ownerName").GetString());
    }
    [Fact] public void EditsSeveralRunsWithoutChangingFormattingLanguagesEventsOrOriginal()
    {
        string xml = Screen(Rich()); var rows = Runs(xml);
        var result = Apply(xml, Edit(rows[1], "A < B & café 😀"), Edit(rows[3], "Italic"), Edit(rows[4], "Following"));
        var expected = XDocument.Parse(xml); var text = EnglishText(expected);
        text.Descendants("span").First().Value = "A < B & café 😀"; text.Descendants("span").Last().Value = "Italic"; text.Descendants("b").Single().Value = "Following";
        Assert.True(XNode.DeepEquals(expected, XDocument.Parse(Candidate(result)))); Assert.Contains("<br", Candidate(result));
        Assert.Equal(3, result.GetProperty("changes").GetProperty("edits").GetArrayLength()); Assert.Equal(1, result.GetProperty("changes").GetProperty("affectedTextFields").GetInt32());
        Assert.Equal("ON", Runs(xml)[1].GetProperty("text").GetString());
    }
    [Fact] public void SupportsPlainAndEmptyCaptionsWithoutInventingFormatting()
    {
        foreach (string value in new[] { "Plain & <text>", "" })
        {
            string xml = Screen(value); var result = Apply(xml, Edit(Runs(xml).Single(), "New <field/> & 😀"));
            var text = EnglishText(XDocument.Parse(Candidate(result))); Assert.False(text.HasElements); Assert.Equal("New <field/> & 😀", text.Value);
        }
    }
    [Fact] public void EncodedBodyRetainsItsStorageModeAndAttributes()
    {
        string xml = Screen("  <body><p><span style='font-weight:bold'>One</span> two</p></body>  "); var rows = Runs(xml);
        Assert.Equal("encodedBody", rows[0].GetProperty("storage").GetString());
        string candidate = Candidate(Apply(xml, Edit(rows[0], "一 & <two>")));
        var field = EnglishText(XDocument.Parse(candidate)); Assert.False(field.HasElements);
        var body = XElement.Parse(field.Value); Assert.Equal("font-weight:bold", (string?)body.Descendants("span").Single().Attribute("style"));
        Assert.Equal("一 & <two>", body.Descendants("span").Single().Value); Assert.Contains(" two", field.Value);
        Assert.StartsWith("  ", field.Value); Assert.EndsWith("  ", field.Value);
    }
    [Fact] public void WhitespaceInlineRunsAndParagraphBoundariesRemainExplicit()
    {
        string xml = Screen(XElement.Parse("<body>\n<p><b>A</b> <i>B</i></p>\n<p/></body>", LoadOptions.PreserveWhitespace));
        var rows = Runs(xml); Assert.Equal(new[] { "A", " ", "B", "" }, rows.Select(r => r.GetProperty("text").GetString()));
        var result = Apply(xml, Edit(rows.Last(), "Second line"));
        var text = EnglishText(XDocument.Parse(Candidate(result), LoadOptions.PreserveWhitespace)); Assert.Equal(2, text.Descendants("p").Count()); Assert.Contains("</b> <i>", text.ToString(SaveOptions.DisableFormatting));
    }
    [Fact] public void EditingCdataEscapesReplacementWithoutIntroducingMarkup()
    {
        string xml = Screen(new XCData("Original")); var result = Apply(xml, Edit(Runs(xml).Single(), "]]> & <field/>"));
        var field = EnglishText(XDocument.Parse(Candidate(result))); Assert.Equal("]]> & <field/>", field.Value); Assert.False(field.HasElements);
    }
    [Fact] public void FingerprintCultureAndRunTextRejectStaleSelectionsBeforeAnyMutation()
    {
        string xml = Screen(Rich()); var rows = Runs(xml); var stale = Edit(rows[1], "Changed"); stale.Selection.ExpectedText = "Wrong";
        var doc = new ClassicXmlDocument(xml); string before = JsonSerializer.Serialize(doc.ScreenTextCatalog(new ClassicScreenTextInspection { MaxCharacters = 4096 }));
        Assert.Throws<InvalidOperationException>(() => doc.EditScreenTexts(new ClassicScreenTextRequest { Edits = new[] { Edit(rows[0], "First"), stale } }));
        Assert.Equal(before, JsonSerializer.Serialize(doc.ScreenTextCatalog(new ClassicScreenTextInspection { MaxCharacters = 4096 })));
        Assert.Throws<InvalidOperationException>(() => Apply(xml.Replace("color:red", "color:blue"), Edit(rows[1], "Changed")));
        var wrongCulture = Edit(rows[0], "First"); wrongCulture.Selection.Culture = "en-us"; Assert.Throws<InvalidOperationException>(() => Apply(xml, wrongCulture));
    }
    [Fact] public void DuplicateUnknownVolatileAndWrongRunSelectionsAreRejected()
    {
        string xml = Screen(Rich()); var edit = Edit(Runs(xml)[0], "New"); Assert.Throws<ArgumentException>(() => Apply(xml, edit, edit));
        edit.Selection.XPath = "//Text"; Assert.Throws<ArgumentException>(() => Apply(xml, edit));
        edit.Selection.XPath = "//*[@ID='3']/AttributeList/Text"; Assert.Throws<ArgumentException>(() => Apply(xml, edit));
        edit = Edit(Runs(xml)[0], "New"); edit.Selection.RunIndex = 999; Assert.Throws<ArgumentException>(() => Apply(xml, edit));
    }
    [Theory]
    [InlineData("<body><p><field ref='tag'>dynamic</field></p></body>")]
    [InlineData("<body><p><span fieldRef='tag'>dynamic</span></p></body>")]
    [InlineData("<body xmlns='urn:unknown'><p>Unknown</p></body>")]
    [InlineData("<body><p>One<!-- comment --></p></body>")]
    [InlineData("<body><p><script>run</script></p></body>")]
    [InlineData("<body><p><p>Nested</p></p></body>")]
    [InlineData("<body><p><br>Not empty</br></p></body>")]
    public void UnknownDynamicAndMalformedRichStructuresAreInspectionOnly(string body)
    {
        string xml = Screen(XElement.Parse(body)); var row = Runs(xml).Single(); Assert.False(row.GetProperty("canPreview").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("selection").ValueKind); Assert.NotEmpty(row.GetProperty("unsupportedReason").GetString()!);
        var selection = new ClassicScreenTextSelection { XPath = row.GetProperty("xPath").GetString()!, Culture = "en-US", RunIndex = 0, ExpectedTextFingerprint = row.GetProperty("textFingerprint").GetString()!, ExpectedText = row.GetProperty("text").GetString()! };
        Assert.Throws<ArgumentException>(() => Apply(xml, new ClassicScreenTextEdit { Selection = selection, NewText = "Replace" }));
    }
    [Theory]
    [InlineData("<!DOCTYPE body [<!ENTITY x SYSTEM 'file:///missing'>]><body><p>&x;</p></body>")]
    [InlineData("<field>Dynamic</field>")]
    [InlineData("<body><p>A</p></body><!-- outside -->")]
    [InlineData("<?xml version='1.0'?><body><p>A</p></body>")]
    public void SerializedUnknownOrUnsafeMarkupStaysProtected(string value)
    { Assert.False(Runs(Screen(value)).Single().GetProperty("canPreview").GetBoolean()); }
    [Fact] public void DuplicateCultureAndPropertyAndMissingMetadataAreProtected()
    {
        string xml = Screen("A");
        foreach (string malformed in new[] { xml.Replace("de-DE", "en-US"), xml.Replace("CompositionName=\"ToolTipText\"", "CompositionName=\"Text\""), xml.Replace("<Culture>en-US</Culture>", ""), xml.Replace("<Text>A</Text>", "<Text>A</Text><Text>B</Text>") })
            Assert.Contains(Inspect(malformed).GetProperty("runs").EnumerateArray(), r => !r.GetProperty("canPreview").GetBoolean());
    }
    [Fact] public void FiltersAndPagesRetainOriginalRunIndexes()
    {
        string xml = Screen(Rich()); var filtered = Inspect(xml, culture: "EN-us", query: "on").GetProperty("runs").EnumerateArray().ToArray();
        Assert.Single(filtered); Assert.Equal(1, Selection(filtered[0]).RunIndex); Assert.Equal("en-US", Selection(filtered[0]).Culture);
        var strict = FeatureOperations.Execute("comfort", "inspect_screen_texts", ExtensionWire.Json(new { xml, query = "on", caseSensitive = true })); Assert.Equal(0, strict.GetProperty("matchedRuns").GetInt32());
        var page = FeatureOperations.Execute("comfort", "inspect_screen_texts", ExtensionWire.Json(new { xml, offset = 1, maxRuns = 1 })); Assert.True(page.GetProperty("hasMore").GetBoolean()); Assert.Equal(2, page.GetProperty("nextOffset").GetInt32());
    }
    [Fact] public void ClippedAndOversizedRunsCannotProduceMisleadingEditableSelections()
    {
        string xml = Screen(new string('中', 2000)); var clipped = Inspect(xml, 100).GetProperty("runs")[0]; Assert.True(clipped.GetProperty("textClipped").GetBoolean()); Assert.False(clipped.GetProperty("canPreview").GetBoolean());
        Assert.True(Runs(xml).Single().GetProperty("canPreview").GetBoolean());
        Assert.False(Runs(Screen(new string('中', 4097))).Single().GetProperty("canPreview").GetBoolean());
        Assert.Throws<ArgumentException>(() => Apply(Screen("A"), Edit(Runs(Screen("A")).Single(), new string('x', 4097))));
    }
    [Fact] public void CatalogBoundsEscapedUnicodePayloadAndMakesProgress()
    {
        // Multiple bounded captions, each under the per-caption content limit.
        var doc = XDocument.Parse(Screen("Placeholder")); var owner = doc.Descendants("Hmi.Screen.Button").Single();
        foreach (int i in Enumerable.Range(0, 20)) owner.Add(Property("Caption" + i, Translation((100+i).ToString(), "en-US", new XElement("body", new XElement("p", new string('中', 4000))))));
        var result = Inspect(doc.ToString(SaveOptions.DisableFormatting)); Assert.True(result.GetProperty("payloadLimited").GetBoolean()); Assert.True(result.GetRawText().Length < 262144);
        Assert.InRange(result.GetProperty("nextOffset").GetInt32(), 1, 22);
    }
    [Fact] public void RejectsInvalidBoundsUnsafeCharactersAndNoOps()
    {
        string xml = Screen("A"); var row = Runs(xml).Single(); Assert.Throws<ArgumentException>(() => Apply(xml, Edit(row, "A")));
        Assert.Throws<XmlException>(() => Apply(xml, Edit(row, "bad\u0001")));
        Assert.Throws<ArgumentException>(() => FeatureOperations.Execute("comfort", "inspect_screen_texts", ExtensionWire.Json(new { xml, offset = -1 })));
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "inspect_screen_texts", ExtensionWire.Json(new { xml, unexpected = true })));
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "edit_screen_texts", ExtensionWire.Json(new { snapshot = xml, arguments = new { edits = new[] { new { newText = "B" } } } })));
        Assert.Throws<ArgumentException>(() => Inspect("<Document><Hmi.Tag.TagTable/></Document>"));
    }
    [Fact] public void CaptionOperationsUseExistingHostRoutes()
    {
        var descriptors = FeatureOperations.Describe("comfort"); Assert.All(descriptors, d => Assert.True(d.IsCompatible("comfort")));
        Assert.Equal("local", descriptors.Single(d => d.Name == "inspect_screen_texts").Route); Assert.Equal("classicXml", descriptors.Single(d => d.Name == "edit_screen_texts").Route);
        Assert.Equal("screenText", descriptors.Single(d => d.Name == "edit_screen_texts").SnapshotKind);
        Assert.False(new ExtensionOperation { Name = "wrong", Route = "classicXml", Preview = true, SnapshotKind = "screenText", InputSchema = ExtensionWire.Json(new { }) }.IsCompatible("unified"));
    }
    [Fact] public void ResourceHashPreservesRichTextWhitespaceAndIgnoresOnlyOuterFormatting()
    {
        string directory = Path.Combine(Path.GetTempPath(), "caption-hash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "payload.xml"); string xml = Screen(XElement.Parse("<body><p><b>A</b> <i>B</i></p></body>", LoadOptions.PreserveWhitespace));
            File.WriteAllText(path, xml); string hash = TransferFiles.SemanticHash(directory);
            File.WriteAllText(path, xml.Replace("</b> <i>", "</b>  <i>")); Assert.NotEqual(hash, TransferFiles.SemanticHash(directory));
            File.WriteAllText(path, xml.Replace("</b> <i>", "</b><i>")); Assert.NotEqual(hash, TransferFiles.SemanticHash(directory));
            File.WriteAllText(path, xml.Replace("<Document>", "<Document>\n  ").Replace("ID=\"1\"", "ID=\"A1\"")); Assert.Equal(hash, TransferFiles.SemanticHash(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact] public void WhitespaceOnlyCaptionChangeCreatesAHashBoundNativePreview()
    {
        using var backend = new CaptionBackend(); var workflow = new ClassicXmlWorkflow(backend, Path.Combine(backend.Root, "plans"));
        var before = backend.ReadClassicResource("hmi", "screen"); var row = Runs(backend.Xml)[1];
        var plan = ExtensionWire.Json(workflow.PreviewTransformed("hmi", "screen", before.Fingerprint,
            xml => Candidate(Apply(xml, Edit(row, "  "))), new { action = "caption" }));
        Assert.NotEqual(plan.GetProperty("beforeFingerprint").GetString(), plan.GetProperty("candidateFingerprint").GetString());
        string directory = Path.Combine(plan.GetProperty("artifactDirectory").GetString()!, "candidate-files");
        Assert.Contains("</b>  <i>", File.ReadAllText(Path.Combine(directory, "payload.xml")));
        Assert.Equal(new byte[] { 0, 255, 1 }, File.ReadAllBytes(Path.Combine(directory, "image.bin")));
        Assert.Contains("</b> <i>", File.ReadAllText(Path.Combine(before.Directory, "payload.xml")));
    }
    private sealed class CaptionBackend : IClassicResourceBackend, IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "caption-preview-" + Guid.NewGuid().ToString("N"));
        public string Xml = Screen(XElement.Parse("<body><p><b>A</b> <i>B</i></p></body>", LoadOptions.PreserveWhitespace));
        public CaptionBackend() { Directory.CreateDirectory(Path.Combine(Root, "native")); File.WriteAllText(Path.Combine(Root, "native", "payload.xml"), Xml); File.WriteAllBytes(Path.Combine(Root, "native", "image.bin"), new byte[] { 0, 255, 1 }); }
        public ClassicResource ReadClassicResource(string hmiId, string path) => new ClassicResource { HmiId = hmiId, Path = path, Directory = Path.Combine(Root, "native"), XmlFile = "payload.xml" };
        public void ImportClassicResource(ClassicResource before, string candidateDirectory, string expectedFingerprint) => throw new NotSupportedException("Native import is outside this fixture.");
        public void Dispose() => Directory.Delete(Root, true);
    }
}
