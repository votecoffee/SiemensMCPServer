using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Analysis;
using SiemensMCPServer.Mcp;
using Xunit;

public sealed class HmiAnalysisFileTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "hmi-analysis-test-" + Guid.NewGuid().ToString("N"));
        public string Export => Path.Combine(Root, "export");
        public string Controller => Path.Combine(Root, "controller.L5X");
        public string Request => Path.Combine(Root, "request.json");
        public string Output => Path.Combine(Root, "report");
        public HmiAnalysisFileRequest Spec;
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Export, "objects", "tags", "native")); Directory.CreateDirectory(Path.Combine(Export, "objects", "screen", "native"));
            File.WriteAllText(Path.Combine(Export, "objects", "tags", "native", "payload.xml"), HmiReferenceFixture.Table(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Export, "objects", "screen", "native", "payload.xml"), HmiReferenceFixture.Screen(), new UTF8Encoding(false));
            File.WriteAllText(Controller, ControllerFixture.L5x, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Export, "export-index.json"), JsonSerializer.Serialize(new { format = "tia-engineering-export-v1", family = "classic", targetId = "hmi", path = "",
                entries = new[] { new { path = "tags", sourceDirectory = "objects/tags/native" }, new { path = "screen", sourceDirectory = "objects/screen/native" } }, failures = Array.Empty<object>() }));
            Spec = new HmiAnalysisFileRequest { ExportDirectory = Export,
                ControllerFiles = new[] { new ControllerFileInput { Id = "plc", Format = "rockwell-l5x", Path = Controller } },
                ConnectionMappings = HmiReferenceFixture.Input().ConnectionMappings };
            Save();
        }
        public void Save() => File.WriteAllText(Request, JsonSerializer.Serialize(Spec, JsonDefaults.Options), new UTF8Encoding(false));
        public void Dispose() => Directory.Delete(Root, true);
    }
    [Fact] public void LoadsActualMetadataFilesAndWritesHashManifestReportsWithoutChangingInputs()
    {
        using var fixture = new Fixture(); string before = HmiAnalysisFiles.FileHash(fixture.Controller);
        fixture.Spec.Changes = new[] { new HmiProposedChange { Kind = "tag", Name = @"HMI\Axis\Position" } }; fixture.Save();
        HmiAnalysisFiles.Run(fixture.Request, fixture.Output);
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "summary.json")));
        Assert.Equal(1, summary.RootElement.GetProperty("bindingStatuses").GetProperty("valid").GetInt32());
        Assert.True(summary.RootElement.GetProperty("impactReferences").GetInt32() > 0);
        Assert.Equal(before, HmiAnalysisFiles.FileHash(fixture.Controller));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            Assert.Equal(file.GetProperty("sha256").GetString(), HmiAnalysisFiles.FileHash(Path.Combine(fixture.Output, file.GetProperty("path").GetString()!)));
    }
    [Theory] [InlineData("../outside")] [InlineData("/absolute")] [InlineData("C:/absolute")] [InlineData("objects/./tags/native")]
    public void ExportIndexCannotReadOutsideItsRootOrUseAmbiguousPaths(string directory)
    {
        using var fixture = new Fixture(); string index = Path.Combine(fixture.Export, "export-index.json");
        File.WriteAllText(index, File.ReadAllText(index).Replace("objects/tags/native", directory));
        Assert.Throws<ArgumentException>(() => HmiAnalysisFiles.Load(fixture.Spec));
    }
    [Fact] public void ExistingOutputAndSourceExportDirectoriesAreNeverOverwritten()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Output); File.WriteAllText(Path.Combine(fixture.Output, "marker"), "preserved");
        Assert.Throws<ArgumentException>(() => HmiAnalysisFiles.Run(fixture.Request, fixture.Output));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(fixture.Output, "marker")));
        Assert.Throws<ArgumentException>(() => HmiAnalysisFiles.Run(fixture.Request, Path.Combine(fixture.Export, "analysis")));
    }
    [Fact] public void IncompleteAndUnsupportedExportsRetainTheirCoverageLimitations()
    {
        using var fixture = new Fixture(); string index = Path.Combine(fixture.Export, "export-index.json");
        File.WriteAllText(index, File.ReadAllText(index).Replace("\"failures\":[]", "\"failures\":[{\"error\":\"unsupported native object\"}]"));
        var loaded = HmiAnalysisFiles.Load(fixture.Spec); Assert.False(loaded.Analysis.ExportReportedComplete); Assert.Single(loaded.Analysis.ExportFailures);
    }
    [Fact] public void CapturedConnectionDefinitionsCheckNamesAndPreserveNativeDriverMetadata()
    {
        using var fixture = new Fixture(); string path = Path.Combine(fixture.Export, "object.json");
        File.WriteAllText(path, "{\"nativeType\":\"Siemens.Engineering.Hmi.Communication.HmiConnection\",\"path\":\"connections/Rockwell\",\"attributes\":[{\"name\":\"Name\",\"value\":\"Rockwell\"},{\"name\":\"DriverName\",\"value\":\"Rockwell EtherNet/IP\"}]}");
        var loaded = HmiAnalysisFiles.Load(fixture.Spec); Assert.True(loaded.Analysis.ConnectionMetadataAvailable);
        Assert.Equal("Rockwell EtherNet/IP", loaded.Analysis.ConnectionDefinitions.Single().Attributes["DriverName"]);
        Assert.Equal("valid", HmiReferenceAnalyzer.Analyze(loaded.Analysis).Bindings.Single().Status);
        loaded.Analysis.ConnectionDefinitions[0].Name = "Other"; Assert.Equal("missingHmiConnection", HmiReferenceAnalyzer.Analyze(loaded.Analysis).Bindings.Single().Status);
    }
    [Fact] public void CsvIsAReadableProjectionAndJsonRetainsExactUntrustedReferenceText()
    {
        using var fixture = new Fixture(); string screen = Path.Combine(fixture.Export, "objects", "screen", "native", "payload.xml");
        File.WriteAllText(screen, HmiReferenceFixture.Screen("=1+1"));
        HmiAnalysisFiles.Run(fixture.Request, fixture.Output);
        Assert.Contains("\"'=1+1\"", File.ReadAllText(Path.Combine(fixture.Output, "references.csv")));
        using var references = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "references.json")));
        Assert.Contains(references.RootElement.EnumerateArray(), r => r.GetProperty("referenceText").GetString() == "=1+1");
    }
}
