using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SiemensTiaOpenness.Mcp;
using Xunit;

namespace SiemensTiaOpenness.Tests;

public sealed class TextListTests
{
    // Synthetic classic schema; no customer project data is distributed.
    internal static string Fixture(string english = "Old & ready") => new XDocument(
        new XElement("Document", new XElement("Engineering", new XAttribute("version", "V15.1")),
        new XElement("Hmi.TextGraphicList.TextList", new XAttribute("ID", "0"),
            new XElement("AttributeList", new XElement("Name", "Messages_TL"), new XElement("ListRange", "Decimal")),
            new XElement("ObjectList", Entry("1", "7", false, english, "Español intacto"), Entry("9", "0", true, "Fallback", "Reserva")))))
        .ToString();

    private static XElement Entry(string id, string index, bool fallback, string english, string spanish) =>
        new XElement("Hmi.TextGraphicList.TextListEntry", new XAttribute("ID", id), new XAttribute("CompositionName", "Entries"),
            new XElement("AttributeList", new XElement("DefaultEntry", fallback ? "true" : "false"), new XElement("EntryType", "SingleValue"),
                new XElement("From", index), new XElement("To", index)),
            new XElement("ObjectList", new XElement("MultilingualText", new XAttribute("ID", id + "A"), new XAttribute("CompositionName", "Text"),
                new XElement("ObjectList", Item(id + "B", "en-US", english), Item(id + "C", "es-ES", spanish)))));

    private static XElement Item(string id, string culture, string value) =>
        new XElement("MultilingualTextItem", new XAttribute("ID", id), new XAttribute("CompositionName", "Items"),
            new XElement("AttributeList", new XElement("Culture", culture), new XElement("Text", value)));

    private static TextEdit Edit(string newText = "New “ready” & café\nSecond line") => new TextEdit {
        Entry = "7", Culture = "en-US", ExpectedText = "Old & ready", NewText = newText
    };

    [Fact]
    public void EditsOnlySelectedLanguageAndPreservesMetadataAndOriginal()
    {
        var source = new TextListDocument(Fixture());
        string original = source.ToXml();
        var edited = source.Edit(new[] { Edit() });
        Assert.Equal(original, source.ToXml());
        Assert.Equal(Edit().NewText, edited.Read().Single(v => v.Entry == "7" && v.Culture == "en-US").Text);
        Assert.Equal("Español intacto", edited.Read().Single(v => v.Entry == "7" && v.Culture == "es-ES").Text);
        Assert.Equal("Fallback", edited.Read().Single(v => v.Entry == "Default" && v.Culture == "en-US").Text);
        var expected = XDocument.Parse(original);
        expected.Descendants("MultilingualTextItem").First().Element("AttributeList").Element("Text").Value = Edit().NewText;
        Assert.True(XNode.DeepEquals(expected, XDocument.Parse(edited.ToXml())));
    }

    [Fact]
    public void RejectsStaleOldTextAndMissingOrDuplicateTargets()
    {
        var source = new TextListDocument(Fixture());
        var stale = Edit(); stale.ExpectedText = "outdated";
        Assert.Throws<InvalidOperationException>(() => source.Edit(new[] { stale }));
        var missing = Edit(); missing.Culture = "de-DE";
        Assert.Throws<InvalidOperationException>(() => source.Edit(new[] { missing }));
        var absent = Edit(); absent.Entry = "99";
        Assert.Throws<InvalidOperationException>(() => source.Edit(new[] { absent }));
        Assert.Throws<ArgumentException>(() => source.Edit(new[] { Edit(), Edit() }));
    }

    [Fact]
    public void FingerprintIgnoresExportIdsAndFormattingButDetectsUnselectedLanguageDrift()
    {
        var source = new TextListDocument(Fixture());
        var changed = XDocument.Parse(Fixture());
        int id = 100;
        foreach (var attribute in changed.Descendants().Attributes("ID")) attribute.Value = (++id).ToString();
        Assert.Equal(source.Fingerprint, new TextListDocument(changed.ToString(SaveOptions.DisableFormatting)).Fingerprint);
        changed.Descendants("Culture").First(e => e.Value == "es-ES").Parent.Element("Text").Value = "changed elsewhere";
        Assert.NotEqual(source.Fingerprint, new TextListDocument(changed.ToString()).Fingerprint);
    }

