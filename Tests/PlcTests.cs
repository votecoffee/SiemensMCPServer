using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class PlcTests
{
    [Theory][InlineData(null)][InlineData("Online")][InlineData("Connecting")][InlineData("Disconnecting")][InlineData("Protected")][InlineData("NotReachable")][InlineData("Incompatible")][InlineData("offline")]
    public void PlcMutationsRequireAnExplicitVerifiedOfflineState(string? state) => Assert.Throws<InvalidOperationException>(() => PlcPolicy.RequireOffline(state));
    [Fact] public void VerifiedOfflineIsAccepted() => PlcPolicy.RequireOffline("Offline");
    [Fact] public void SoftwareScopeExcludesHardwareOnlineAndWatchForceOperations()
    {
        Assert.True(PlcPolicy.AllowsRootNavigator("BlockGroup")); Assert.True(PlcPolicy.AllowsRootNavigator("TypeGroup")); Assert.True(PlcPolicy.AllowsRootNavigator("TagTableGroup"));
        Assert.False(PlcPolicy.AllowsRootNavigator("WatchAndForceTableGroup")); Assert.False(PlcPolicy.AllowsRootNavigator("TechnologicalObjectGroup")); Assert.False(PlcPolicy.AllowsRootNavigator("Parent"));
        Assert.True(PlcPolicy.AllowsResourceType("Siemens.Engineering.SW.Blocks.FB")); Assert.False(PlcPolicy.AllowsResourceType("Siemens.Engineering.HW.Device")); Assert.False(PlcPolicy.AllowsResourceType("Siemens.Engineering.Online.OnlineProvider"));
    }
    [Theory][InlineData("SCL", "payload.scl")][InlineData("STL", "payload.awl")]
    public void NativeSourceFilesPreserveExactCodeUnicodeAndBom(string language, string fileName)
    {
        using var temp = new Scratch(); var source = Source(language); PlcSourceFiles.Write(temp.Path, source);
        Assert.Equal(source.Code, PlcSourceFiles.Read(temp.Path).Code);
        Assert.Equal(new byte[] { 239, 187, 191 }, File.ReadAllBytes(Path.Combine(temp.Path, fileName)).Take(3));
    }
    [Fact] public void SourceReplacementScopeMustBeExplicit()
    {
        var source = Source(); source.AllowBlockReplacement = false;
        Assert.Throws<ArgumentException>(() => source.Validate());
    }
    [Fact] public void WorkerCannotEscalateReplacementOrRewriteExactSource()
    {
        var requested = Source(); var candidate = Source(); requested.RequireExactCandidate(candidate);
        candidate.Code += "// changed";
        Assert.Throws<InvalidOperationException>(() => requested.RequireExactCandidate(candidate));
        requested.AllowBlockReplacement = false; candidate = Source();
        Assert.Throws<ArgumentException>(() => requested.RequireExactCandidate(candidate));
    }
    [Theory][InlineData("../outside")][InlineData("C:\\outside")][InlineData("bad/name")][InlineData("name\nnext")]
    public void SourceNameCannotBecomeAPath(string name) { var source = Source(); source.Name = name; Assert.Throws<ArgumentException>(() => source.Validate()); }
    [Fact] public void SourceRejectsUnknownLanguageEmptyCodeAndExtraFiles()
    {
        using var temp = new Scratch(); var source = Source(); source.Language = "python"; Assert.Throws<ArgumentException>(() => source.Validate());
        source = Source(); source.Code = ""; Assert.Throws<ArgumentException>(() => source.Validate());
        PlcSourceFiles.Write(temp.Path, Source()); File.WriteAllText(Path.Combine(temp.Path, "extra.scl"), "unexpected"); Assert.Throws<ArgumentException>(() => PlcSourceFiles.Read(temp.Path));
    }
    [Fact] public void PlcBundleOrdersTypesTagsThenBlocksAndAcceptsGeneralFormat()
    {
        using var temp = new Scratch(); string files = Path.Combine(temp.Path, "native"); Directory.CreateDirectory(files); File.WriteAllText(Path.Combine(files, "payload.xml"), "<Document/>");
        var entries = new[] {
            new TransferResource { Path = "p:BlockGroup/p:Blocks/n:FC", ImportPath = "p:BlockGroup/p:Blocks", NativeType = "Siemens.Engineering.SW.Blocks.FC", SourceDirectory = "native" },
            new TransferResource { Path = "p:TypeGroup/p:Types/n:UDT", ImportPath = "p:TypeGroup/p:Types", NativeType = "Siemens.Engineering.SW.Types.PlcStruct", SourceDirectory = "native" },
            new TransferResource { Path = "p:TagTableGroup/p:TagTables/n:Tags", ImportPath = "p:TagTableGroup/p:TagTables", NativeType = "Siemens.Engineering.SW.Tags.PlcTagTable", SourceDirectory = "native" } };
        File.WriteAllText(Path.Combine(temp.Path, "export-index.json"), JsonSerializer.Serialize(new { format = "tia-engineering-export-v1", entries, failures = Array.Empty<object>() }, JsonDefaults.Options));
        var ordered = TransferBundle.Read(temp.Path); Assert.Contains("TypeGroup", ordered[0].Path); Assert.Contains("TagTableGroup", ordered[1].Path); Assert.Contains("BlockGroup", ordered[2].Path);
    }
    [Fact] public void SourcePlanBindsCodeFilesAndCannotReplayNativeFailure()
    {
        using var temp = new Scratch(); string sourceDir = Path.Combine(temp.Path, "source"); PlcSourceFiles.Write(sourceDir, Source());
        var backend = new Backend(); var workflow = new EngineeringWorkflow(backend, Path.Combine(temp.Path, "plans"));
        var report = JsonSerializer.SerializeToElement(workflow.Preview("source", "plc", "", backend.State.Fingerprint, sourceDirectory: sourceDir), JsonDefaults.Options);
        string id = report.GetProperty("planId").GetString()!, hash = report.GetProperty("candidateSha256").GetString()!;
        string code = Path.Combine(report.GetProperty("artifactDirectory").GetString()!, "candidate-files", "payload.scl");
        byte[] original = File.ReadAllBytes(code); File.AppendAllText(code, "\n//changed");
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash)); Assert.Equal(0, backend.Attempts);
        File.WriteAllBytes(code, original); backend.Fail = true;
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash)); Assert.Equal(1, backend.Attempts);
        Assert.Throws<InvalidOperationException>(() => workflow.Apply(id, hash));
    }
    [Fact] public void PlcReadbackAllowsOnlyKnownComputedMetadataToChange()
    {
        var expected = new EngineeringSnapshot { TargetId = "plc", NativeType = "Siemens.Engineering.SW.Blocks.FB", Attributes = new[] {
            new EngineeringValue { Name = "HeaderAuthor", Type = "System.String", Value = "Author", Editable = true },
            new EngineeringValue { Name = "ModifiedDate", Type = "System.DateTime", Value = "old", Editable = false },
            new EngineeringValue { Name = "CreationDate", Type = "System.DateTime", Value = "created", Editable = false } } };
        var actual = JsonSerializer.Deserialize<EngineeringSnapshot>(JsonSerializer.Serialize(expected, JsonDefaults.Options), JsonDefaults.Options)!;
        actual.Attributes[1].Value = "new";
        Assert.NotEqual(expected.Fingerprint, actual.Fingerprint); Assert.Equal(expected.ReadbackFingerprint, actual.ReadbackFingerprint);
        actual.Attributes[0].Value = "unexpected authored change"; Assert.NotEqual(expected.ReadbackFingerprint, actual.ReadbackFingerprint);
        actual.Attributes[0].Value = "Author"; actual.Attributes[2].Value = "different object"; Assert.NotEqual(expected.ReadbackFingerprint, actual.ReadbackFingerprint);
    }
    private static PlcSourceInput Source(string language = "SCL") => new PlcSourceInput { Name = "Codex_Test", Language = language,
        Code = "FUNCTION \"FC_Test\" : Void\n// Grüße 🟢\nBEGIN\nEND_FUNCTION\n", AllowBlockReplacement = true };
    private sealed class Backend : IEngineeringBackend
    {
        public EngineeringSnapshot State = new EngineeringSnapshot { TargetId = "plc", NativeType = "Siemens.Engineering.SW.PlcSoftware" }; public int Attempts; public bool Fail;
        public EngineeringSnapshot ReadEngineering(string targetId, string path) => State;
        public void ValidateCreation(EngineeringSnapshot state, string type, CreationArgument[] args) => throw new NotSupportedException();
        public object ValidateImport(EngineeringPlan plan) => PlcSourceFiles.Read(Path.Combine(plan.Directory, "candidate-files"));
        public object ExportEngineering(string targetId, string path, string directory, bool requireComplete) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "baseline.json"), "{}"); return new { }; }
        public object ApplyEngineering(EngineeringPlan plan) { Attempts++; if (Fail) throw new InvalidOperationException("native failure"); return new { }; }
    }
    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plc-tests-" + Guid.NewGuid().ToString("N"));
        public Scratch() { Directory.CreateDirectory(Path); } public void Dispose() { Directory.Delete(Path, true); }
    }
}
