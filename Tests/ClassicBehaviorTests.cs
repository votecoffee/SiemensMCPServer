using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ClassicBehaviorTests
{
    [Fact] public void BehaviorFingerprintDetectsWhitespaceBetweenStyledTextRuns()
    {
        string xml = Xml.Replace("<Name>Press</Name>", "<Name>Press</Name><Text><body><p><b>A</b> <i>B</i></p></body></Text>");
        string Fingerprint(string text) => JsonSerializer.SerializeToElement(new ClassicXmlDocument(text).BehaviorCatalog(true), JsonDefaults.Options).GetProperty("objects")[0].GetProperty("fingerprint").GetString()!;
        Assert.NotEqual(Fingerprint(xml), Fingerprint(xml.Replace("</b> <i>", "</b><i>")));
    }
    private const string Xml = "<Document><Hmi.Screen.Screen ID='0'><AttributeList><Name>S</Name></AttributeList><ObjectList><Hmi.Screen.Button ID='1' CompositionName='ScreenItems'><AttributeList><Name>A</Name></AttributeList><ObjectList><Hmi.Event.Event ID='2' CompositionName='Events'><AttributeList><Name>Press</Name></AttributeList><ObjectList><Hmi.Event.Function ID='3' CompositionName='Functions'><AttributeList><Name>SetTag</Name></AttributeList></Hmi.Event.Function></ObjectList><LinkList><Function TargetID='3'/><Control TargetID='1'/></LinkList></Hmi.Event.Event></ObjectList></Hmi.Screen.Button><Hmi.Screen.Button ID='4' CompositionName='ScreenItems'><AttributeList><Name>B</Name></AttributeList></Hmi.Screen.Button></ObjectList></Hmi.Screen.Screen></Document>";
    private static ClassicBehaviorChange Change(string action = "clone")
    {
        var node = JsonSerializer.SerializeToElement(new ClassicXmlDocument(Xml).BehaviorCatalog(true), JsonDefaults.Options).GetProperty("objects").EnumerateArray().First();
        return new ClassicBehaviorChange { Action = action, XPath = node.GetProperty("xPath").GetString()!, ExpectedFingerprint = node.GetProperty("fingerprint").GetString()!, DestinationXPath = action == "clone" ? "//Hmi.Screen.Button[AttributeList/Name='B']" : "" };
    }
    [Fact] public void CloneRetainsCompositionAndRemapsInternalReferencesOnly()
    {
        var change = Change(); change.Edits = new[] { new ClassicXmlEdit { XPath = "/Document/Hmi.Event.Event/AttributeList/Name", ExpectedValue = "Press", NewValue = "Release" } };
        var after = XDocument.Parse(new ClassicXmlDocument(Xml).ChangeBehaviors(new[] { change }));
        var copied = after.Descendants("Hmi.Event.Event").Last();
        Assert.Equal("Events", (string?)copied.Attribute("CompositionName")); Assert.Equal("Release", copied.Element("AttributeList")!.Element("Name")!.Value);
        Assert.Equal(copied.Descendants("Hmi.Event.Function").Single().Attribute("ID")!.Value, copied.Descendants("Function").Single().Attribute("TargetID")!.Value);
        Assert.Equal("1", copied.Descendants("Control").Single().Attribute("TargetID")!.Value);
        var ids = after.Descendants().Attributes("ID").Select(a => a.Value).ToArray(); Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal("Press", after.Descendants("Hmi.Event.Event").First().Element("AttributeList")!.Element("Name")!.Value);
    }
    [Fact] public void RemoveExactBehaviorAndRejectStaleOutsideAndOverlappingSelection()
    {
        Assert.Empty(XDocument.Parse(new ClassicXmlDocument(Xml).ChangeBehaviors(new[] { Change("remove") })).Descendants("Hmi.Event.Event"));
        var stale = Change(); stale.ExpectedFingerprint = "stale"; Assert.Throws<InvalidOperationException>(() => new ClassicXmlDocument(Xml).ChangeBehaviors(new[] { stale }));
        var outside = Change(); outside.DestinationXPath = "/Document"; Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(Xml).ChangeBehaviors(new[] { outside }));
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(Xml).ChangeBehaviors(new[] { Change(), Change() }));
    }
    [Fact] public void InboundReferencesPreventBehaviorRemoval()
    {
        string xml = Xml.Replace("</Hmi.Screen.Screen>", "<LinkList><Event TargetID='2'/></LinkList></Hmi.Screen.Screen>");
        Assert.Throws<InvalidOperationException>(() => new ClassicXmlDocument(xml).ChangeBehaviors(new[] { Change("remove") }));
    }
    [Fact] public void EditedCloneMustRetainResolvableReferencesAndSupportedIdentityFormat()
    {
        var change = Change(); change.Edits = new[] { new ClassicXmlEdit { XPath = "//Control/@TargetID", ExpectedValue = "1", NewValue = "missing" } };
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(Xml).ChangeBehaviors(new[] { change }));
        var node = XDocument.Parse("<Hmi.Event.Event ID='1' UId='opaque'/>").Root!;
        Assert.Throws<NotSupportedException>(() => NativeXmlIds.Clone(node, XDocument.Parse(Xml)));
    }
    [Fact] public void ExportIdRenumberingPreservesSemanticHashAndReferenceChangesDoNot()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-id-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "payload.xml"); File.WriteAllText(path, Xml); string hash = TransferFiles.SemanticHash(root);
            string renumbered = Xml.Replace("ID='", "ID='A"); File.WriteAllText(path, renumbered);
            Assert.Equal(hash, TransferFiles.SemanticHash(root));
            var first = JsonSerializer.SerializeToElement(new ClassicXmlDocument(Xml).BehaviorCatalog(true), JsonDefaults.Options).GetProperty("objects")[0];
            var second = JsonSerializer.SerializeToElement(new ClassicXmlDocument(renumbered).BehaviorCatalog(true), JsonDefaults.Options).GetProperty("objects")[0];
            Assert.Equal(first.GetProperty("fingerprint").GetString(), second.GetProperty("fingerprint").GetString());
            File.WriteAllText(path, renumbered.Replace("Control TargetID='A1'", "Control TargetID='A4'")); Assert.NotEqual(hash, TransferFiles.SemanticHash(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