    [Fact]
    public void RejectsFormattedTextAndRangesWithoutChangingSource()
    {
        var xml = XDocument.Parse(Fixture());
        xml.Descendants("Text").First().ReplaceNodes(new XElement("body", new XElement("p", new XElement("span", "styled"))));
        var rich = new TextListDocument(xml.ToString());
        Assert.False(rich.Read().First().Editable);
        Assert.Throws<InvalidOperationException>(() => rich.Edit(new[] { Edit() }));
        xml = XDocument.Parse(Fixture());
        xml.Descendants("To").First().Value = "8";
        var range = new TextListDocument(xml.ToString());
        var edit = Edit(); edit.Entry = "7..8";
        Assert.Throws<InvalidOperationException>(() => range.Edit(new[] { edit }));
    }

    internal static string NativePlainFixture()
    {
        var xml = XDocument.Parse(Fixture());
        foreach (var text in xml.Descendants("Text")) text.ReplaceNodes(new XElement("body", new XElement("p", text.Value)));
        return xml.ToString();
    }

    [Fact]
    public void NativeV151PlainParagraphEditsPreserveWrapperAndOtherLanguages()
    {
        var source = new TextListDocument(NativePlainFixture());
        Assert.True(source.Read().First().Editable);
        Assert.Equal("Old & ready", source.Read().First().Text);
        var edited = source.Edit(new[] { Edit("New “ready” & café") });
        Assert.Equal("New “ready” & café", edited.Read().First().Text);
        var expected = XDocument.Parse(source.ToXml());
        expected.Descendants("p").First().Value = "New “ready” & café";
        Assert.True(XNode.DeepEquals(expected, XDocument.Parse(edited.ToXml())));
        Assert.Equal(source.Fingerprint, edited.Edit(new[] { new TextEdit { Entry = "7", Culture = "en-US", ExpectedText = "New “ready” & café", NewText = "Old & ready" } }).Fingerprint);
    }

    [Theory]
    [InlineData("<body><p style='color:red'>styled</p></body>")]
    [InlineData("<body><p><span>run</span></p></body>")]
    [InlineData("<body><p>one</p><p>two</p></body>")]
    [InlineData("<body><p>caption<field>dynamic</field></p></body>")]
    [InlineData("<body><p>caption</p></body><unknown />")]
    [InlineData("<body><!-- annotation --><p>caption</p></body>")]
    public void UnsupportedNativeMarkupRemainsReadOnly(string markup)
    {
        var xml = XDocument.Parse(Fixture());
        xml.Descendants("Text").First().ReplaceNodes(XElement.Parse("<Text>" + markup + "</Text>").Nodes());
        var source = new TextListDocument(xml.ToString());
        Assert.False(source.Read().First().Editable);
        Assert.Throws<InvalidOperationException>(() => source.Edit(new[] { Edit() }));
    }

    [Fact]
    public void RejectsAmbiguousAndEntityXml()
    {
        var xml = XDocument.Parse(Fixture());
        var list = xml.Root.Element("Hmi.TextGraphicList.TextList");
        xml.Root.Add(new XElement(list));
        Assert.Throws<InvalidDataException>(() => new TextListDocument(xml.ToString()));
        Assert.ThrowsAny<Exception>(() => new TextListDocument("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///missing'>]><Document>&x;</Document>"));
        xml = XDocument.Parse(Fixture());
        var entries = xml.Descendants("Hmi.TextGraphicList.TextListEntry").First();
        entries.Parent.Add(new XElement(entries));
        Assert.Throws<InvalidDataException>(() => new TextListDocument(xml.ToString()));
    }

