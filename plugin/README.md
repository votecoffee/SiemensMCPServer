# SiemensMCPServer plugin 0.18.1

Host 0.18.1 and Comfort worker 0.15.0 add [classic TextLists validation and evidence](CLASSIC-TEXT-LIST-WORKFLOW.md): original-byte encoding/metadata checks, exact entry/translation delta preservation, actual screen bindings, scoped folder/ZIP preparation and native workflow receipt consistency. Prepare and native text-list imports enforce required preflight checks automatically. These additions have offline/package coverage; native acceptance of the new staging route remains pending.

The project and GitHub repository are now **SiemensMCPServer**. The plugin/MCP ID is `siemens-mcp-server`, the local marketplace is `siemens-mcp-server-local`, and the host is `SiemensMCPServer.Mcp.exe`. Remove the old `siemens-tia-openness` plugin or direct MCP registration through Codex before installing the renamed plugin. The installer refuses to register a second copy while the old server is enabled. Do not remove its cache manually while it is running. This rename needs a host upgrade; feature-worker reload remains available afterward.

This local Windows plugin provides Comfort/classic and Unified engineering object tools, native resource import/export, bulk transfers, and specialized text-list and screen-caption workflows. It uses the official C# MCP SDK and .NET Framework 4.8. No OpenAI API key is needed to connect it to Codex. The separately built desktop WinForms application retains its classic HMI workflow and also supports API-visible Unified and offline PLC native resource transfers. The plugin package contains the MCP server and workers. See [feature coverage](FEATURE-COVERAGE.md) for implemented MCP routes and remaining boundaries.

Host/Comfort worker 0.14.1 preserves literal carriage returns and mixed CR/LF values in classic text-list XML and fingerprints. This needs a host upgrade/reconnect: the new `internalText` / `literalTextV1` worker contract is rejected by older hosts, retaining their previous generation. Native acceptance of exact multiline values remains separate.

A selected historical V15.1 native pilot passed single-line English/German Unicode edits, native readback, compile and save/reopen, then restored the original saved state. CRLF/LF edits in the tested native body/p entries failed strict readback and rolled back. See [validation scope](NATIVE-ACCEPTANCE.md); offline newline preservation is not native multiline acceptance.

Host 0.15.0 and desktop 1.3.0 share [GUI-editable safeguards](SAFEGUARDS.md): write ceilings, operation restrictions, confirmations, preview limits, project scope and request limits. Permissions are rechecked for subsequent operations; scheduling/location changes require a host restart. Local settings do not grant client or Siemens permission.

Host/plugin **0.18.1** uses the app-owned framework 0.17.0 gateway: 14 stable front-door tools discover/invoke 51 native engineering operations. The native executable retains direct access to those 51 tools. Fresh installations and upgrades always install the standalone Siemens plugin; no Universal Tools plugin or shared registration is used. See [app host operation](APP-HOST.md).

## Install the built package

The native host supports [concurrent requests](CONCURRENCY.md): bounded parallel local analysis, separate control/status, cancellable queues and generation-safe reload. Native calls and transactions remain serialized on the owning STA. The standalone framework gateway currently serializes calls to its native backend; direct MCP clients retain the native concurrency lanes.

Version 0.12.0 adds [multilingual project texts](PROJECT-TEXTS.md): comments, descriptions, captions and text values with native categories/paths, missing-translation filters, exact previews and reviewed import/readback. The new native import route needs a 0.12.0 host upgrade/reconnect once; later worker logic can reload. `scripts/Edit-ProjectTexts.ps1` extracts complete retained workbooks or prepares disk-only candidates. Native import acceptance remains pending.

Comfort worker 0.11.0 adds [project HMI reference analysis](HMI-REFERENCE-ANALYSIS.md), Rockwell L5X/PLCopen declaration import, binding checks and change-impact reports. Use the bundled `scripts/Analyze-HmiReferences.ps1` for complete exports and large controller files. The four read-only operations reload on host 0.10.4 or newer with the existing tool schemas.

