using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class NativeComfortXmlTests
{
    // Anonymous V15.1-shaped fixture: screen controls use ObjectName, internal links #ID.
    private const string Xml = "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Screen</Name><Number>1</Number></AttributeList><ObjectList>"
        + "<Hmi.Screen.ScreenLayer ID='1' CompositionName='Layers'><AttributeList><Name>Layer</Name></AttributeList><ObjectList>"
        + "<Hmi.Screen.Button ID='2' CompositionName='ScreenItems'><AttributeList><ObjectName>Start</ObjectName><Left>10</Left><Top>20</Top><Width>80</Width><Height>30</Height></AttributeList><ObjectList>"
        + "<Hmi.Event.Event ID='3' CompositionName='Events'><AttributeList><Name>Press</Name></AttributeList><ObjectList>"
        + "<Hmi.Event.FunctionListEventHandler ID='4' CompositionName='EventHandler'><ObjectList>"
        + "<Hmi.Event.FunctionListEntry ID='5' CompositionName='FunctionListEntries'><AttributeList><Name>ShowScreen</Name></AttributeList><ObjectList>"
        + "<Hmi.Event.FunctionListEntryParameter ID='6' CompositionName='Parameters'><AttributeList><Name>Screen</Name></AttributeList><LinkList><Value TargetID='#0'/></LinkList></Hmi.Event.FunctionListEntryParameter>"
        + "</ObjectList></Hmi.Event.FunctionListEntry></ObjectList></Hmi.Event.FunctionListEventHandler></ObjectList>"
        + "<LinkList><Function TargetID='#4'/><Owner TargetID='#2'/><Layer TargetID='#1'/><Tag TargetID='@OpenLink'><Name>Tag_A</Name></Tag></LinkList></Hmi.Event.Event>"
        + "<MultilingualText ID='7' CompositionName='Text'><ObjectList><MultilingualTextItem ID='8' CompositionName='Items'><AttributeList><Culture>en-US</Culture><Text><body><p><b>Start</b> <i>ready</i></p></body></Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>"
        + "</ObjectList></Hmi.Screen.Button><Hmi.Screen.Button ID='9' CompositionName='ScreenItems'><AttributeList><ObjectName>Stop</ObjectName><Left>100</Left><Top>20</Top><Width>80</Width><Height>30</Height></AttributeList></Hmi.Screen.Button>"
        + "</ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>";
    private static JsonElement Inspect(string operation, string xml = Xml) => FeatureOperations.Execute("comfort", operation, ExtensionWire.Json(new { xml }));
    private static JsonElement[] Items(string xml = Xml) => Inspect("inspect_screen_items", xml).GetProperty("items").EnumerateArray().ToArray();
    private static JsonElement Behavior(string xml = Xml) => Inspect("inspect_behaviors", xml).GetProperty("objects")[0];
    private static string Candidate(string operation, object arguments, string xml = Xml) => FeatureOperations.Execute("comfort", operation, ExtensionWire.Json(new { snapshot = xml, arguments })).GetProperty("candidateXml").GetString()!;
    private static ClassicScreenItemChange Clone(string xml = Xml) => new ClassicScreenItemChange {
        Action = "clone", Selection = ExtensionWire.Parse<ClassicScreenItemSelection>(Items(xml)[0].GetProperty("selection")), DestinationXPath = Items(xml)[1].GetProperty("containerXPath").GetString(),
        Names = new[] { new ClassicScreenItemName { SourceXPath = Items(xml)[0].GetProperty("xPath").GetString()!, NewName = "Copy" } } };
    private static string Change(params ClassicScreenItemChange[] changes) => Candidate("change_screen_items", new { changes });
    private static string Renumber(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        foreach (var id in doc.Descendants().Attributes("ID")) id.Value = "A" + id.Value;
        foreach (var link in doc.Descendants().Attributes("TargetID").Where(a => a.Value != "@OpenLink")) link.Value = "#A" + link.Value.Substring(1);
        return doc.ToString(SaveOptions.DisableFormatting);
    }
    [Fact] public void NativeNamesEnableControlGeometryAndCaptionInspection()
    {
        Assert.Equal(new[] { "Start", "Stop" }, Items().Select(r => r.GetProperty("name").GetString()));
        Assert.All(Items(), r => Assert.True(r.GetProperty("cloneAvailable").GetBoolean()));
        var layout = Inspect("inspect_screen_layout").GetProperty("items");
        Assert.Equal("Start", layout[0].GetProperty("name").GetString()); Assert.True(layout[0].GetProperty("geometryAvailable").GetBoolean());
        Assert.Equal("Start", Inspect("inspect_screen_texts").GetProperty("runs")[0].GetProperty("ownerName").GetString());
        Assert.Equal("Start", Behavior().GetProperty("ownerName").GetString()); Assert.True(Behavior().GetProperty("cloneAvailable").GetBoolean());
    }
    [Fact] public void CloneRemapsHashPrefixedInternalLinksAndRetainsExternalTargets()
    {
        var doc = XDocument.Parse(Change(Clone()), LoadOptions.PreserveWhitespace);
        var copy = doc.Descendants("Hmi.Screen.Button").Single(e => e.Element("AttributeList")!.Element("ObjectName")!.Value == "Copy");
        Assert.Null(copy.Element("AttributeList")!.Element("Name"));
        Assert.Equal("#" + copy.Attribute("ID")!.Value, copy.Descendants("Owner").Single().Attribute("TargetID")!.Value);
        Assert.Equal("#" + copy.Descendants("Hmi.Event.FunctionListEventHandler").Single().Attribute("ID")!.Value, copy.Descendants("Function").Single().Attribute("TargetID")!.Value);
        Assert.Equal("#0", copy.Descendants("Value").Single().Attribute("TargetID")!.Value); Assert.Equal("#1", copy.Descendants("Layer").Single().Attribute("TargetID")!.Value);
        Assert.Equal("@OpenLink", copy.Descendants("Tag").Single().Attribute("TargetID")!.Value); Assert.Equal(" ", copy.Descendants("b").Single().NextNode!.ToString());
        NativeXmlIds.VerifyReferences(doc.Root!.Elements().Last(), doc);
        Assert.Equal(doc.Descendants().Attributes("ID").Count(), doc.Descendants().Attributes("ID").Select(a => a.Value).Distinct().Count());
    }
    [Fact] public void ReferenceCatalogResolvesNativeTargetsAndAllowsOnlySupportedSymbolicChanges()
    {
        var refs = Inspect("inspect_references").GetProperty("references").EnumerateArray().ToArray();
        Assert.Equal("Screen", refs.Single(r => r.GetProperty("linkType").GetString() == "Value").GetProperty("targetName").GetString());
        var owner = refs.Single(r => r.GetProperty("linkType").GetString() == "Owner"); Assert.Equal("Start", owner.GetProperty("targetName").GetString()); Assert.False(owner.GetProperty("editable").GetBoolean());
        var tag = refs.Single(r => r.GetProperty("linkType").GetString() == "Tag"); Assert.True(tag.GetProperty("editable").GetBoolean()); Assert.Equal("Start", tag.GetProperty("controlName").GetString());
        var doc = XDocument.Parse(Candidate("rebind_references", new { edits = new[] { new { selection = tag.GetProperty("selection"), newName = "Tag_B" } } }));
        Assert.Equal("Tag_B", doc.Descendants("Tag").Single().Element("Name")!.Value); Assert.Equal("#2", doc.Descendants("Owner").Single().Attribute("TargetID")!.Value);
    }
    [Fact] public void BehaviorOwnerMappingPreservesHashPrefixAndNestedFunctionParameters()
    {
        var row = Behavior(); var map = row.GetProperty("ownerLinks")[0];
        var doc = XDocument.Parse(Candidate("change_behaviors", new { changes = new[] { new {
            action = "clone", xPath = row.GetProperty("xPath").GetString(), expectedFingerprint = row.GetProperty("fingerprint").GetString(), destinationXPath = Items()[1].GetProperty("xPath").GetString(),
            referenceTargets = new[] { new { xPath = map.GetProperty("xPath").GetString(), expectedTargetXPath = map.GetProperty("expectedTargetXPath").GetString(), newTargetXPath = Items()[1].GetProperty("xPath").GetString() } } } } }));
        var copy = doc.Descendants("Hmi.Event.Event").Last();
        Assert.Equal("#9", copy.Descendants("Owner").Single().Attribute("TargetID")!.Value);
        Assert.Equal("#" + copy.Descendants("Hmi.Event.FunctionListEventHandler").Single().Attribute("ID")!.Value, copy.Descendants("Function").Single().Attribute("TargetID")!.Value);
        Assert.Equal("#0", copy.Descendants("Value").Single().Attribute("TargetID")!.Value);
        Assert.Equal("#2", doc.Descendants("Hmi.Event.Event").First().Descendants("Owner").Single().Attribute("TargetID")!.Value);
    }
    [Theory] [InlineData("control")] [InlineData("behavior")]
    public void RetainedHashPrefixedLinksBlockRemoval(string kind)
    {
        string xml = Xml.Replace("<ObjectName>Stop</ObjectName>", "<ObjectName>Stop</ObjectName><Dependency TargetID='#" + (kind == "control" ? "2" : "3") + "'/>");
        if (kind == "control")
        {
            var row = Items(xml)[0]; Assert.False(row.GetProperty("standaloneRemovalAvailable").GetBoolean());
            Assert.Throws<InvalidOperationException>(() => Candidate("change_screen_items", new { changes = new[] { new { action = "remove", selection = row.GetProperty("selection") } } }, xml));
        }
        else
        {
            var row = Behavior(xml); Assert.False(row.GetProperty("standaloneRemovalAvailable").GetBoolean());
            Assert.Throws<InvalidOperationException>(() => Candidate("change_behaviors", new { changes = new[] { new { action = "remove", xPath = row.GetProperty("xPath").GetString(), expectedFingerprint = row.GetProperty("fingerprint").GetString() } } }, xml));
        }
    }
    [Fact] public void NativeFingerprintsAndHostReadbackIgnoreIdRenumberingButDetectChangedTargets()
    {
        string renamed = Renumber(Xml);
        Assert.Equal(Items()[0].GetProperty("selection").GetProperty("expectedFingerprint").GetString(), Items(renamed)[0].GetProperty("selection").GetProperty("expectedFingerprint").GetString());
        Assert.Equal(Behavior().GetProperty("fingerprint").GetString(), Behavior(renamed).GetProperty("fingerprint").GetString());
        string root = Path.Combine(Path.GetTempPath(), "native-comfort-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "payload.xml"); File.WriteAllText(file, Xml); string baseline = TransferFiles.SemanticHash(root);
            File.WriteAllText(file, renamed); Assert.Equal(baseline, TransferFiles.SemanticHash(root));
            File.WriteAllText(file, renamed.Replace("TargetID=\"#A0\"", "TargetID=\"#A1\"")); Assert.NotEqual(baseline, TransferFiles.SemanticHash(root));
            File.WriteAllText(file, Xml.Replace("TargetID='#0'", "TargetID='#object-0'")); Assert.NotEqual(baseline, TransferFiles.SemanticHash(root));
            File.WriteAllText(file, Xml.Replace("</b> <i>", "</b>  <i>")); Assert.NotEqual(baseline, TransferFiles.SemanticHash(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory] [InlineData("#missing")] [InlineData("##0")] [InlineData("#@OpenLink")] [InlineData("")]
    public void UnknownNativeLinksStayUnavailable(string target)
    {
        string xml = Xml.Replace("TargetID='#0'", "TargetID='" + target + "'");
        Assert.False(Items(xml)[0].GetProperty("cloneAvailable").GetBoolean()); Assert.False(Behavior(xml).GetProperty("cloneAvailable").GetBoolean());
        Assert.Throws<NotSupportedException>(() => Candidate("change_screen_items", new { changes = new[] { Clone(xml) } }, xml));
    }
    [Theory] [InlineData("<Name>Other</Name>")] [InlineData("<ObjectName>Other</ObjectName>")]
    public void AmbiguousControlNamesAreNeverSelectedForModification(string extra)
    {
        string xml = Xml.Replace("<ObjectName>Start</ObjectName>", "<ObjectName>Start</ObjectName>" + extra);
        Assert.False(Items(xml)[0].GetProperty("cloneAvailable").GetBoolean()); Assert.False(Inspect("inspect_screen_layout", xml).GetProperty("items")[0].GetProperty("geometryAvailable").GetBoolean());
    }
    [Fact] public void PackageBuilderUsesExistingObjectNameWithoutAddingName()
    {
        var template = XDocument.Parse(Xml); template.Descendants("Tag").Remove();
        var result = ComfortPackage.Build(new ComfortPackageSpec { Resources = new[] { new ComfortResourceSpec { Kind = "screen", Name = "Generated", TemplateXml = template.ToString(), KeepTemplateMembers = false,
            Members = new[] { new ComfortMemberSpec { Prototype = "Start", Name = "Copy" } } } } });
        var doc = XDocument.Parse(result.Files.Single(f => f.Path.EndsWith("payload.xml")).Content); var copy = doc.Descendants("Hmi.Screen.Button").Single();
        Assert.Equal("Copy", copy.Element("AttributeList")!.Element("ObjectName")!.Value); Assert.Null(copy.Element("AttributeList")!.Element("Name"));
        Assert.Equal("#" + copy.Attribute("ID")!.Value, copy.Descendants("Owner").Single().Attribute("TargetID")!.Value); Assert.Equal("#0", copy.Descendants("Value").Single().Attribute("TargetID")!.Value);
    }
    [Fact] public void MissingPrototypeLinksCannotBecomeValidThroughNewIdAllocation()
    {
        var doc = XDocument.Parse("<Document><Hmi.Screen.Screen ID='0'/></Document>");
        var prototype = XElement.Parse("<Hmi.Screen.Button ID='4'><LinkList><Owner TargetID='#1'/></LinkList></Hmi.Screen.Button>");
        Assert.Throws<ArgumentException>(() => NativeXmlIds.Clone(prototype, doc));
    }
    [Theory] [InlineData("#0")] [InlineData("@OpenLink")] [InlineData("")]
    public void InvalidExportedIdentitiesCannotCollideWithLinkSyntax(string id)
    { Assert.Throws<ArgumentException>(() => NativeXmlIds.Normalize(XDocument.Parse("<Document><Hmi.Screen.Screen ID='" + id + "'/></Document>"))); }
    [Fact] public void EveryComfortNativePreviewDeclaresTheNewHostContract()
    {
        var operations = FeatureOperations.Describe("comfort").Where(o => o.Route == "classicXml").ToArray(); Assert.Equal(6, operations.Length);
        Assert.All(operations, o => { Assert.Equal("classicReferences", o.SnapshotKind); Assert.True(o.IsCompatible("comfort")); Assert.False(o.IsCompatible("unified")); o.Preview = false; Assert.False(o.IsCompatible("comfort")); });
        var recipe = new FeatureRecipes("comfort", "[{\"name\":\"set_left\",\"xPath\":\"//Left\",\"newValue\":\"20\"}]").Operations.Single();
        Assert.Equal("classicReferences", recipe.SnapshotKind); Assert.True(recipe.IsCompatible("comfort"));
    }
}
