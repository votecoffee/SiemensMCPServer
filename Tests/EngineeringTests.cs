using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class EngineeringTests
{
    public enum Mode { Manual, Automatic }
    private static EngineeringSnapshot State() => new EngineeringSnapshot { TargetId = "hmi", Path = "p:Tags/n:Tag1", NativeType = "native",
        CanDelete = true, CanImport = true, Attributes = new[] {
            new EngineeringValue { Name = "Name", Type = typeof(string).FullName!, Value = "Tag1", Editable = true },
            new EngineeringValue { Name = "MaxLength", Type = typeof(uint).FullName!, Value = "10", Editable = true },
            new EngineeringValue { Name = "Bound", Type = typeof(bool).FullName!, Value = "true", Editable = false } } };
    private static EngineeringEdit Edit(string attribute = "Name", string old = "Tag1", string value = "Tag2") => new EngineeringEdit { Attribute = attribute, ExpectedValue = old, NewValue = value };
    [Theory][InlineData("delete")][InlineData("import")]
    public void RevokedPermissionBlocksAnAlreadyPreparedPlanWithoutBackendEffect(string action)
    {
        using var temp = new Scratch(); var backend = new Backend(); var settings = new SiemensMCPServer.SafeguardSettings();
        var workflow = new EngineeringWorkflow(backend, temp.Path, () => settings);
        string source = System.IO.Path.Combine(temp.Path, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(System.IO.Path.Combine(source, "payload.xml"), "<Document/>");
        var plan = Json(workflow.Preview(action, "hmi", backend.State.Path, backend.State.Fingerprint, sourceDirectory: source, overwrite: action == "import"));
        if (action == "delete") settings.AllowDelete = false; else settings.AllowOverwrite = false;
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
        Assert.Equal(0, backend.Attempts);
    }
    [Fact] public void TypedEditsPreserveUnselectedAttributesAndOriginal()
    {
        var state = State(); var after = state.Edit(new[] { Edit() });
        Assert.Equal("Tag1", state.Attributes[0].Value); Assert.Equal("Tag2", after.Attributes[0].Value);
        Assert.Equal("10", after.Attributes[1].Value); Assert.NotEqual(state.Fingerprint, after.Fingerprint);
    }
    [Fact] public void RejectsStaleDuplicateUnknownReadonlyAndIncompleteEdits()
    {
        var state = State();
        Assert.Throws<InvalidOperationException>(() => state.Edit(new[] { Edit(old: "stale") }));
        Assert.Throws<ArgumentException>(() => state.Edit(new[] { Edit(), Edit() }));
        Assert.Throws<InvalidOperationException>(() => state.Edit(new[] { Edit("Missing") }));
        Assert.Throws<InvalidOperationException>(() => state.Edit(new[] { Edit("Bound", "true", "false") }));
        state.Errors = new[] { "Cannot inspect bindings" };
        Assert.Throws<InvalidOperationException>(() => state.Edit(new[] { Edit() }));
    }
    [Theory]
    [InlineData("-1")][InlineData("4294967296")][InlineData("1.5")]
    public void NativeIntegerBoundsAreEnforced(string value) => Assert.ThrowsAny<Exception>(() => State().Edit(new[] { Edit("MaxLength", "10", value) }));
    [Fact] public void ScalarsUseExactEnumsBooleansColorsAndFiniteNumbers()
    {
        Assert.Equal(Mode.Automatic, EngineeringCodec.Parse("Automatic", typeof(Mode)));
        Assert.Throws<ArgumentException>(() => EngineeringCodec.Parse("1", typeof(Mode)));
        Assert.Throws<ArgumentException>(() => EngineeringCodec.Parse("automatic", typeof(Mode)));
        Assert.Throws<ArgumentException>(() => EngineeringCodec.Parse("Manual, Automatic", typeof(Mode)));
        Assert.Throws<ArgumentException>(() => EngineeringCodec.Parse("TRUE", typeof(bool)));
        Assert.Throws<ArgumentException>(() => EngineeringCodec.Parse("NaN", typeof(double)));
        Assert.Equal("#80FF0102", EngineeringCodec.Format(EngineeringCodec.Parse("#80FF0102", typeof(System.Drawing.Color))));
        Assert.Throws<NotSupportedException>(() => EngineeringCodec.Parse("abc", typeof(Uri)));
    }
    [Fact] public void FingerprintsBindChildIdentityAndNativeCapabilities()
    {
        var state = State(); string fingerprint = state.Fingerprint;
        state.Children = new[] { new EngineeringChild { Path = "n:new", NativeType = "type" } }; Assert.NotEqual(fingerprint, state.Fingerprint);
        fingerprint = state.Fingerprint; state.CanDelete = false; Assert.NotEqual(fingerprint, state.Fingerprint);
    }
    [Fact] public void NullScalarCanBeInitializedWithoutChangingOtherAttributes()
    {
        var state = State(); state.Attributes[0].IsNull = true; state.Attributes[0].Value = "<null>";
        var after = state.Edit(new[] { Edit(old: "<null>", value: "") });
        Assert.False(after.Attributes[0].IsNull); Assert.Equal("", after.Attributes[0].Value); Assert.True(state.Attributes[0].IsNull);
    }
    [Fact] public void DynamizedOwnerProtectsTextChildrenFromEditCreateDeleteAndImport()
    {
        var state = State(); state.Creations = new[] { new EngineeringCreation { NativeType = "example" } };
        state.ProtectInheritedDynamization();
        Assert.True(state.InheritedDynamization); Assert.All(state.Attributes, a => Assert.False(a.Editable));
        Assert.False(state.CanDelete); Assert.False(state.CanImport); Assert.Empty(state.Creations);
        Assert.Throws<InvalidOperationException>(() => state.Edit(new[] { Edit() }));
    }
    [Fact] public void PlansRequireExactReadAndAreSingleAttemptEvenOnFailure()
    {
        using var temp = new Scratch(); var backend = new Backend(); var workflow = new EngineeringWorkflow(backend, temp.Path);
        Assert.Throws<InvalidOperationException>(() => workflow.Preview("edit", "hmi", backend.State.Path, "stale", new[] { Edit() }));
        var plan = Json(workflow.Preview("edit", "hmi", backend.State.Path, backend.State.Fingerprint, new[] { Edit() }));
        backend.Fail = true;
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
        Assert.Equal(1, backend.Attempts);
    }
    [Fact] public void CandidateTamperingAndBomCannotReachBackend()
    {
        using var temp = new Scratch(); var backend = new Backend(); var workflow = new EngineeringWorkflow(backend, temp.Path);
        var plan = Json(workflow.Preview("edit", "hmi", backend.State.Path, backend.State.Fingerprint, new[] { Edit() }));
        string candidate = System.IO.Path.Combine(plan.GetProperty("artifactDirectory").GetString()!, "candidate.json");
        byte[] bytes = File.ReadAllBytes(candidate); File.WriteAllBytes(candidate, Encoding.UTF8.GetPreamble().Concat(bytes).ToArray());
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!)); Assert.Equal(0, backend.Attempts);
    }
    [Fact] public void ImportStagesCompanionsAndDetectsChangedCandidateFiles()
    {
        using var temp = new Scratch(); string source = temp.Directory("source"); File.WriteAllText(System.IO.Path.Combine(source, "tags.hmi.yml"), "Tags: []");
        File.WriteAllText(System.IO.Path.Combine(source, "module.js"), "export function hello() {}\n");
        var backend = new Backend(); var workflow = new EngineeringWorkflow(backend, temp.Directory("plans"));
        var plan = Json(workflow.Preview("import", "hmi", backend.State.Path, backend.State.Fingerprint, sourceDirectory: source, overwrite: true));
        string dir = plan.GetProperty("artifactDirectory").GetString()!;
        Assert.True(File.Exists(System.IO.Path.Combine(dir, "candidate-files", "module.js")));
        File.AppendAllText(System.IO.Path.Combine(dir, "candidate-files", "module.js"), "//changed");
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!)); Assert.Equal(0, backend.Attempts);
    }
    [Fact] public void PlanAppliesExactCandidateAndRejectsReplay()
    {
        using var temp = new Scratch(); var backend = new Backend(); var workflow = new EngineeringWorkflow(backend, temp.Path);
        var plan = Json(workflow.Preview("edit", "hmi", backend.State.Path, backend.State.Fingerprint, new[] { Edit() }));
        var result = Json(workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
        Assert.Equal("applied", result.GetProperty("state").GetString()); Assert.Equal("Tag2", backend.State.Attributes[0].Value);
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(plan.GetProperty("planId").GetString()!, plan.GetProperty("candidateSha256").GetString()!));
    }
    [Fact] public void DeleteRequiresExplicitNativeCapability()
    {
        using var temp = new Scratch(); var backend = new Backend(); backend.State.CanDelete = false;
        var workflow = new EngineeringWorkflow(backend, temp.Path);
        Assert.Throws<InvalidOperationException>(() => workflow.Preview("delete", "hmi", backend.State.Path, backend.State.Fingerprint));
    }
    [Fact] public void XmlRejectsDtdAndResourceNameComesFromXml()
    {
        Assert.ThrowsAny<Exception>(() => TransferFiles.Xml("<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///test'>]><a>&x;</a>"));
        Assert.Equal("NativeCycleName", TransferFiles.XmlObjectName("<Document><Hmi.Cycle.Cycle><AttributeList><Name>NativeCycleName</Name></AttributeList></Hmi.Cycle.Cycle></Document>"));
    }
    [Fact] public void SemanticExportHashIgnoresOnlyVolatileHeadersIdsAndFormatting()
    {
        using var temp = new Scratch(); string file = System.IO.Path.Combine(temp.Path, "resource.xml");
        File.WriteAllText(file, "<Document><DocumentInfo><ExportDate>old</ExportDate></DocumentInfo><Hmi.TextList ID='1'><AttributeList><Name> List </Name></AttributeList></Hmi.TextList></Document>"); string before = TransferFiles.SemanticHash(temp.Path);
        File.WriteAllText(file, "<Document>\n<DocumentInfo><ExportDate>new</ExportDate></DocumentInfo>\n<Hmi.TextList ID='7'>\n<AttributeList><Name> List </Name></AttributeList>\n</Hmi.TextList></Document>"); Assert.Equal(before, TransferFiles.SemanticHash(temp.Path));
        File.WriteAllText(file, File.ReadAllText(file).Replace(" List ", "List")); Assert.NotEqual(before, TransferFiles.SemanticHash(temp.Path));
    }
    [Theory][InlineData("../escape")][InlineData("/absolute")][InlineData("C:/absolute")][InlineData("folder/../../escape")][InlineData("folder\\file")]
    public void BundlePathsCannotEscape(string path) => Assert.Throws<ArgumentException>(() => TransferBundle.SafePath(System.IO.Path.GetTempPath(), path));
    [Fact] public void BundleRejectsFailuresDuplicatesAndOrdersDependencies()
    {
        using var temp = new Scratch(); string native = temp.Directory("objects/00000/native"); File.WriteAllText(System.IO.Path.Combine(native, "payload.xml"), "<Document/>");
        var screens = new TransferResource { Path = "p:ScreenFolder/p:Screens/n:Screen", ImportPath = "p:ScreenFolder/p:Screens", NativeType = "Siemens.Engineering.Hmi.Screen.Screen", SourceDirectory = "objects/00000/native" };
        var tags = new TransferResource { Path = "p:TagFolder/p:TagTables/n:Tags", ImportPath = "p:TagFolder/p:TagTables", NativeType = "Siemens.Engineering.Hmi.Tag.TagTable", SourceDirectory = "objects/00000/native" };
        void Index(object[] entries, object[] failures) => File.WriteAllText(System.IO.Path.Combine(temp.Path, "export-index.json"), JsonSerializer.Serialize(new { format = "tia-hmi-export-v1", entries, failures }, JsonDefaults.Options));
        Index(new object[] { screens, tags }, Array.Empty<object>()); Assert.Equal(tags.Path, TransferBundle.Read(temp.Path)[0].Path);
        Index(new object[] { screens, screens }, Array.Empty<object>()); Assert.Throws<ArgumentException>(() => TransferBundle.Read(temp.Path));
        Index(new object[] { screens }, new object[] { "failed export" }); Assert.Throws<ArgumentException>(() => TransferBundle.Read(temp.Path));
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonDefaults.Options);
    private sealed class Backend : IEngineeringBackend
    {
        public EngineeringSnapshot State = EngineeringTests.State(); public int Attempts; public bool Fail;
        public EngineeringSnapshot ReadEngineering(string targetId, string path) => State;
        public void ValidateCreation(EngineeringSnapshot state, string type, CreationArgument[] args) { if (!state.Creations.Any(c => c.NativeType == type)) throw new NotSupportedException(); }
        public object ValidateImport(EngineeringPlan plan) => new { validated = true };
        public object ExportEngineering(string targetId, string path, string directory, bool requireComplete) { System.IO.Directory.CreateDirectory(directory); File.WriteAllText(System.IO.Path.Combine(directory, "state.json"), JsonSerializer.Serialize(State, JsonDefaults.Options)); return new { }; }
        public object ApplyEngineering(EngineeringPlan plan) { Attempts++; if (Fail || State.Fingerprint != plan.Before.Fingerprint) throw new InvalidOperationException(); State = plan.Candidate ?? State; return new { }; }
    }
    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia-engineering-tests-" + Guid.NewGuid().ToString("N"));
        public Scratch() { System.IO.Directory.CreateDirectory(Path); }
        public string Directory(string name) { string path = System.IO.Path.Combine(Path, name); System.IO.Directory.CreateDirectory(path); return path; }
        public void Dispose() { System.IO.Directory.Delete(Path, true); }
    }
}
