using System;
using System.ComponentModel;
using ModelContextProtocol;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SiemensMCPServer.Mcp
{
    internal sealed class ToolPayload
    {
        [System.Text.Json.Serialization.JsonRequired]
        public JsonElement Result { get; set; }
    }

    [McpServerToolType]
    internal sealed class McpTools
    {
        private readonly OpennessRuntime runtime;
        public McpTools(OpennessRuntime runtime) { this.runtime = runtime; }

        [McpServerTool(Name = "tia_list_extensions", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("List trusted feature extensions, loaded versions, primary/local worker process IDs, generation IDs and operation input schemas. Includes native/local/control queue counts, limits, active request names/IDs and cancellation/failure counters. Runs on the control lane, so local/native workloads do not occupy its queue. Starts pure primary workers as needed; does not load Siemens APIs or attach to TIA. Existing dedicated tools use these same workers.")]
        public Task<CallToolResult> Extensions(IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.ListExtensions(token));

        [McpServerTool(Name = "tia_reload_extensions", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Reload a staged trusted extension by module name; empty module reloads all. Queues on the native STA and drains active analysis generation leases before an atomic verified switch; new worker calls wait at that barrier. Successful reload invalidates ALL outstanding previews; failed reload keeps existing workers/previews. Preserves TIA attachment and unsaved engineering changes. Does not save or change the project. External versions are selected by active.json under the configured extension root.")]
        public Task<CallToolResult> ReloadExtensions(string module, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.ReloadExtensions(module, token));

        [McpServerTool(Name = "tia_read_extension", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Run advertised inspections/validation with argumentsJson from tia_list_extensions. Comfort local operations validate native text-list XML encoding/BOM, reviewed entry/translation deltas, preservation, TextList screen bindings and retained workflow receipts; preflight uses original bytes. inspect_references follows exact LinkList names. Local operations need no TIA attachment. Project operations browse libraries/languages or export/read/search project-text XLSX. PLC reads require explicit plcId/path and Offline state. Workers receive snapshots only; no native import, compile or save from local validation.")]
        public Task<CallToolResult> ReadExtension(string module, string operation, string argumentsJson, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.ReadExtension(module, operation, argumentsJson, token));

        [McpServerTool(Name = "tia_preview_extension", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview an advertised extension operation against an exact attached target and read fingerprint. argumentsJson must match its schema. Classic XML uses targetId=hmiId and resource path. Unified uses path=arguments.screenPath plus arguments.itemPath. PLC source uses targetId=plcId and empty path. Project texts use targetId=project, empty path and the workbookFingerprint from read_translation_units; arguments bind the exact attached project and language pair. Host retains identity/offline checks, candidate files, hashes and native transactions; workers receive serialized snapshots only. Does not change TIA. Returns a generation-bound plan for tia_apply_extension_plan.")]
        public Task<CallToolResult> PreviewExtension(string module, string operation, string targetId, string path, string expectedFingerprint, string argumentsJson, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.PreviewExtension(module, operation, targetId, path, expectedFingerprint, argumentsJson, token));

        [McpServerTool(Name = "tia_apply_extension_plan", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Apply an authorized extension preview once with its plan ID and candidate hash. Rejects reloaded generations and delegates to the host's existing native transaction/readback guards. Requires --allow-writes. Does not save; compile/save remain separate.")]
        public Task<CallToolResult> ApplyExtension(string planId, string candidateSha256, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ApplyExtension(planId, candidateSha256), token, writes: true));

        [McpServerTool(Name = "tia_list_versions", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("List installed TIA versions compatible with this server build, the selected API, and whether writes are enabled, current shared safeguard policy/settings path and launch permission.")]
        public Task<CallToolResult> Versions(IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Versions(token));

        [McpServerTool(Name = "hmi_prepare_classic_export_bundle", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Prepare selected classic HMI exports as an indexed import bundle; local artifacts only. For categories=['TextLists'], accept a folder/ZIP with one nested TextLists root; stream-verify the whole archive without extracting unrelated categories. Optional exact selectedTextLists, required V15.1 metadata/encoding/companion/identity checks, manifest verification and CRC/hash-verified ZIP publication. Failed checks block publication. Optional repairXmlDeclaration fixes only UTF-8's unsupported utf8 declaration alias, preserving body bytes and separate originals. Other categories retain the WinForms ExportSummary.txt layout. Native import requires separate preview/apply, compile and save.")]
        public Task<CallToolResult> PrepareClassicBundle(string sourceDirectory,
            [Description("Explicit categories: ProjectGraphics, Connections, Cycles, VBScripts, TagTables, TextLists, GraphicLists, ScreenTemplates, PopupScreens, SlideInScreens, PermanentArea, Screens.")] string[] categories,
            [Description("Use [] to preserve exported folder names. If the GUI sanitized folder names, supply original native names as {exportedPath:'Screens/ExportedFolder',name:'Original folder name'}; use forward slashes and exact category-relative paths.")] ClassicFolderName[] folderNames,
            IProgress<ProgressNotificationValue> progress, CancellationToken token, bool repairXmlDeclaration = false, string targetVersion = "V15.1", string[]? selectedTextLists = null)
            => Result(progress, () => runtime.PrepareClassicBundle(sourceDirectory, categories, folderNames, token, repairXmlDeclaration, targetVersion, selectedTextLists));

        [McpServerTool(Name = "tia_select_version", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Select an installed compatible TIA engineering version before using Openness. Changing versions after loading Siemens assemblies requires a server restart.")]
        public Task<CallToolResult> SelectVersion([Description("Version returned by tia_list_versions, such as V15.1 or V19.")] string version, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.SelectVersion(version, token));

        [McpServerTool(Name = "tia_list_processes", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("List running TIA processes and their open project paths for the selected API. Does not attach or open a project.")]
        public Task<CallToolResult> Processes(IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ListProcesses(), token));

        [McpServerTool(Name = "tia_get_api_capabilities", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Inspect loaded Siemens API capabilities for Comfort/classic, Unified and PLC engineering without attaching. API member presence does not establish native write acceptance.")]
        public Task<CallToolResult> Capabilities(IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ApiCapabilities(), token));

        [McpServerTool(Name = "hmi_list_screens", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("List Unified screens, recursively through screen groups when the installed API exposes them (flat composition on V16/V17). Returns exact screen paths for the Unified read/edit tools.")]
        public Task<CallToolResult> Screens(string hmiId, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ListScreens(hmiId), token));

        [McpServerTool(Name = "hmi_list_screen_items", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("List the native screen-item composition of one Unified screen. Does not enter faceplate type definitions or nested control parts.")]
        public Task<CallToolResult> ScreenItems(string hmiId, string screenPath, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ListScreenItems(hmiId, screenPath), token));

        [McpServerTool(Name = "hmi_read_unified_object", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Read an existing Unified screen or item: supported scalar fields, multilingual captions/tooltips, dynamizations, and a fingerprint of returned fields. Use empty itemPath for the screen. Complex rich text and dynamized fields are inspection-only.")]
        public Task<CallToolResult> ReadUnified(string hmiId, string screenPath, string itemPath, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ReadUnifiedObject(hmiId, screenPath, itemPath), token));

        [McpServerTool(Name = "hmi_preview_unified_edits", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview changes to existing supported Unified static properties or single-run multilingual text. Requires the read fingerprint and exact old values. Preserves other locales and rich-text styling. Writes JSON recovery artifacts; does not modify TIA.")]
        public Task<CallToolResult> PreviewUnified(string hmiId, string screenPath, string itemPath, string expectedFingerprint,
            [Description("Each edit supplies property, culture (empty for a scalar), expectedValue, and newValue. Numbers use invariant decimal strings, booleans true/false, colors #AARRGGBB, and captions plain text.")] UnifiedEdit[] edits, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewUnified(hmiId, screenPath, itemPath, expectedFingerprint, edits), token));

        [McpServerTool(Name = "hmi_apply_unified_edits", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Apply an authorized Unified screen-object preview once, using its plan ID and candidate hash. Rechecks returned-field fingerprint and dynamizations under exclusive access, sets only changed properties, verifies native readback in a transaction, and leaves the project unsaved. Requires --allow-writes.")]
        public Task<CallToolResult> ApplyUnified(string planId, string candidateSha256, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ApplyUnified(planId, candidateSha256), token, writes: true));

        [McpServerTool(Name = "tia_attach", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Attach to a running TIA process using its exact project path. Returns Comfort/classic and Unified HMI IDs, plus PLC target IDs. Reattaching invalidates pending previews. Siemens may show its local access prompt.")]
        public Task<CallToolResult> Attach(int processId, [Description("Exact absolute project path from tia_list_processes.")] string expectedProjectPath, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.Attach(processId, expectedProjectPath), token));

        [McpServerTool(Name = "tia_get_status", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Return the current attached project and classic/Unified HMI target IDs and capabilities.")]
        public Task<CallToolResult> Status(IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.Status(), token));

        [McpServerTool(Name = "hmi_list_text_lists", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("List text-list names in a classic HMI or a Unified HMI whose native API exposes paired-YAML export/import. V19 Unified lacks that route.")]
        public Task<CallToolResult> TextLists(string hmiId, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ListTextLists(hmiId), token));

        [McpServerTool(Name = "hmi_read_text_list", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Read a classic text list or a supported Unified paired-YAML text list, all languages and a semantic fingerprint. Uses temporary local export files. Unified fingerprint covers the complete exported pair. Unsupported native routes, formatted text and ranges fail closed or stay inspection-only.")]
        public Task<CallToolResult> Read(string hmiId, string listName, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.Read(hmiId, listName), token));

        [McpServerTool(Name = "hmi_preview_text_edits", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview edits to existing plain text in SingleValue entries. Preserves unselected languages and objects. Requires the fingerprint and exact old text from hmi_read_text_list. Writes before/candidate snapshots and a diff locally; does not modify TIA. Returns a plan with its configured expiry (30 minutes by default) and candidate SHA-256 for apply.")]
        public Task<CallToolResult> Preview(string hmiId, string listName, string expectedFingerprint,
            [Description("Each edit specifies entry (numeric string or Default), culture, expectedText, and newText. Only existing entries/cultures are supported.")] TextEdit[] edits, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.Preview(hmiId, listName, expectedFingerprint, edits), token));

        [McpServerTool(Name = "hmi_apply_text_edits", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Apply an authorized preview to the attached engineering project. Requires its plan ID and candidate hash. Rechecks current content under exclusive access, imports in a transaction, and verifies native readback before committing. Each plan can be attempted once. Unified paired-YAML import recreates all exported user lists while preserving configuration and unselected texts; inspect the preview scope. Does not save the project. Requires --allow-writes.")]
        public Task<CallToolResult> Apply(string planId, string candidateSha256, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.Apply(planId, candidateSha256), token, writes: true));

        [McpServerTool(Name = "hmi_preview_classic_text_list_changes", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview Comfort/classic text-list entry and translation creation/removal using the fingerprint and structure returned by hmi_read_text_list. addEntry needs a new numeric entry, templateEntry and explicit translations for every template culture. removeEntry needs expectedEntryFingerprint. addTranslation needs entry, an already active project culture, templateCulture and text. removeTranslation needs entry, culture and expectedText; V15.1 retains active language slots, so clear existing text with hmi_preview_text_edits instead. Default/range/formatted/linked entries are protected. Preserves other content, allocates fresh XML IDs, and writes before/candidate/diff artifacts. Apply with hmi_apply_text_edits; no project change during preview.")]
        public Task<CallToolResult> PreviewClassicTextStructure(string hmiId, string listName, string expectedFingerprint, ClassicTextListChange[] changes, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewClassicTextStructure(hmiId, listName, expectedFingerprint, changes), token));

        [McpServerTool(Name = "hmi_compile", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Compile the selected HMI with Siemens Openness and return native diagnostics plus compileScope. Unified may use the nearest owning device item/device's compiler when its software has no service; that compiler covers its owning scope. Any included PLC must be Offline. Lookup/compile failures are not retried at a broader scope. Does not save or download. Requires --allow-writes.")]
        public Task<CallToolResult> Compile(string hmiId, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.Compile(hmiId), token, writes: true));

        [McpServerTool(Name = "tia_save_project", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Save the entire attached TIA project, including changes already present from other editors. Use only when the user authorizes saving this project; provide its exact path. Requires --allow-writes.")]
        public Task<CallToolResult> Save(string expectedProjectPath, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.Save(expectedProjectPath), token, writes: true));

        [McpServerTool(Name = "tia_archive_project", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Create a native compressed backup of the exact attached saved project in a new local artifact directory. Returns archive path, size and SHA-256. Refuses unsaved changes; never saves or changes the project location. Requires --allow-writes.")]
        public Task<CallToolResult> Archive(string expectedProjectPath, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.Archive(expectedProjectPath), token, writes: true));

        [McpServerTool(Name = "tia_reopen_project", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Close and reopen the exact attached project in its existing TIA process. Use only within the user's authorization to reopen that project. Refuses unsaved changes and invalidates every pending preview. Does not save or discard edits; on failure inspect TIA and reattach explicitly. Requires --allow-writes.")]
        public Task<CallToolResult> Reopen(string expectedProjectPath, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.Reopen(expectedProjectPath), token, writes: true));

        [McpServerTool(Name = "hmi_read_object", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Browse Comfort/classic or Unified engineering objects using Siemens self-description. Start with empty path; follow exact returned child paths. Returns supported scalar attributes with types/enum choices, child paths, native creation arguments, import/export availability and fingerprint. Multilingual texts, event handlers, dynamizations, tags, alarms, scripts and settings are navigable when the API exposes them. $projectGraphics selects shared project graphics. Parent/services cannot be traversed. Complex non-scalar values require native export/import.")]
        public Task<CallToolResult> ReadObject(string hmiId, string path, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ReadEngineeringObject(hmiId, path), token));

        [McpServerTool(Name = "hmi_preview_object_edits", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview changes to advertised editable scalar attributes on a Comfort or Unified object. Exact fingerprint and old values required. Includes supported enums, numeric/string/bool/color properties, script source exposed as strings, and individual multilingual text items. Raw Unified multilingual Text must use native formatted markup; use the dedicated Unified tools for preserving simple caption styling. Dynamized properties and existing classic cycles cannot be overwritten. Does not change TIA.")]
        public Task<CallToolResult> PreviewObject(string hmiId, string path, string expectedFingerprint, EngineeringEdit[] edits, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewEngineering("edit", hmiId, path, expectedFingerprint, edits, "", null, "", false), token));

        [McpServerTool(Name = "hmi_preview_create_object", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview creating one object in a Comfort/Unified composition using only its advertised native creation type and named scalar arguments. Read the composition first for exact nativeType, parameter names, mandatory fields and enum choices. Supports available folders, Unified screens/items, tags, alarms, connections, event handlers, dynamizations and parts. Additional properties can be edited after creation. Does not change TIA.")]
        public Task<CallToolResult> PreviewCreate(string hmiId, string path, string expectedFingerprint, string nativeType, CreationArgument[] arguments, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewEngineering("create", hmiId, path, expectedFingerprint, null, nativeType, arguments, "", false), token));

        [McpServerTool(Name = "hmi_preview_delete_object", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview deleting an explicitly selected Comfort/Unified composition member. Requires exact read fingerprint and native Delete support. Root objects, singletons and classic cycles cannot be deleted. Deletion can invalidate references; compile afterwards. JSON snapshots are inspection evidence, not a restorable project backup. Does not change TIA.")]
        public Task<CallToolResult> PreviewDelete(string hmiId, string path, string expectedFingerprint, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewEngineering("delete", hmiId, path, expectedFingerprint, null, "", null, "", false), token));

        [McpServerTool(Name = "hmi_export_objects", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Export a selected Comfort/Unified object, composition, folder or entire HMI (empty path) into a new local artifact directory. Recurses through API-visible descendants, preserving XML/YAML/JS and binary sidecars where native export is available, plus metadata snapshots and an export index. Returns hashes and explicit failures; this is not a complete project backup. Use $projectGraphics for shared project graphics. Does not change the project.")]
        public Task<CallToolResult> ExportObjects(string hmiId, string path, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ExportEngineeringObject(hmiId, path), token));

        [McpServerTool(Name = "hmi_preview_import", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview native resource import into an exact Comfort/Unified composition. sourceDirectory contains one classic resource XML plus sidecars, or the complete Unified YAML/JS companion set. All files are staged and hash-bound; current native content is exported for stale detection. Unified imports require overwrite=true to acknowledge native replacement scope. Existing classic cycles are always protected. Does not change TIA. Native imports can add objects and resolve external references; candidate content equality is not asserted.")]
        public Task<CallToolResult> PreviewImport(string hmiId, string path, string expectedFingerprint, string sourceDirectory, bool overwrite, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewEngineering("import", hmiId, path, expectedFingerprint, null, "", null, sourceDirectory, overwrite), token));

        [McpServerTool(Name = "hmi_preview_import_bundle", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview a complete export bundle produced by hmi_export_objects. Uses export-index.json to import native resources in dependency order, recreating missing supported folder hierarchy. Select empty path for an entire HMI or the exact exported subtree path. Includes shared project graphics for whole-HMI exports. Existing classic cycles are skipped. Failed/incomplete exports are rejected. All files are staged and hash-bound; native baseline is rechecked on apply. Unified imports require overwrite=true. Native XML/YAML files can be edited before preview; preserve companions and export index. Metadata-only objects have no import route and are not restored by this operation.")]
        public Task<CallToolResult> PreviewBundle(string hmiId, string path, string expectedFingerprint, string sourceDirectory, bool overwrite, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewEngineering("bundle", hmiId, path, expectedFingerprint, null, "", null, sourceDirectory, overwrite), token));

        [McpServerTool(Name = "hmi_apply_object_plan", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Apply a reviewed object edit/create/delete/import preview once using its planId and candidateSha256. Requires --allow-writes. Rechecks native state under exclusive access and commits one transaction after native readback. Imports also recheck the baseline export and retain after-export evidence. Leaves the project unsaved; compile and inspect results before saving.")]
        public Task<CallToolResult> ApplyObject(string planId, string candidateSha256, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ApplyEngineeringPlan(planId, candidateSha256), token, writes: true));

        [McpServerTool(Name = "hmi_read_classic_resource", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Comfort/classic V15.1-first workflow: export one selected screen, tag table, script, list or other native XML resource with sidecars. Returns complete XML, a bounded scalar catalog with exact XPath/value/editability fields, its content fingerprint, and artifact files. Choose an exact resource path from hmi_read_object. Does not modify TIA.")]
        public Task<CallToolResult> ReadClassicResource(string hmiId, string path, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ReadClassicXml(hmiId, path), token));

        [McpServerTool(Name = "hmi_preview_classic_behavior_changes", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview clone/remove operations on native exported Comfort event/animation/binding objects. Read hmi_read_classic_resource.behaviorCatalog first. Each change requires action (clone/remove), xPath and expectedFingerprint. Clone additionally requires destinationXPath selecting the exact Hmi.* owner in this resource, plus optional scalar edits addressed within a Document-wrapped clone. Retains the native composition/type and assigns fresh IDs while preserving local references. Removal rejects inbound references. No TIA mutation during preview; apply with hmi_apply_classic_xml_edits. Missing native prototypes and unexported objects require native engineering.")]
        public Task<CallToolResult> PreviewClassicBehaviors(string hmiId, string path, string expectedFingerprint, ClassicBehaviorChange[] changes, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewClassicBehaviors(hmiId, path, expectedFingerprint, changes), token));

        [McpServerTool(Name = "hmi_preview_classic_xml_edits", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview Comfort/classic XML changes to existing scalar elements or attributes, including screen captions/layout and resource configuration. Each edit supplies xPath, expectedValue and newValue. XPath must select exactly one node inside the resource; use structural/name selectors, not volatile IDs. Preserves resource identity, other fields/languages and sidecars. Existing cycles are inspection-only. Complex XML structure changes require a reviewed resource import. Does not change TIA.")]
        public Task<CallToolResult> PreviewClassicResource(string hmiId, string path, string expectedFingerprint, ClassicXmlEdit[] edits, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewClassicXml(hmiId, path, expectedFingerprint, edits), token));

        [McpServerTool(Name = "hmi_apply_classic_xml_edits", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Apply a Comfort/classic resource preview once using planId and candidateSha256. Requires --allow-writes. Rechecks the native resource plus companions, imports under exclusive access and a transaction, and requires native readback to match the candidate. Leaves the project unsaved. Compile and inspect the HMI afterwards.")]
        public Task<CallToolResult> ApplyClassicResource(string planId, string candidateSha256, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ApplyClassicXml(planId, candidateSha256), token, writes: true));

        [McpServerTool(Name = "plc_list_targets", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("List PLC software targets in the explicitly attached project, including exact plcId and current native online state. No connection, mode change or download is performed. PLC exports and mutations require verified Offline state.")]
        public Task<CallToolResult> Plcs(IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ListPlcs(), token));

        [McpServerTool(Name = "plc_read_object", ReadOnly = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Browse a selected PLC's offline engineering object model. Start with empty path and follow returned paths for blocks/groups, PLC data types, tag tables/groups and external sources. Returns generic state.targetId, fingerprint, editable scalar types and advertised creation options. Excludes hardware, technology objects, watch/force tables and runtime services.")]
        public Task<CallToolResult> ReadPlc(string plcId, string path, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ReadPlcObject(plcId, path), token));

        [McpServerTool(Name = "plc_read_resource", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Export and return native XML for one selected PLC block, data type or tag table, plus artifacts and content hash. Requires verified Offline state. Protected blocks may expose only their public interface. XML is the selected API/version's native SimaticML representation.")]
        public Task<CallToolResult> ReadPlcXml(string plcId, string path, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ReadPlcResource(plcId, path), token));

        [McpServerTool(Name = "plc_export_objects", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Export a selected PLC resource/group or all supported PLC software roots with empty path. Preserves native XML and metadata, with explicit failures and a bundle index. Requires Offline state. Not a full project archive; protected/library blocks and external source text have native API limits.")]
        public Task<CallToolResult> ExportPlc(string plcId, string path, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.ExportPlcObjects(plcId, path), token));

        [McpServerTool(Name = "plc_preview_object_edits", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview exact typed scalar edits on a PLC engineering object using its read fingerprint and old values. Program body changes use native XML import or source generation. Does not mutate TIA.")]
        public Task<CallToolResult> PreviewPlcEdits(string plcId, string path, string expectedFingerprint, EngineeringEdit[] edits, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewPlc("edit", plcId, path, expectedFingerprint, edits, "", null, "", false), token));

        [McpServerTool(Name = "plc_preview_create_object", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview creating a PLC group, tag/table, type or other object only when the native composition advertises the type and supported scalar creation arguments. Use XML import/source generation where no native constructor exists. Does not mutate TIA.")]
        public Task<CallToolResult> PreviewPlcCreate(string plcId, string path, string expectedFingerprint, string nativeType, CreationArgument[] arguments, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewPlc("create", plcId, path, expectedFingerprint, null, nativeType, arguments, "", false), token));

        [McpServerTool(Name = "plc_preview_delete_object", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview deletion of one explicitly selected PLC software composition member with native Delete support. Binds a subtree baseline and read fingerprint. Can invalidate call/type/tag references; compile afterwards. Does not mutate TIA.")]
        public Task<CallToolResult> PreviewPlcDelete(string plcId, string path, string expectedFingerprint, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewPlc("delete", plcId, path, expectedFingerprint, null, "", null, "", false), token));

        [McpServerTool(Name = "plc_preview_import", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview native PLC XML import into an exact block/type/tag-table composition. sourceDirectory contains the native resource XML and companions. Requires its composition fingerprint, captures a baseline, stages all files and records overwrite scope. Requires Offline state for the baseline export. Does not mutate TIA.")]
        public Task<CallToolResult> PreviewPlcImport(string plcId, string path, string expectedFingerprint, string sourceDirectory, bool overwrite, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewPlc("import", plcId, path, expectedFingerprint, null, "", null, sourceDirectory, overwrite), token));

        [McpServerTool(Name = "plc_preview_xml_import", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Stage bounded candidateXml directly for native PLC import into the selected composition; useful to API clients without local file tools. Provide the composition fingerprint from plc_read_object, not a single-resource content hash. Uses the same preview, baseline and overwrite checks as plc_preview_import. Does not mutate TIA.")]
        public Task<CallToolResult> PreviewPlcXml(string plcId, string path, string expectedFingerprint, string candidateXml, bool overwrite, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewPlcXmlImport(plcId, path, expectedFingerprint, candidateXml, overwrite), token));

        [McpServerTool(Name = "plc_preview_import_bundle", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview an indexed PLC export bundle: PLC types, tag tables, then blocks, recreating supported user groups. Requires Offline state and a complete baseline. Imports only native serializable resources; metadata-only external sources are not reconstructed. Does not mutate TIA.")]
        public Task<CallToolResult> PreviewPlcBundle(string plcId, string path, string expectedFingerprint, string sourceDirectory, bool overwrite, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewPlc("bundle", plcId, path, expectedFingerprint, null, "", null, sourceDirectory, overwrite), token));

        [McpServerTool(Name = "plc_preview_generate_source", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Preview native generation from exact SCL or STL code. Requires PLC-root fingerprint, unique sourceName, sourceLanguage SCL/STL, sourceCode and allowBlockReplacement=true to acknowledge native replacement of blocks declared in the source. Retains the new external source in default groups, binds a full supported PLC baseline and code files, and requires Offline state. Does not mutate TIA. Native compile/save remain separate.")]
        public Task<CallToolResult> PreviewPlcSource(string plcId, string expectedFingerprint, string sourceName, string sourceLanguage, string sourceCode, bool allowBlockReplacement, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.PreviewPlcSource(plcId, expectedFingerprint, sourceName, sourceLanguage, sourceCode, allowBlockReplacement), token));

        [McpServerTool(Name = "plc_apply_object_plan", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Apply a reviewed PLC edit/create/delete/import/source plan once using its ID and candidate hash. Requires --allow-writes and verified Offline state. Rechecks target/baseline under exclusive access, commits a Siemens transaction after native readback, and leaves the project unsaved. Native generation/import acceptance is not content equality or runtime validation; inspect artifacts and compile.")]
        public Task<CallToolResult> ApplyPlc(string planId, string candidateSha256, IProgress<ProgressNotificationValue> progress, CancellationToken token)
            => Result(progress, () => runtime.Invoke(s => s.ApplyEngineeringPlan(planId, candidateSha256), token, writes: true));

        [McpServerTool(Name = "plc_compile", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolPayload))]
        [Description("Compile the selected offline PLC software and return Siemens diagnostics. Requires --allow-writes and verified Offline state. Does not save, download, connect or change CPU mode.")]
        public Task<CallToolResult> CompilePlc(string plcId, IProgress<ProgressNotificationValue> progress, CancellationToken token) => Result(progress, () => runtime.Invoke(s => s.CompilePlc(plcId), token, writes: true));

        private static async Task<CallToolResult> Result(IProgress<ProgressNotificationValue> progress, Func<Task<object>> operation)
        {
            try
            {
                long sequence = 0;
                var stages = new ConcurrentQueue<string>(); int sending = 0;
                Action<string> observe = stage => {
                    stages.Enqueue(stage);
                    if (Interlocked.CompareExchange(ref sending, 1, 0) == 0) ThreadPool.QueueUserWorkItem(_ => {
                        do {
                            while (stages.TryDequeue(out string? next)) {
                                try { progress.Report(new ProgressNotificationValue { Progress = Interlocked.Increment(ref sequence), Message = next }); } catch { }
                            }
                            Interlocked.Exchange(ref sending, 0);
                        } while (!stages.IsEmpty && Interlocked.CompareExchange(ref sending, 1, 0) == 0);
                    });
                };
                using var observation = RequestLane.Observe(observe);
                object value = await operation().ConfigureAwait(false);
                var payload = new { result = value };
                string json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
                return new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(payload, JsonDefaults.Options), Content = new[] { new TextContentBlock { Text = json } } };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new CallToolResult { IsError = true, Content = new[] { new TextContentBlock { Text = ex.GetBaseException().Message } } };
            }
        }
    }
}
