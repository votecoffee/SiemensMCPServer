using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Offline;

// Closed, serialized-content-only surface. Never accept a worker/module recipe,
// filesystem snapshot, native target, host configuration or alternate executable.
internal static class OfflineOperations
{
    public const int MaxInputBytes = 4 * 1024 * 1024;
    public const int MaxOutputBytes = 1024 * 1024;
    private static object InputContract => new {
        argument = "input", wireType = "string", maxDecodedUtf8Bytes = MaxInputBytes,
        encoding = "Serialize the described operation object as JSON once. The operation inputSchema describes that decoded object, not the outer MCP arguments."
    };
    private static readonly string[] Comfort = {
        "analyze_script_triggers",
        "import_controller_declarations", "analyze_hmi_references", "validate_hmi_bindings", "reference_impact",
        "inspect_behaviors", "inspect_references", "inspect_screen_items", "inspect_screen_texts", "inspect_screen_layout",
        "validate_package_bindings", "validate_native_text_list", "validate_text_list_delta",
        "validate_text_list_bindings", "validate_text_list_workflow", "inspect_xml"
    };

    public static void ValidateArguments(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("The direct offline profile accepts no options. Native targets, writes, workers and service configuration are unavailable.");
    }

    public static object Catalog() => new {
        profile = "direct-offline", version = AppRelease.DisplayVersion,
        operations = FeatureOperations.Describe("comfort").Where(op => Comfort.Contains(op.Name, StringComparer.Ordinal))
            .Select(op => new { name = op.Name, op.Description }).Concat(new[] {
                new { name = "inspect_text_list", Description = "Inspect supplied classic XML or a Unified paired-YAML text-list bundle; bounded translation rows, no file access." }
            }).ToArray(),
        maxInputBytes = MaxInputBytes, maxOutputBytes = MaxOutputBytes,
        inputContract = InputContract,
        management = new { status = "Not reporting", restartAvailable = false },
        nativeEngineeringAvailable = false, runtimeMonitoringAvailable = false, filesystemAccessAvailable = false,
        scope = "Caller-supplied serialized exports and declarations only. No file reads/writes, native import, compile, save, attachment, controller, service or worker launch. Caller-supplied native evidence is not established by this server."
    };

    public static object Describe(string operation)
    {
        if (operation == "inspect_text_list") return new { name = operation, inputContract = InputContract, inputSchema = new {
            type = "object", additionalProperties = false, required = new[] { "snapshot" }, properties = new {
                snapshot = new { type = "string", maxLength = MaxInputBytes }, offset = new { type = "integer", minimum = 0, maximum = 1000000 },
                maxItems = new { type = "integer", minimum = 1, maximum = 200 }
            }
        }};
        RequireAllowed(operation);
        var op = FeatureOperations.Describe("comfort").Single(op => op.Name == operation);
        return new { name = operation, op.Description, op.InputSchema, inputContract = InputContract };
    }

    private static void RequireAllowed(string operation)
    {
        if (!Comfort.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Operation unavailable in the direct offline profile.");
    }

    public static JsonElement Execute(string operation, string input)
    {
        if (input == null) throw new ArgumentException("input must be a JSON string containing one object matching the described operation schema.");
        // Bound the decoded string before parsing, including surrounding whitespace.
        // Never recursively decode strings or accept an alternate argument envelope.
        if (Encoding.UTF8.GetByteCount(input) > MaxInputBytes)
            throw new ArgumentException("Decoded input exceeds the 4 MiB offline UTF-8 limit.");
        JsonDocument document;
        try { document = JsonDocument.Parse(input); }
        catch (JsonException) { throw new ArgumentException("input must contain one valid JSON object matching the described operation schema."); }
        using (document) return Execute(operation, document.RootElement);
    }

    public static JsonElement Execute(string operation, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("input must contain a JSON object matching the described operation schema.");
        if (Encoding.UTF8.GetByteCount(input.GetRawText()) > MaxInputBytes)
            throw new ArgumentException("Decoded input exceeds the 4 MiB offline UTF-8 limit.");
        // Reject ambiguous JSON at every depth, before any operation sees it.
        CheckKeys(input);
        JsonElement result;
        if (operation == "inspect_text_list") {
            foreach (var property in input.EnumerateObject())
                if (property.Name != "snapshot" && property.Name != "offset" && property.Name != "maxItems")
                    throw new ArgumentException("Unknown text-list inspection argument.");
            string snapshot = input.GetProperty("snapshot").GetString() ?? throw new ArgumentException("Supply an exported text-list snapshot.");
            int offset = input.TryGetProperty("offset", out var start) ? start.GetInt32() : 0;
            int count = input.TryGetProperty("maxItems", out var size) ? size.GetInt32() : 100;
            var document = TextListDocuments.Parse(snapshot);
            result = ExtensionWire.Json(new { document.Name, document.Fingerprint, page = HmiAnalysisOperations.Page(document.Read().Cast<object>(), offset, count), nativeBindingVerified = false });
        } else {
            RequireAllowed(operation);
            // Do not allow the extension protocol's alternate "arguments" envelope.
            if (input.TryGetProperty("arguments", out _) || input.TryGetProperty("snapshot", out _))
                throw new ArgumentException("Supply the described operation arguments directly.");
            result = FeatureOperations.Execute("comfort", operation, input);
        }
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > MaxOutputBytes)
            throw new ArgumentException("Offline result exceeds 1 MiB; reduce the selection or page size.");
        return result;
    }

    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) {
            var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) {
                if (!keys.Add(property.Name)) throw new ArgumentException("Duplicate JSON property: " + property.Name);
                CheckKeys(property.Value);
            }
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckKeys(item);
    }
}
