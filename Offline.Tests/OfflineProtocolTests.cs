using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SiemensMCPServer.Offline;
using Xunit;

namespace SiemensMCPServer.Offline.Tests;

// Actual JSON-RPC bytes -> SDK schema/binding -> production OfflineTools -> parser.
// No product Main/lifetime, subprocess, installation, settings or native service.
public sealed class OfflineProtocolTests
{
    private static object Analysis(int padding = 0) => new {
        analysis = new { resources = new[] { new { path = "Screens/minimal.xml", xml =
            "<?xml version=\"1.0\"?><Document><Engineering version=\"V15.1\"/><!--Café 日本語 😀" + new string('x', padding)
            + "--><Hmi.Screen.Screen ID=\"0\"><AttributeList><Name>Overview</Name></AttributeList></Hmi.Screen.Screen></Document>" } } },
        view = "references", maxItems = 1
    };

    private static JsonElement Success(JsonElement response)
    {
        Assert.False(response.TryGetProperty("isError", out var error) && error.GetBoolean(), response.ToString());
        var envelope = response.GetProperty("structuredContent");
        Assert.Equal("direct-offline", envelope.GetProperty("profile").GetString());
        Assert.False(envelope.GetProperty("nativeOperationsPerformed").GetBoolean());
        return envelope.GetProperty("result");
    }

