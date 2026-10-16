using System;
using System.Linq;
using System.Text.Json;
using SiemensTiaOpenness.Mcp;

namespace SiemensTiaOpenness.Extensions
{
    internal static class FeatureOperations
    {
        private static ExtensionOperation Op(string name, string description, string route, bool preview, object schema) => new ExtensionOperation {
            Name = name, Description = description, Route = route, Preview = preview, InputSchema = ExtensionWire.Json(schema)
        };
        public static ExtensionOperation[] Describe(string module)
        {
            object xmlSchema = new { type = "object", required = new[] { "xml", "editable" }, properties = new { xml = new { type = "string" }, editable = new { type = "boolean" } } };
            switch (module)
            {
                case "comfort": return new[] {
                    Op("inspect_xml", "Inspect supplied classic XML without attaching to TIA.", "local", false, xmlSchema),
                    Op("edit_xml", "Edit existing scalar XML fields in the selected native resource.", "classicXml", true, new { type = "object", required = new[] { "edits" }, properties = new { edits = new { type = "array", items = new { type = "object", required = new[] { "xPath", "expectedValue", "newValue" }, properties = new { xPath = new { type = "string" }, expectedValue = new { type = "string" }, newValue = new { type = "string" } } } } } }),
                    Op("change_behaviors", "Clone/remove existing native behavior prototypes.", "classicXml", true, new { type = "object", required = new[] { "changes" } }),
                    Op("edit_text_list", "Transform supplied classic text-list translations.", "internal", true, new { type = "object" }),
                    Op("change_text_list", "Transform supplied classic text-list structure.", "internal", true, new { type = "object" }) };
                case "unified": return new[] {
                    Op("edit_object", "Edit supported static fields/captions of a selected Unified screen/item. arguments: screenPath, itemPath, edits.", "unifiedObject", true, new { type = "object", required = new[] { "screenPath", "itemPath", "edits" } }),
                    Op("edit_text_list", "Transform a supplied paired-YAML text-list document.", "internal", true, new { type = "object" }) };
                case "plc": return new[] {
                    Op("prepare_source", "Validate exact SCL/STL source. arguments: name, language, code, allowBlockReplacement. Preview uses an offline PLC root.", "plcSource", true, new { type = "object", required = new[] { "name", "language", "code", "allowBlockReplacement" } }),
                    Op("inspect_source", "Validate supplied SCL/STL source without attaching or generating blocks.", "local", false, new { type = "object", required = new[] { "name", "language", "code", "allowBlockReplacement" } }) };
                default: throw new ArgumentException("Unknown bundled extension module.");
            }
        }
        public static JsonElement Execute(string module, string operation, JsonElement input)
        {
            if (!Describe(module).Any(o => o.Name == operation)) throw new ArgumentException("Unknown extension operation.");
            JsonElement args = input.TryGetProperty("arguments", out var nested) ? nested : input;
            if (module == "comfort")
            {
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
