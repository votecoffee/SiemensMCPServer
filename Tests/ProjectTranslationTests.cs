using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SiemensMCPServer;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class ProjectTranslationTests
{
    const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static ProjectTextCapture Capture() => new() { SourceLanguage = "en-US", TargetLanguage = "de-DE" };
    static string Project => Path.Combine(Path.GetTempPath(), "synthetic.ap15_1");
    internal static byte[] Workbook(string target = "Alt", string sheet = "User Texts", string substitutions = "", string version = "1.3", bool rich = false, bool shared = true)
    {
        XNamespace ns = Main;
        var strings = new[] { "Category", "ViewPath", "Internal ID", "Substitutions", "en-US*", "de-DE", "<HMI comment>", "Panel\\Tags\\Ready\\Comment", "synthetic-native-id", substitutions, "Ready", target };
        var parts = new Dictionary<string, string> {
            ["_rels/.rels"] = $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='book' Type='{Rel}/officeDocument' Target='xl/workbook.xml'/></Relationships>",
            ["xl/workbook.xml"] = $"<workbook xmlns='{Main}' xmlns:r='{Rel}'><sheets><sheet name='{sheet}' sheetId='1' r:id='sheet'/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='sheet' Type='{Rel}/worksheet' Target='worksheets/sheet.xml'/><Relationship Id='strings' Type='{Rel}/sharedStrings' Target='sharedStrings.xml'/></Relationships>",
            ["docProps/custom.xml"] = $"<Properties xmlns='http://schemas.openxmlformats.org/officeDocument/2006/custom-properties'><property name='TIA_Version'><v>{version}</v></property></Properties>",
            ["retained.bin"] = "untouched bytes and metadata"
        };
        var sst = new XElement(ns + "sst", new XAttribute("count", 12), new XAttribute("uniqueCount", 12), strings.Select(s => new XElement(ns + "si", new XElement(ns + "t", s))));
        if (rich) sst.Elements().Last().ReplaceNodes(new XElement(ns + "r", new XElement(ns + "t", target)));
        var rows = new[] { 1, 2 }.Select(row => new XElement(ns + "row", new XAttribute("r", row), Enumerable.Range(0, 6).Select(col => {
            var cell = new XElement(ns + "c", new XAttribute("r", ((char)('A' + col)).ToString() + row), new XAttribute("s", "0"), new XAttribute("t", "s"), new XElement(ns + "v", (row - 1) * 6 + col));
            if (!shared && row == 2 && col == 5) { cell.SetAttributeValue("t", "inlineStr"); cell.ReplaceNodes(new XElement(ns + "is", new XElement(ns + "t", target))); }
            return cell;
        })));
        parts["xl/sharedStrings.xml"] = sst.ToString(SaveOptions.DisableFormatting);
        parts["xl/worksheets/sheet.xml"] = new XElement(ns + "worksheet", new XElement(ns + "sheetData", rows)).ToString(SaveOptions.DisableFormatting);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var part in parts) using (var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false))) writer.Write(part.Value);
        return output.ToArray();
    }
    static ProjectTranslationEdits Request(ProjectTextWorkbook book, string text, bool source = false) => new() { ExpectedProjectPath = Project, Capture = Capture(),
        Edits = new[] { new ProjectTranslationEdit { Selection = Assert.Single(book.TranslationUnits(Capture())).Select(source), NewText = text } } };
    static Dictionary<string, byte[]> Parts(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes); using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var input = e.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); });
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ChangesOnlySelectedLiteralTextAndRetainsNativeMetadataAndOtherCells(bool shared)
    {
        byte[] before = Workbook(shared: shared); var book = new ProjectTextWorkbook(before); var unit = Assert.Single(book.TranslationUnits(Capture()));
        Assert.True(unit.Editable); Assert.Equal("<HMI comment>", unit.Category); Assert.Equal("de-DE", unit.Select().Culture);
        string text = "=literal & Über 😀\r\nNext\t_x0041_";
        var candidate = book.EditTranslations(Request(book, text)); var updated = new ProjectTextWorkbook(candidate.Bytes);
        Assert.Equal(text, Assert.Single(updated.TranslationUnits(Capture())).TargetText);
        Assert.Equal("Ready", Assert.Single(updated.TranslationUnits(Capture())).SourceText);
        Assert.Equal("Alt", Assert.Single(book.TranslationUnits(Capture())).TargetText);
        Assert.NotEqual(candidate.BeforeFingerprint, candidate.AfterFingerprint); Assert.Equal(updated.Fingerprint, candidate.AfterFingerprint);
        var old = Parts(before); var after = Parts(candidate.Bytes);
        foreach (var part in old.Keys.Where(p => p != "xl/sharedStrings.xml" && p != "xl/worksheets/sheet.xml")) Assert.Equal(old[part], after[part]);
        Assert.Null(updated.Cells.Single(c => c.Address == "F2").Formula);
    }
    [Fact] public void AllowsFillingAnExistingEmptyTargetAndRequiresExplicitSourceOptIn()
    {
        var book = new ProjectTextWorkbook(Workbook("")); Assert.True(Assert.Single(book.TranslationUnits(Capture())).TargetEmpty);
        Assert.Equal("Neu", Assert.Single(new ProjectTextWorkbook(book.EditTranslations(Request(book, "Neu")).Bytes).TranslationUnits(Capture())).TargetText);
        var request = Request(book, "Source", true); Assert.Throws<ArgumentException>(() => book.EditTranslations(request)); request.UpdateSourceLanguage = true;
        Assert.Equal("Source", Assert.Single(new ProjectTextWorkbook(book.EditTranslations(request).Bytes).TranslationUnits(Capture())).SourceText);
    }
    [Fact] public void RejectsStaleContextCultureIdentityDuplicatesNoopsAndInvalidUnicode()
    {
        var book = new ProjectTextWorkbook(Workbook()); var edit = Request(book, "New");
        edit.Edits[0].Selection.ExpectedText = "wrong"; Assert.Throws<InvalidOperationException>(() => book.EditTranslations(edit));
        edit = Request(book, "New"); edit.Edits[0].Selection.ExpectedUnitFingerprint = new string('0', 64); Assert.Throws<InvalidOperationException>(() => book.EditTranslations(edit));
        edit = Request(book, "New"); edit.Edits[0].Selection.Culture = "fr-FR"; Assert.Throws<ArgumentException>(() => book.EditTranslations(edit));
        edit = Request(book, "New"); edit.Edits = new[] { edit.Edits[0], edit.Edits[0] }; Assert.Throws<ArgumentException>(() => book.EditTranslations(edit));
        Assert.Throws<ArgumentException>(() => book.EditTranslations(Request(book, "Alt")));
        Assert.Throws<ArgumentException>(() => book.EditTranslations(Request(book, "\0")));
        Assert.Throws<System.Xml.XmlException>(() => book.EditTranslations(Request(book, "\uD800")));
        Assert.Throws<ArgumentException>(() => book.EditTranslations(Request(book, new string('x', 32768))));
    }
    [Theory] [InlineData("System Texts", "", false)] [InlineData("Other", "", false)] [InlineData("User Texts", "ref=0", false)] [InlineData("User Texts", "", true)]
    public void ProtectsSystemUnknownSubstitutedAndStyledText(string sheet, string substitutions, bool rich)
    {
        var book = new ProjectTextWorkbook(Workbook(sheet: sheet, substitutions: substitutions, rich: rich));
        Assert.False(Assert.Single(book.TranslationUnits(Capture())).Editable); Assert.Throws<ArgumentException>(() => book.EditTranslations(Request(book, "New")));
    }
    [Fact] public void RejectsUnknownSchemaAndMismatchedCultureHeaders()
    {
        Assert.Throws<NotSupportedException>(() => new ProjectTextWorkbook(Workbook(version: "9.9")).TranslationUnits(Capture()));
        Assert.Throws<NotSupportedException>(() => new ProjectTextWorkbook(Workbook()).TranslationUnits(new ProjectTextCapture { SourceLanguage = "en-US", TargetLanguage = "es-ES" }));
    }
    [Fact] public void ExtractionFiltersMissingTranslationsAndReturnsHashBoundSelections()
    {
        var bytes = Workbook(""); var snapshot = ProjectTextFiles.FromBytes(bytes, Capture());
        var args = new { expectedProjectPath = Project, capture = Capture(), options = new { query = "ready", missingOnly = true, maxUnits = 1 } };
        var result = ProjectTranslationOperations.Execute("read_translation_units", ExtensionWire.Json(new { phase = "snapshot", snapshot, arguments = args }));
        Assert.Equal(new ProjectTextWorkbook(bytes).Fingerprint, result.GetProperty("workbookFingerprint").GetString());
        var unit = Assert.Single(result.GetProperty("units").EnumerateArray()); Assert.True(unit.GetProperty("canPreview").GetBoolean());
        Assert.Equal("de-DE", unit.GetProperty("targetSelection").GetProperty("culture").GetString());
        var clipped = ProjectTranslationOperations.Execute("read_translation_units", ExtensionWire.Json(new { phase = "snapshot", snapshot, arguments = new { expectedProjectPath = Project, capture = Capture(), options = new { maxCharacters = 2 } } }));
        Assert.False(clipped.GetProperty("units")[0].GetProperty("canPreview").GetBoolean());
    }
    sealed class Backend : IProjectTranslationBackend
    {
        public byte[] Bytes = Workbook(); public int Imports; public bool Fail;
        public ProjectLanguageSnapshot CaptureProjectLanguages(string path) => new();
        public ProjectTextSnapshot CaptureProjectTexts(string path, ProjectTextCapture capture) => ProjectTextFiles.FromBytes(Bytes, capture);
        public object ImportProjectTranslations(string path, ProjectTextCapture capture, byte[] candidate, string before, string after, bool source)
        {
            Imports++; if (Fail) throw new InvalidOperationException("Synthetic native failure");
            Assert.Equal(new ProjectTextWorkbook(Bytes).Fingerprint, before); Assert.Equal(new ProjectTextWorkbook(candidate).Fingerprint, after); Bytes = candidate; return new { verified = true };
        }
    }
    [Fact] public void WorkflowPreservesEvidenceGuardsCandidateHashAndConsumesAttemptedPlans()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try {
            var backend = new Backend(); var book = new ProjectTextWorkbook(backend.Bytes); var workflow = new ProjectTranslationWorkflow(backend, directory, new LocalFeatureEngine());
            Assert.Throws<InvalidOperationException>(() => workflow.Preview("stale", Request(book, "New")));
            var preview = ExtensionWire.Json(workflow.Preview(book.Fingerprint, Request(book, "New"))); string id = preview.GetProperty("planId").GetString()!, hash = preview.GetProperty("candidateSha256").GetString()!;
            Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, "wrong")); Assert.Equal(0, backend.Imports);
            var applied = ExtensionWire.Json(workflow.Apply(id, hash)); Assert.False(applied.GetProperty("projectSaved").GetBoolean()); Assert.Equal(1, backend.Imports);
            Assert.True(File.Exists(Path.Combine(directory, id, "before.xlsx"))); Assert.True(File.Exists(Path.Combine(directory, id, "apply.json")));
            Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
            book = new ProjectTextWorkbook(backend.Bytes); preview = ExtensionWire.Json(workflow.Preview(book.Fingerprint, Request(book, "Again"))); backend.Fail = true;
            id = preview.GetProperty("planId").GetString()!; hash = preview.GetProperty("candidateSha256").GetString()!;
            Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash)); Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact] public void NativeImportRouteIsProjectOnlyPreviewOnlyAndUnknownArgumentsAreRejected()
    {
        var operation = FeatureOperations.Describe("project").Single(o => o.Name == "edit_project_texts");
        Assert.True(operation.IsCompatible("project")); Assert.False(operation.IsCompatible("comfort")); operation.Preview = false; Assert.False(operation.IsCompatible("project"));
        Assert.Throws<JsonException>(() => ExtensionWire.Parse<ProjectTranslationEdits>(ExtensionWire.Json(new { expectedProjectPath = Project, capture = Capture(), edits = Array.Empty<object>(), destination = "unsafe" })));
    }
}
