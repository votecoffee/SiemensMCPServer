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
                    Op("read_translation_units", "Extract native project-text comments/descriptions/captions by language, category and object path, with exact selections, missing-translation filtering and snapshot fingerprints. Existing active language pairs only; no automatic translation.", "projectRead", false, ProjectTranslationOperations.Schema(false), "projectTexts"),
                    Op("edit_project_texts", "Preview exact existing translation updates in native User Texts schema 1.3; preserves IDs, metadata, substitutions and other rows. Source updates require explicit opt-in. Requires host 0.12.0+; native import and full text-snapshot readback use the projectTexts route.", "projectTexts", true, ProjectTranslationOperations.Schema(true)),
                    Op("list_project_languages", "List already-active project cultures and editing/reference language flags. Requires the exact attached project path; never activates a language.", "projectRead", false, ProjectTextOperations.Schema("list_project_languages"), "projectLanguages"),
                    Op("read_project_texts", "Export native project texts for two explicit active cultures to retained XLSX, then read bounded cells/sheets. Fresh export per call; includes headers/metadata, with no translation-column inference or import.", "projectRead", false, ProjectTextOperations.Schema("read_project_texts"), "projectTexts"),
                    Op("find_in_project_texts", "Export project-text XLSX and search decoded cells/cached formula values for literal text. Includes hidden sheets, headers and metadata. Never evaluates formulas, opens external links, edits or imports the workbook.", "projectRead", false, ProjectTextOperations.Schema("find_in_project_texts"), "projectTexts"),
                    Op("list_libraries", "List the attached project library and already-open global libraries with exact IDs and bounded metadata. Does not open, upgrade or save libraries.", "projectRead", false, LibraryInspectionOperations.Schema(false), "libraryCatalog"),
                    Op("browse_library", "Browse one returned library/path: folders, types, versions or master-copy content descriptions. Read-only metadata; no instance creation, dependency traversal or library updates.", "projectRead", false, LibraryInspectionOperations.Schema(true), "libraryCatalog") };
                case "comfort": return new[] {
                    Op("analyze_script_triggers", "Inventory supplied classic VBScript call candidates, literal tag dependencies and native Script/VBScript event links with acquisition metadata and exact source hashes/locators. Reports configured-handler pressure and unproven reconciliation/clear paths; never executes scripts or establishes scheduler/deployed runtime behavior.", "local", false, SiemensMCPServer.Analysis.HmiScriptAnalysis.Schema()),
                    Op("import_controller_declarations", "Import read-only Rockwell L5X or PLCopen XML declarations with source hashes; inspect paged symbols, types and members. No manually built symbol list, native import or network/controller access.", "local", false, HmiAnalysisOperations.Schema("import_controller_declarations")),
                    Op("analyze_hmi_references", "Trace supplied project classic HMI resources through screen/control/event/animation tag links into mapped controller declarations. Reports exact source paths, missing/ambiguous/opaque references and hashes. Large export folders use the companion analysis command.", "local", false, HmiAnalysisOperations.Schema("analyze_hmi_references")),
                    Op("validate_hmi_bindings", "Validate HMI bindings against imported controller scopes/members/types/array bounds/aliases and explicit connection mappings. Static declaration evidence only; native connection partners and runtime are not established.", "local", false, HmiAnalysisOperations.Schema("validate_hmi_bindings")),
                    Op("reference_impact", "Report affected HMI objects for proposed tag/connection/binding/controller-symbol changes against an expected analysis hash; follows aliases and nested members. Does not edit or apply changes.", "local", false, HmiAnalysisOperations.Schema("reference_impact")),
                    Op("inspect_behaviors", "Inspect bounded exported event/animation prototypes and exact external Owner/Control link targets, with owner-subtree paging. Supplied XML only; native acceptance remains separate.", "local", false, BehaviorSchema(false)),
                    Op("inspect_references", "Inspect exported classic LinkList references, exact symbolic names and owning controls. Native-ID and unsupported roles are inspection-only; supplied XML only, with bounded pages and exact owner/type filters.", "local", false, ReferenceSchema(false)),
                    Op("rebind_references", "Change exact existing symbolic Tag, ControllerTag and Connection names with per-link fingerprints and old values. Preserves native quoting verbatim; does not verify target existence/types or rewrite scripts. Requires host 0.10.4+ native-reference contract.", "classicXml", true, ReferenceSchema(true), "classicReferences"),
                    Op("inspect_screen_items", "Inspect exact exported classic screen control prototypes, fingerprints and existing destination containers. Reports clone eligibility and standalone removal references; supplied XML only.", "local", false, ScreenItemSchema(false)),
                    Op("change_screen_items", "Clone/remove exported classic controls or groups within one screen. Requires explicit names for all copied controls, remaps native IDs and guards retained references. Uses host 0.10.4+ native-reference contract.", "classicXml", true, ScreenItemSchema(true), "classicReferences"),
                    Op("inspect_screen_texts", "Inspect/filter bounded multilingual classic screen text runs, retaining exact field fingerprints, cultures, run indexes and formatting. Supplied XML only; dynamic or unknown markup is inspection-only.", "local", false, ScreenTextSchema(false)),
                    Op("edit_screen_texts", "Edit exact static text runs or existing empty slots in classic screen captions, including multiple styled runs. Requires host 0.10.4+ native-reference and whitespace-preserving readback.", "classicXml", true, ScreenTextSchema(true), "classicReferences"),
                    Op("inspect_screen_layout", "Inspect bounded stored geometry of classic XML screen items and return exact selection paths/bounds. Supplied XML only; no attachment or native acceptance claim.", "local", false, LayoutSchema(false)),
                    Op("arrange_screen_items", "Move, align, distribute equal gaps or resize selected classic screen items. Uses exact paths/expected bounds and the native resource fingerprint; returns exact geometry edits. Stored geometry only; does not evaluate runtime dynamizations.", "classicXml", true, LayoutSchema(true), "classicReferences"),
                    Op("build_screen_package", "Build a sidecar-free classic screen/tag import bundle from native XML prototypes and a supplied PLC symbol manifest. Offline only; writes no files and does not establish native acceptance.", "local", false, ComfortPackage.Schema(true)),
                    Op("validate_package_bindings", "Check screen-to-HMI-tag references and symbolic PLC connection/type declarations in supplied XML. Offline only; no native connection partner verification.", "local", false, ComfortPackage.Schema(false)),
                    Op("validate_native_text_list", "Validate original XML declaration/encoding/BOM bytes, V15.1 native TextLists metadata, identity, keys/cultures and known import constraints. Source/candidate hashes and named check receipts; no native import/compile/save/runtime claim.", "local", false, ClassicTextListValidation.Schema("validate_native_text_list")),
                    Op("validate_text_list_delta", "Validate the exact reviewed text-list addition/update/removal plan, baseline/candidate hashes and all unselected/default/language content. Optional explicit blank-other-new-translations policy and supplied native export readback comparison. Offline only.", "local", false, ClassicTextListValidation.Schema("validate_text_list_delta")),
                    Op("validate_text_list_bindings", "Validate exact exported screen LinkList/TextList bindings against an explicit list inventory and optional expected control/link paths. Compare a supplied unchanged screen in full. Never infer canonical versus _1 names; no runtime/script lookup.", "local", false, ClassicTextListEvidence.Schema(false)),
                    Op("validate_text_list_workflow", "Build a source/candidate/readback and native apply/compile/save evidence receipt for exact project/PID/HMI/list identities. Compare compile diagnostics with an optional baseline; missing stages remain not-run. Validates caller-supplied evidence only; never performs or retries native jobs.", "local", false, ClassicTextListEvidence.Schema(true)),
                    Op("inspect_xml", "Inspect supplied classic XML without attaching to TIA.", "local", false, xmlSchema),
                    Op("edit_xml", "Edit existing scalar XML fields in the selected native resource.", "classicXml", true, new { type = "object", required = new[] { "edits" }, properties = new { edits = new { type = "array", items = new { type = "object", required = new[] { "xPath", "expectedValue", "newValue" }, properties = new { xPath = new { type = "string" }, expectedValue = new { type = "string" }, newValue = new { type = "string" } } } } } }, "classicReferences"),
                    Op("change_behaviors", "Clone/remove frozen native event/animation prototypes. Clone owner types must match; explicit same-type Owner/Control maps may retarget external native links. Combined removals guard retained references; scalar edits preserve identity metadata.", "classicXml", true, BehaviorSchema(true), "classicReferences"),
                    Op("edit_text_list", "Transform supplied classic text-list translations, preserving literal CR/LF text. Requires host 0.14.1+.", "internalText", true, new { type = "object" }, "literalTextV1"),
                    Op("change_text_list", "Transform supplied classic text-list structure, preserving literal CR/LF text. Requires host 0.14.1+.", "internalText", true, new { type = "object" }, "literalTextV1") };
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
            if (module == "project" && new[] { "read_translation_units", "edit_project_texts" }.Contains(operation)) return ProjectTranslationOperations.Execute(operation, input);
            if (module == "project") return operation == "list_libraries" || operation == "browse_library"
                ? LibraryInspectionOperations.Execute(operation, input) : ProjectTextOperations.Execute(operation, input);
            JsonElement args = input.TryGetProperty("arguments", out var nested) ? nested : input;
            if (module == "comfort")
            {
                if (operation == "analyze_script_triggers") return SiemensMCPServer.Analysis.HmiScriptAnalysis.Execute(args);
                if (operation == "validate_native_text_list") return ExtensionWire.Json(ClassicTextListValidation.Bytes(ExtensionWire.Parse<ClassicByteValidation>(args)));
                if (operation == "validate_text_list_delta") return ClassicTextListValidation.Delta(ExtensionWire.Parse<ClassicDeltaValidation>(args));
                if (operation == "validate_text_list_bindings") return ExtensionWire.Json(ClassicTextListEvidence.Bindings(ExtensionWire.Parse<TextListBindingEvidence>(args)));
                if (operation == "validate_text_list_workflow") return ExtensionWire.Json(ClassicTextListEvidence.Workflow(ExtensionWire.Parse<TextListWorkflowEvidence>(args)));
                if (new[] { "import_controller_declarations", "analyze_hmi_references", "validate_hmi_bindings", "reference_impact" }.Contains(operation))
                    return HmiAnalysisOperations.Execute(operation, args);
                if (operation == "inspect_behaviors")
                {
                    var request = ExtensionWire.Parse<ClassicBehaviorInspection>(args);
                    RequireScreenXml(request.Xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(request.Xml).InspectBehaviors(request));
                }
                if (operation == "inspect_references")
                {
                    var request = ExtensionWire.Parse<ClassicReferenceInspection>(args);
                    RequireScreenXml(request.Xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(request.Xml).ReferenceCatalog(request));
                }
                if (operation == "rebind_references")
                {
                    string xml = input.GetProperty("snapshot").GetString()!;
                    RequireScreenXml(xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(xml).RebindReferences(ExtensionWire.Parse<ClassicReferenceRequest>(args)));
                }
                if (operation == "inspect_screen_items")
                {
                    var request = ExtensionWire.Parse<ClassicScreenItemInspection>(args);
                    RequireScreenXml(request.Xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(request.Xml).ScreenItemCatalog(request));
                }
                if (operation == "change_screen_items")
                {
                    string xml = input.GetProperty("snapshot").GetString()!;
                    RequireScreenXml(xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(xml).ChangeScreenItems(ExtensionWire.Parse<ClassicScreenItemRequest>(args)));
                }
                if (operation == "inspect_screen_texts")
                {
                    var request = ExtensionWire.Parse<ClassicScreenTextInspection>(args);
                    RequireScreenXml(request.Xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(request.Xml).ScreenTextCatalog(request));
                }
                if (operation == "edit_screen_texts")
                {
                    string xml = input.GetProperty("snapshot").GetString()!;
                    RequireScreenXml(xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(xml).EditScreenTexts(ExtensionWire.Parse<ClassicScreenTextRequest>(args)));
                }
                if (operation == "inspect_screen_layout")
                {
                    var request = ExtensionWire.Parse<ClassicLayoutInspection>(args);
                    RequireScreenXml(request.Xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(request.Xml).LayoutCatalog(request.Offset, request.MaxItems));
                }
                if (operation == "arrange_screen_items")
                {
                    string xml = input.GetProperty("snapshot").GetString()!;
                    RequireScreenXml(xml);
                    return ExtensionWire.Json(new ClassicXmlDocument(xml).ArrangeLayout(ExtensionWire.Parse<ClassicLayoutRequest>(args)));
                }
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
                    string snapshot = input.GetProperty("snapshot").GetString()!; RequireScreenXml(snapshot);
                    var doc = new ClassicXmlDocument(snapshot);
                    string xml = operation == "edit_xml" ? doc.Edit(ExtensionWire.Parse<ClassicXmlEdit[]>(args.GetProperty("edits")))
                        : doc.ChangeReviewedBehaviors(ExtensionWire.Parse<ClassicBehaviorOperation[]>(args.GetProperty("changes")));
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
        private static void RequireScreenXml(string xml)
        {
            if (xml == null || xml.Length == 0 || xml.Length > 16 * 1024 * 1024) throw new ArgumentException("Supply native screen XML within the 16 MiB limit.");
        }
        private static object BehaviorSchema(bool edit)
        {
            if (!edit) return new { type = "object", additionalProperties = false, required = new[] { "xml" }, properties = new {
                xml = new { type = "string", minLength = 1, maxLength = 16777216 }, rootXPath = new { type = "string", maxLength = 4096 },
                offset = new { type = "integer", minimum = 0, maximum = 5000 }, maxItems = new { type = "integer", minimum = 1, maximum = 200 } } };
            return new { type = "object", additionalProperties = false, required = new[] { "changes" }, properties = new {
                changes = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object", additionalProperties = false,
                    required = new[] { "action", "xPath", "expectedFingerprint" }, properties = new {
                        action = new { type = "string", @enum = new[] { "clone", "remove" } }, xPath = new { type = "string", maxLength = 4096 },
                        expectedFingerprint = new { type = "string", pattern = "^[0-9a-f]{64}$" }, destinationXPath = new { type = "string", maxLength = 4096 },
                        edits = new { type = "array", maxItems = 100, items = new { type = "object", required = new[] { "xPath", "expectedValue", "newValue" }, properties = new {
                            xPath = new { type = "string", maxLength = 4096 }, expectedValue = new { type = "string", maxLength = 4096 }, newValue = new { type = "string", maxLength = 4096 } } } },
                        referenceTargets = new { type = "array", maxItems = 50, items = new { type = "object", additionalProperties = false,
                            required = new[] { "xPath", "expectedTargetXPath", "newTargetXPath" }, properties = new {
                                xPath = new { type = "string", maxLength = 4096 }, expectedTargetXPath = new { type = "string", maxLength = 4096 }, newTargetXPath = new { type = "string", maxLength = 4096 } } } } } } } } };
        }
        private static object ReferenceSchema(bool edit)
        {
            if (!edit) return new { type = "object", additionalProperties = false, required = new[] { "xml" }, properties = new {
                xml = new { type = "string", minLength = 1, maxLength = 16777216 }, rootXPath = new { type = "string", maxLength = 4096 },
                linkType = new { type = "string", maxLength = 128 }, offset = new { type = "integer", minimum = 0, maximum = 5000 }, maxItems = new { type = "integer", minimum = 1, maximum = 200 } } };
            return new { type = "object", additionalProperties = false, required = new[] { "edits" }, properties = new {
                edits = new { type = "array", minItems = 1, maxItems = 200, items = new { type = "object", additionalProperties = false, required = new[] { "selection", "newName" }, properties = new {
                    selection = new { type = "object", additionalProperties = false, required = new[] { "xPath", "expectedFingerprint", "expectedName" }, properties = new {
                        xPath = new { type = "string", minLength = 1, maxLength = 4096 }, expectedFingerprint = new { type = "string", pattern = "^[0-9a-f]{64}$" },
                        expectedName = new { type = "string", minLength = 1, maxLength = 4096 } } }, newName = new { type = "string", minLength = 1, maxLength = 4096 } } } } } };
        }
        private static object ScreenTextSchema(bool edit)
        {
            if (!edit) return new { type = "object", additionalProperties = false, required = new[] { "xml" }, properties = new {
                xml = new { type = "string", minLength = 1, maxLength = 16777216 }, offset = new { type = "integer", minimum = 0, maximum = 20000 },
                maxRuns = new { type = "integer", minimum = 1, maximum = 200 }, maxCharacters = new { type = "integer", minimum = 1, maximum = 4096 },
                culture = new { type = "string", maxLength = 128 }, query = new { type = "string", maxLength = 256 }, caseSensitive = new { type = "boolean" } } };
            return new { type = "object", additionalProperties = false, required = new[] { "edits" }, properties = new {
                edits = new { type = "array", minItems = 1, maxItems = 200, items = new { type = "object", additionalProperties = false, required = new[] { "selection", "newText" }, properties = new {
                    selection = new { type = "object", additionalProperties = false, required = new[] { "xPath", "culture", "expectedTextFingerprint", "runIndex", "expectedText" }, properties = new {
                        xPath = new { type = "string", minLength = 1, maxLength = 4096 }, culture = new { type = "string", minLength = 1, maxLength = 128 },
                        expectedTextFingerprint = new { type = "string", pattern = "^[0-9a-f]{64}$" }, runIndex = new { type = "integer", minimum = 0, maximum = 19999 },
                        expectedText = new { type = "string", maxLength = 4096 } } }, newText = new { type = "string", maxLength = 4096 } } } } } };
        }
        private static object ScreenItemSchema(bool edit)
        {
            if (!edit) return new { type = "object", additionalProperties = false, required = new[] { "xml" }, properties = new {
                xml = new { type = "string", minLength = 1, maxLength = 16777216 }, rootXPath = new { type = "string", maxLength = 4096 },
                offset = new { type = "integer", minimum = 0, maximum = 2000 }, maxItems = new { type = "integer", minimum = 1, maximum = 200 } } };
            var selection = new { type = "object", additionalProperties = false, required = new[] { "xPath", "expectedFingerprint" }, properties = new {
                xPath = new { type = "string", minLength = 1, maxLength = 4096 }, expectedFingerprint = new { type = "string", pattern = "^[0-9a-f]{64}$" } } };
            var edits = new { type = "array", maxItems = 100, items = new { type = "object", additionalProperties = false, required = new[] { "xPath", "expectedValue", "newValue" }, properties = new {
                xPath = new { type = "string", maxLength = 4096 }, expectedValue = new { type = "string", maxLength = 4096 }, newValue = new { type = "string", maxLength = 4096 } } } };
            return new { type = "object", additionalProperties = false, required = new[] { "changes" }, properties = new {
                changes = new { type = "array", minItems = 1, maxItems = 50, items = new { type = "object", additionalProperties = false, required = new[] { "action", "selection" }, properties = new {
                    action = new { type = "string", @enum = new[] { "clone", "remove" } }, selection,
                    destinationXPath = new { type = "string", maxLength = 4096 },
                    names = new { type = "array", minItems = 1, maxItems = 500, items = new { type = "object", additionalProperties = false, required = new[] { "sourceXPath", "newName" }, properties = new {
                        sourceXPath = new { type = "string", maxLength = 4096 }, newName = new { type = "string", minLength = 1, maxLength = 128 } } } }, edits },
                    oneOf = new object[] { new { properties = new { action = new { @enum = new[] { "clone" } } }, required = new[] { "destinationXPath", "names" } },
                        new { properties = new { action = new { @enum = new[] { "remove" } } }, not = new { anyOf = new[] { new { required = new[] { "destinationXPath" } }, new { required = new[] { "names" } }, new { required = new[] { "edits" } } } } } } } } } };
        }
        private static object LayoutSchema(bool arrange)
        {
            if (!arrange) return new { type = "object", additionalProperties = false, required = new[] { "xml" }, properties = new {
                xml = new { type = "string", minLength = 1, maxLength = 16777216 }, offset = new { type = "integer", minimum = 0, maximum = 10000 }, maxItems = new { type = "integer", minimum = 1, maximum = 200 } } };
            var bounds = new { type = "object", additionalProperties = false, required = new[] { "left", "top", "width", "height" }, properties = new {
                left = new { type = "integer", minimum = -1000000, maximum = 1000000 }, top = new { type = "integer", minimum = -1000000, maximum = 1000000 },
                width = new { type = "integer", minimum = 1, maximum = 1000000 }, height = new { type = "integer", minimum = 1, maximum = 1000000 } } };
            return new { type = "object", additionalProperties = false, required = new[] { "action", "items" }, properties = new {
                action = new { type = "string", @enum = new[] { "move", "align", "distribute", "resize" } },
                items = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object", additionalProperties = false,
                    required = new[] { "xPath", "expectedBounds" }, properties = new { xPath = new { type = "string", maxLength = 4096 }, expectedBounds = bounds } } },
                deltaX = new { type = "integer", minimum = -1000000, maximum = 1000000 }, deltaY = new { type = "integer", minimum = -1000000, maximum = 1000000 },
                width = new { type = "integer", minimum = 1, maximum = 1000000 }, height = new { type = "integer", minimum = 1, maximum = 1000000 },
                alignment = new { type = "string", @enum = new[] { "left", "right", "horizontalCenter", "top", "bottom", "verticalCenter" } },
                axis = new { type = "string", @enum = new[] { "horizontal", "vertical" } }, anchorXPath = new { type = "string", maxLength = 4096 } },
                oneOf = new object[] {
                    new { properties = new { action = new { @enum = new[] { "move" } } }, required = new[] { "deltaX", "deltaY" } },
                    new { properties = new { action = new { @enum = new[] { "align" } } }, required = new[] { "alignment", "anchorXPath" } },
                    new { properties = new { action = new { @enum = new[] { "distribute" } } }, required = new[] { "axis" } },
                    new { properties = new { action = new { @enum = new[] { "resize" } } }, anyOf = new[] { new { required = new[] { "width" } }, new { required = new[] { "height" } } } } } };
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
