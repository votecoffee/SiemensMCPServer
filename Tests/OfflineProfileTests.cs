using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Offline;
using Xunit;

namespace SiemensMCPServer.Tests;

public sealed class OfflineProfileTests
{
    [Fact] public void ClosedCatalogContainsOnlySerializedContentOperations()
    {
        var catalog = ExtensionWire.Json(OfflineOperations.Catalog());
        Assert.Equal("direct-offline", catalog.GetProperty("profile").GetString());
        Assert.False(catalog.GetProperty("nativeEngineeringAvailable").GetBoolean());
        Assert.False(catalog.GetProperty("runtimeMonitoringAvailable").GetBoolean());
        Assert.False(catalog.GetProperty("filesystemAccessAvailable").GetBoolean());
        var names = catalog.GetProperty("operations").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Equal(17, names.Length); Assert.Contains("inspect_text_list", names); Assert.Contains("analyze_script_triggers", names);
        foreach (var name in names) Assert.NotEqual(JsonValueKind.Undefined, ExtensionWire.Json(OfflineOperations.Describe(name!)).ValueKind);
    }

    [Theory]
    [InlineData("tia_attach")][InlineData("tia_list_processes")][InlineData("tia_save_project")]
    [InlineData("read_project_texts")][InlineData("read_source")][InlineData("prepare_source")]
    [InlineData("edit_text_list")][InlineData("edit_xml")][InlineData("build_screen_package")]
    [InlineData("$hello")][InlineData("reload_extensions")][InlineData("comfort/inspect_xml")]
    public void NativeFileWorkerAndMutationRoutesCannotBeSelected(string operation)
    {
        Assert.Throws<ArgumentException>(() => OfflineOperations.Describe(operation));
        Assert.Throws<ArgumentException>(() => OfflineOperations.Execute(operation, ExtensionWire.Json(new { })));
    }

    [Theory][InlineData("--allow-writes")][InlineData("--tia-version=V15.1")]
    [InlineData("--settings-file=C:/policy.json")][InlineData("--extensions-root=C:/workers")]
    [InlineData("--config=C:/service.json")][InlineData("--profile=engineering")]
    public void StartupCannotExpandProfile(string flag) => Assert.Throws<ArgumentException>(() => OfflineOperations.ValidateArguments(new[] { flag }));

    [Fact] public void TracesReferencesAndValidatesBindingsAndImpactWithSameHash()
    {
        var analysis = HmiReferenceFixture.Input();
        var trace = OfflineOperations.Execute("analyze_hmi_references", ExtensionWire.Json(new { analysis, view = "references", maxItems = 1 }));
        string hash = trace.GetProperty("summary").GetProperty("analysisSha256").GetString()!;
        var bindings = OfflineOperations.Execute("validate_hmi_bindings", ExtensionWire.Json(new { analysis }));
        Assert.Equal(hash, bindings.GetProperty("summary").GetProperty("analysisSha256").GetString());
        var impact = OfflineOperations.Execute("reference_impact", ExtensionWire.Json(new { analysis, expectedAnalysisSha256 = hash,
            changes = new[] { new { kind = "tag", name = @"HMI\Axis\Position", newName = @"HMI\Axis\New" } } }));
        Assert.True(impact.GetProperty("affectedReferences").GetInt32() > 0);
        Assert.False(bindings.GetProperty("summary").GetProperty("nativeBindingVerified").GetBoolean());
    }

