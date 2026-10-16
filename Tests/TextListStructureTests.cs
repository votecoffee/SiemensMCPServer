using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SiemensTiaOpenness;
using SiemensTiaOpenness.Mcp;
using SiemensTiaOpenness.Tests;
using Xunit;

public sealed class TextListStructureTests
{
    private static readonly string[] Active = { "en-US", "es-ES", "de-DE" };
    private static ClassicTextListChange Add(string entry = "8") => new ClassicTextListChange { Action = "addEntry", Entry = entry, TemplateEntry = "7", Translations = new[] {
        new ClassicTranslation { Culture = "en-US", Text = "New <entry> 🟢" }, new ClassicTranslation { Culture = "es-ES", Text = "Nuevo" } } };
    [Fact] public void NewEntryPreservesOriginalCulturesAndUsesUniqueNativeIds()
    {
        var before = new TextListDocument(TextListTests.Fixture()); var after = before.ChangeStructure(new[] { Add() }, Active);
        Assert.Equal(4, before.Read().Length); Assert.Equal(6, after.Read().Length);
        Assert.Equal("New <entry> 🟢", after.Read().Single(v => v.Entry == "8" && v.Culture == "en-US").Text);
        Assert.Equal("Español intacto", after.Read().Single(v => v.Entry == "7" && v.Culture == "es-ES").Text);
        var ids = XDocument.Parse(after.ToXml()).Descendants().Attributes("ID").Select(a => a.Value).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Fallback", after.ToXml());
    }
    [Fact] public void EntryRemovalRequiresExactFingerprintAndPreservesDefault()
    {
        var before = new TextListDocument(TextListTests.Fixture());
        var entry = JsonSerializer.SerializeToElement(before.Structure(), JsonDefaults.Options).GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("entry").GetString() == "7");
        var change = new ClassicTextListChange { Action = "removeEntry", Entry = "7", ExpectedEntryFingerprint = "stale" };
        Assert.Throws<InvalidOperationException>(() => before.ChangeStructure(new[] { change }, Active));
        change.ExpectedEntryFingerprint = entry.GetProperty("fingerprint").GetString()!;
        Assert.All(before.ChangeStructure(new[] { change }, Active).Read(), v => Assert.Equal("Default", v.Entry));
        change.Entry = "Default"; Assert.Throws<NotSupportedException>(() => before.ChangeStructure(new[] { change }, Active));
    }
    [Fact] public void AddAndRemoveTranslationPreservesOtherLanguageText()
    {
        var before = new TextListDocument(TextListTests.Fixture());
        var change = new ClassicTextListChange { Action = "addTranslation", Entry = "7", Culture = "de-DE", TemplateCulture = "en-US", Text = "Neu — ä" };
        var after = before.ChangeStructure(new[] { change }, Active); Assert.Equal(5, after.Read().Length);
        Assert.Equal("Neu — ä", after.Read().Single(v => v.Entry == "7" && v.Culture == "de-DE").Text);
        var remove = new ClassicTextListChange { Action = "removeTranslation", Entry = "7", Culture = "de-DE", ExpectedText = "wrong" };
        Assert.Throws<InvalidOperationException>(() => after.ChangeStructure(new[] { remove }, Active));
        remove.ExpectedText = "Neu — ä"; Assert.Equal(before.Fingerprint, after.ChangeStructure(new[] { remove }, Active).Fingerprint);
        change.Culture = "fr-FR"; Assert.Throws<InvalidOperationException>(() => before.ChangeStructure(new[] { change }, Active));
    }
    [Fact] public void OverlapIncompleteTranslationsAndProtectedTemplatesAreRejected()
    {
        var before = new TextListDocument(TextListTests.Fixture());
        Assert.Throws<InvalidOperationException>(() => before.ChangeStructure(new[] { Add("7") }, Active));
        Assert.Throws<ArgumentException>(() => before.ChangeStructure(new[] { Add("08") }, Active));
        Assert.Throws<ArgumentException>(() => before.ChangeStructure(new[] { Add(), Add() }, Active));
        var incomplete = Add(); incomplete.Translations = incomplete.Translations.Take(1).ToArray(); Assert.Throws<ArgumentException>(() => before.ChangeStructure(new[] { incomplete }, Active));
        var defaultTemplate = Add(); defaultTemplate.TemplateEntry = "Default"; Assert.Throws<NotSupportedException>(() => before.ChangeStructure(new[] { defaultTemplate }, Active));
        var xml = XDocument.Parse(before.ToXml()); xml.Descendants("To").First().Value = "10";
        Assert.Throws<InvalidOperationException>(() => new TextListDocument(xml.ToString()).ChangeStructure(new[] { Add("8") }, Active));
    }
    [Fact] public void StructurePreviewUsesExistingHashBoundSingleAttemptApply()
    {
        string root = Path.Combine(Path.GetTempPath(), "text-structure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new Backend(); var workflow = new TextListWorkflow(backend, root); var before = new TextListDocument(backend.Xml);
            var preview = JsonSerializer.SerializeToElement(workflow.PreviewStructure("h", before.Name, before.Fingerprint, new[] { Add() }, Active), JsonDefaults.Options);
            Assert.Equal(0, backend.Imports);
            string id = preview.GetProperty("planId").GetString()!, hash = preview.GetProperty("candidateSha256").GetString()!;
            workflow.Apply(id, hash); Assert.Equal(1, backend.Imports); Assert.Equal(6, new TextListDocument(backend.Xml).Read().Length);
            Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Backend : ITextListBackend
    {
        public string Xml = TextListTests.Fixture(); public int Imports;
        public string ReadTextList(string hmiId, string listName) => Xml;
        public void ImportTextList(string hmiId, string listName, string fingerprint, string candidateXml) { TextListWorkflow.RequireFingerprint(new TextListDocument(Xml), fingerprint); Xml = candidateXml; Imports++; }
    }
}
