using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SiemensTiaOpenness.Mcp;
using Xunit;

namespace SiemensTiaOpenness.Tests;

public sealed class UnifiedYamlTests
{
    // Synthetic values, using the structure of Siemens' published YAML v2 examples.
    private const string Config = "#Version: 2.0\nTextListContainers:\n  TextList:\n    ResourceListType: TextList\n    ResourceLists:\n      Messages_TL:\n        RangeType: Decimal\n        Entries:\n          Entry_0:\n            Type: SingleValue\n            Value: 7\n            FromValue: 7\n            ToValue: 7\n            IsDefaultEntry: false\n            Text: MyTextLibrary.Text_0\n          Entry_1:\n            Type: Range\n            Value: 8\n            FromValue: 8\n            ToValue: 9\n            IsDefaultEntry: false\n            Text: MyTextLibrary.Text_1\n";
    private const string Library = "#Version: 2.0\n# Keep this comment and all unselected text exactly.\nTextLibraries:\n  MyTextLibrary:\n    Type: Text\n    Languages:\n      - en-US\n      - es-ES\n    DefaultLanguage: en-US\n    Entries:\n      Text_0:\n        Text:\n          - \"Old & ready\"\n          - 'Español intacto'\n      Text_1:\n        Text:\n          - \"Range text\"\n          - \"Rango\"\n";
    private static string Bundle(string config = Config, string library = Library) => JsonSerializer.Serialize(new UnifiedTextListBundle {
        ListName = "Messages_TL", Configuration = config, TextLibrary = library, NativeListNames = new[] { "Messages_TL" }
    }, JsonDefaults.Options);
    private static TextEdit Edit(string value = "New “ready” & café\nLine 2 😀") => new TextEdit { Entry = "7", Culture = "en-US", ExpectedText = "Old & ready", NewText = value };