    private static void Error(JsonElement response, string text)
    {
        Assert.True(response.GetProperty("isError").GetBoolean(), response.ToString());
        Assert.Contains(text, response.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact] public async Task AdvertisedStringContractAcceptsSerializedReferenceArguments()
    {
        await using var wire = await Wire.Create();
        var listed = await wire.Send("tools/list", new { });
        var tools = listed.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(3, tools.Length);
        var result = Success(await wire.Analyze(JsonSerializer.Serialize(Analysis())));
        var schema = tools.Single(t => t.GetProperty("name").GetString() == "siemens_offline_analyze").GetProperty("inputSchema");
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("input").GetProperty("type").GetString());
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "input");
        Assert.Equal(1, result.GetProperty("summary").GetProperty("resources").GetInt32());
        Assert.False(result.GetProperty("summary").GetProperty("nativeBindingVerified").GetBoolean());
    }

    [Fact] public async Task DiscoveryDistinguishesWireStringFromDecodedObjectSchema()
    {
        await using var wire = await Wire.Create();
        foreach (var operation in new[] { "analyze_hmi_references", "inspect_text_list" }) {
            var response = await wire.Call("siemens_offline_describe", new { operation });
            using var data = JsonDocument.Parse(response.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal("object", data.RootElement.GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.Equal("string", data.RootElement.GetProperty("inputContract").GetProperty("wireType").GetString());
            Assert.Equal(OfflineOperations.MaxInputBytes, data.RootElement.GetProperty("inputContract").GetProperty("maxDecodedUtf8Bytes").GetInt32());
        }
        var catalog = await wire.Call("siemens_offline_catalog", new { });
        using var document = JsonDocument.Parse(catalog.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("string", document.RootElement.GetProperty("inputContract").GetProperty("wireType").GetString());
    }

    [Fact] public async Task ScriptTriggerInventoryTraversesTheRealSdkWithoutInferringRuntimeBehavior()
    {
        await using var wire = await Wire.Create();
        var catalog = await wire.Call("siemens_offline_catalog", new { });
        using var catalogData = JsonDocument.Parse(catalog.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(17, catalogData.RootElement.GetProperty("operations").GetArrayLength());
        Assert.False(catalogData.RootElement.GetProperty("runtimeMonitoringAvailable").GetBoolean());
        string xml = "<Document><Hmi.RuntimeScripting.VBScript ID='0'><AttributeList><Name>Refresh</Name><Code>Sub Refresh()\nx = SmartTags(\"État\")\nEnd Sub</Code></AttributeList></Hmi.RuntimeScripting.VBScript></Document>";
        string input = JsonSerializer.Serialize(new { resources = new[] { new { hmiId = "panel", path = "VBScripts/Refresh.xml", xml } }, view = "dependencies" });
        var result = Success(await wire.Analyze(input, "analyze_script_triggers"));
        Assert.Equal("État", result.GetProperty("page").GetProperty("items")[0].GetProperty("tag").GetString());
        var summary = result.GetProperty("summary");
        Assert.Equal(64, summary.GetProperty("analysisSha256").GetString()!.Length);
        Assert.False(summary.GetProperty("measuredInvocationRateAvailable").GetBoolean());
        Assert.False(summary.GetProperty("queueOverflowEstablished").GetBoolean());
        Assert.Equal("unknown", summary.GetProperty("schedulerStatus").GetString());
        Error(await wire.Analyze("{\"resources\":[],\"maxItems\":201}", "analyze_script_triggers"), "Invalid script analysis");
    }

    [Fact] public async Task LargeUnicodeExportRetainsHashAcrossJsonEscaping()
    {
        await using var wire = await Wire.Create();
        var analysis = Analysis(853000);
        string compact = JsonSerializer.Serialize(analysis, new JsonSerializerOptions {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string escaped = JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
        Assert.True(Encoding.UTF8.GetByteCount(compact) > 852905);
        var first = Success(await wire.Analyze(compact));
        var second = Success(await wire.Analyze(escaped));
        Assert.Equal(first.GetProperty("summary").GetProperty("analysisSha256").GetString(),
            second.GetProperty("summary").GetProperty("analysisSha256").GetString());
    }

    [Fact] public async Task EmptyDocumentReachesResourceValidationInsteadOfInputTypeFailure()
    {
        await using var wire = await Wire.Create();
        string input = JsonSerializer.Serialize(new { analysis = new { resources = new[] { new {
            path = "Screens/minimal.xml", xml = "<?xml version=\"1.0\"?><Document><Engineering version=\"V15.1\"/></Document>"
        } } }, view = "references", maxItems = 1 });
        Error(await wire.Analyze(input), "Each HMI resource must contain one native classic Hmi resource");
    }

    [Theory]
    [InlineData("")][InlineData("{")][InlineData("[]")][InlineData("null")][InlineData("true")][InlineData("123")]
    [InlineData("\"{}\"")]
    public async Task MalformedAndNonobjectJsonFailWithoutRecursiveDecoding(string input)
    {
        await using var wire = await Wire.Create();
        Error(await wire.Analyze(input), "JSON object");
    }

    [Fact] public async Task DuplicateKeysAlternateEnvelopesAndNativeNamesStayRejected()
    {
        await using var wire = await Wire.Create();
        Error(await wire.Analyze("{\"analysis\":{\"resources\":[],\"resources\":[]}}"), "Duplicate JSON property: resources");
        Error(await wire.Analyze("{\"arguments\":{\"xml\":\"<Document/>\"}}", "inspect_xml"), "arguments directly");
        Error(await wire.Analyze("{}", "tia_attach"), "unavailable in the direct offline profile");
    }

    [Fact] public async Task DecodedUtf8CapCountsWhitespaceAndMultibyteCharacters()
    {
        await using var wire = await Wire.Create();
        Error(await wire.Analyze(new string(' ', OfflineOperations.MaxInputBytes) + "{}", "inspect_xml"), "4 MiB");
        // Raw non-ASCII uses two UTF-8 bytes per character, inside a sub-8-MiB wire frame.
        string input = "{\"xml\":\"" + new string('é', OfflineOperations.MaxInputBytes / 2) + "\"}";
        Error(await wire.Analyze(input, "inspect_xml"), "4 MiB");
        Success(await wire.Analyze(JsonSerializer.Serialize(Analysis())));
    }

    [Fact] public async Task ExactlyFourMiBOfDecodedJsonStillPasses()
    {
        await using var wire = await Wire.Create();
        string input = JsonSerializer.Serialize(Analysis());
        input += new string(' ', OfflineOperations.MaxInputBytes - Encoding.UTF8.GetByteCount(input));
        Assert.Equal(OfflineOperations.MaxInputBytes, Encoding.UTF8.GetByteCount(input));
        Success(await wire.Analyze(input));
    }

    private sealed class Wire : IAsyncDisposable
    {
        private readonly Pipe requests = new();
        private readonly Pipe responses = new();
        private readonly IHost host;
        private readonly StreamWriter writer;
        private readonly StreamReader reader;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        private int nextId;

        private Wire()
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddMcpServer().WithStreamServerTransport(
                new BoundedInputStream(requests.Reader.AsStream()), responses.Writer.AsStream()).WithTools<OfflineTools>();
            host = builder.Build();
            writer = new StreamWriter(requests.Writer.AsStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            reader = new StreamReader(responses.Reader.AsStream(), Encoding.UTF8);
        }

        public static async Task<Wire> Create()
        {
            var wire = new Wire();
            try {
                await wire.host.StartAsync(wire.timeout.Token);
                await wire.Send("initialize", new { protocolVersion = "2025-11-25", capabilities = new { },
                    clientInfo = new { name = "offline-argument-regression", version = "1.0" } });
                await wire.writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), wire.timeout.Token);
                return wire;
            } catch { await wire.DisposeAsync(); throw; }
        }

        public Task<JsonElement> Analyze(string input, string operation = "analyze_hmi_references") =>
            Call("siemens_offline_analyze", new { operation, input });

        public Task<JsonElement> Call(string name, object arguments) => Send("tools/call", new { name, arguments });

        public async Task<JsonElement> Send(string method, object parameters)
        {
            int id = ++nextId;
            string packet = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }, new JsonSerializerOptions {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            await writer.WriteLineAsync(packet.AsMemory(), timeout.Token);
            while (true) {
                string line = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidDataException("SDK closed the fixture stream.");
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                Assert.False(document.RootElement.TryGetProperty("error", out _), line);
                return document.RootElement.GetProperty("result").Clone();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await writer.DisposeAsync();
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await host.StopAsync(drain.Token); }
            finally { host.Dispose(); reader.Dispose(); timeout.Dispose(); }
        }
    }
}
