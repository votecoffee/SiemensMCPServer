using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using SiemensMCPServer;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ProjectTextTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Package = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static Dictionary<string, string> Parts(string cells = "<c r=\"A1\" t=\"s\"><v>0</v></c>") => new() {
        ["_rels/.rels"] = $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"office\" Type=\"{Rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
        ["xl/workbook.xml"] = $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"Texts\" sheetId=\"1\" r:id=\"sheet\" state=\"hidden\"/></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"sheet\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"strings\" Type=\"{Rel}/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>",
        ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\">{cells}</row></sheetData></worksheet>",
        ["xl/sharedStrings.xml"] = $"<sst xmlns=\"{Main}\"><si><r><t xml:space=\"preserve\">Über 😀 </t></r><r><t>Über</t></r><rPh><t>phonetic</t></rPh></si></sst>"
    };
    private static byte[] Zip(IDictionary<string, string> parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var part in parts) using (var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false))) writer.Write(part.Value);
        return buffer.ToArray();
    }
    private static ProjectTextSnapshot Snapshot(byte[] bytes) => new() { SourceLanguage = "en-US", TargetLanguage = "de-DE", Bytes = bytes.Length,
        FileSha256 = TextListDocument.Hash(bytes), WorkbookBase64Url = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') };
    private static JsonElement Arguments(object? options = null, object? capture = null) => ExtensionWire.Json(new {
        expectedProjectPath = Path.Combine(Path.GetTempPath(), "synthetic.ap15_1"), capture = capture ?? new { sourceLanguage = "en-US", targetLanguage = "de-DE" }, options = options ?? new { } });
    private static JsonElement Execute(string operation, byte[] bytes, object? options = null) => ProjectTextOperations.Execute(operation,
        ExtensionWire.Json(new { phase = "snapshot", arguments = Arguments(options), snapshot = Snapshot(bytes) }));

    [Fact] public void ReadsSharedRichInlineCachedFormulaAndRawNumericValuesWithoutEvaluation()
    {
        var parts = Parts("<c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>Line_x000A_End _x005F_x0041_</t></is></c><c r=\"C1\" t=\"str\"><f>HYPERLINK(\"https://example.invalid/REMOTE\")</f><v>Cache</v></c><c r=\"D1\"><v>1.50</v></c>");
        var book = new ProjectTextWorkbook(Zip(parts));
        Assert.Equal("hidden", Assert.Single(book.Sheets).State);
        Assert.Equal("Über 😀 Über", book.Cells[0].Text); Assert.Equal("Line\nEnd _x0041_", book.Cells[1].Text);
        Assert.Equal("Cache", book.Cells[2].Text); Assert.Contains("REMOTE", book.Cells[2].Formula); Assert.Equal("1.50", book.Cells[3].Text);
        var searched = Execute("find_in_project_texts", Zip(parts), new { query = "REMOTE", caseSensitive = true });
        Assert.Equal(0, searched.GetProperty("totalMatches").GetInt32());
    }
    [Fact] public void ResolvesMovedPartsRootTargetsStrictNamespacesAndImplicitCoordinates()
    {
        var parts = Parts("<c t=\"s\"><v>0</v></c><c t=\"inlineStr\"><is><t>Second</t></is></c>");
        parts["_rels/.rels"] = parts["_rels/.rels"].Replace("xl/workbook.xml", "/data/book.xml");
        parts["data/book.xml"] = parts["xl/workbook.xml"];
        parts["data/_rels/book.xml.rels"] = parts["xl/_rels/workbook.xml.rels"].Replace("worksheets/sheet1.xml", "../xl/worksheets/sheet1.xml").Replace("sharedStrings.xml", "/xl/sharedStrings.xml");
        parts.Remove("xl/workbook.xml"); parts.Remove("xl/_rels/workbook.xml.rels");
        foreach (string key in parts.Keys.ToArray()) parts[key] = parts[key].Replace(Main, "http://purl.oclc.org/ooxml/spreadsheetml/main").Replace(Rel, "http://purl.oclc.org/ooxml/officeDocument/relationships");
        var book = new ProjectTextWorkbook(Zip(parts));
        Assert.Equal(new[] { "A1", "B1" }, book.Cells.Select(c => c.Address));
    }
    [Fact] public void ReadsBoundedPagesAndFindsUnicodeMatchesBeyondReturnedCellText()
    {
        byte[] bytes = Zip(Parts("<c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>42</v></c>"));
        var first = Execute("read_project_texts", bytes, new { maxCells = 1, maxCharacters = 6 });
        Assert.Equal("Über ", first.GetProperty("cells")[0].GetProperty("text").GetString());
        Assert.True(first.GetProperty("cells")[0].GetProperty("textTruncated").GetBoolean());
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32()); Assert.True(first.GetProperty("hasMore").GetBoolean());
        var last = Execute("read_project_texts", bytes, new { offset = 1 });
        Assert.Equal("B1", last.GetProperty("cells")[0].GetProperty("address").GetString()); Assert.False(last.GetProperty("hasMore").GetBoolean());
        var found = Execute("find_in_project_texts", bytes, new { query = "über", caseSensitive = false, maxMatches = 1 });
        Assert.Equal(2, found.GetProperty("totalMatches").GetInt32()); Assert.True(found.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, found.GetProperty("matches")[0].GetProperty("character").GetInt32());
        Assert.Throws<ArgumentException>(() => Execute("read_project_texts", bytes, new { sheet = "Missing" }));
    }
    [Fact] public void SnapshotAndFileHashesPreserveTheCompleteOriginalWorkbook()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tia-project-text-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            byte[] bytes = Zip(Parts()); string path = Path.Combine(directory, "project-texts.xlsx"); File.WriteAllBytes(path, bytes);
            var snapshot = ProjectTextFiles.Read(directory);
            Assert.Equal(bytes, ProjectTextFiles.Decode(snapshot)); Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(TextListDocument.Hash(bytes), snapshot.FileSha256);
            snapshot.FileSha256 = "tamper"; Assert.Throws<InvalidDataException>(() => ProjectTextFiles.Decode(snapshot));
            File.WriteAllBytes(path, Array.Empty<byte>()); Assert.Throws<InvalidDataException>(() => ProjectTextFiles.Read(directory));
        } finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("../outside.xml")][InlineData("/absolute.xml")][InlineData("xl\\bad.xml")][InlineData("xl/./bad.xml")][InlineData("C:/bad.xml")]
    public void RejectsUnsafeZipMembers(string path)
    {
        var parts = Parts(); parts[path] = "bad";
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
    }
    [Fact] public void RejectsAmbiguousMissingAndEscapingRelationshipsButNeverResolvesExternalMetadata()
    {
        var parts = Parts(); string rels = parts["xl/_rels/workbook.xml.rels"];
        parts["xl/_rels/workbook.xml.rels"] = rels.Replace("Target=\"worksheets/sheet1.xml\"", "TargetMode=\"External\" Target=\"https://example.invalid/sheet\"");
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
        parts["xl/_rels/workbook.xml.rels"] = rels.Replace("worksheets/sheet1.xml", "../../outside.xml");
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
        parts["xl/_rels/workbook.xml.rels"] = rels.Replace("</Relationships>", $"<Relationship Id=\"link\" Type=\"{Rel}/externalLink\" TargetMode=\"External\" Target=\"file:///unavailable/book.xlsx\"/></Relationships>");
        Assert.Equal(1, new ProjectTextWorkbook(Zip(parts)).IgnoredExternalRelationships);
        parts["xl/_rels/workbook.xml.rels"] = rels.Replace("Id=\"strings\"", "Id=\"sheet\"");
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
    }
    [Theory]
    [InlineData("<c r=\"A1\" t=\"s\"><v>99</v></c>")]
    [InlineData("<c r=\"A1\"><v>1</v></c><c r=\"A1\"><v>2</v></c>")]
    [InlineData("<c r=\"XFE1\"><v>1</v></c>")]
    [InlineData("<c r=\"A2\"><v>1</v></c>")]
    [InlineData("<c r=\"A1\" t=\"b\"><v>2</v></c>")]
    [InlineData("<c r=\"A1\"><v>1</v><v>2</v></c>")]
    public void RejectsInvalidAndAmbiguousCellData(string cells)
        => Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(Parts(cells))));
    [Fact] public void EnforcesDecodedTextCellPartAndMemberBoundsAndRejectsDtd()
    {
        var parts = Parts(); parts["xl/sharedStrings.xml"] = $"<sst xmlns=\"{Main}\"><si><t>{new string('x', 65537)}</t></si></sst>";
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
        parts["xl/sharedStrings.xml"] = $"<sst xmlns=\"{Main}\"><si><t>{new string('x', 65536)}</t></si></sst>";
        parts["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\">{string.Concat(Enumerable.Repeat("<c t=\"s\"><v>0</v></c>", 257))}</row></sheetData></worksheet>";
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
        parts = Parts(); parts["large.xml"] = new string('x', 16 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
        parts = Parts(); for (int i = 0; i < 513; i++) parts["extra/" + i] = "";
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
        parts = Parts(); parts["xl/sharedStrings.xml"] = $"<!DOCTYPE sst [<!ENTITY injected 'bad'>]><sst xmlns=\"{Main}\"><si><t>&injected;</t></si></sst>";
        Assert.Throws<XmlException>(() => new ProjectTextWorkbook(Zip(parts)));
        parts = Parts(); parts["XL/sharedstrings.xml"] = parts["xl/sharedStrings.xml"];
        Assert.Throws<InvalidDataException>(() => new ProjectTextWorkbook(Zip(parts)));
    }
    private sealed class Backend : IProjectTextBackend
    {
        public int Calls;
        public ProjectLanguageSnapshot CaptureProjectLanguages(string path) { Calls++; return new ProjectLanguageSnapshot { Languages = new[] { new ProjectLanguage { Culture = "en-US" } } }; }
        public ProjectTextSnapshot CaptureProjectTexts(string path, ProjectTextCapture capture) { Calls++; return Snapshot(Zip(Parts())); }
    }
    [Fact] public void ValidatesBeforeCaptureRejectsClientSnapshotsAndReportsReadOnlyScope()
    {
        var backend = new Backend(); var workflow = new ProjectTextWorkflow(backend, new LocalFeatureEngine());
        var result = ExtensionWire.Json(workflow.Read("read_project_texts", "projectTexts", Arguments()));
        Assert.False(result.GetProperty("projectTextsImported").GetBoolean()); Assert.Equal(1, backend.Calls);
        Assert.Throws<ArgumentException>(() => workflow.Read("find_in_project_texts", "projectTexts", Arguments(new { query = "", caseSensitive = true })));
        Assert.Throws<JsonException>(() => workflow.Read("read_project_texts", "projectTexts", Arguments(new { destination = "C:/unrequested.xlsx" })));
        Assert.Throws<ArgumentException>(() => workflow.Read("read_project_texts", "projectTexts", Arguments(capture: new { sourceLanguage = "en-US", targetLanguage = "en-US" })));
        Assert.Throws<ArgumentException>(() => workflow.Read("list_project_languages", "projectLanguages", Arguments()));
        Assert.Throws<JsonException>(() => workflow.Read("read_project_texts", "projectTexts", ExtensionWire.Json(new { expectedProjectPath = "C:/synthetic.ap19", capture = new { sourceLanguage = "en-US", targetLanguage = "de-DE" }, options = new { }, snapshot = new { } })));
        Assert.Equal(1, backend.Calls);
        var languages = ExtensionWire.Json(workflow.Read("list_project_languages", "projectLanguages", Arguments(capture: new { })));
        Assert.Equal("en-US", languages.GetProperty("data").GetProperty("languages")[0].GetProperty("culture").GetString());
    }
    [Fact] public void ProjectTextRoutesAreLimitedToReadOnlyProjectSnapshots()
    {
        var operations = FeatureOperations.Describe("project").Where(o => o.SnapshotKind != "libraryCatalog").ToArray();
        Assert.Equal(3, operations.Length);
        Assert.All(operations, operation => { Assert.True(operation.IsCompatible("project")); Assert.False(operation.IsCompatible("comfort")); operation.Preview = true; Assert.False(operation.IsCompatible("project")); });
    }
}
