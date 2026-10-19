using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ClassicResourceTests
{
    private const string Xml = "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Screen1</Name><Width>800</Width><Height>480</Height></AttributeList><ObjectList><MultilingualText CompositionName='Caption'><ObjectList><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>Old &amp; text</Text></AttributeList></MultilingualTextItem><MultilingualTextItem><AttributeList><Culture>de-DE</Culture><Text>Deutsch</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Screen.Screen></Document>";
    private static ClassicXmlEdit Edit(string xpath = "//Width", string old = "800", string value = "900") => new ClassicXmlEdit { XPath = xpath, ExpectedValue = old, NewValue = value };
    [Fact] public void ComfortLayoutEditPreservesLanguagesAndOtherFields()
    {
        var document = new ClassicXmlDocument(Xml); var result = XDocument.Parse(document.Edit(new[] { Edit() }));
        Assert.Equal("900", result.Root!.Element("Hmi.Screen.Screen")!.Element("AttributeList")!.Element("Width")!.Value);
        Assert.Contains("Deutsch", result.ToString()); Assert.Contains("Old &amp; text", result.ToString()); Assert.Contains("480", result.ToString());
    }
    [Fact] public void CaptionEditsEscapeUnicodeAndPreserveOtherCultures()
    {
        var result = new ClassicXmlDocument(Xml).Edit(new[] { Edit("//MultilingualTextItem[AttributeList/Culture='en-US']/AttributeList/Text", "Old & text", "New <caption> — 🟢") });
        Assert.Contains("Deutsch", result); Assert.Contains("New &lt;caption&gt; — 🟢", result);
    }
    [Theory]
    [InlineData("//Name", "Screen1")][InlineData("//Missing", "")][InlineData("//AttributeList", "")]
    [InlineData("//Engineering/@version", "V15.1")][InlineData("//*[@ID='0']/AttributeList/Width", "800")][InlineData("//Hmi.Screen.Screen/@ID", "0")]
    public void RejectsAmbiguousComplexOutsideIdentityAndVolatileSelectors(string xpath, string old)
        => Assert.ThrowsAny<ArgumentException>(() => new ClassicXmlDocument(Xml).Edit(new[] { Edit(xpath, old) }));
    [Fact] public void RejectsStaleAndDuplicateSelectorsBeforeApplyingAnyEdit()
    {
        Assert.Throws<InvalidOperationException>(() => new ClassicXmlDocument(Xml).Edit(new[] { Edit(old: "stale") }));
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(Xml).Edit(new[] { Edit(), Edit("/Document/Hmi.Screen.Screen/AttributeList/Width") }));
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(Xml).Edit(new[] { Edit(value: "800") }));
    }
    [Fact] public void SlideinEdgeCannotBeChangedByScalarEdits()
    {
        const string xml = "<Document><Hmi.Screen.ScreenSlidein><AttributeList><SlideinType>Left</SlideinType><GridColor>0, 0, 0</GridColor></AttributeList></Hmi.Screen.ScreenSlidein></Document>";
        var document = new ClassicXmlDocument(xml);
        Assert.Throws<ArgumentException>(() => document.Edit(new[] { Edit("//SlideinType", "Left", "Right") }));
        var fields = JsonSerializer.SerializeToElement(document.Catalog(true), JsonDefaults.Options).GetProperty("fields").EnumerateArray().ToArray();
        Assert.Contains(fields, f => f.GetProperty("value").GetString() == "Left" && !f.GetProperty("editable").GetBoolean());
        Assert.Contains("1, 2, 3", document.Edit(new[] { Edit("//GridColor", "0, 0, 0", "1, 2, 3") }));
    }
    [Fact] public void ResourceParserRejectsDtdAndMultipleResources()
    {
        Assert.ThrowsAny<Exception>(() => new ClassicXmlDocument("<!DOCTYPE Document [<!ENTITY a SYSTEM 'file:///missing'>]><Document>&a;</Document>"));
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument("<Document><Hmi.Screen.Screen/><Hmi.Screen.Screen/></Document>"));
    }
    [Fact] public void CatalogPathsSelectExactEditableScalarsWithoutVolatileIdentities()
    {
        var fields = JsonSerializer.SerializeToElement(new ClassicXmlDocument(Xml).Catalog(true), JsonDefaults.Options).GetProperty("fields").EnumerateArray().ToArray();
        Assert.Contains(fields, f => f.GetProperty("xPath").GetString()!.EndsWith("/@ID") && !f.GetProperty("editable").GetBoolean());
        Assert.Contains(fields, f => f.GetProperty("value").GetString() == "Caption" && !f.GetProperty("editable").GetBoolean());
        foreach (var field in fields.Where(f => f.GetProperty("editable").GetBoolean()))
        {
            string path = field.GetProperty("xPath").GetString()!, value = field.GetProperty("value").GetString()!;
            Assert.DoesNotContain("@ID", path);
            string result = new ClassicXmlDocument(Xml).Edit(new[] { Edit(path, value, value + " updated") });
            Assert.Equal(value + " updated", XDocument.Parse(result).XPathSelectElement(path)!.Value);
        }
    }
    [Fact] public void CatalogDistinguishesRepeatedAndNamespacedScalars()
    {
        string xml = "<Document><Hmi.Screen.Screen><AttributeList><Name>S</Name></AttributeList><ObjectList xmlns:q='urn:a&apos;&quot;b'><q:Value q:Flag='first'>one</q:Value><q:Value q:Flag='second'>two</q:Value></ObjectList></Hmi.Screen.Screen></Document>";
        var fields = JsonSerializer.SerializeToElement(new ClassicXmlDocument(xml).Catalog(true), JsonDefaults.Options).GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(fields.Length, fields.Select(f => f.GetProperty("xPath").GetString()).Distinct().Count());
        foreach (var field in fields.Where(f => f.GetProperty("editable").GetBoolean()))
        {
            string path = field.GetProperty("xPath").GetString()!, value = field.GetProperty("value").GetString()!;
            string result = new ClassicXmlDocument(xml).Edit(new[] { Edit(path, value, value + " changed") });
            Assert.Contains(value + " changed", XDocument.Parse(result).Root!.Value + string.Join("", XDocument.Parse(result).Descendants().Attributes().Select(a => a.Value)));
        }
    }
    [Fact] public void CatalogIsBoundedAndProtectedResourcesAdvertiseNoEdits()
    {
        string xml = "<Document><Hmi.Screen.Screen><AttributeList><Name>S</Name></AttributeList><ObjectList>" + string.Concat(Enumerable.Repeat("<Value>value</Value>", 1100)) + "</ObjectList></Hmi.Screen.Screen></Document>";
        var catalog = JsonSerializer.SerializeToElement(new ClassicXmlDocument(xml).Catalog(false), JsonDefaults.Options);
        Assert.True(catalog.GetProperty("truncated").GetBoolean()); Assert.Equal(1000, catalog.GetProperty("fields").GetArrayLength());
        Assert.All(catalog.GetProperty("fields").EnumerateArray(), f => Assert.False(f.GetProperty("editable").GetBoolean()));
    }
    [Fact] public void CompositionMetadataAndEmptyContainersRequireStructuralImport()
    {
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(Xml).Edit(new[] { Edit("//MultilingualText/@CompositionName", "Caption", "Other") }));
        string xml = "<Document><Hmi.Screen.Screen><AttributeList><Name>S</Name></AttributeList><ObjectList/></Hmi.Screen.Screen></Document>";
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument(xml).Edit(new[] { Edit("//ObjectList", "", "data") }));
    }
    [Fact] public void PreviewPreservesBinarySidecarsAndApplyIsSingleAttempt()
    {
        using var fixture = new Fixture(); var workflow = new ClassicXmlWorkflow(fixture, fixture.Plans);
        var snapshot = fixture.ReadClassicResource("hmi", "screen");
        var plan = JsonSerializer.SerializeToElement(workflow.Preview("hmi", "screen", snapshot.Fingerprint, new[] { Edit() }), JsonDefaults.Options);
        string directory = plan.GetProperty("artifactDirectory").GetString()!;
        Assert.Equal(new byte[] { 0, 255, 1, 10 }, File.ReadAllBytes(Path.Combine(directory, "candidate-files", "image.png")));
        string id = plan.GetProperty("planId").GetString()!, hash = plan.GetProperty("candidateSha256").GetString()!;
        workflow.Apply(id, hash); Assert.Equal(1, fixture.Attempts);
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
    }
    [Fact] public void BrokerRejectsExtensionIdentityAndSlideinEdgeChanges()
    {
        using var fixture = new Fixture(); var workflow = new ClassicXmlWorkflow(fixture, fixture.Plans);
        var before = fixture.ReadClassicResource("hmi", "screen");
        Assert.Throws<InvalidOperationException>(() => workflow.PreviewTransformed("hmi", "screen", before.Fingerprint,
            xml => xml.Replace("<Name>Screen1</Name>", "<Name>Renamed</Name>"), new { }));
        File.WriteAllText(Path.Combine(before.Directory, before.XmlFile), "<Document><Hmi.Screen.ScreenSlidein><AttributeList><SlideinType>Left</SlideinType><GridColor>0</GridColor></AttributeList></Hmi.Screen.ScreenSlidein></Document>");
        before = fixture.ReadClassicResource("hmi", "screen");
        Assert.Throws<InvalidOperationException>(() => workflow.PreviewTransformed("hmi", "screen", before.Fingerprint,
            xml => xml.Replace("<SlideinType>Left</SlideinType>", "<SlideinType>Right</SlideinType>"), new { }));
        Assert.Equal(0, fixture.Attempts);
    }
    [Fact] public void ChangedCandidateOrSidecarCannotReachNativeBackend()
    {
        using var fixture = new Fixture(); var workflow = new ClassicXmlWorkflow(fixture, fixture.Plans);
        var plan = JsonSerializer.SerializeToElement(workflow.Preview("hmi", "screen", fixture.ReadClassicResource("hmi", "screen").Fingerprint, new[] { Edit() }), JsonDefaults.Options);
        File.AppendAllText(Path.Combine(plan.GetProperty("artifactDirectory").GetString()!, "candidate-files", "image.png"), "tamper");
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!)); Assert.Equal(0, fixture.Attempts);
    }
    [Fact] public void BrokerRejectsInconsistentLineCandidatesFromAnExtensionBeforeStaging()
    {
        using var fixture = new Fixture(); var workflow = new ClassicXmlWorkflow(fixture, fixture.Plans);
        var before = fixture.ReadClassicResource("hmi", "screen");
        const string xml = "<Document><Hmi.Screen.Screen><AttributeList><Name>Screen1</Name></AttributeList><ObjectList><Hmi.Screen.Line CompositionName='ScreenItems'><AttributeList><ObjectName>Line</ObjectName><Left>10</Left><Top>20</Top><Width>80</Width><Height>0</Height><StartLeft>10</StartLeft><StartTop>20</StartTop><EndLeft>90</EndLeft><EndTop>20</EndTop></AttributeList></Hmi.Screen.Line></ObjectList></Hmi.Screen.Screen></Document>";
        File.WriteAllText(Path.Combine(before.Directory, before.XmlFile), xml);
        before = fixture.ReadClassicResource("hmi", "screen");
        Assert.Throws<ArgumentException>(() => workflow.PreviewTransformed("hmi", "screen", before.Fingerprint,
            source => source.Replace("<Left>10</Left>", "<Left>11</Left>"), new { }));
        Assert.Equal(0, fixture.Attempts); Assert.False(Directory.Exists(fixture.Plans));
        Assert.Equal(xml, File.ReadAllText(Path.Combine(before.Directory, before.XmlFile)));
    }
    [Fact] public void MissingExternalCompanionCannotProduceAnApplicableScalarPreview()
    {
        using var fixture = new Fixture();
        string resource = Path.Combine(fixture.ReadClassicResource("hmi", "screen").Directory, "payload.xml");
        File.WriteAllText(resource, Xml.Replace("<Width>800</Width>", "<Width>800</Width><Image external='path'>image.png</Image>"));
        var workflow = new ClassicXmlWorkflow(fixture, fixture.Plans);
        Assert.Throws<FileNotFoundException>(() => workflow.Preview("hmi", "screen", fixture.ReadClassicResource("hmi", "screen").Fingerprint,
            new[] { Edit("//Image", "image.png", "missing.png") }));
        Assert.Equal(0, fixture.Attempts);
    }
    [Fact] public void NativeFailureConsumesPlanAndProtectedCycleCannotPreview()
    {
        using var fixture = new Fixture(); var workflow = new ClassicXmlWorkflow(fixture, fixture.Plans);
        fixture.Editable = false;
        Assert.Throws<InvalidOperationException>(() => workflow.Preview("hmi", "screen", fixture.ReadClassicResource("hmi", "screen").Fingerprint, new[] { Edit() }));
        fixture.Editable = true; fixture.Fail = true;
        var plan = JsonSerializer.SerializeToElement(workflow.Preview("hmi", "screen", fixture.ReadClassicResource("hmi", "screen").Fingerprint, new[] { Edit() }), JsonDefaults.Options);
        string id = plan.GetProperty("planId").GetString()!, hash = plan.GetProperty("candidateSha256").GetString()!;
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash)); Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash)); Assert.Equal(1, fixture.Attempts);
    }
    [Fact] public void BaselineFingerprintRemainsBoundWhenOriginalArtifactChanges()
    {
        using var fixture = new Fixture(); var workflow = new ClassicXmlWorkflow(fixture, fixture.Plans);
        var before = fixture.ReadClassicResource("hmi", "screen");
        var plan = JsonSerializer.SerializeToElement(workflow.Preview("hmi", "screen", before.Fingerprint, new[] { Edit() }), JsonDefaults.Options);
        File.WriteAllText(Path.Combine(before.Directory, before.XmlFile), Xml.Replace("<Width>800</Width>", "<Width>1000</Width>"));
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
        Assert.Equal(1, fixture.Attempts);
    }
    private sealed class Fixture : IClassicResourceBackend, IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "comfort-resource-test-" + Guid.NewGuid().ToString("N"));
        private readonly string native;
        public string Plans => Path.Combine(root, "plans"); public int Attempts; public bool Fail; public bool Editable = true;
        public Fixture() { native = Path.Combine(root, "native"); Directory.CreateDirectory(native); File.WriteAllText(Path.Combine(native, "payload.xml"), Xml, new UTF8Encoding(false)); File.WriteAllBytes(Path.Combine(native, "image.png"), new byte[] { 0, 255, 1, 10 }); }
        public ClassicResource ReadClassicResource(string hmiId, string path) => new ClassicResource { HmiId = hmiId, Path = path, Directory = native, XmlFile = "payload.xml", Editable = Editable };
        public void ImportClassicResource(ClassicResource before, string candidateDirectory, string expectedFingerprint) { Attempts++; if (Fail || expectedFingerprint != TransferFiles.SemanticHash(native)) throw new InvalidOperationException(); }
        public void Dispose() { Directory.Delete(root, true); }
    }
}
