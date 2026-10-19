using System;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ReviewedBehaviorTests
{
    private const string Xml = "<Document><Hmi.Screen.Screen ID='0'><AttributeList><Name>S</Name></AttributeList><ObjectList>"
        + "<Hmi.Screen.Button ID='1' CompositionName='ScreenItems'><AttributeList><Name>A</Name></AttributeList><ObjectList><Hmi.Event.Event ID='2' CompositionName='Events'><AttributeList><Name>Press</Name>"
        + "<Text><body><p><b>Start</b> <i>ready</i></p></body></Text></AttributeList><ObjectList><Hmi.Event.Function ID='3' CompositionName='Functions'><AttributeList><Name>SetTag</Name><Code>Tag_A = 1</Code></AttributeList></Hmi.Event.Function></ObjectList>"
        + "<LinkList><Function TargetID='3'/><Control TargetID='1'/><Tag TargetID='@OpenLink'><Name>Tag_A</Name></Tag></LinkList></Hmi.Event.Event></ObjectList></Hmi.Screen.Button>"
        + "<Hmi.Screen.Button ID='4' CompositionName='ScreenItems'><AttributeList><Name>B</Name></AttributeList><ObjectList><Hmi.Event.Event ID='5' CompositionName='Events'><AttributeList><Name>Release</Name></AttributeList></Hmi.Event.Event></ObjectList></Hmi.Screen.Button>"
        + "<Hmi.Screen.IOField ID='6' CompositionName='ScreenItems'><AttributeList><Name>C</Name></AttributeList></Hmi.Screen.IOField></ObjectList></Hmi.Screen.Screen></Document>";
    private static JsonElement Inspect(string xml = Xml, string rootXPath = "", int offset = 0, int maxItems = 200) => FeatureOperations.Execute("comfort", "inspect_behaviors", ExtensionWire.Json(new { xml, rootXPath, offset, maxItems }));
    private static JsonElement Row(string xml = Xml, string type = "Hmi.Event.Event") => Inspect(xml).GetProperty("objects").EnumerateArray().First(r => r.GetProperty("nativeType").GetString() == type);
    private static string TargetPath(string xml, string name) => FeatureOperations.Execute("comfort", "inspect_screen_items", ExtensionWire.Json(new { xml })).GetProperty("items").EnumerateArray().Single(r => r.GetProperty("name").GetString() == name).GetProperty("xPath").GetString()!;
    private static ClassicBehaviorOperation Operation(JsonElement row, string action = "clone", string destination = "") => new ClassicBehaviorOperation {
        Action = action, XPath = row.GetProperty("xPath").GetString()!, ExpectedFingerprint = row.GetProperty("fingerprint").GetString()!, DestinationXPath = destination };
    private static ClassicBehaviorOperation Clone(string xml = Xml) => Operation(Row(xml), destination: TargetPath(xml, "B"));
    private static string Change(string xml, params ClassicBehaviorOperation[] changes) => FeatureOperations.Execute("comfort", "change_behaviors", ExtensionWire.Json(new { snapshot = xml, arguments = new { changes } })).GetProperty("candidateXml").GetString()!;
    private static ClassicBehaviorReferenceTarget Map(JsonElement row, string target) => new ClassicBehaviorReferenceTarget { XPath = row.GetProperty("ownerLinks")[0].GetProperty("xPath").GetString()!,
        ExpectedTargetXPath = row.GetProperty("ownerLinks")[0].GetProperty("expectedTargetXPath").GetString()!, NewTargetXPath = target };
    [Fact] public void CatalogProvidesPagingOwnerScopeAndCloneRelativeExternalOwnerLinks()
    {
        var page = Inspect(maxItems: 1); Assert.Equal(3, page.GetProperty("matchedObjects").GetInt32()); Assert.Equal(1, page.GetProperty("nextOffset").GetInt32()); Assert.True(page.GetProperty("hasMore").GetBoolean());
        var row = page.GetProperty("objects")[0]; Assert.Equal(TargetPath(Xml, "A"), row.GetProperty("ownerXPath").GetString());
        Assert.Equal(TargetPath(Xml, "A"), row.GetProperty("ownerLinks")[0].GetProperty("expectedTargetXPath").GetString());
        Assert.StartsWith("/Document[1]/Hmi.Event.Event[1]/LinkList[1]/Control[1]", row.GetProperty("ownerLinks")[0].GetProperty("xPath").GetString());
        Assert.Single(row.GetProperty("ownerLinks").EnumerateArray()); Assert.Equal(2, Inspect(rootXPath: TargetPath(Xml, "A")).GetProperty("matchedObjects").GetInt32());
        Assert.Equal("Hmi.Event.Function", Inspect(offset: 1, maxItems: 1).GetProperty("objects")[0].GetProperty("nativeType").GetString());
    }
    [Fact] public void CloneCanExplicitlyRebindOwnerWhilePreservingInternalLinksFormattingAndCode()
    {
        var clone = Clone(); clone.ReferenceTargets = new[] { Map(Row(), TargetPath(Xml, "B")) };
        clone.Edits = new[] { new ClassicXmlEdit { XPath = "/Document/Hmi.Event.Event/AttributeList/Name", ExpectedValue = "Press", NewValue = "CopiedPress" } };
        var doc = XDocument.Parse(Change(Xml, clone), LoadOptions.PreserveWhitespace);
        var copied = doc.Descendants("Hmi.Event.Event").Single(e => e.Element("AttributeList")!.Element("Name")!.Value == "CopiedPress");
        Assert.Equal("4", copied.Element("LinkList")!.Element("Control")!.Attribute("TargetID")!.Value);
        Assert.Equal(copied.Descendants("Hmi.Event.Function").Single().Attribute("ID")!.Value, copied.Descendants("Function").Single().Attribute("TargetID")!.Value);
        Assert.Equal("Tag_A", copied.Descendants("Tag").Single().Element("Name")!.Value); Assert.Equal("Tag_A = 1", copied.Descendants("Code").Single().Value);
        Assert.Equal(" ", copied.Descendants("b").Single().NextNode!.ToString());
        Assert.Equal("1", doc.Descendants("Hmi.Event.Event").First().Descendants("Control").Single().Attribute("TargetID")!.Value);
        Assert.Equal(doc.Descendants().Attributes("ID").Count(), doc.Descendants().Attributes("ID").Select(a => a.Value).Distinct().Count());
    }
    [Fact] public void NoOwnerMapPreservesTheOriginalExternalOwnerAsBefore()
    { Assert.Equal("1", XDocument.Parse(Change(Xml, Clone())).Descendants("Hmi.Event.Event").Last().Descendants("Control").Single().Attribute("TargetID")!.Value); }
    [Fact] public void CombinedRemovalsAllowReferencesBetweenRemovedBehaviorsButBlockRetainedOnes()
    {
        string linked = Xml.Replace("<Name>Release</Name>", "<Name>Release</Name><Dependency TargetID='2'/>");
        var rows = Inspect(linked).GetProperty("objects").EnumerateArray().Where(r => r.GetProperty("nativeType").GetString() == "Hmi.Event.Event").ToArray();
        Assert.False(rows[0].GetProperty("standaloneRemovalAvailable").GetBoolean());
        var a = Operation(rows[0], "remove"); var b = Operation(rows[1], "remove");
        Assert.Throws<InvalidOperationException>(() => Change(linked, a)); Assert.Empty(XDocument.Parse(Change(linked, a, b)).Descendants("Hmi.Event.Event"));
    }
    [Fact] public void OriginalIdsRemainReservedWhenRemovalPrecedesCloneInTheRequest()
    {
        var remove = Operation(Inspect().GetProperty("objects")[2], "remove"); var doc = XDocument.Parse(Change(Xml, remove, Clone()));
        Assert.DoesNotContain(doc.Descendants().Attributes("ID"), id => id.Value == "5");
    }
    [Fact] public void FrozenBatchPrototypesExcludeEarlierInsertedChildren()
    {
        // Synthetic composition tests batch snapshot semantics, not native validity of this arrangement.
        string xml = "<Document><Hmi.Screen.Screen ID='0'><AttributeList><Name>S</Name></AttributeList><ObjectList><Hmi.Event.Function ID='1' CompositionName='Events'><AttributeList><Name>Owner1</Name></AttributeList><ObjectList>"
            + "<Hmi.Event.Function ID='2' CompositionName='Functions'><AttributeList><Name>A</Name></AttributeList><ObjectList><Hmi.Event.Function ID='3' CompositionName='Functions'><AttributeList><Name>Nested</Name></AttributeList></Hmi.Event.Function></ObjectList></Hmi.Event.Function>"
            + "<Hmi.Event.Function ID='4' CompositionName='Functions'><AttributeList><Name>B</Name></AttributeList></Hmi.Event.Function></ObjectList></Hmi.Event.Function>"
            + "<Hmi.Event.Function ID='5' CompositionName='Events'><AttributeList><Name>Owner2</Name></AttributeList></Hmi.Event.Function></ObjectList></Hmi.Screen.Screen></Document>";
        var document = XDocument.Parse(xml); var rows = Inspect(xml).GetProperty("objects").EnumerateArray().ToArray();
        string Name(JsonElement r) => document.XPathSelectElement(r.GetProperty("xPath").GetString()!)!.Element("AttributeList")!.Element("Name")!.Value;
        var a = rows.Single(r => Name(r) == "A"); var b = rows.Single(r => Name(r) == "B"); var nested = rows.Single(r => Name(r) == "Nested"); var owner2 = rows.Single(r => Name(r) == "Owner2");
        var result = XDocument.Parse(Change(xml, Operation(b, destination: nested.GetProperty("xPath").GetString()!), Operation(a, destination: owner2.GetProperty("xPath").GetString()!)));
        var copy = result.Descendants("Hmi.Event.Function").Single(e => e.Element("AttributeList")!.Element("Name")!.Value == "Owner2");
        Assert.DoesNotContain(copy.Descendants("Name"), n => n.Value == "B"); Assert.Contains(result.Descendants("Name"), n => n.Value == "B");
    }
    [Fact] public void ScalarEditsCannotBypassExplicitOwnerMapOrIdentityProtection()
    {
        foreach (var edit in new[] { new ClassicXmlEdit { XPath = "//Control/@TargetID", ExpectedValue = "1", NewValue = "4" },
            new ClassicXmlEdit { XPath = "/Document/Hmi.Event.Event/@CompositionName", ExpectedValue = "Events", NewValue = "Dynamizations" } })
        { var clone = Clone(); clone.Edits = new[] { edit }; Assert.Throws<ArgumentException>(() => Change(Xml, clone)); }
    }
    [Fact] public void OwnerMapsRejectStaleTargetWrongTypeInternalLinksAndDuplicates()
    {
        var row = Row(); var map = Map(row, TargetPath(Xml, "B")); var clone = Clone(); map.ExpectedTargetXPath = TargetPath(Xml, "B"); clone.ReferenceTargets = new[] { map };
        Assert.Throws<InvalidOperationException>(() => Change(Xml, clone));
        clone = Clone(); clone.ReferenceTargets = new[] { Map(row, TargetPath(Xml, "C")) }; Assert.Throws<ArgumentException>(() => Change(Xml, clone));
        clone = Clone(); map = Map(row, TargetPath(Xml, "B")); clone.ReferenceTargets = new[] { map, map }; Assert.Throws<ArgumentException>(() => Change(Xml, clone));
        clone = Clone(); map = Map(row, TargetPath(Xml, "B")); map.XPath = "/Document/Hmi.Event.Event/LinkList/Function"; clone.ReferenceTargets = new[] { map }; Assert.Throws<ArgumentException>(() => Change(Xml, clone));
    }
    [Fact] public void FailureDoesNotMutateTheCallerAndOverlappingDestinationsAreGuarded()
    {
        var original = new ClassicXmlDocument(Xml); var before = JsonSerializer.Serialize(original.InspectBehaviors(new ClassicBehaviorInspection())); var stale = Clone(); stale.ExpectedFingerprint = "stale";
        Assert.Throws<InvalidOperationException>(() => original.ChangeReviewedBehaviors(new[] { Clone(), stale })); Assert.Equal(before, JsonSerializer.Serialize(original.InspectBehaviors(new ClassicBehaviorInspection())));
        Assert.Throws<ArgumentException>(() => Change(Xml, Clone(), Clone()));
        var clone = Clone(); clone.DestinationXPath = TargetPath(Xml, "C"); Assert.Throws<ArgumentException>(() => Change(Xml, clone));
    }
    [Fact] public void FingerprintsIgnoreExportIdRenumberingAndDetectAuthoredWhitespace()
    {
        string fingerprint = Row().GetProperty("fingerprint").GetString()!; var doc = XDocument.Parse(Xml, LoadOptions.PreserveWhitespace);
        foreach (var id in doc.Descendants().Attributes("ID")) id.Value = "A" + id.Value;
        foreach (var id in doc.Descendants().Attributes("TargetID").Where(a => a.Value != "@OpenLink")) id.Value = "A" + id.Value;
        Assert.Equal(fingerprint, Row(doc.ToString(SaveOptions.DisableFormatting)).GetProperty("fingerprint").GetString());
        Assert.Throws<InvalidOperationException>(() => Change(Xml.Replace("</b> <i>", "</b>  <i>"), Clone()));
    }
    [Fact] public void UnsupportedUidsAndUnresolvedReferencesRemainInspectionOnly()
    {
        foreach (string xml in new[] { Xml.Replace("ID='2'", "ID='2' UId='opaque'"), Xml.Replace("Control TargetID='1'", "Control TargetID='missing'"), Xml.Replace("Hmi.Event.Event ID='2'", "Hmi.Event.Event") })
        { Assert.False(Row(xml).GetProperty("cloneAvailable").GetBoolean()); Assert.Throws<NotSupportedException>(() => Change(xml, Clone(xml))); }
    }
    [Fact] public void InspectionAndOperationsRejectInvalidArgumentsAndKeepFixedNativeDto()
    {
        Assert.Throws<ArgumentException>(() => Inspect(rootXPath: "//Hmi.Screen.Button")); Assert.Throws<ArgumentException>(() => Inspect(offset: -1)); Assert.Throws<ArgumentException>(() => Inspect(maxItems: 201));
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "inspect_behaviors", ExtensionWire.Json(new { xml = Xml, extra = true })));
        Assert.Throws<ArgumentException>(() => Change(Xml, Array.Empty<ClassicBehaviorOperation>()));
        var clone = Clone(); clone.ReferenceTargets = new[] { Map(Row(), TargetPath(Xml, "B")) }; clone.ReferenceTargets[0].XPath = new string('x', 4097); Assert.Throws<ArgumentException>(() => Change(Xml, clone));
        Assert.Null(typeof(ClassicBehaviorChange).GetProperty("ReferenceTargets")); Assert.All(FeatureOperations.Describe("comfort"), o => Assert.True(o.IsCompatible("comfort")));
    }
    [Fact] public void OwnerLinkAndPageBudgetsRemainBoundedAndMakeProgress()
    {
        string type = "Hmi.Event." + new string('X', 1000);
        string events = string.Concat(Enumerable.Range(2, 100).Select(i => "<" + type + " ID='" + i.ToString("X") + "' CompositionName='Events'><LinkList>" +
            string.Concat(Enumerable.Repeat("<Control TargetID='1'/>", 80)) + "</LinkList></" + type + ">"));
        string xml = "<Document><Hmi.Screen.Screen ID='0'><AttributeList><Name>S</Name></AttributeList><ObjectList><Hmi.Screen.Button ID='1' CompositionName='ScreenItems'><AttributeList><Name>A</Name></AttributeList><ObjectList>" + events + "</ObjectList></Hmi.Screen.Button></ObjectList></Hmi.Screen.Screen></Document>";
        var page = Inspect(xml); Assert.True(page.GetProperty("payloadLimited").GetBoolean()); Assert.True(page.GetRawText().Length < 262144);
        int next = page.GetProperty("nextOffset").GetInt32(); Assert.InRange(next, 1, 99); Assert.True(Inspect(xml, offset: next).GetProperty("nextOffset").GetInt32() > next);
        var first = page.GetProperty("objects")[0]; Assert.True(first.GetProperty("ownerLinkCatalogLimited").GetBoolean()); Assert.Equal(50, first.GetProperty("ownerLinks").GetArrayLength());
    }
    [Fact] public void AmbiguousDestinationAndUnknownWorkerFieldsAreRejected()
    {
        string xml = Xml.Replace("<Name>B</Name></AttributeList>", "<Name>B</Name></AttributeList><ObjectList/>"); Assert.Throws<ArgumentException>(() => Change(xml, Clone(xml)));
        var clone = Clone(); clone.ReferenceTargets = new[] { Map(Row(), TargetPath(Xml, "A")) }; Assert.Throws<ArgumentException>(() => Change(Xml, clone));
        var row = Row(); Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "change_behaviors", ExtensionWire.Json(new { snapshot = Xml, arguments = new { changes = new[] { new {
            action = "clone", xPath = row.GetProperty("xPath").GetString(), expectedFingerprint = row.GetProperty("fingerprint").GetString(), destinationXPath = TargetPath(Xml, "B"), unknown = true } } } })));
    }
    [Fact] public void ReplacingAnEventRetainsItsCompositionPositionBeforeOtherNativeCompositions()
    {
        var document = XDocument.Parse(Xml); var target = document.Descendants("Hmi.Screen.Button").Last();
        target.Element("ObjectList")!.Add(new XElement("Hmi.Screen.Property", new XAttribute("ID", "7"), new XAttribute("CompositionName", "Properties")));
        string xml = document.ToString(); var remove = Operation(Inspect(xml).GetProperty("objects")[2], "remove"); var clone = Clone(xml);
        clone.Edits = new[] { new ClassicXmlEdit { XPath = "/Document/Hmi.Event.Event/AttributeList/Name", ExpectedValue = "Press", NewValue = "CopiedPress" } };
        var result = XDocument.Parse(Change(xml, remove, clone)); var list = result.Descendants("Hmi.Screen.Button").Last().Element("ObjectList")!;
        Assert.Equal(new[] { "Events", "Properties" }, list.Elements().Select(e => (string?)e.Attribute("CompositionName")));
        Assert.Equal("CopiedPress", list.Elements().First().Element("AttributeList")!.Element("Name")!.Value);
    }
    [Fact] public void MissingCompositionCanUseThePrototypeOwnersExistingCompositionOrder()
    {
        var document = XDocument.Parse(Xml); var buttons = document.Descendants("Hmi.Screen.Button").ToArray();
        buttons[0].Element("ObjectList")!.Add(new XElement("Hmi.Screen.Property", new XAttribute("ID", "7"), new XAttribute("CompositionName", "Properties")));
        buttons[1].Element("ObjectList")!.ReplaceNodes(new XElement("Hmi.Screen.Property", new XAttribute("ID", "8"), new XAttribute("CompositionName", "Properties")));
        string xml = document.ToString(); var result = XDocument.Parse(Change(xml, Clone(xml)));
        Assert.Equal(new[] { "Events", "Properties" }, result.Descendants("Hmi.Screen.Button").Last().Element("ObjectList")!.Elements().Select(e => (string?)e.Attribute("CompositionName")));
    }
    [Fact] public void UnknownDestinationCompositionOrderIsNotGuessed()
    {
        var document = XDocument.Parse(Xml); document.Descendants("Hmi.Screen.Button").Last().Element("ObjectList")!.ReplaceNodes(new XElement("Hmi.Screen.Property", new XAttribute("ID", "7"), new XAttribute("CompositionName", "Properties")));
        string xml = document.ToString(); Assert.Throws<NotSupportedException>(() => Change(xml, Clone(xml)));
    }
}
