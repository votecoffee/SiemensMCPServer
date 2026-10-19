# Multilingual project texts

Host/worker **0.12.0** extracts and previews updates to existing comments, descriptions, captions and other text values exposed by Siemens' native project-text export. Records retain categories, object paths, source/target cultures and exact selection fingerprints. The shared engine powers MCP and a file command. The MCP interface remains 51 tools.

The new `projectTexts` preview/import route requires a **0.12.0 host upgrade and MCP reconnect once**. Older hosts reject the project worker and retain their previous generation. Subsequent translation logic can reload on this contract. See [extension updates](EXTENSIONS.md).

## Discover active languages

Attach to the exact process/project and read the attached path from `tia_get_status`. Call `tia_read_extension` with `module="project"`, `operation="list_project_languages"` and:

```json
{
  "expectedProjectPath": "<exact attached-project path>",
  "capture": {},
  "options": {}
}
```

`data.data.languages` lists already-active culture names, English/native display names and reference/editing flags. No language is activated or selected automatically. The native list is limited to 256 active languages; failed/ambiguous reads produce an error.

## Export and read project texts

Use `operation="read_project_texts"` with two distinct active culture names from that result:

```json
{
  "expectedProjectPath": "<exact attached-project path>",
  "capture": { "sourceLanguage": "en-US", "targetLanguage": "de-DE" },
  "options": { "sheet": "", "offset": 0, "maxCells": 100, "maxCharacters": 1024 }
}
```

The host checks the exact project and both active languages under native exclusive access, then calls Siemens `Project.ExportProjectTexts` with that source/target pair. It writes a new `project-texts.xlsx` under a unique artifact directory. Output paths are host-owned; caller destination overrides are rejected. This requires attachment but not `--allow-writes`, since it does not edit the engineering project.

The response contains `extension` and `data`. `data.evidence.workbook` records source/target languages, the original file path components, byte count and SHA-256 of the exact native XLSX. The worker receives serialized bytes and verifies their length/hash. The native workbook remains unchanged; it is not rewritten through a spreadsheet library.

`data.data.sheets` lists sheet names, visibility and explicit-cell counts. `data.data.cells` returns worksheet/address/type, text, formula expression when present, formula presence and clipping flags. Empty `sheet` includes all sheets; otherwise use an exact returned name. Hidden worksheets are included. Cells can contain headers, native identifiers or other metadata; the reader does not infer translation columns or assert that a value can be edited/imported.

Shared strings, inline strings, rich-text runs and OOXML character escapes are decoded. Phonetic annotations are excluded from displayed text. Numeric/date/boolean/error values retain their raw stored values; styles, date serials and number formats are not rendered. Formula expressions are displayed as text and cached values are used for the cell text. No formula evaluation, recalculation, macro execution, Excel launch or external-link access occurs. Missing formula caches remain empty.

Each call performs a **fresh native export**, including paging and search calls. `nextOffset` continues in the selected sheets' current XML cell order; this is not a stable snapshot across project changes. Retain the complete XLSX/hash when a fixed reference is needed. `hasMore`, `payloadLimited`, `textTruncated` and `formulaTruncated` describe incomplete views. No blank/omitted view proves that a project text does not exist.

## Find literal text

Use `operation="find_in_project_texts"` with the same project/capture and replace options with:

```json
{
  "sheet": "",
  "query": "Motor",
  "caseSensitive": false,
  "maxMatches": 50
}
```

Search scans decoded cells and formula caches within the workbook limits, including text beyond the returned read-page clipping limit. It searches headers/metadata as well as text; formula expressions are excluded. Matching is literal, non-overlapping and uses ordinal case comparison. Results identify the sheet, cell address, one-based UTF-16 character position, bounded context and context start/clipping flags. `totalMatches` counts matches in the parsed workbook; `truncated` reports omitted result entries. These results do not resolve engineering references or distinguish alarms from other text categories.

## Limits and malformed data