0.7.0 adds a stable host with reloadable Comfort, Unified and PLC feature workers. After the initial host upgrade, stage an extension update and call `tia_reload_extensions` while keeping Codex and TIA open. Reload retains the native attachment and invalidates outstanding previews; failed replacements retain previous workers. See [extension updates and development](EXTENSIONS.md) for commands and scope.

Comfort/classic V15.1 is the primary development profile. Unified follows with version-specific capability gates. PLC support shares the V15.1-first offline stack. Exact V15.0 compatibility requires separate API build and native validation.

Extract the ZIP to a permanent directory. It includes `server/SiemensMCPServer.Mcp.exe`, its runtime dependencies, workflow skills, MCP descriptions and a Codex compatibility manifest. The Windows Codex profile uses Python 3.13 and `framework/launch.py` to connect to the dedicated app-owned host. The installer provisions pinned dependencies offline from the wheel bundle; a chat never installs packages. The C# backend SDK stays at 2.2.0. Siemens DLLs are not included. Install TIA Portal, the matching Openness API, and the appropriate WinCC engineering components; use a Windows user in the local `Siemens TIA Openness` group. Use the V19 package for the Unified screen workflow on the installed V19 API.

For installation with both skills and tools, run the bundled installer with the original ZIP beside its `.sha256` sidecar:

```powershell
powershell.exe -NoProfile -File 'C:\path\to\extracted-plugin\scripts\Install-CodexPlugin.ps1' -Package 'C:\path\to\plugin.zip'
```

From this repository, the equivalent installer is `build/Install-CodexPlugin.ps1`. It checks ZIP/payload hashes, safe paths, member limits and plugin identity; keeps a versioned source under `%LOCALAPPDATA%\SiemensMCPServer\CodexMarketplace`; prepares the dedicated per-user framework runtime; backs up affected settings; then uses `codex plugin marketplace add` and `codex plugin add`. Existing unrelated plugins/settings are preserved. `-PrepareOnly` verifies/stages a package without registering it. A direct Siemens MCP registration blocks plugin installation to prevent duplicate tools. Resolve that existing registration explicitly before switching installation methods. No TIA project is attached or changed.

Verify the installed configuration using `py -3 -X utf8 scripts/test_codex_integration.py` from the extracted plugin or repository. The probe gets the actual registered command/arguments from Codex, starts that cached server, checks both skills and the tool list, and reads API capabilities/process metadata without attachment or model calls. Start a new Codex chat to load the installed tools and skills. This validates configuration and transport; it does not establish native project editing or a model-driven project trial.

