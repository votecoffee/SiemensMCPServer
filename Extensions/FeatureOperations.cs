using System;
using System.Linq;
using System.Text.Json;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Extensions
{
    internal static class FeatureOperations
    {
        private static ExtensionOperation Op(string name, string description, string route, bool preview, object schema, string snapshotKind = "") => new ExtensionOperation {
            Name = name, Description = description, Route = route, Preview = preview, InputSchema = ExtensionWire.Json(schema), SnapshotKind = snapshotKind
        };
        public static ExtensionOperation[] Describe(string module)
        {
            object xmlSchema = new { type = "object", required = new[] { "xml", "editable" }, properties = new { xml = new { type = "string" }, editable = new { type = "boolean" } } };
            switch (module)
            {
                case "project": return new[] {
                    Op("list_libraries", "List the attached project library and already-open global libraries with exact IDs and bounded metadata. Does not open, upgrade or save libraries.", "projectRead", false, LibraryInspectionOperations.Schema(false), "libraryCatalog"),
                    Op("browse_library", "Browse one returned library/path: folders, types, versions or master-copy content descriptions. Read-only metadata; no instance creation, dependency traversal or library updates.", "projectRead", false, LibraryInspectionOperations.Schema(true), "libraryCatalog") };
                case "comfort": return new[] {
                    Op("build_screen_package", "Build a sidecar-free classic screen/tag import bundle from native XML prototypes and a supplied PLC symbol manifest. Offline only; writes no files and does not establish native acceptance.", "local", false, ComfortPackage.Schema(true)),
                    Op("validate_package_bindings", "Check screen-to-HMI-tag references and symbolic PLC connection/type declarations in supplied XML. Offline only; no native connection partner verification.", "local", false, ComfortPackage.Schema(false)),
                    Op("inspect_xml", "Inspect supplied classic XML without attaching to TIA.", "local", false, xmlSchema),
                    Op("edit_xml", "Edit existing scalar XML fields in the selected native resource.", "classicXml", true, new { type = "object", required = new[] { "edits" }, properties = new { edits = new { type = "array", items = new { type = "object", required = new[] { "xPath", "expectedValue", "newValue" }, properties = new { xPath = new { type = "string" }, expectedValue = new { type = "string" }, newValue = new { type = "string" } } } } } }),
                    Op("change_behaviors", "Clone/remove existing native behavior prototypes.", "classicXml", true, new { type = "object", required = new[] { "changes" } }),
                    Op("edit_text_list", "Transform supplied classic text-list translations.", "internal", true, new { type = "object" }),
                    Op("change_text_list", "Transform supplied classic text-list structure.", "internal", true, new { type = "object" }) };
                case "unified": return new[] {
                    Op("edit_object", "Edit supported static fields/captions of a selected Unified screen/item. arguments: screenPath, itemPath, edits.", "unifiedObject", true, new { type = "object", required = new[] { "screenPath", "itemPath", "edits" } }),
                    Op("edit_text_list", "Transform a supplied paired-YAML text-list document.", "internal", true, new { type = "object" }) };
                case "plc": return new[] {
                    Op("read_source", "Export readable native SCL/STL/DB/UDT source to local artifacts and return bounded text. Explicit PLC/path and verified Offline state required; does not generate blocks or import a source into the project.", "plcRead", false, PlcInspectionOperations.Schema("read_source"), "source"),
                    Op("find_in_source", "Search a native readable-source snapshot for literal text, with line/column/context results and file hash. Same native export and Offline limits as read_source; no LAD/FBD translation.", "plcRead", false, PlcInspectionOperations.Schema("find_in_source"), "source"),
                    Op("cross_references", "Read bounded native cross-reference source/reference/location rows for one PLC object. Requires Offline state and an API exposing CrossReferenceService (present in inspected V18/V19, absent in V15.1/V16/V17). No native handles leave the broker.", "plcRead", false, PlcInspectionOperations.Schema("cross_references"), "crossReferences"),
                    Op("prepare_source", "Validate exact SCL/STL source. arguments: name, language, code, allowBlockReplacement. Preview uses an offline PLC root.", "plcSource", true, new { type = "object", required = new[] { "name", "language", "code", "allowBlockReplacement" } }),
                    Op("inspect_source", "Validate supplied SCL/STL source without attaching or generating blocks.", "local", false, new { type = "object", required = new[] { "name", "language", "code", "allowBlockReplacement" } }) };
                default: throw new ArgumentException("Unknown bundled extension module.");
            }
        }
        public static JsonElement Execute(string module, string operation, JsonElement input)
        {
            if (!Describe(module).Any(o => o.Name == operation)) throw new ArgumentException("Unknown extension operation.");
            if (module == "project") return LibraryInspectionOperations.Execute(operation, input);
            JsonElement args = input.TryGetProperty("arguments", out var nested) ? nested : input;
            if (module == "comfort")
            {
                if (operation == "build_screen_package") return ExtensionWire.Json(ComfortPackage.Build(ExtensionWire.Parse<ComfortPackageSpec>(args)));
                if (operation == "validate_package_bindings") return ExtensionWire.Json(ComfortPackage.ValidateBindings(ExtensionWire.Parse<ComfortBindingInput>(args)));
                if (operation == "inspect_xml")
                {
                    var doc = new ClassicXmlDocument(args.GetProperty("xml").GetString()!);
                    bool editable = args.GetProperty("editable").GetBoolean();
                    return ExtensionWire.Json(new { doc.Name, doc.NativeType, scalarCatalog = doc.Catalog(editable), behaviorCatalog = doc.BehaviorCatalog(editable) });
                }
                if (operation == "edit_xml" || operation == "change_behaviors")
                {
                    var doc = new ClassicXmlDocument(input.GetProperty("snapshot").GetString()!);
                    string xml = operation == "edit_xml" ? doc.Edit(ExtensionWire.Parse<ClassicXmlEdit[]>(args.GetProperty("edits")))
                        : doc.ChangeBehaviors(ExtensionWire.Parse<ClassicBehaviorChange[]>(args.GetProperty("changes")));
                    return ExtensionWire.Json(new { candidateXml = xml, changes = args });
                }
                if (operation == "change_text_list")
                {
                    var before = new TextListDocument(input.GetProperty("snapshot").GetString()!);
                    var after = before.ChangeStructure(ExtensionWire.Parse<ClassicTextListChange[]>(args.GetProperty("changes")), ExtensionWire.Parse<string[]>(args.GetProperty("activeCultures")));
                    return ExtensionWire.Json(new { candidate = after.Serialize() });
                }
            }
            if (operation == "edit_text_list")
            {
                var before = TextListDocuments.Parse(input.GetProperty("snapshot").GetString()!);
                var after = before.Edit(ExtensionWire.Parse<TextEdit[]>(args.GetProperty("edits")));
                return ExtensionWire.Json(new { candidate = after.Serialize() });
            }
            if (module == "unified" && operation == "edit_object")
            {
                var before = ExtensionWire.Parse<UnifiedObjectState>(input.GetProperty("snapshot"));
                var after = before.Edit(ExtensionWire.Parse<UnifiedEdit[]>(args.GetProperty("edits")));
                return ExtensionWire.Json(new { state = after, changes = args });
            }
            if (module == "plc")
            {
                if (operation == "read_source" || operation == "find_in_source" || operation == "cross_references") return PlcInspectionOperations.Execute(operation, input);
                var source = ExtensionWire.Parse<PlcSourceInput>(args);
                source.Validate(); return ExtensionWire.Json(new { source, fileName = source.FileName });
            }
            throw new ArgumentException("Unsupported extension operation.");
        }
        public static void SelfTest(string module)
        {
            Describe(module);
            if (module == "comfort") new ClassicXmlDocument("<Document><Hmi.Screen.Screen><AttributeList><Name>Probe</Name></AttributeList></Hmi.Screen.Screen></Document>").Catalog(true);
            if (module == "unified" && UnifiedTextCodec.Replace("", "probe").Length == 0) throw new InvalidOperationException("Unified self-test failed.");
            if (module == "plc") new PlcSourceInput { Name = "Probe", Language = "SCL", Code = "// probe", AllowBlockReplacement = true }.Validate();
        }
    }
}
