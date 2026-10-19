# HMI references, controller declarations and impact

Comfort worker 0.11.0 adds four read-only operations through the existing `local` route. They reload on host 0.10.4 or newer without new MCP tools. A companion command handles complete export folders and large controller files. Both routes share the same analysis engine and perform no native edits or controller communication.

## Sources

Export the exact HMI with `hmi_export_objects` and `path=""`. Retain its index and native files. Analysis covers API-visible classic screens, templates, popups, controls, events, animations and tags. Subtree exports, failures, unsupported resources, code and expressions remain visible as coverage limits. Siemens may not export every engineering object.

| Format | Imported declarations | Limits |
| --- | --- | --- |
| `rockwell-l5x` | Controller/program tags, types present in `DataTypes`, members, arrays, aliases and external access. | Logic/values are ignored. AOI/module/predefined structures absent from exported declarations stay unresolved. |
| `plcopen-xml` | TC6 2.0/2.01 types, structures, arrays, configuration/resource globals, POU interfaces and declared addresses. | No POU instance expansion or enum/subrange/pointer/generic/vendor-type adapter. |

The importer retains identity, revision where present, source hashes and partial-export limitations. See the primary [Rockwell reference](https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/1756-rm084_-en-p.pdf) and [PLCopen XML specification](https://www.plcopen.org/standards/xml-echange/).

An explicit mapping associates an HMI connection with a metadata document. `expectedControllerName` checks imported identity. Captured native connection snapshots verify that the name exists in the export and retain available attributes. This does not prove a network partner: `nativePartnerVerified` and `nativeBindingVerified` remain false.

## Complete export-folder command

Supply original controller files rather than flattened symbol lists. Create a local request:

```json
{
  "exportDirectory": "C:\\Engineering\\HmiExport",
  "controllerFiles": [
    {
      "id": "line-plc",
      "format": "rockwell-l5x",
      "path": "C:\\Engineering\\Controller.L5X"
    }
  ],
  "connectionMappings": [
    {
      "connection": "PLC_Connection",
      "controllerId": "line-plc",
      "expectedControllerName": "DemoPLC",
      "addressSyntax": "wincc-rockwell"
    }
  ],
  "changes": [
    {
      "kind": "controllerSymbol",
      "controllerId": "line-plc",
      "name": "Program:Main.Axis"
    }
  ]
}
```

Use exact connection/controller names. Omit `hmiId` on a mapping to apply it to every supplied HMI, or select one target. Target-specific mappings take precedence.

From the repository:

```powershell
dotnet build Extensions/SiemensMCPServer.ExtensionWorker.csproj -c Release
powershell.exe -NoProfile -File build/Analyze-HmiReferences.ps1 -RequestPath 'C:\Engineering\analysis-request.json' -OutputDirectory 'C:\Engineering\analysis-report'
```

Extracted plugins include `scripts/Analyze-HmiReferences.ps1`, which selects the active Comfort worker. `-WorkerDirectory` selects an explicit built worker. Analyzing existing files needs no TIA attachment or native write permission.

The output directory must be new and outside the source export. Inputs and existing reports are preserved. Outputs:

- `summary.json`: counts, statuses, snapshot hash, engine version and coverage.
- `sources.json`, `controllers.json`: byte-hashed inputs, resource hashes and normalized declarations.
- `connections.json`: captured connection metadata and mappings.
- `tags.json`, `bindings.json`, `references.json`: declarations, validation, exact resource/control/owner paths and chains.
- `references.csv`: a flat searchable projection.
- `diagnostics.json`: opaque regions, failures and unsupported resources.
- `impact.json`, `impact.csv`: affected references/objects, certainty and proposed binding checks, when requested.
- `manifest.json`: lengths and SHA-256 hashes for the completed report set.

JSON retains exact text. CSV prefixes formula-leading cells with an apostrophe for spreadsheet viewing. Sources are checked again before report creation. Failed runs can leave incomplete output without a completion manifest; select a fresh output directory for another run.

## Address and validation rules

`addressSyntax="canonical"` is the default. Rockwell program paths use `Program:Main.Tag.Member`; `defaultScope` explicitly selects a scope for unqualified names.

`addressSyntax="wincc-rockwell"` supports `Main:Tag.Member` when that program/tag declaration exists. Literal-tag collisions remain ambiguous. See Siemens' [program tag addressing](https://docs.tia.siemens.cloud/r/en-us/v21/communicating-with-plcs-basic-panels-panels-comfort-panels-rt-advanced-rt-professional/communication-with-other-plcs-basic-panels-panels-comfort-panels-rt-advanced-rt-professional/communication-drivers-basic-panels-panels-comfort-panels-rt-advanced-rt-professional/allen-bradley-basic-panels-panels-comfort-panels-rt-advanced-rt-professional/allen-bradley-ethernet/ip-basic-panels-panels-comfort-panels-rt-advanced-rt-professional/performance-features-of-communication-basic-panels-panels-comfort-panels-rt-advanced-rt-professional/addressing-in-the-compactlogix-controllogix-cpu-type-basic-panels-panels-comfort-panels-rt-advanced-rt-professional/addressing-basic-panels-panels-comfort-panels-rt-advanced-rt-professional).

Rockwell paths can appear in `LogicalAddress` while `AddressAccessMode` says `Absolute`. Both are retained; the explicit syntax determines interpretation. Simultaneously populated `ControllerTag` and logical addresses are reported as conflicting.

Checks cover scopes, members, atomic types, array ranks/bounds, literal element/bit selectors, aliases/cycles, missing types, hidden members and external access. Names are compared case-insensitively within explicit HMI/controller scopes. Short HMI names resolve only when unique; qualified names are not matched by suffix. XML IDs stay resource-local.

Array dimensions come from native type descriptors. `Length` is retained and is not assumed to mean element count: INT arrays can report byte lengths. No numeric coercion, array flattening, string-capacity/encoding or driver-conversion inference is performed. Computed/indirect addresses stay unresolved. Read-only access is flagged for consumer review; read/write behavior is not inferred.

## MCP operations

Call `tia_read_extension(module="comfort", operation=..., argumentsJson=...)`.

| Operation | Request | Result |
| --- | --- | --- |
| `import_controller_declarations` | `controllerDocuments`: `id`, `format`, raw XML `content`; optional `section` (`symbols`/`types`/`members`) and paging. | Source/controller summaries and normalized declarations. |
| `analyze_hmi_references` | `analysis`; optional `view` (`references`/`bindings`/`tags`/`diagnostics`), `offset`, `maxItems`. | Summary/hash and graph rows. |
| `validate_hmi_bindings` | `analysis` and optional paging. | Per-tag validation and unresolved reasons. |
| `reference_impact` | `analysis`, `expectedAnalysisSha256`, `changes` and optional paging. | Affected references/objects, certainty and proposed checks. |

`analysis` contains `resources` (`hmiId`, exact `path`, raw native `xml`), optional `controllerDocuments`, `connectionMappings`, `connectionDefinitions`/`connectionMetadataAvailable`, `exportReportedComplete`, `exportFailures` and `unsupportedResources`. The file command constructs this input automatically.

Requests are stateless. Keep the same snapshots for paging and follow `nextOffset` while `hasMore` is true. `analysisSha256` includes engine version/build identity, resources, documents, mappings and coverage. Stale impact fingerprints are rejected. Worker operations accept serialized content only; filesystem-path arguments are rejected.

## Proposed changes

Each proposal requires `kind` and `name`:

- `tag` selects an exact HMI tag; `newName` exposes collisions and references requiring review.
- `connection` selects an HMI connection; `newControllerId` checks tags against another imported controller.
- `binding` selects an HMI tag; `newConnection` and/or `newSymbol` validate the proposed target.
- `controllerSymbol` selects a declared/raw controller path with explicit `controllerId`, following parents, arrays and alias/base dependencies.

Use `hmiId` to scope selectors. Proposals are assessed independently against the baseline. Whole-array consumers are included for element changes; unrelated indexed elements are excluded where exact paths are known. Unresolved named dependencies are possible impacts. Code, expressions, computed names and unexported objects may add other impacts. Screen links do not establish runtime reachability.

Reports do not authorize or apply edits. Use existing preview/apply tools and separate compile/save checks for native changes.

## Limits and verification

The engine bounds resource counts/depth, declarations, paths, tags and references. Worker requests are limited to 24 MiB of serialized characters and 1–200 rows per page within an escaped-JSON budget. Oversized rows direct callers to the file command. File input supports larger XML, validates indexed relative paths, rejects linked export paths and retains hashes.

Unit tests cover formats, scopes, arrays, aliases, ambiguity, preservation, fingerprints, impact boundaries and file handling. `scripts/test_hmi_reference_analysis.py` checks real net48 workers, file output and compatible-host reload. Fresh V15.1 export/L5X analysis remains distinct from native binding/runtime acceptance; see [validation status](NATIVE-ACCEPTANCE.md).