This follows the official [local plugin packaging/installation workflow](https://developers.openai.com/plugins/build/plugins); the installed Codex CLI's `plugin add --help` confirms its supported install command. Clients lacking plugin installation can use direct MCP registration:

```powershell
codex mcp add siemens-mcp-server -- 'C:\path\to\extracted-plugin\server\SiemensMCPServer.Mcp.exe' --allow-writes
```

Start a new chat or reconnect the server to load its tools. Direct registration loads tools; the packaged skill is loaded when installing the plugin through a local plugin marketplace. Do not register the same server both ways.

For a repo-local plugin marketplace, put the extracted folder at `plugins/siemens-mcp-server` in your working repository and create `.agents/plugins/marketplace.json` using the format in the official [local plugin documentation](https://developers.openai.com/plugins/build/plugins). Then install the plugin from that local source in the desktop app. Availability of local marketplaces depends on the client. Direct MCP registration is the simpler fallback.

The bundled MCP configuration enables writes because this plugin provides editing tools. Launch the executable without `--allow-writes` for inspection and previews only. Apply, compile, and save are then rejected by the server. Codex also applies its configured tool approval policy.

## Classic text-list structure changes

`hmi_read_text_list` includes per-entry fingerprints, template eligibility and active project cultures. Preview ordinary SingleValue entry creation/removal and supported translation changes with `hmi_preview_classic_text_list_changes`, then use `hmi_apply_text_edits` with the returned plan/hash. `addEntry` uses an existing plain `templateEntry` and explicit `translations` for every template culture; fresh native XML IDs avoid collisions. `removeEntry` requires its `expectedEntryFingerprint`. `addTranslation` requires an already active project `culture`, an existing `templateCulture` and `text`. V15.1 XML import retains active language slots, so `removeTranslation` is rejected before staging; clear existing text with `hmi_preview_text_edits` and `newText=""`. Other profiles still require native acceptance for slot removal, exact `expectedText`, and at least one retained translation. Default/range/formatted/linked entries remain protected. New project languages must first be activated in TIA. This route changes an existing list and keeps other entries, locales and metadata.

## Comfort event and binding structure

Classic resource reads also return a bounded `behaviorCatalog` for native exported event/animation/binding objects and their Hmi children. Use scalar previews for existing fields. `hmi_preview_classic_behavior_changes` supports `clone` from an exact catalog `xPath`/`expectedFingerprint` into a `destinationXPath` selecting the native Hmi owner in the same resource, or `remove` of the exact object. Clone edits use paths within a temporary Document-wrapped clone, for example `/Document/Hmi.Event.Event/AttributeList/Name`, with exact old values. The native type/composition is retained; IDs are reassigned and local references remapped. Inbound references block removal, overlapping changes are rejected, and sidecars remain with the resource. Apply with `hmi_apply_classic_xml_edits`. This uses actual exported prototypes; absent/unexported native objects require native engineering or reviewed resource import. It never executes HMI events/scripts.

The inspected V15.1 classic HmiTarget API has no dedicated alarm-definition endpoint. API capabilities explicitly report that limit. The native alarm editor's XLSX workflow is separate; this plugin does not invent alarm XML or represent alarm-view controls missing from Openness exports as supported objects.

## First use

Open a backed-up or disposable TIA project first. Ask the chat to list compatible versions and running projects, then identify the exact project and HMI. Example request:

> In the selected project, change entry 7 of Messages_TL in en-US from "Old message" to "New message". Show the preview, apply the requested change, compile the HMI, and report the result. Leave saving to me.

Call `tia_get_api_capabilities` to inspect the loaded API, then attach to the exact process/project and use the target's family and capability flags. For text lists, use `hmi_list_text_lists`, `hmi_read_text_list`, `hmi_preview_text_edits`, and `hmi_apply_text_edits`. A preview returns a diff, a plan ID, the native import scope, and a candidate hash. Apply rechecks the snapshot under native exclusive access and commits only after native readback matches the candidate. A plan uses the configured preview lifetime (30 minutes by default) and can be attempted once. A failed or interrupted operation requires inspecting the current HMI before retrying.

Artifacts live under `%LOCALAPPDATA%\SiemensMCPServer\Mcp\Sessions` by default. Override with `--artifact-root=C:\some\directory`. Each preview retains original/candidate snapshots, hashes, a diff and an apply result. Classic snapshots are XML; Unified snapshots are JSON. Unified text-list snapshots contain both native YAML files and list identities. These are recovery artifacts for the selected workflow, not complete project archives. The project is never saved automatically.

`tia_archive_project(expectedProjectPath)` creates a native compressed project archive and returns its SHA-256. It requires a saved project and uses a short temporary path for the V15.1 archive path limit before retaining the result in the artifact directory. `tia_reopen_project(expectedProjectPath)` verifies saved changes by closing/reopening that exact project in its current TIA process. It refuses unsaved changes, never saves/discards them, and invalidates every preview. Use reopen only within the user's authorized scope; inspect TIA and reattach explicitly after a failure.

Simple native V15.1 text-list captions use a `<Text><body><p>…</p></body></Text>` wrapper. The specialized text-list tools expose the paragraph's plain text and preserve the wrapper during text/structure edits. Multiple paragraphs, styles, nested runs and dynamic markup remain protected.

## Comfort V15.1 resource editing

Comfort worker 0.7.2 also provides [screen/tag package generation and offline PLC binding checks](COMFORT-PACKAGES.md). These local operations clone native XML prototypes and produce a reviewed import bundle without changing the project. They reload on the existing 0.7.1 host; native import/compile acceptance remains separate.

Use `hmi_read_object` to select a screen/resource path, then `hmi_read_classic_resource` for its full native XML, content fingerprint including companion files, and retained artifacts. It also returns `scalarCatalog`: up to 1000 existing fields with exact `xPath`, current `value`, `editable` and `readOnlyReason`. Paths distinguish repeated/namespaced nodes without using export IDs. The catalog reports truncation; the full XML remains available. This covers native XML-exportable screens, tag tables, scripts, text/graphic lists and related classic resources.

`hmi_preview_classic_xml_edits` accepts that fingerprint and edits with `xPath`, `expectedValue`, and `newValue`. Reuse a returned editable catalog path/value or use structural/name selectors after inspecting the actual XML. Each XPath must select exactly one existing scalar inside the resource. Volatile IDs, resource renaming, composition metadata, structural containers, ambiguous selectors and complex subtree replacement are rejected. Other fields/languages and image sidecars remain intact. Existing cycles are read-only. Catalog eligibility does not establish native semantic acceptance.

`hmi_apply_classic_xml_edits` consumes the plan/hash once, rechecks the selected native resource, imports in a transaction, and requires matching native readback while ignoring volatile export IDs/headers. It leaves the project unsaved. Prefer the specialized text-list tool for translation changes; use reviewed native resource import for structural changes. Selected V15.1 native edit/compile/save/reopen tests have passed; see [native acceptance](NATIVE-ACCEPTANCE.md) for exact scope.

Version 0.6.3 also protects a slide-in's edge identity and verifies native XML external-file references before staging/import. Each companion path must resolve inside its staged resource, relative to the XML file; missing files, absolute paths and traversal are rejected. Keep original binary companions with the XML. Classic tag names are HMI-wide, so copying a table into a new folder still requires unique contained tag names and reviewed references.

## Unified screen objects

Use `hmi_list_screens` to discover screens recursively through screen groups, then `hmi_list_screen_items` for the selected screen's native item composition. Read a screen/item with `hmi_read_unified_object`; pass an empty `itemPath` to inspect the screen itself. This does not enter faceplate type definitions or nested control parts.

`hmi_preview_unified_edits` accepts a read fingerprint and edits containing `property`, `culture`, `expectedValue`, and `newValue`. Use an empty culture for scalar fields. Supported scalar properties are `Left`, `Top`, `Width`, `Height`, `Visible`, `Enabled`, `BackColor`, and `ForeColor`, where exposed by the native object. Values use invariant decimal strings, `true`/`false`, or `#AARRGGBB` colors. Unsupported or read-only properties are not writable.

Text properties include `Text`, `AlternateText`, `ToolTipText`, and `DisplayName` where present. Existing languages and simple rich text with one text run can be edited; styling and other language resources are preserved. Assignments are formatted as required by the Unified screen API. Multi-run text, dynamic fields, complex markup, and properties with dynamizations remain inspection-only. The snapshot fingerprint covers the returned fields and binding names, not every setting or event handler of the native object. The backend changes only selected properties.

Apply with `hmi_apply_unified_edits`, then use the common `hmi_compile` and, if authorized, `tia_save_project`. Screen edits use a Siemens transaction and verify the returned-field fingerprint before committing.

The 0.7.2 host first looks for a compile service on the selected HMI software. For Unified only, an absent service permits lookup on up to eight owning device items, starting with the nearest, and finally their containing device. Lookup stays within the native owner chain captured during discovery; project and sibling services are excluded. The selected native compiler runs once, and lookup/compile failures are never retried at a broader scope. A device compiler can cover other software in that owning scope, so included PLCs must be natively Offline. Results identify `compileScope` (kind, path, native type and `ownerFallback`). This host change requires upgrading/reconnecting the host; a worker-only reload cannot add it. Native Unified compile acceptance remains pending.

## Comfort and Unified object tools

Start with `hmi_read_object(hmiId, path="")`. Follow returned child paths rather than constructing names. The result includes a fingerprint, scalar values/types, enum choices, editable flags, children, native creation signatures, unsupported attribute types, inspection errors, and import/export availability. Path segments beginning with `p:` select a native navigator, `n:` a named member, `l:` a multilingual culture, `s:` a classic slide-in side/type, and `i:` an unnamed indexed member. Names are URI-escaped. `$projectGraphics` selects graphics shared by the project's HMIs. Parent and service navigation are excluded.

The browser covers API-visible descendants: classic screen/tag/script folders and resource compositions; Unified screens and nested parts, tags/tables/groups, connections, alarm definitions/classes, logs, scripts, events, dynamizations, and runtime configuration. Presence in the graph does not promise every value or operation is writable. Unsupported complex values are reported; use native export/import when available. These tools configure engineering data.

- `hmi_preview_object_edits`: typed changes with `attribute`, `expectedValue`, and `newValue`. Use invariant strings, advertised enum names, `true`/`false`, `#AARRGGBB`, ISO round-trip dates, or invariant time spans. A returned null uses `<null>` as its expected value and can be initialized to a supported scalar. Arbitrary CLR objects and numeric enum casts are rejected. Native constraints still apply.
- `hmi_preview_create_object`: select an advertised `nativeType` from a composition and supply exact named creation arguments. Only inferred supported scalar arguments are accepted. Set other properties in a later edit.
- `hmi_preview_delete_object`: delete an explicitly selected composition member. The preview retains a subtree export and rechecks it before deletion. Roots, singletons, and classic cycles are protected. Inspect references and compile after deletion.
- `hmi_apply_object_plan`: apply any of those plans or an import plan once using its hash. Native exclusive access and a transaction protect the operation. Edits require matching readback; creation/deletion verifies membership; imports retain an after-export. The project remains unsaved.

An individual multilingual `Text` item is a raw native string in the generic tools. Unified screen strings may require formatted `<body>` markup. Prefer the dedicated caption workflow for retaining simple text styling. Generic tools can explicitly change a binding or event object; static edits to a property with a dynamization are blocked. Existing classic cycles cannot be edited or deleted.

## Native exports and imports

`hmi_export_objects` exports a resource, category/folder, or whole API-visible HMI with empty path. The new artifact directory contains native XML/YAML/JS, companion images/files, metadata snapshots, and `export-index.json`. Whole-HMI exports include shared project graphics. Each native resource has a separate directory preserving relative sidecar references. Failures remain explicit in the index. Metadata snapshots are not restorable native resources, and this export is not a complete project backup.

`hmi_preview_import` stages a directory into an import-capable composition. Classic imports require one top-level resource XML and its sidecars. Unified imports use their directory-based native format, including YAML/text libraries/JavaScript companions, and require `overwrite=true` to acknowledge native replacement scope. Classic scripts use `ExportOptions.None`; permanent/global screens use their dedicated singleton import methods. Integrated classic connections remain subject to Siemens' export restriction.

Unified directory previews parse the YML files and check known script JS/text-list library companions. Where only a filename overload exists, exactly one primary configuration is required and its actual filename is passed to Siemens. Directory-only overloads retain native default filenames. Deferred export results are fully enumerated; false or unsuccessful transfer results fail the operation. These checks do not establish native schema/content acceptance. See Siemens' [script export](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/export-of-global-script) and [script import](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/import-of-global-script) contracts.

`hmi_preview_import_bundle` stages an indexed bundle. It imports resources in dependency order: shared graphics, connections, cycles, scripts, tags, text/graphic lists, shared screen resources, then screens. Supported missing folder hierarchy is recreated; existing classic cycles are skipped. Category bundles stay within the selected subtree. Bundles with export failures are rejected; select a complete category instead. Metadata-only objects are not restored; use direct creation/edits for those.

For the WinForms application's older `ExportSummary.txt` layout, first call `hmi_prepare_classic_export_bundle(sourceDirectory, categories, folderNames)`. Select the exact export root and explicit categories from its summary. Use `folderNames=[]` to retain exported folder names, or supply mappings such as `{exportedPath:"Screens/A_B",name:"A/B"}` when the GUI sanitized an original folder name. The converter handles all twelve legacy categories, preserves native XML/sidecar bytes, and writes a new indexed bundle outside the source. Failed categories are rejected. The legacy exporter can omit individual failed resources while reporting a category successful, so conversion establishes no completeness claim. Review its `conversion.json`, then read the target root and pass the returned artifact directory to `hmi_preview_import_bundle`. Conversion requires no TIA attachment and changes no project.

Both previews retain exact candidate files and an API-visible baseline. Apply detects changed staged files and stale native content. Native imports can normalize defaults and resolve external links, so generic imports report native acceptance and after-export evidence with content equality not asserted. Review the evidence and compile diagnostics. Specialized text-list/caption routes continue to require candidate/readback equality before committing.

## Siemens PLC engineering

Openness exposes PLC software in inspected V15.1–V19 APIs. After attaching to the exact project, choose a `plcId` with `plc_list_targets`, then browse `plc_read_object` from empty path. Supported roots are `BlockGroup`, `TypeGroup`, `TagTableGroup`, and `ExternalSourceGroup`. Generic snapshots/compile reports now use `targetId`; HMI tool input names remain `hmiId`.

`plc_read_resource` returns native XML for one block, PLC data type or tag table. `plc_export_objects` exports a resource, group or all supported roots. These are version-specific SimaticML files. Protected blocks may expose only a public interface; failures remain explicit. They are not full project archives or evidence of runtime behavior.

Host/worker 0.7.3 adds [readable-source export/search and native cross-references](PLC-INSPECTION.md) through `tia_read_extension`. These read operations use explicit PLC/object paths, verified Offline state and bounded snapshots. V15.1–V19 expose source export; native cross-references are present in inspected V18/V19 APIs and absent in V15.1/V16/V17. The new host route requires a host upgrade/reconnect, after which worker formatting/search can reload independently. Native attached-project acceptance remains pending.

Host 0.7.4 supports flat Unified screen discovery on V16/V17, exact-identity loading of V21 product modules and the V21 project-graphics service path. V15.1–V19 API builds pass MCP/desktop checks; V20/V21 remain unvalidated without their SDKs. These native host changes require an upgrade/reconnect; see [API compatibility](API-COMPATIBILITY.md).

Host/worker 0.8.0 adds read-only [project/open global library inspection](LIBRARY-INSPECTION.md) in a fourth `project` worker. Discover `list_libraries` and `browse_library` with `tia_list_extensions`; call them through `tia_read_extension` using the exact attached-project path and returned IDs/paths. The 51-tool facade is unchanged. The new host route requires a host upgrade/reconnect; later formatting operations can reload on the same snapshot contract. The bundled [library skill](skills/inspect-libraries/SKILL.md) describes selection and paging. Native attached-project acceptance remains pending.

Host/worker 0.9.0 adds [project-text XLSX export/read/search](PROJECT-TEXTS.md) and active-language discovery through the same project worker. It retains the original native workbook/hash and reads bounded cell/formula-cache views without Excel, formula execution, workbook edits or native text import. The snapshot kinds require a host upgrade/reconnect; a 0.8.0 host rejects that worker while retaining its previous generation. See the bundled [project-text skill](skills/inspect-project-texts/SKILL.md). API builds/signatures pass V15.1–V19; native attached-project acceptance remains pending.

Comfort workers provide [layout previews](COMFORT-LAYOUT.md), [styled caption-run edits](COMFORT-SCREEN-TEXTS.md), [control/group copies and removals](COMFORT-SCREEN-ITEMS.md), [symbolic reference rebinding](COMFORT-REFERENCES.md) and [behavior copies with explicit owner links](COMFORT-BEHAVIORS.md). Copies preserve native metadata and use fresh IDs; removals guard retained references. Scripts and computed names are not analyzed.

Host/worker 0.10.4 fixes native V15.1 control `ObjectName` fields and `#ID` reference handling in cloning, inspection, deletion guards and host readback hashes. This requires a host upgrade/reconnect. The current Comfort bundle declares `classicReferences`; 0.10.0/0.10.3 reject it atomically and retain their previous worker. Subsequent workers using this contract can reload with the same 51-tool interface. Native import/compile/save acceptance of these newer operations remains pending; see [native XML compatibility](EXTENSIONS.md#classic-native-xml).

Comfort worker 0.10.5 adds zero-extent Line layout, control `TabIndex` inspection and native composition placement for behavior copies. It reloads on 0.10.4; the separate 0.10.5 host cleanup/readback-diagnostic improvements require host upgrade/reconnect. See [extensions](EXTENSIONS.md).

Use `plc_preview_object_edits`, `plc_preview_create_object`, or `plc_preview_delete_object` for advertised native operations. For program/interface/tag content, use `plc_preview_import` with files or `plc_preview_xml_import` with exact candidate XML. Imports require the target composition's fingerprint from `plc_read_object`, not a resource content hash. `plc_preview_import_bundle` imports types, tag tables, then blocks and supported user groups. Batch block/type imports permit temporarily missing references where the API supports that option; compile afterwards. Metadata-only objects are not reconstructed.

`plc_preview_generate_source` accepts the PLC-root fingerprint, unique source name, `SCL`/`STL`, and exact code. Source files use `.scl`/`.awl` and UTF-8 with a BOM. `allowBlockReplacement=true` acknowledges native replacement of blocks declared in the source; inspect all code and scope. Generation uses default groups, keeps the external source, rejects an existing root source name, and retains code/baseline/after-export evidence. CPU/API language support and native generation still need target-specific validation.

Apply with `plc_apply_object_plan`, then run `plc_compile`. Exports and mutations require a verified native `Offline` state; unknown, transitional, protected or online states are rejected. The server does not connect/disconnect the PLC or change CPU mode. Compile runs outside a transaction. Scalar readback allows known read-only PLC modification/compile metadata to update while requiring authored and other returned fields to match; all fields still participate in pre-apply stale checks.

PLC scope excludes downloading, online value writes, forcing/watch tables, hardware/technology-object configuration, password handling and safety acceptance. `tia_save_project` remains an explicit whole-project save covering HMI and PLC edits. Native import/generation, compile, save/reopen, and runtime behavior are separate results.

## Unified text-list API matrix

| API/profile | Available route | Verification boundary |
| --- | --- | --- |
| Installed V15.1 API | Classic XML text lists | Classic builds and offline regression tests pass; no Unified types in this API |
| Installed V19 API | Classic XML text lists; Unified screen discovery and property/caption edits | Native assembly members and read-only process discovery checked; no Unified text-list API is exposed |
| API with `HmiTextLists.Export/Import(DirectoryInfo, string)` and YAML v2 | Paired-YAML adapter for Unified user text lists | Source adapter and synthetic tests implemented; a matching newer API installation and native trial are still required |

Siemens documents the newer route using a configuration file plus a matching multilingual library. Omitting the latter loses message content, so this adapter requires both. Import recreates the exported user lists; it is not an in-place assignment to one entry. See [Siemens' export description](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/text-lists/export-of-text-lists) and [import description](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/text-lists/import-of-text-lists).

The YAML adapter preserves the configuration string and changes only existing translation scalar spans in the language library. It checks ordered language slots, text references, native list identities, and the full pair's semantic fingerprint. Shared text references, range entries, folded/literal block scalars, YAML anchors/tags, missing companions, unknown format versions and ambiguous selectors are rejected. System text lists are excluded. Native export omits lists containing formatted text; selecting such a list reports an unsupported export rather than synthesizing a replacement.

The installed V19 API cannot use that YAML route. It is never redirected through classic XML. No V21 binary or V21 native acceptance is claimed by this delivery. A modular API can expose additional types after target discovery; inspect both API and per-target capability results.

## Supported scope and version selection

- Existing plain-text SingleValue text-list entries and existing language resources, using the matching classic or Unified adapter.
- Existing supported Unified screen captions, tooltips, static layout, visibility/enabled fields and colors. Other languages and unselected properties are preserved.
- Advertised native object creation/deletion and supported scalar attributes for both families; classic XML and Unified resource imports can express additional content changes, including tag/script and exported list/screen content.
- No universal adapter for complex CLR values, library faceplate definitions, project text XLSX, running-device control, or native routes absent from the selected API. System text lists are excluded from the specialized translation adapter.
- Native HMI compile diagnostics and an explicit whole-project save operation.
- No downloading, device connection, or runtime control.

The build's API is fixed. Use `tia_list_versions` to discover compatible installed engineering versions. If multiple versions provide that API, call `tia_select_version` before other Openness operations. Restart the server to change engineering versions after loading Siemens assemblies. You can also pass `--tia-version=V19` or set `TIA_PORTAL_VERSION`.

V15.1–V20 use the classic assembly family. The V21 MCP build also requires its modular STEP 7 assembly for PLC support, alongside Base/WinCC references; that profile requires separate validation. Compilation and process discovery do not prove native import/generation/compile/save or runtime success.

## Build from this repository

Use a .NET 8 or newer SDK, .NET Framework 4.8 targeting support, and the installed Siemens API:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V15.1
dotnet test Tests/SiemensMCPServer.Tests.csproj -c Release
py -3 -X utf8 scripts/test_mcp_protocol.py Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe --native-read-only
powershell.exe -NoProfile -File scripts/test_native_contracts.ps1 -Version V15.1 -ServerDirectory Mcp/bin/Release/V15.1/net48
powershell.exe -NoProfile -File build/Build-Plugin.ps1 -TiaPortalVersion V15.1 -FrameworkWheel 'C:\packages\universal_codex_tools-0.17.0-py3-none-any.whl'
```

The protocol test checks all 51 engineering tools, required schemas, same-host extension reload, classic export conversion, and apply/compile/save write guards. It never attaches, imports, compiles, or saves a project. `--native-read-only` exercises real Siemens process enumeration, API-member checks, and unattached-object rejection. The native contract script checks installed classic/PLC signatures and production export dispatch without creating a TiaPortal instance. Unit tests cover XML/YAML, caption styling, object preview lifecycles, typed bounds, companion files, legacy category/folder conversion, hashes, DTD rejection and unsafe bundle paths. These checks do not simulate Siemens transactions or establish native acceptance.

The packaging script emits a fresh ZIP and SHA-256 file under `artifacts/`, with a payload manifest, source-file hash manifest, build identity, and dependency notices in the ZIP. It accepts the same `TiaPortalVersion`, `TiaPortalInstallVersion`, and `TiaPublicApiDir` choices as the desktop build. It rejects packages containing Siemens engineering DLLs. Verify a built ZIP and run its freshly extracted executable with `py -3 -X utf8 scripts/verify_plugin.py PATH-TO-ZIP --native-read-only`; the verifier also rejects tampered payloads, unexpected members, and path traversal controls.

## Native acceptance scope

See [validation status](NATIVE-ACCEPTANCE.md) for public native-testing scope and outstanding boundaries. Support must be verified for the selected operation, API and device; earlier checks do not certify every release or project. Unified text lists require an API exposing the paired-YAML methods and verification of both files. Builds, API inspection and offline tests do not establish native project or runtime acceptance. Keep project-specific evidence outside the public repository or under the ignored `.local/` directory.

An API application can reuse these tools through an MCP client and route OpenAI function calls to it. This package does not include a model-calling application or a public HTTP endpoint.