    [Fact]
    public void PreviewWritesDiffAndBackupAndApplyIsSingleUse()
    {
        using var directory = new Scratch();
        var backend = new FakeBackend();
        var workflow = new TextListWorkflow(backend, directory.Path);
        var preview = Preview(workflow, backend);
        string artifact = preview.GetProperty("artifactDirectory").GetString();
        Assert.Equal(backend.Xml, File.ReadAllText(System.IO.Path.Combine(artifact, "before.xml")));
        Assert.Equal(0, backend.Imports);
        workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString());
        Assert.Equal(1, backend.Imports);
        Assert.Contains("New", new TextListDocument(backend.Xml).Read().First().Text);
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString()));
    }

    [Fact]
    public void StaleNativeContentCannotBeImportedAndPlanCannotBeRetried()
    {
        using var directory = new Scratch();
        var backend = new FakeBackend();
        var workflow = new TextListWorkflow(backend, directory.Path);
        var preview = Preview(workflow, backend);
        backend.Xml = Fixture("Changed by TIA editor");
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString()));
        Assert.Equal(0, backend.Imports);
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString()));
    }

    [Fact]
    public void CandidateTamperingAndWrongHashesCannotReachImporter()
    {
        using var directory = new Scratch();
        var backend = new FakeBackend();
        var workflow = new TextListWorkflow(backend, directory.Path);
        var preview = Preview(workflow, backend);
        string id = preview.GetProperty("planId").GetString();
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, "incorrect"));
        File.AppendAllText(System.IO.Path.Combine(preview.GetProperty("artifactDirectory").GetString(), "candidate.xml"), "tampered");
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, preview.GetProperty("candidateSha256").GetString()));
        Assert.Equal(0, backend.Imports);
    }

    [Fact]
    public void FailedNativeImportConsumesPlanAndRecordsFailure()
    {
        using var directory = new Scratch();
        var backend = new FakeBackend { Fail = true };
        var workflow = new TextListWorkflow(backend, directory.Path);
        var preview = Preview(workflow, backend);
        string id = preview.GetProperty("planId").GetString(), hash = preview.GetProperty("candidateSha256").GetString();
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
        var report = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(preview.GetProperty("artifactDirectory").GetString(), "apply.json")));
        Assert.Equal("failed", report.RootElement.GetProperty("state").GetString());
        Assert.Equal(Fixture(), backend.Xml);
    }

    [Fact]
    public void MissingEditFieldsAreRejectedByJsonDeserializer()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TextEdit>("{\"Entry\":\"7\",\"Culture\":\"en-US\",\"NewText\":\"new\"}"));
    }

    [Fact]
    public void EmptyElementSpellingDoesNotChangeFingerprint()
    {
        string xml = Fixture("");
        var source = new TextListDocument(xml);
        Assert.Equal(source.Fingerprint, new TextListDocument(xml.Replace("<Text></Text>", "<Text />")).Fingerprint);
        Assert.Equal(source.Fingerprint, new TextListDocument(xml.Replace("<Text />", "<Text></Text>")).Fingerprint);
    }

    [Fact]
    public void BomTamperingCannotReachImporter()
    {
        using var directory = new Scratch();
        var backend = new FakeBackend();
        var workflow = new TextListWorkflow(backend, directory.Path);
        var preview = Preview(workflow, backend);
        string candidate = System.IO.Path.Combine(preview.GetProperty("artifactDirectory").GetString(), "candidate.xml");
        File.WriteAllBytes(candidate, new byte[] { 0xef, 0xbb, 0xbf }.Concat(File.ReadAllBytes(candidate)).ToArray());
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString()));
        Assert.Equal(0, backend.Imports);
    }

    [Fact]
    public void EmptyEditsAndUnchangedTextCannotCreatePlan()
    {
        using var directory = new Scratch();
        var backend = new FakeBackend();
        var workflow = new TextListWorkflow(backend, directory.Path);
        var fingerprint = new TextListDocument(backend.Xml).Fingerprint;
        Assert.Throws<ArgumentException>(() => workflow.Preview("hmi", "Messages_TL", fingerprint, Array.Empty<TextEdit>()));
        Assert.Throws<ArgumentException>(() => workflow.Preview("hmi", "Messages_TL", fingerprint, new[] { Edit("Old & ready") }));
    }

    private static JsonElement Preview(TextListWorkflow workflow, FakeBackend backend) => JsonSerializer.SerializeToElement(
        workflow.Preview("hmi", "Messages_TL", new TextListDocument(backend.Xml).Fingerprint, new[] { Edit() }));

    private sealed class FakeBackend : ITextListBackend
    {
        public string Xml = Fixture(); public int Imports; public bool Fail;
        public string ReadTextList(string hmiId, string listName) => Xml;
        public void ImportTextList(string hmiId, string listName, string expectedFingerprint, string candidateXml)
        {
            TextListWorkflow.RequireFingerprint(new TextListDocument(Xml), expectedFingerprint);
            if (Fail) throw new InvalidOperationException("Native import failed; transaction not committed.");
            Imports++; Xml = candidateXml;
        }
    }

    private sealed class Scratch : IDisposable
    {
        public string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia-mcp-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
