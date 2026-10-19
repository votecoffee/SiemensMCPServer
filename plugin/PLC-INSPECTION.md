# PLC readable source and cross-references

Host/worker **0.7.3** adds three operations through the existing `tia_read_extension` tool: `read_source`, `find_in_source` and `cross_references`. The host captures native data from one explicitly selected PLC object; workers receive serialized snapshots. The server still exposes 51 MCP tools.

Upgrade/reconnect the host before using this new `plcRead` route. A pre-0.7.3 host rejects a worker advertising that route and keeps its previous generation. After upgrading, formatting/search implementations can reload through `tia_reload_extensions(module="plc")`. Plugin documentation and skills may require a new chat to refresh. See [extension updates](EXTENSIONS.md).

## Select the project and object

Attach to the exact process/project, choose a PLC with `plc_list_targets`, then browse `plc_read_object` from empty path. Follow returned paths to one block, type or tag. Requests require its exact `plcId` and object `path`; root/composition paths and hardware navigation are rejected. Both source and cross-reference capture require a verified native **Offline** state. These operations do not connect/disconnect a CPU or change its mode.

Use `tia_get_api_capabilities().plcInspection` to inspect API-member presence and `tia_list_extensions` for operation schemas. Member presence does not establish that the selected native object exposes the service or accepts the operation.

## Read native source

Call `tia_read_extension` with `module="plc"`, `operation="read_source"` and `argumentsJson` containing:

```json
{
  "plcId": "<exact ID from plc_list_targets>",
  "path": "<exact block/type path from plc_read_object>",
  "capture": { "withDependencies": false },
  "options": { "maxCharacters": 65536 }
}
```

Siemens `PlcExternalSourceSystemGroup.GenerateSource` writes a new local file. It exports existing engineering code; it does not import a source or generate blocks in the project. Supported native forms are SCL (`.scl`), STL/AWL (`.awl`), data blocks (`.db`) and PLC data types (`.udt`). LAD/FBD and other languages require the existing native XML export workflow. Native protection and device/language restrictions remain enforced by Siemens.

`withDependencies` is required and passed unchanged to native generation options. Set it explicitly to include native dependencies when that larger export is intended. A dependency in an unsupported language or protected object can cause native generation to fail; the server does not synthesize a translation or silently retry with a different scope.

The tool result contains `extension` and `data`. For PLC reads, `data.evidence` is host-generated metadata: target, native type, language, dependency choice, artifact directory, filename, byte count and SHA-256 of the exact native file. `data.data.text` is the worker's bounded text view; `truncated` and `totalCharacters` describe its coverage. `capturedUtc` records the capture time. Native files retain their original bytes/BOM; text decoding accepts UTF-8 with or without its BOM.

Native source inspection is limited to a nonempty file of at most **4 MiB**. Returned text defaults to 65,536 UTF-16 characters; `maxCharacters` permits 1–262,144 and never splits a surrogate pair. A truncated view does not replace the complete retained file. These local artifacts are private engineering data; keep them outside the public checkout or under `.local/`.

## Find text in source

Use `operation="find_in_source"` with the same target and capture object, replacing options with:

```json
{
  "query": "StationCounter",
  "caseSensitive": false,
  "maxMatches": 50
}
```

Search captures fresh native source and scans its complete decoded text within the file limit. It returns literal, non-overlapping matches with one-based line/UTF-16 column positions, bounded context, the total match count and an explicit truncation flag. It does not interpret code syntax, resolve symbols or distinguish executable code from comments. Queries are 1–256 characters on one line; `maxMatches` defaults to 50 and permits 1–200. Each context is at most 512 characters, with its start column and truncation flag. No regular expressions are executed.

## Read native cross-references

Use `operation="cross_references"` and:

```json
{
  "plcId": "<exact PLC ID>",
  "path": "<exact object path>",
  "capture": { "filter": "AllObjects", "maxRows": 200 },
  "options": {}
}
```

Filters are `AllObjects`, `ObjectsWithReferences`, `ObjectsWithoutReferences` and `UnusedObjects`. Row limits are explicit, from 1–1,000. The host requests the actual native `CrossReferenceService` on the selected PLC object; absent APIs/services produce an unsupported result, with no XML approximation.

Rows retain the native source/reference/location relationship and source ancestry, including sources without reference rows. Display fields include native names, addresses, types, devices/paths, reference locations, access modes and referenced-as names where available. Native display paths are not `plc_read_object` navigation paths. `UnderlyingObject` and `ReferencedAs` handles are never read or sent to workers. Textual access modes such as `Force` are metadata, not operations.

The reader bounds depth, visited nodes, rows and total returned text. Individual fields are capped at 4,096 characters; a separate 262,144-character payload budget can stop collection before the requested row limit. `truncated` reports omitted rows; `fieldsTruncated` reports clipped fields. Missing/failed native fields produce an error rather than an empty-reference claim. Native results are a view at read time, without a guarantee of compile freshness or coverage of dynamic references. Truncated results cannot prove that an object is unused.

## Compatibility and verification

| Inspected profile | Readable-source API | Native cross-reference API | Native project acceptance |
| --- | --- | --- | --- |
| V15.1 | Present, including dependency options | Absent | Pending |
| V19 | Present, including dependency options | Present; selected object must expose its service | Pending |
| Other profiles | Capability/member checks and a matching build required | Capability/member checks and a matching build required | Separate validation required |

Offline tests use synthetic snapshots and cover capture scope, preflight rejection, UTF-8/BOM hashes, Unicode search positions, nested references, metadata failures and bounds. The real net48 worker/MCP probe checks schemas, Unicode, first-frame BOM compatibility, unattached-read rejection, injected-snapshot rejection, same-host reload, startup diagnostics and rejection/rollback on an older host. It never attaches to TIA. See [validation status](NATIVE-ACCEPTANCE.md).

Primary references: [Siemens native source generation](https://docs.tia.siemens.cloud/r/es-es/v20/tia-portal-openness-api-para-la-automatizacion-de-flujos-de-trabajo-de-ingenieria/tia-portal-openness-api/funciones-para-el-acceso-a-los-datos-de-un-dispositivo-plc/bloques/generar-fuente-a-partir-de-bloque), [Siemens STEP 7 cross-reference model](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/getting-cross-references-for-step7). The V15.1/V19 matrix above comes from installed assembly metadata, not an assumption that V20 documentation establishes earlier-version compatibility.
