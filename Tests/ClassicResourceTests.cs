using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SiemensTiaOpenness.Mcp;
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
    [Fact] public void ResourceParserRejectsDtdAndMultipleResources()
    {
        Assert.ThrowsAny<Exception>(() => new ClassicXmlDocument("<!DOCTYPE Document [<!ENTITY a SYSTEM 'file:///missing'>]><Document>&a;</Document>"));
        Assert.Throws<ArgumentException>(() => new ClassicXmlDocument("<Document><Hmi.Screen.Screen/><Hmi.Screen.Screen/></Document>"));
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
    [Fact] public void ChangedCandidateOrSidecarCannotReachNativeBackend()
    {
        using var fixture = new Fixture(); var workflow = new ClassicXmlWorkflow(fixture, fixture.Plans);
        var plan = JsonSerializer.SerializeToElement(workflow.Preview("hmi", "screen", fixture.ReadClassicResource("hmi", "screen").Fingerprint, new[] { Edit() }), JsonDefaults.Options);
        File.AppendAllText(Path.Combine(plan.GetProperty("artifactDirectory").GetString()!, "candidate-files", "image.png"), "tamper");
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!)); Assert.Equal(0, fixture.Attempts);
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
