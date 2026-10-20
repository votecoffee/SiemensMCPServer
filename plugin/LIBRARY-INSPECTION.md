# Project and global library inspection

Host/worker **0.8.0** adds a `project` extension module with `list_libraries` and `browse_library`, through the existing `tia_read_extension` tool. The server retains 51 MCP tools. The host owns native handles and reads documented library metadata; the worker receives serialized pages.

Upgrade/reconnect the host before installing the new project module. Older hosts do not support its `projectRead` route. Existing Comfort/Unified/PLC routes remain compatible; the 0.8.0 PLC worker was exercised on a 0.7.4 host. After the upgrade, project-worker updates using the same capture contract can reload with `tia_reload_extensions(module="project")`. See [extension updates](EXTENSIONS.md).

## Select the attached project

Attach using the exact process/project path and inspect `tia_get_status`. Every library request requires that exact absolute `expectedProjectPath`. The host rechecks the attachment and expected path under native exclusive access before capture. No PLC target or online service is selected: library metadata inspection does not connect to controllers or infer their state.

Use `tia_get_api_capabilities().projectLibraries` for member presence and `tia_list_extensions` for the current schemas. An advertised API is not proof of native acceptance for every project or library format.

## List available libraries

Call `tia_read_extension` with `module="project"`, `operation="list_libraries"` and `argumentsJson`:

```json
{
  "expectedProjectPath": "<exact attached-project path>",
  "capture": { "offset": 0, "maxRows": 100 },
  "options": {}
}
```

The result has `extension` and `data`. `data.data.rows` contains the project library (`libraryId="project"`) followed by already-open global libraries. Each global ID is `global:` plus SHA-256 of its normalized, case-folded native file path. This is an identity for selection, not a content fingerprint. Its native path, author, read-only and modified flags are metadata fields. The tool does not open files or open/upgrade/close libraries. A closed library must be opened separately before it can be selected.

## Browse a library

Use `operation="browse_library"`, with the returned ID and an empty path:

```json
{
  "expectedProjectPath": "<exact attached-project path>",
  "capture": {
    "libraryId": "project",
    "path": "",
    "offset": 0,
    "maxRows": 100
  },
  "options": {}
}
```

The library root returns `types` and `masterCopies`. Follow exact returned paths; names are escaped as URI path components. `selected` describes the selected entry and `rows` contains one page of its children.

| Entry | Returned metadata / children |
| --- | --- |
| Type folder | Child folders and types |
| Type | GUID, author, API-supported namespace/minimum device version/status/update flag; child versions |
| Version | GUID, version number, author, modified date, state and API-supported default-version flag |
| Master-copy folder | Child folders and master copies |
| Master copy | Author, creation date and child content descriptions |
| Content description | Content name and CLR type name |

Version/content rows are leaves. `canBrowse=false` indicates that there are no further supported children; reading their exact path still returns their selected metadata. Human-readable root labels are generated; other row names come from the native object. Missing API members are listed in `unsupportedFields`. Failed native getters produce errors, not blank success results.

Only documented scalar metadata is read. Complex comments, `Parent`, `TypeObject`, dependencies/dependents and engineering-instance handles are excluded. This does not extract a faceplate definition, export library source, instantiate a master copy or assess dependency completeness. Library creation, updates, release/default-version changes and save are separate unsupported operations.

## Paging and bounds

`offset` defaults to 0 and permits 0–10,000; `maxRows` defaults to 100 and permits 1–200. Continue with `nextOffset` when `hasMore=true`. If the offset limit is reached, `offsetLimitReached=true` and `nextOffset=null` report that remaining entries cannot be retrieved by this endpoint. Browse narrower folders where possible.

Each capture limits metadata visits to 20,000 and selected-entry/row metadata text to 262,144 UTF-16 characters. `payloadLimited=true` indicates that another row would exceed this budget. Scalar fields are capped at 2,048 characters without splitting surrogate pairs; clipped field names appear in `truncatedFields`. Identity names and selection paths are validated rather than shortened; folder nesting is limited to 32 levels. Oversized scans, unsupported identities and ambiguous matches fail explicitly.

Pages use native composition order (folders before types/master copies) and are observations at read time. Repeated paging does not hold a snapshot across requests; edits or library closure can change ordering or invalidate a path. Rediscover after changes. `hasMore=false` describes this bounded composition read, not dependency coverage or a complete reusable engineering design. Truncated fields/pages cannot prove that a type or dependency is absent or unused.

The host's `data.evidence` records the exact project/library/path, offset, continuation and limit flags. It also returns `capturedUtc`, `projectSaved=false`, `librariesOpened=false` and `libraryInstancesCreated=false`. These describe this operation's actions; concurrent changes by other editors are outside that claim. No library files are exported by these reads.

## Compatibility and validation

V15.1–V19 installed API metadata exposes the required library compositions and base fields. V16–V18 checks used V19 compatibility APIs, not native V16–V18 installations. Newer optional fields are reported individually; for example, V15.1 lacks the type namespace/minimum-device-version/status/update fields and the version default flag used by the V19 API. V20/V21 SDK validation and attached-project library acceptance remain pending. See [API compatibility](API-COMPATIBILITY.md) and [native validation status](NATIVE-ACCEPTANCE.md).

Synthetic tests cover nested paths, GUIDs/version/state metadata, master-copy contents, missing/duplicate identities, strict requests, excluded native handles, metadata failures, paging, Unicode and scan/text/offset limits. The real worker/MCP probe checks route schemas, BOM/Unicode transport, unattached-read rejection, client snapshot injection, same-host reload, rejected startup and compatibility of an existing worker on the previous host. It never attaches to a project.

Primary model reference: [Siemens TIA Portal Openness manual](https://cache.industry.siemens.com/dl/files/163/109477163/att_926042/v1/TIAPortalOpennessenUS_en-US.pdf), library object model and library functions. The version observations above were checked against installed assemblies independently of that manual.