    [Fact] public void TextListRowsRetainLanguagesAndPageWithoutMutatingSource()
    {
        string snapshot = TextListTests.Fixture();
        var result = OfflineOperations.Execute("inspect_text_list", ExtensionWire.Json(new { snapshot, offset = 0, maxItems = 1 }));
        Assert.Equal("Messages_TL", result.GetProperty("name").GetString());
        Assert.Single(result.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.True(result.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Equal(snapshot, TextListTests.Fixture());
    }

    [Fact] public void PathsAlternateEnvelopesAndDuplicateKeysAreRejected()
    {
        Assert.Throws<JsonException>(() => OfflineOperations.Execute("import_controller_declarations", ExtensionWire.Json(new {
            controllerDocuments = new[] { new { id = "plc", format = "rockwell-l5x", path = "missing.L5X" } } })));
        Assert.Throws<ArgumentException>(() => OfflineOperations.Execute("inspect_xml", ExtensionWire.Json(new { arguments = new { xml = "<Document/>", editable = true } })));
        using var duplicate = JsonDocument.Parse("{\"snapshot\":\"first\",\"snapshot\":\"second\"}");
        Assert.Throws<ArgumentException>(() => OfflineOperations.Execute("inspect_text_list", duplicate.RootElement));
        using var nested = JsonDocument.Parse("{\"controllerDocuments\":[{\"id\":\"first\",\"id\":\"second\"}]}");
        Assert.Throws<ArgumentException>(() => OfflineOperations.Execute("import_controller_declarations", nested.RootElement));
    }

    [Fact] public void UnifiedTextListInspectionRetainsOrderedLanguageSlots()
    {
        const string configuration = "TextListContainers:\n  TextList:\n    ResourceListType: TextList\n    ResourceLists:\n      Messages_TL:\n        RangeType: Decimal\n        Entries:\n          Entry_0:\n            Type: SingleValue\n            Value: 7\n            FromValue: 7\n            ToValue: 7\n            IsDefaultEntry: false\n            Text: MyTexts.Text_0\n";
        const string library = "TextLibraries:\n  MyTexts:\n    Type: Text\n    Languages:\n      - en-US\n      - es-ES\n    DefaultLanguage: en-US\n    Entries:\n      Text_0:\n        Text:\n          - Ready\n          - Listo\n";
        string snapshot = JsonSerializer.Serialize(new { format = "unified-yaml-v2", listName = "Messages_TL", configuration = "#Version: 2.0\n" + configuration, textLibrary = "#Version: 2.0\n" + library, nativeListNames = new[] { "Messages_TL" } });
        var result = OfflineOperations.Execute("inspect_text_list", ExtensionWire.Json(new { snapshot }));
        var rows = result.GetProperty("page").GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("Ready", rows.Single(row => row.GetProperty("culture").GetString() == "en-US").GetProperty("text").GetString());
        Assert.Equal("Listo", rows.Single(row => row.GetProperty("culture").GetString() == "es-ES").GetProperty("text").GetString());
    }

    [Fact] public void InputCapCountsEncodedBytes()
    {
        var input = JsonSerializer.SerializeToElement(new { xml = new string('a', OfflineOperations.MaxInputBytes), editable = false });
        Assert.Throws<ArgumentException>(() => OfflineOperations.Execute("inspect_xml", input));
    }

    [Fact] public async Task WirePreservesUnicodeAcrossReadsAndResetsOnlyOnNewline()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"text\":\"Español 😀\"}\n{}\n");
        using var source = new MemoryStream(bytes);
        using var bounded = new BoundedInputStream(source);
        using var output = new MemoryStream();
        var buffer = new byte[3]; int count;
        while ((count = await bounded.ReadAsync(buffer.AsMemory())) != 0) output.Write(buffer, 0, count);
        Assert.Equal(bytes, output.ToArray());
    }

    [Theory][InlineData(false)][InlineData(true)] public async Task OversizedWireFrameFailsBeforeCompleteSdkMessage(bool asynchronous)
    {
        using var source = new MemoryStream(Enumerable.Repeat((byte)'x', BoundedInputStream.MaxFrameBytes + 1).ToArray());
        using var bounded = new BoundedInputStream(source);
        var buffer = new byte[65536];
        if (asynchronous) await Assert.ThrowsAsync<InvalidDataException>(async () => { while (await bounded.ReadAsync(buffer.AsMemory()) != 0) { } });
        else Assert.Throws<InvalidDataException>(() => { while (bounded.Read(buffer, 0, buffer.Length) != 0) { } });
    }
}