| Limit | Value |
| --- | --- |
| Native XLSX file | Nonempty, unlinked, at most 8 MiB |
| ZIP contents | At most 512 entries, 16 MiB per file and 64 MiB total declared uncompressed file bytes |
| Workbook metadata | At most 128 worksheets, 512 relationships per inspected relationships part and 100,000 shared strings |
| Cell inspection | At most 200,000 explicit cells total and 200,000 explicit rows per sheet |
| Decoded text | 65,536 UTF-16 characters per value; separate 16,777,216-character budgets for shared-string storage and expanded cell/formula values, counting repeated cell uses |
| Read page | Offset 0–200,000; 1–200 cells; 1–4,096 returned characters per value |
| Read-page text budget | 262,144 characters across returned cell identity/text/formula fields; `payloadLimited` and `nextOffset` report continuation |
| Search | Single-line query of 1–256 characters; 1–200 matches; contexts at most 512 characters |

ZIP traversal/case collisions, missing/external worksheet relationships, ambiguous parts/cells, invalid string indices, out-of-range coordinates, invalid surrogate text, DTDs and exceeded bounds produce errors. Internal relationships are resolved within the ZIP; files are never extracted for inspection. Package/workbook external relationships may be counted as ignored; this is not a complete external-link inventory of every worksheet part. External relationships are never fetched.

This is a bounded OOXML cell view, not a complete spreadsheet or Siemens workbook validator. If native export or inspection fails, the error retains the artifact location and, when available, the original file hash. Native files may contain private engineering data; retain them outside the public tree or under ignored `.local/`.

## Extract translation records

Call `tia_read_extension` with `module="project"`, `operation="read_translation_units"` and the same `expectedProjectPath`/`capture` used above. Use `options` such as:

```json
{ "sheet": "User Texts", "query": "Motor", "missingOnly": false, "offset": 0, "maxUnits": 50, "maxCharacters": 4096 }
```

Optional `category` filters an exact returned category. `query` searches source/target text, category and path without case sensitivity. Empty `sheet` includes supported worksheets. `missingOnly=true` selects empty targets; it does not judge translation quality or whether an empty source requires translation.

`data.data` contains `workbookFingerprint`, `units`, total/matched/editable/missing counts, a bounded category summary, `nextOffset` and `hasMore`. Units expose category, object path, row, cultures, values, clipping/editability flags, plus `targetSelection`/`sourceSelection` where eligible. Clipped values have no editable selection. Each read performs a fresh export; use the retained workbook and file command for a stable, complete extraction.

## Preview and apply updates

Copy an exact returned selection. Call `tia_preview_extension` with `module="project"`, `operation="edit_project_texts"`, `targetId="project"`, `path=""`, `expectedFingerprint=<workbookFingerprint>` and `argumentsJson` containing:

```json
{
  "expectedProjectPath": "<exact attached-project path>",
  "capture": { "sourceLanguage": "en-US", "targetLanguage": "de-DE" },
  "updateSourceLanguage": false,
  "edits": [{
    "selection": {
      "sheet": "User Texts", "row": 42, "culture": "de-DE",
      "expectedUnitFingerprint": "<returned unit fingerprint>",
      "expectedText": "<exact existing target text>"
    },
    "newText": "<reviewed translation without rich markup>"
  }]
}
```

The preview checks a fresh full workbook fingerprint, exact row context and old text. It retains `before.xlsx`, `candidate.xlsx` and `preview.json` without editing TIA. Source-language selections require explicit `updateSourceLanguage=true`. Use separate captures for additional target cultures. Existing empty target slots can be filled; rows, text identities and language slots cannot be added or deleted here.

Review the old/new values, cultures and import scope. Apply an authorized plan once with `tia_apply_extension_plan`, its plan ID and candidate hash. Apply requires `--allow-writes`, the same worker generation, an unexpired plan (30 minutes by default; see [Safeguards](SAFEGUARDS.md)) and unchanged candidate bytes. Under native exclusive access it checks the original project, active languages, Offline state of every PLC in the project, and a fresh full text snapshot. It imports with Siemens `ImportProjectTexts`, records native status/log location, re-exports the pair, and commits its transaction only if the complete decoded cell fingerprint matches the candidate. Failed attempts retain evidence and consume the plan; inspect the project before retrying.

