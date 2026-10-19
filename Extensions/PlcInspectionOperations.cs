using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Extensions
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class PlcSourceReadOptions { public int MaxCharacters { get; set; } = 65536; }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class PlcSourceSearchOptions
    {
        [JsonRequired] public string Query { get; set; } = "";
        [JsonRequired] public bool CaseSensitive { get; set; }
        public int MaxMatches { get; set; } = 50;
    }
    internal static class PlcInspectionOperations
    {
        public static object Schema(string operation)
        {
            object capture = operation == "cross_references"
                ? (object)new { type = "object", additionalProperties = false, required = new[] { "filter", "maxRows" }, properties = new {
                    filter = new { type = "string", @enum = new[] { "AllObjects", "ObjectsWithReferences", "ObjectsWithoutReferences", "UnusedObjects" } }, maxRows = new { type = "integer", minimum = 1, maximum = 1000 } } }
                : new { type = "object", additionalProperties = false, required = new[] { "withDependencies" }, properties = new { withDependencies = new { type = "boolean" } } };
            object options = operation == "find_in_source"
                ? (object)new { type = "object", additionalProperties = false, required = new[] { "query", "caseSensitive" }, properties = new {
                    query = new { type = "string", minLength = 1, maxLength = 256 }, caseSensitive = new { type = "boolean" }, maxMatches = new { type = "integer", minimum = 1, maximum = 200 } } }
                : operation == "read_source" ? (object)new { type = "object", additionalProperties = false, properties = new { maxCharacters = new { type = "integer", minimum = 1, maximum = 262144 } } }
                : new { type = "object", additionalProperties = false };
            return new { type = "object", additionalProperties = false, required = new[] { "plcId", "path", "capture", "options" }, properties = new {
                plcId = new { type = "string", description = "Exact ID from plc_list_targets." }, path = new { type = "string", description = "One object path returned by plc_read_object." }, capture, options } };
        }
        public static JsonElement Execute(string operation, JsonElement input)
        {
            var request = ExtensionWire.Parse<PlcInspectionInput>(input.GetProperty("arguments"));
            request.Validate(operation == "cross_references" ? "crossReferences" : "source");
            PlcSourceReadOptions? readOptions = null; PlcSourceSearchOptions? searchOptions = null;
            if (operation == "read_source") {
                readOptions = ExtensionWire.Parse<PlcSourceReadOptions>(request.Options);
                if (readOptions.MaxCharacters < 1 || readOptions.MaxCharacters > 262144) throw new ArgumentException("Use 1 to 262144 returned characters.");
            }
            else if (operation == "find_in_source") {
                searchOptions = ExtensionWire.Parse<PlcSourceSearchOptions>(request.Options);
                if (string.IsNullOrEmpty(searchOptions.Query) || searchOptions.Query.Length > 256 || searchOptions.Query.Any(char.IsControl) || searchOptions.MaxMatches < 1 || searchOptions.MaxMatches > 200)
                    throw new ArgumentException("Use a single-line literal query of 1 to 256 characters and 1 to 200 matches.");
            }
            else if (operation != "cross_references" || request.Options.EnumerateObject().Any()) throw new ArgumentException("Cross-reference options must be empty.");
            string phase = input.GetProperty("phase").GetString()!;
            if (phase == "validate") return ExtensionWire.Json(new { validated = true });
            if (phase != "snapshot") throw new ArgumentException("Unknown PLC inspection phase.");
            if (operation == "cross_references")
            {
                var snapshot = ExtensionWire.Parse<PlcReferenceSnapshot>(input.GetProperty("snapshot"));
                return ExtensionWire.Json(new { snapshot.Rows, snapshot.Truncated, snapshot.FieldsTruncated });
            }
            var source = ExtensionWire.Parse<PlcSourceSnapshot>(input.GetProperty("snapshot"));
            if (source.Text == null || PlcInspectionFiles.Utf8.GetByteCount(source.Text) > PlcInspectionFiles.MaxBytes) throw new ArgumentException("Source snapshot exceeds the inspection limit.");
            if (operation == "read_source")
            {
                string text = PlcInspectionFiles.Prefix(source.Text, readOptions!.MaxCharacters);
                return ExtensionWire.Json(new { text, truncated = text.Length != source.Text.Length, totalCharacters = source.Text.Length });
            }
            var search = searchOptions!;
            var matches = new List<object>(); int count = 0, line = 0;
            using (var reader = new System.IO.StringReader(source.Text))
            {
                string? value;
                while ((value = reader.ReadLine()) != null)
                {
                    line++; int offset = 0;
                    while (offset <= value.Length - search.Query.Length)
                    {
                        int column = value.IndexOf(search.Query, offset, search.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                        if (column < 0) break;
                        count++;
                        if (matches.Count < search.MaxMatches) {
                            int start = Math.Max(0, column - 80);
                            if (start > 0 && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1])) start--;
                            string context = PlcInspectionFiles.Prefix(value.Substring(start), 512);
                            matches.Add(new { line, column = column + 1, context, contextStartColumn = start + 1, contextTruncated = start != 0 || context.Length != value.Length });
                        }
                        offset = column + search.Query.Length;
                    }
                }
            }
            return ExtensionWire.Json(new { query = search.Query, matches, totalMatches = count, truncated = count > matches.Count,
                positionUnits = "One-based lines and UTF-16 columns; literal non-overlapping matches." });
        }
    }
}