    [Fact]
    public void OrderedLanguageSlotsResolveToCorrectMessages()
    {
        var document = new UnifiedTextListDocument(Bundle());
        Assert.Equal("Old & ready", document.Read().Single(r => r.Entry == "7" && r.Culture == "en-US").Text);
        Assert.Equal("Español intacto", document.Read().Single(r => r.Entry == "7" && r.Culture == "es-ES").Text);
        Assert.False(document.Read().First(r => r.Entry == "entry:Entry_1").Editable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("New “ready” & café\nLine 2 😀")]
    [InlineData("# yes: [brackets] \"quote\" \\ slash")]
    public void PatchesOnlySelectedScalarAndPreservesConfigurationAndOtherLanguageBytes(string value)
    {
        var source = new UnifiedTextListDocument(Bundle());
        var candidate = (UnifiedTextListDocument)source.Edit(new[] { Edit(value) });
        Assert.Equal(Config, candidate.Bundle.Configuration);
        Assert.Equal(Library.Replace("\"Old & ready\"", JsonSerializer.Serialize(value)), candidate.Bundle.TextLibrary);
        Assert.Equal(value, candidate.Read().Single(r => r.Entry == "7" && r.Culture == "en-US").Text);
        Assert.Equal(Library, source.Bundle.TextLibrary);
    }

    [Fact]
    public void MissingCompanionMissingLanguageAndUnknownVersionFailClosed()
    {
        Assert.Throws<InvalidDataException>(() => new UnifiedTextListDocument(Bundle(library: "")));
        Assert.Throws<InvalidDataException>(() => new UnifiedTextListDocument(Bundle(library: Library.Replace("          - 'Español intacto'\n", ""))));
        Assert.Throws<InvalidDataException>(() => new UnifiedTextListDocument(Bundle(config: Config.Replace("#Version: 2.0", "#Version: 3.0"))));
    }

    [Fact]
    public void SharedReferencesAndRangeEntriesCannotBeEdited()
    {
        var shared = new UnifiedTextListDocument(Bundle(config: Config.Replace("MyTextLibrary.Text_1", "MyTextLibrary.Text_0")));
        Assert.False(shared.Read().First(r => r.Entry == "7").Editable);
        Assert.Throws<InvalidOperationException>(() => shared.Edit(new[] { Edit() }));
        var edit = Edit(); edit.Entry = "entry:Entry_1"; edit.ExpectedText = "Range text";
        Assert.Throws<InvalidOperationException>(() => new UnifiedTextListDocument(Bundle()).Edit(new[] { edit }));
    }

    [Fact]
    public void StaleTextUnknownCultureAndDuplicateEditsFailClosed()
    {
        var document = new UnifiedTextListDocument(Bundle());
        var stale = Edit(); stale.ExpectedText = "outdated";
        Assert.Throws<InvalidOperationException>(() => document.Edit(new[] { stale }));
        var missing = Edit(); missing.Culture = "de-DE";
        Assert.Throws<InvalidOperationException>(() => document.Edit(new[] { missing }));
        Assert.Throws<ArgumentException>(() => document.Edit(new[] { Edit(), Edit() }));
    }

    [Fact]
    public void FingerprintIncludesUnselectedLanguageAndConfiguration()
    {
        string hash = new UnifiedTextListDocument(Bundle()).Fingerprint;
        Assert.NotEqual(hash, new UnifiedTextListDocument(Bundle(library: Library.Replace("Español intacto", "Changed elsewhere"))).Fingerprint);
        Assert.NotEqual(hash, new UnifiedTextListDocument(Bundle(config: Config.Replace("RangeType: Decimal", "RangeType: BitNumber"))).Fingerprint);
    }

    [Fact]
    public void AliasesTagsAndDuplicateKeysAreRejected()
    {
        Assert.ThrowsAny<Exception>(() => new UnifiedTextListDocument(Bundle(library: Library.Replace("MyTextLibrary:", "MyTextLibrary: &anchor"))));
        Assert.ThrowsAny<Exception>(() => new UnifiedTextListDocument(Bundle(library: Library.Replace("\"Old & ready\"", "!!str \"Old & ready\""))));
        Assert.ThrowsAny<Exception>(() => new UnifiedTextListDocument(Bundle(library: Library.Replace("    Type: Text", "    Type: Text\n    Type: Text"))));
    }

    [Fact]
    public void NativeAdapterRequiresExactDirectoryInfoOverloadsAndBothFiles()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia-yaml-adapter-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(UnifiedTextListNative.Supports(typeof(UnsupportedSoftware)));
            var software = new FakeSoftware();
            Assert.True(UnifiedTextListNative.Supports(software.GetType()));
            string snapshot = UnifiedTextListNative.Export(software, "Messages_TL", root);
            var candidate = (UnifiedTextListDocument)new UnifiedTextListDocument(snapshot).Edit(new[] { Edit() });
            UnifiedTextListNative.Import(software, candidate, root);
            Assert.True(software.HmiTextLists.ImportSawBothFiles);
            Assert.Equal(candidate.Bundle.Configuration, software.HmiTextLists.ImportedConfiguration);
            Assert.Equal(candidate.Bundle.TextLibrary, software.HmiTextLists.ImportedLibrary);
            software.HmiTextLists.OmitLibrary = true;
            Assert.Throws<InvalidDataException>(() => UnifiedTextListNative.Export(software, "Messages_TL", root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void NativeAdapterPropagatesReportedImportFailure()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia-yaml-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var software = new FakeSoftware(); software.HmiTextLists.Fail = true;
            Assert.Throws<InvalidOperationException>(() => UnifiedTextListNative.Import(software, new UnifiedTextListDocument(Bundle()), root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ExistingPreviewApplyWorkflowSupportsPairedYamlSnapshots()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia-yaml-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new Backend(); var workflow = new TextListWorkflow(backend, root);
            var preview = JsonSerializer.SerializeToElement(workflow.Preview("unified", "Messages_TL", TextListDocuments.Parse(backend.Snapshot).Fingerprint, new[] { Edit() }));
            string directory = preview.GetProperty("artifactDirectory").GetString();
            Assert.True(File.Exists(System.IO.Path.Combine(directory, "before.json")));
            Assert.True(File.Exists(System.IO.Path.Combine(directory, "candidate.json")));
            Assert.Equal(0, backend.Writes);
            workflow.Apply(preview.GetProperty("planId").GetString(), preview.GetProperty("candidateSha256").GetString());
            Assert.Equal(1, backend.Writes);
            Assert.Equal(Edit().NewText, TextListDocuments.Parse(backend.Snapshot).Read().Single(r => r.Entry == "7" && r.Culture == "en-US").Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Backend : ITextListBackend
    {
        public string Snapshot = Bundle(); public int Writes;
        public string ReadTextList(string hmiId, string listName) => Snapshot;
        public void ImportTextList(string hmiId, string listName, string expectedFingerprint, string candidateXml)
        {
            TextListWorkflow.RequireFingerprint(TextListDocuments.Parse(Snapshot), expectedFingerprint);
            Writes++; Snapshot = candidateXml;
        }
    }

    public sealed class FakeSoftware
    {
        public FakeComposition HmiTextLists { get; } = new FakeComposition();
    }
    public sealed class FakeComposition : System.Collections.IEnumerable
    {
        public bool OmitLibrary, ImportSawBothFiles, Fail;
        public string ImportedConfiguration, ImportedLibrary;
        public System.Collections.IEnumerator GetEnumerator() => new[] { new FakeList() }.GetEnumerator();
        public FileInfo[] Export(DirectoryInfo directory, string name)
        {
            File.WriteAllText(System.IO.Path.Combine(directory.FullName, name), Config);
            if (!OmitLibrary) File.WriteAllText(System.IO.Path.Combine(directory.FullName, "Textlists.TextLibrary.hmi.yml"), Library);
            return directory.GetFiles();
        }
        public bool Import(DirectoryInfo directory, string name)
        {
            string configuration = System.IO.Path.Combine(directory.FullName, name), library = System.IO.Path.Combine(directory.FullName, "Textlists.TextLibrary.hmi.yml");
            ImportSawBothFiles = File.Exists(configuration) && File.Exists(library);
            ImportedConfiguration = File.ReadAllText(configuration); ImportedLibrary = File.ReadAllText(library);
            return !Fail;
        }
    }
    public sealed class FakeList { public string Name => "Messages_TL"; }
    public sealed class UnsupportedSoftware { public UnsupportedComposition HmiTextLists => new UnsupportedComposition(); }
    public sealed class UnsupportedComposition : System.Collections.IEnumerable
    {
        public System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
        public void Export(string path, string name) { }
        public bool Import(string path, string name) => true;
    }
}
