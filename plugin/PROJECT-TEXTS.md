# Project-text XLSX export and inspection

Host/worker **0.9.0** adds three `project` operations through `tia_read_extension`: `list_project_languages`, `read_project_texts` and `find_in_project_texts`. This addresses native text export/inspection for Comfort/classic V15.1 and matching APIs. It is a project-wide text workflow; it does not create/edit alarm definitions, change workbook cells or import translations. The MCP interface remains 51 tools.

The new `projectLanguages`/`projectTexts` snapshot kinds require a host upgrade/reconnect. A 0.8.0 host rejects the newer project worker and retains its existing generation. After upgrading, operations that format/search the same native workbook snapshot may reload independently. See [extension updates](EXTENSIONS.md).

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
| Decoded text | 65,536 UTF-16 characters per value; 16,777,216 characters across shared-string storage, cell values and formula expressions, counting repeated cell uses |
| Read page | Offset 0–200,000; 1–200 cells; 1–4,096 returned characters per value |
| Read-page text budget | 262,144 characters across returned cell identity/text/formula fields; `payloadLimited` and `nextOffset` report continuation |
| Search | Single-line query of 1–256 characters; 1–200 matches; contexts at most 512 characters |

ZIP traversal/case collisions, missing/external worksheet relationships, ambiguous parts/cells, invalid string indices, out-of-range coordinates, invalid surrogate text, DTDs and exceeded bounds produce errors. Internal relationships are resolved within the ZIP; files are never extracted for inspection. Package/workbook external relationships may be counted as ignored; this is not a complete external-link inventory of every worksheet part. External relationships are never fetched.

This is a bounded OOXML cell view, not a complete spreadsheet or Siemens workbook validator. If native export or inspection fails, the error retains the artifact location and, when available, the original file hash. Native files may contain private engineering data; retain them outside the public tree or under ignored `.local/`.

## Import and validation boundaries

Native **import is not implemented**, and `tia_get_api_capabilities().projectTexts.importImplemented` reports false. Siemens ties project-text exports to the originating project and existing text identities; Save As or object replacement can invalidate import. A future reviewed import workflow needs source/target protection, original-project binding, native results and independent readback. Do not pass an edited workbook to unrelated resource-import tools.

V15.1–V19 builds, installed export/language signatures and unattached protocol checks pass; V16–V18 used V19 compatibility APIs. Synthetic workbook tests and the real net48 worker/MCP probe cover Unicode, hashes, paging/search, malformed data, guards, reload and older-host rejection. These do not establish native export from an attached project or coverage of every HMI/PLC text category. V20/V21 SDK and attached-project acceptance remain pending. No new desktop GUI workflow is included.

Primary references: [Siemens project-text export](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/import/export-of-project-data/project-texts/export-of-project-texts), [Siemens import restrictions](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/import/export-of-project-data/project-texts/import-of-project-texts), [Microsoft cell-value model](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-retrieve-the-values-of-cells-in-a-spreadsheet) and [shared strings](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/working-with-the-shared-string-table). Earlier-version observations were checked against installed API metadata separately.
