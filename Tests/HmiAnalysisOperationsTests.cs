using System;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer.Analysis;
using SiemensMCPServer.Extensions;
using Xunit;

public sealed class HmiAnalysisOperationsTests
{
    [Fact] public void NewOperationsUseReadOnlyLocalRoutesOnTheExistingWorkerProtocol()
    {
        foreach (var name in new[] { "import_controller_declarations", "analyze_hmi_references", "validate_hmi_bindings", "reference_impact" })
        {
            var operation = FeatureOperations.Describe("comfort").Single(o => o.Name == name);
            Assert.Equal("local", operation.Route); Assert.False(operation.Preview); Assert.Empty(operation.SnapshotKind); Assert.True(operation.IsCompatible("comfort"));
            Assert.False(operation.InputSchema.GetProperty("additionalProperties").GetBoolean());
        }
    }
    [Theory] [InlineData("symbols", 9)] [InlineData("types", 1)] [InlineData("members", 4)]
    public void ImportsMetadataWithBoundedPagedInspection(string section, int total)
    {
        var input = HmiReferenceFixture.Input();
        var result = FeatureOperations.Execute("comfort", "import_controller_declarations", ExtensionWire.Json(new {
            controllerDocuments = input.ControllerDocuments, section, offset = 0, maxItems = 1 }));
        Assert.Single(result.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal(total, result.GetProperty("page").GetProperty("totalItems").GetInt32()); Assert.False(result.GetProperty("nativeBindingVerified").GetBoolean());
    }
    [Fact] public void AnalysisValidationAndImpactReturnTheSameSnapshotHashWithoutNativeAccess()
    {
        var input = HmiReferenceFixture.Input();
        var result = FeatureOperations.Execute("comfort", "analyze_hmi_references", ExtensionWire.Json(new { analysis = input, view = "references", maxItems = 1 }));
        string hash = result.GetProperty("summary").GetProperty("analysisSha256").GetString()!;
        Assert.True(result.GetProperty("page").GetProperty("hasMore").GetBoolean());
        var validated = FeatureOperations.Execute("comfort", "validate_hmi_bindings", ExtensionWire.Json(new { analysis = input }));
        Assert.Equal(hash, validated.GetProperty("summary").GetProperty("analysisSha256").GetString());
        var impact = FeatureOperations.Execute("comfort", "reference_impact", ExtensionWire.Json(new { analysis = input, expectedAnalysisSha256 = hash,
            changes = new[] { new HmiProposedChange { Kind = "tag", Name = @"HMI\Axis\Position", NewName = @"HMI\Axis\NewPosition" } } }));
        Assert.True(impact.GetProperty("affectedReferences").GetInt32() > 0); Assert.Equal(hash, impact.GetProperty("analysisSha256").GetString());
        Assert.Throws<InvalidOperationException>(() => FeatureOperations.Execute("comfort", "reference_impact", ExtensionWire.Json(new {
            analysis = input, expectedAnalysisSha256 = "stale", changes = new[] { new HmiProposedChange { Kind = "tag", Name = @"HMI\Axis\Position" } } })));
    }
    [Theory] [InlineData(-1, 10)] [InlineData(0, 0)] [InlineData(0, 201)] [InlineData(1000001, 1)]
    public void RejectsInvalidPageBounds(int offset, int maxItems)
        => Assert.Throws<ArgumentException>(() => HmiAnalysisOperations.Page(new object[] { new { value = "text" } }, offset, maxItems));
    [Fact] public void RepliesPageByEscapedJsonCostAndRefuseOversizedSingleRows()
    {
        var page = ExtensionWire.Json(HmiAnalysisOperations.Page(Enumerable.Range(0, 50).Select(i => (object)new { i, text = new string('中', 2000) }), 0, 50));
        Assert.True(page.GetProperty("payloadLimited").GetBoolean()); Assert.True(page.GetProperty("nextOffset").GetInt32() > 0);
        Assert.Throws<ArgumentException>(() => HmiAnalysisOperations.Page(new object[] { new { text = new string('中', 50000) } }, 0, 1));
    }
    [Fact] public void FilesystemPathsAndUnknownArgumentsCannotEnterTheSerializedWorkerRoute()
    {
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "analyze_hmi_references", ExtensionWire.Json(new { exportDirectory = @"C:\input" })));
        Assert.Throws<JsonException>(() => FeatureOperations.Execute("comfort", "import_controller_declarations", ExtensionWire.Json(new {
            controllerDocuments = new[] { new { id = "plc", format = "rockwell-l5x", path = @"C:\input.L5X" } } })));
    }
}
