using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SiemensMCPServer.Analysis;

namespace SiemensMCPServer.Extensions
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ControllerImportRequest
    {
        [JsonRequired] public ControllerDocumentInput[] ControllerDocuments { get; set; } = Array.Empty<ControllerDocumentInput>();
        public string Section { get; set; } = "symbols";
        public int Offset { get; set; }
        public int MaxItems { get; set; } = 100;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class HmiAnalysisRequest
    {
        [JsonRequired] public HmiAnalysisInput Analysis { get; set; } = null!;
        public string View { get; set; } = "references";
        public int Offset { get; set; }
        public int MaxItems { get; set; } = 100;
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class HmiImpactRequest
    {
        [JsonRequired] public HmiAnalysisInput Analysis { get; set; } = null!;
        [JsonRequired] public string ExpectedAnalysisSha256 { get; set; } = "";
        [JsonRequired] public HmiProposedChange[] Changes { get; set; } = Array.Empty<HmiProposedChange>();
        public int Offset { get; set; }
        public int MaxItems { get; set; } = 100;
    }
    internal static class HmiAnalysisOperations
    {
        private static object StringSchema(int length) => new { type = "string", maxLength = length };
        internal static object DocumentsSchema => new { type = "array", minItems = 1, maxItems = 32, items = new { type = "object", additionalProperties = false,
            required = new[] { "id", "format", "content" }, properties = new { id = StringSchema(1024), format = new { type = "string", @enum = new[] { "rockwell-l5x", "plcopen-xml" } }, content = StringSchema(16777216) } } };
        internal static object MappingsSchema => new { type = "array", maxItems = 1000, items = new { type = "object", additionalProperties = false,
            required = new[] { "connection", "controllerId" }, properties = new { hmiId = StringSchema(4096), connection = StringSchema(1024), controllerId = StringSchema(1024),
                defaultScope = StringSchema(1024), addressSyntax = new { type = "string", @enum = new[] { "canonical", "wincc-rockwell" } }, expectedControllerName = StringSchema(1024) } } };
        private static object AnalysisSchema => new { type = "object", additionalProperties = false, required = new[] { "resources" }, properties = new {
            resources = new { type = "array", minItems = 1, maxItems = 2000, items = new { type = "object", additionalProperties = false, required = new[] { "path", "xml" },
                properties = new { hmiId = StringSchema(4096), path = StringSchema(4096), xml = StringSchema(16777216) } } },
            controllerDocuments = DocumentsSchema, connectionMappings = MappingsSchema,
            connectionMetadataAvailable = new { type = "boolean" }, connectionDefinitions = new { type = "array", maxItems = 1000, items = new { type = "object", additionalProperties = false, required = new[] { "name" },
                properties = new { hmiId = StringSchema(4096), name = StringSchema(1024), path = StringSchema(4096), nativeType = StringSchema(1024),
                    attributes = new { type = "object", maxProperties = 200, additionalProperties = StringSchema(8192) } } } }, exportReportedComplete = new { type = "boolean" },
            exportFailures = new { type = "array", maxItems = 10000, items = StringSchema(8192) },
            unsupportedResources = new { type = "array", maxItems = 10000, items = StringSchema(8192) } } };
        public static object Schema(string operation)
        {
            var properties = new Dictionary<string, object> {
                ["offset"] = new { type = "integer", minimum = 0, maximum = 1000000 }, ["maxItems"] = new { type = "integer", minimum = 1, maximum = 200 }
            };
            string[] required;
            if (operation == "import_controller_declarations")
            {
                properties["controllerDocuments"] = DocumentsSchema; properties["section"] = new { type = "string", @enum = new[] { "symbols", "types", "members" } };
                required = new[] { "controllerDocuments" };
            }
            else
            {
                properties["analysis"] = AnalysisSchema; required = new[] { "analysis" };
                if (operation == "reference_impact")
                {
                    properties["expectedAnalysisSha256"] = new { type = "string", pattern = "^[0-9a-f]{64}$" };
                    properties["changes"] = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object", additionalProperties = false,
                        required = new[] { "kind", "name" }, properties = new { kind = new { type = "string", @enum = new[] { "tag", "connection", "binding", "controllerSymbol" } },
                            name = StringSchema(1024), hmiId = StringSchema(4096), controllerId = StringSchema(1024), newName = StringSchema(4096),
                            newConnection = StringSchema(4096), newSymbol = StringSchema(4096), newControllerId = StringSchema(4096) } } };
                    required = new[] { "analysis", "expectedAnalysisSha256", "changes" };
                }
                else if (operation == "analyze_hmi_references") properties["view"] = new { type = "string", @enum = new[] { "references", "bindings", "tags", "diagnostics" } };
            }
            return new { type = "object", additionalProperties = false, required, properties };
        }
        public static object Page(IEnumerable<object> source, int offset, int maxItems)
        {
            if (offset < 0 || offset > 1000000 || maxItems < 1 || maxItems > 200) throw new ArgumentException("offset must be 0..1000000 and maxItems 1..200.");
            var all = source.ToArray(); var items = new List<object>(); int characters = 0, next = offset;
            foreach (var item in all.Skip(offset).Take(maxItems))
            {
                int cost = JsonSerializer.Serialize(item, ExtensionWire.Options).Length;
                if (cost > 258048) throw new ArgumentException("One analysis row exceeds the MCP reply budget; use the complete export-folder analysis command.");
                if (characters + cost > 258048) break;
                items.Add(item); characters += cost; next++;
            }
            return new { items, totalItems = all.Length, offset, nextOffset = next, hasMore = next < all.Length,
                payloadLimited = items.Count < Math.Min(maxItems, Math.Max(0, all.Length - offset)) };
        }
        public static object Summary(HmiAnalysisReport result) => new { result.EngineVersion, result.EngineBuildId, result.AnalysisSha256, resources = result.Sources.Length, controllers = result.Controllers.Length,
            tags = result.Tags.Length, references = result.References.Length,
            bindingStatuses = result.Bindings.GroupBy(b => b.Status).ToDictionary(g => g.Key, g => g.Count()),
            referenceStatuses = result.References.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()),
            opaqueRegions = result.Diagnostics.Count(d => d.Code == "opaqueExpression"), diagnostics = result.Diagnostics.Length,
            result.ExportReportedComplete, result.ConnectionMetadataAvailable, connections = result.ConnectionDefinitions.Length, exportFailures = result.ExportFailures.Length, unsupportedResources = result.UnsupportedResources.Length,
            result.NativeBindingVerified, result.Scope };
        public static JsonElement Execute(string operation, JsonElement args)
        {
            // MCP is a bounded serialized-content route; file input/output exists only in the explicit CLI mode.
            if (args.GetRawText().Length > 24 * 1024 * 1024) throw new ArgumentException("Analysis request exceeds 24 MiB. Use the export-folder command for complete projects/large L5X files.");
            if (operation == "import_controller_declarations")
            {
                var request = ExtensionWire.Parse<ControllerImportRequest>(args);
                if (request.ControllerDocuments == null || request.ControllerDocuments.Length == 0 || request.ControllerDocuments.Length > 32) throw new ArgumentException("Supply 1..32 controller documents.");
                var declarations = request.ControllerDocuments.Select(ControllerDeclarations.Import).ToArray();
                if (declarations.GroupBy(d => d.Id, ControllerDeclarations.Names).Any(g => g.Count() != 1)) throw new ArgumentException("Duplicate controller IDs.");
                IEnumerable<object> rows = request.Section == "symbols" ? declarations.SelectMany(d => d.Symbols.Select(s => (object)new { controllerId = d.Id, symbol = s }))
                    : request.Section == "types" ? declarations.SelectMany(d => d.Types.Select(t => (object)new { controllerId = d.Id, t.Name, t.Kind, t.BaseType, t.Dimensions, members = t.Members.Length }))
                    : request.Section == "members" ? declarations.SelectMany(d => d.Types.SelectMany(t => t.Members.Select(m => (object)new { controllerId = d.Id, type = t.Name, member = m })))
                    : throw new ArgumentException("section must be symbols, types or members.");
                return ExtensionWire.Json(new { controllers = declarations.Select(d => new { d.Id, d.Name, d.Format, d.SourceSha256, d.SoftwareRevision, d.PartialExport,
                    symbols = d.Symbols.Length, types = d.Types.Length, d.Limitations }), page = Page(rows, request.Offset, request.MaxItems), nativeBindingVerified = false });
            }
            if (operation == "reference_impact")
            {
                var request = ExtensionWire.Parse<HmiImpactRequest>(args); var analysis = HmiReferenceAnalyzer.Analyze(request.Analysis);
                var impact = HmiReferenceImpact.Report(request.Analysis, analysis, request.ExpectedAnalysisSha256, request.Changes);
                return ExtensionWire.Json(new { summary = Summary(analysis), impact.AnalysisSha256, impact.ProposalSha256, impact.Changes, impact.Issues,
                    affectedReferences = impact.Items.Length, affectedObjects = impact.Items.Select(i => i.Reference.HmiId + "\n" + i.Reference.ResourcePath + "\n" + i.Reference.OwnerXPath).Distinct().Count(),
                    page = Page(impact.Items, request.Offset, request.MaxItems), blindSpots = impact.BlindSpots.Length, impact.Scope });
            }
            var read = ExtensionWire.Parse<HmiAnalysisRequest>(args); var report = HmiReferenceAnalyzer.Analyze(read.Analysis);
            string view = operation == "validate_hmi_bindings" ? "bindings" : read.View;
            IEnumerable<object> records = view == "references" ? report.References : view == "bindings" ? (IEnumerable<object>)report.Bindings
                : view == "tags" ? report.Tags : view == "diagnostics" ? report.Diagnostics : throw new ArgumentException("Unknown analysis view.");
            return ExtensionWire.Json(new { summary = Summary(report), view, page = Page(records, read.Offset, read.MaxItems) });
        }
    }
}
