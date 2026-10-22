using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SiemensMCPServer.Extensions;

namespace SiemensMCPServer.Offline;

[McpServerToolType]
internal sealed class OfflineTools
{
    private static readonly SemaphoreSlim Slots = new(2, 2);

    [McpServerTool(Name = "siemens_offline_catalog", ReadOnly = true, OpenWorld = false)]
    [Description("List the closed direct offline profile, serialized-content operations and limits. No native, service, worker, controller or file access.")]
    public JsonElement Catalog() => ExtensionWire.Json(OfflineOperations.Catalog());

    [McpServerTool(Name = "siemens_offline_describe", ReadOnly = true, OpenWorld = false)]
    [Description("Get the exact input schema for one offline analysis operation before calling it.")]
    public JsonElement Describe(string operation) => ExtensionWire.Json(OfflineOperations.Describe(operation));

    [McpServerTool(Name = "siemens_offline_analyze", ReadOnly = true, OpenWorld = false)]
    [Description("Analyze caller-supplied exported XML, Unified text-list bundles, controller declarations or evidence. Discover/describe first. Two concurrent analyses; excess requests return busy. No file paths or native operations.")]
    public async Task<CallToolResult> Analyze(string operation, JsonElement input, CancellationToken cancellationToken)
    {
        if (!Slots.Wait(0)) return Error("Offline analysis is busy (two active requests). Wait for completion.");
        try {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() => OfflineOperations.Execute(operation, input), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = ExtensionWire.Json(new { profile = "direct-offline", nativeOperationsPerformed = false, result });
            return new CallToolResult { StructuredContent = envelope, Content = new[] { new TextContentBlock { Text = envelope.GetRawText() } } };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Error(ex.GetBaseException().Message); }
        finally { Slots.Release(); }
    }

    private static CallToolResult Error(string message) => new() { IsError = true,
        Content = new[] { new TextContentBlock { Text = message.Length > 2048 ? message[..2048] : message } } };
}
