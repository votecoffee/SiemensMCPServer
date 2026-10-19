using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class PlcInspectionTests
{
    private const string BlockPath = "p:BlockGroup/p:Blocks/n:Example";
    private static JsonElement Arguments(object? capture = null, object? options = null, string path = BlockPath) => ExtensionWire.Json(new {
        plcId = "devices/Controller/CPU", path, capture = capture ?? new { withDependencies = false }, options = options ?? new { } });
    private sealed class Backend : IPlcInspectionBackend
    {
        public int Reads;
        public bool WithDependencies;
        public PlcSourceSnapshot CapturePlcSource(string id, string path, bool dependencies) {
            Reads++; WithDependencies = dependencies; return new PlcSourceSnapshot { PlcId = id, Path = path, Text = "Count := Count + 1;\r\n// COUNT 😀\nLast := 0;", FileSha256 = "native-file-hash" };
        }
        public PlcReferenceSnapshot CapturePlcReferences(string id, string path, PlcReferenceCapture capture) {
            Reads++; return new PlcReferenceSnapshot { PlcId = id, Path = path, Filter = capture.Filter, Truncated = true };
        }
    }
    [Fact] public void ReadsNativeSnapshotWithoutChangingRequestedDependencyScope()
    {
        var backend = new Backend(); var workflow = new PlcInspectionWorkflow(backend, new LocalFeatureEngine());
        var reply = ExtensionWire.Json(workflow.Read("read_source", "source", Arguments(new { withDependencies = true }, new { maxCharacters = 5 })));
        Assert.Equal(1, backend.Reads); Assert.True(backend.WithDependencies);
        Assert.Equal("Count", reply.GetProperty("data").GetProperty("text").GetString());
        Assert.True(reply.GetProperty("data").GetProperty("truncated").GetBoolean());
        Assert.Equal("native-file-hash", reply.GetProperty("evidence").GetProperty("fileSha256").GetString());
        Assert.False(reply.GetProperty("sourceGeneratedIntoProject").GetBoolean());
    }
    [Fact] public void LiteralSearchHasExactPositionsCountAndBoundedContext()
    {
        var reply = ExtensionWire.Json(new PlcInspectionWorkflow(new Backend(), new LocalFeatureEngine()).Read("find_in_source", "source",
            Arguments(options: new { query = "count", caseSensitive = false, maxMatches = 2 }))).GetProperty("data");
        Assert.Equal(3, reply.GetProperty("totalMatches").GetInt32()); Assert.True(reply.GetProperty("truncated").GetBoolean());
        var matches = reply.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(1, matches[0].GetProperty("line").GetInt32()); Assert.Equal(10, matches[1].GetProperty("column").GetInt32());
        var source = new PlcSourceSnapshot { Text = new string('x', 3000) + "needle" + new string('x', 3000) };
        var match = PlcInspectionOperations.Execute("find_in_source", ExtensionWire.Json(new { phase = "snapshot", snapshot = source,
            arguments = Arguments(options: new { query = "needle", caseSensitive = true }) })).GetProperty("matches")[0];
        Assert.Equal(3001, match.GetProperty("column").GetInt32()); Assert.True(match.GetProperty("contextTruncated").GetBoolean());
        Assert.Contains("needle", match.GetProperty("context").GetString()); Assert.True(match.GetProperty("context").GetString()!.Length <= 512);
    }
    [Fact] public void WorkerOptionsAreValidatedBeforeNativeCapture()
    {
        var backend = new Backend(); var workflow = new PlcInspectionWorkflow(backend, new LocalFeatureEngine());
        Assert.Throws<ArgumentException>(() => workflow.Read("find_in_source", "source", Arguments(options: new { query = "", caseSensitive = true })));
        Assert.Throws<JsonException>(() => workflow.Read("read_source", "source", Arguments(options: new { destination = "unexpected" })));
        Assert.Throws<ArgumentException>(() => workflow.Read("read_source", "source", Arguments(options: new { maxCharacters = 262145 })));
        Assert.Equal(0, backend.Reads);
    }
    [Fact] public void NativeScopeCannotBeHiddenInWorkerOptionsOrRedirectedToHardware()
    {
        var backend = new Backend(); var workflow = new PlcInspectionWorkflow(backend, new LocalFeatureEngine());
        foreach (string path in new[] { "", "$projectGraphics", "p:DeviceItems/n:CPU", "p:OnlineProvider/n:CPU", "p:BlockGroup/p:Blocks" })
            Assert.Throws<ArgumentException>(() => workflow.Read("read_source", "source", Arguments(path: path)));
        Assert.Throws<JsonException>(() => workflow.Read("read_source", "source", Arguments(new { withDependencies = false, destination = "unexpected" })));
        Assert.Throws<JsonException>(() => workflow.Read("read_source", "source", Arguments(new { })));
        Assert.Throws<ArgumentException>(() => workflow.Read("cross_references", "crossReferences", Arguments(new { filter = "Force", maxRows = 10 })));
        Assert.Throws<ArgumentException>(() => workflow.Read("cross_references", "crossReferences", Arguments(new { filter = "AllObjects", maxRows = 1001 })));
        Assert.Equal(0, backend.Reads);
    }
    [Fact] public void ReadTruncationDoesNotSplitSupplementaryUnicodeCharacters()
    {
        var source = new PlcSourceSnapshot { Text = "A😀B" };
        var result = PlcInspectionOperations.Execute("read_source", ExtensionWire.Json(new { phase = "snapshot", snapshot = source,
            arguments = Arguments(options: new { maxCharacters = 2 }) }));
        Assert.Equal("A", result.GetProperty("text").GetString()); Assert.True(result.GetProperty("truncated").GetBoolean());
    }
    [Fact] public void SourceFilesPreserveRawBomHashAndDecodeStrictUtf8()
    {
        string directory = Path.Combine(Path.GetTempPath(), "plc-inspection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            string path = Path.Combine(directory, "payload.scl"); File.WriteAllText(path, "// Über 😀", new UTF8Encoding(true));
            var source = PlcInspectionFiles.Read(directory, "payload.scl");
            Assert.Equal("// Über 😀", source.Text); Assert.Equal(File.ReadAllBytes(path).LongLength, source.Bytes);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), source.FileSha256);
            File.WriteAllBytes(path, new byte[] { 255, 254, 65, 0 }); Assert.Throws<DecoderFallbackException>(() => PlcInspectionFiles.Read(directory, "payload.scl"));
            File.WriteAllBytes(path, new byte[] { 0 }); Assert.Throws<InvalidDataException>(() => PlcInspectionFiles.Read(directory, "payload.scl"));
            File.WriteAllBytes(path, new byte[PlcInspectionFiles.MaxBytes + 1]); Assert.Throws<InvalidDataException>(() => PlcInspectionFiles.Read(directory, "payload.scl"));
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public void ReadCatalogDeclaresOnlyAllowedNativeSnapshots()
    {
        var operations = FeatureOperations.Describe("plc").Where(o => o.Route == "plcRead").ToArray(); Assert.Equal(3, operations.Length);
        Assert.All(operations, o => { Assert.True(o.IsCompatible("plc")); Assert.False(o.IsCompatible("comfort")); Assert.False(o.Preview); });
        var op = operations[0]; op.SnapshotKind = "download"; Assert.False(op.IsCompatible("plc"));
        op.SnapshotKind = "source"; op.Preview = true; Assert.False(op.IsCompatible("plc"));
        op.Preview = false; op.Route = "local"; Assert.False(op.IsCompatible("plc"));
    }
    [Fact] public void CrossReferenceCaptureRetainsTruncationEvidence()
    {
        var backend = new Backend();
        var result = ExtensionWire.Json(new PlcInspectionWorkflow(backend, new LocalFeatureEngine()).Read("cross_references", "crossReferences",
            Arguments(new { filter = "ObjectsWithReferences", maxRows = 3 })));
        Assert.Equal(1, backend.Reads); Assert.True(result.GetProperty("evidence").GetProperty("truncated").GetBoolean());
        Assert.Equal("ObjectsWithReferences", result.GetProperty("evidence").GetProperty("filter").GetString());
    }
}