Import processes the native workbook, including unchanged rows. This is not an arbitrary edited-workbook upload endpoint. No automatic translation, language activation, compile, save, download or runtime operation occurs. Compile affected targets and save separately within the user's scope.

## Supported translation schema

The adapter recognizes native `TIA_Version=1.3` workbooks with exact headers `Category`, `ViewPath`, `Internal ID`, `Substitutions`, `<sourceCulture>*`, `<targetCulture>` and optional `ref="N"` columns. Unknown layouts remain available through raw inspection; translation roles are never guessed.

Only existing plain-string cells in `User Texts` are editable. System/unknown sheets, styled string runs, formulas, detected external package/workbook relationships, nonempty substitution/reference fields, possible rich/dynamic markup containing `<`, and oversized values are inspection-only. New rich/dynamic markup also requires a specialized adapter. Use the existing [screen-text](COMFORT-SCREEN-TEXTS.md), text-list or capability-checked Unified workflows for their supported structured text.

Multiline Unicode and literal OOXML escape-like sequences are preserved. Formula-leading text is stored as a string. Shared strings are appended for selected cells, so one shared value update cannot change another cell. Untouched ZIP parts retain their bytes; changed worksheet/string parts retain unrelated XML and cell styles. Native file hashes and decoded text fingerprints are separate evidence.

Translation reads return 1–100 units, with 1–32,767 characters per source/target and a 258,048-character serialized row budget. Category summaries are capped at 128 categories. Previews accept 1–200 exact selections and at most 32,767 UTF-16 characters per new value.

## File extraction and candidate preparation

After building the worker, run:

```powershell
powershell.exe -NoProfile -File build/Edit-ProjectTexts.ps1 -RequestPath 'C:\Engineering\texts-request.json' -OutputDirectory 'C:\Engineering\texts-report'
```

Extracted plugins include the command under `scripts/`. `-WorkerDirectory` selects an explicit built worker. An extraction request is:

```json
{
  "workbookPath": "C:\\Engineering\\project-texts.xlsx",
  "capture": { "sourceLanguage": "en-US", "targetLanguage": "de-DE" }
}
```

Outputs are `translation-units.json`, `translation-units.csv`, `summary.json` and a completion `manifest.json` with all report hashes. To prepare a candidate, add `expectedWorkbookFingerprint` from the summary, optional `updateSourceLanguage`, and `edits` using the same selections/schema as the MCP preview. This also writes `before.xlsx`, `candidate.xlsx` and `changes.json`. It performs no native import. The input workbook/request are rehashed and preserved; choose a new output directory each run. A failed run may leave partial output without a completion manifest. Requests are limited to 2 MiB. CSV formula-leading fields are escaped for spreadsheet viewing; JSON/XLSX retain exact text.

## Import and validation boundaries

Native import is implemented through reviewed project-text plans. Siemens ties exports to the originating project and existing text identities; Save As or object replacement can invalidate import. Do not pass a workbook to unrelated resource-import tools. Coverage comes from the actual categories exported by the selected API/project; this does not claim every property, protected block, PLC language construct, faceplate or Unified object is represented.

V15.1 native extraction has been exercised. Live import/rollback trials and Unified/PLC category acceptance remain pending. Static tests cover exact edits, preserved source/metadata, hashes, Unicode, blank targets, source opt-in, protected fields, reload and older-host rejection. Build/protocol checks are separate from native import, rendered HMI appearance or runtime acceptance; V20/V21 SDK validation remains pending. No new desktop GUI workflow is included.

Primary references: [Siemens project-text export](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/import/export-of-project-data/project-texts/export-of-project-texts), [Siemens import restrictions](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/import/export-of-project-data/project-texts/import-of-project-texts), [Microsoft cell-value model](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-retrieve-the-values-of-cells-in-a-spreadsheet) and [shared strings](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/working-with-the-shared-string-table). Earlier-version observations were checked against installed API metadata separately.
