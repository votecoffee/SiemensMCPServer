# Classic TextLists validation and evidence

Host 0.18.0 and Comfort worker 0.15.0 reuse the existing text-list, transfer and reference APIs. The native TextLists metadata profile is **V15.1 only**. Other targets are explicitly unsupported by that profile; Unified paired YAML has a separate adapter. These checks cover supplied engineering exports, not PLC message generation, naming standards or runtime behavior.

## Find the operations

In the standalone gateway, search for `text list validation`, `XML encoding` or `classic export bundle`, then describe the returned backend tool. Acquire `siemens-engineering` only when invoking backend operations. `tia_list_extensions` advertises the Comfort operation schemas. Call `tia_read_extension` with `module="comfort"`, the operation name, and a JSON string in `argumentsJson`. Local validation needs no TIA attachment and performs no native engineering operation.

| Operation | Input and result |
| --- | --- |
| `validate_native_text_list` | Original `xmlBytesBase64`, `targetVersion="V15.1"`, optional exact `expectedSha256` and `baselineBytesBase64`. Returns declaration/BOM, strict decoding, bounded XML, identity, keys/cultures, native IDs, LinkList ordering and native text metadata checks. |
| `validate_text_list_delta` | `beforeXml`, `candidateXml`, `expectedBeforeFingerprint`, reviewed `changes`/`edits`, `activeCultures`, optional `expectedCandidateSha256` and `readbackXml`. Computes the complete intended list using the existing editor and compares semantic fingerprints, including all untouched defaults, keys, cultures and metadata. |
| `validate_text_list_bindings` | Screen `xml`, exact `knownTextLists`, optional `expectedBindings=[{xPath,expectedName}]` and `unchangedScreenAfterXml`. Reuses `inspect_references`; returns exact link/owner/control paths and a complete screen preservation comparison. |
| `validate_text_list_workflow` | Exact `projectPath`, `processId`, `hmiId`, `lists=[{delta,applyReceipt?}]`, optional `compileReceipt`, `baselineCompileReceipt` and `saveReceipt`. Validates supplied receipt consistency, native readback snapshot bytes and complete delta preservation. |

`delta` uses the same fields as `validate_text_list_delta`. Native receipt objects are the unmodified results from `hmi_apply_text_edits`, `hmi_compile` and `tia_save_project`. Include the actual `native-after.xml` string as `delta.readbackXml`; its declared-encoding snapshot hash must match the apply receipt. Keep the exact native export bytes and their separate staging receipt too. These inputs can be supplied or forged by a caller: consistency validation does not authenticate provenance, execution order or native effects. Retain original artifacts, timestamps, request/context identity and logs independently.

Reports identify the profile, rule-set/tool versions, applicability, location and `passed`, `failed`, `not-run` or `unsupported` checks. A baseline can classify an identical failure as `inherited-still-blocking`; it never exempts a required failure. String hashes are UTF-8 snapshot hashes unless explicitly labeled as original byte hashes. Semantic hashes exclude documented volatile native IDs/export headers. A passing offline report is not native acceptance; missing import/readback/compile/save stages remain not-run or not-established. Runtime remains not-run.

Rule set 1.1.0 distinguishes required checks, optional supplied evidence and non-applicable input conditions. Folder input reports `bundle.archive-integrity` as `not-run` with `applicability="not-applicable-folder-input"`; no source ZIP exists to inspect. The generated payload ZIP must still pass `bundle.published-archive`. Missing optional manifests/evidence are labeled accordingly. A skipped check explicitly marked required blocks publication.

## Review entry and translation changes

Read the exact native list and its fingerprint. Use existing `hmi_preview_text_edits` for value changes and `hmi_preview_classic_text_list_changes` for ordinary numeric entry additions/removals. Changes carry their existing template, expected-text or entry-fingerprint guards. Preview automatically compares the worker candidate with the exact requested transformation, preserving all unselected content. A preview never authorizes apply.

For a local delta check, send the actual before/candidate XML, the source fingerprint and the same `changes`/`edits`. Set `blankOtherNewTranslations=true` and an explicit `authoredCulture`, such as `en-US`, only when that is the desired policy. Every new other-language value must then be explicitly empty. Existing translations remain covered by exact preservation. No translations are invented. V15.1 cannot round-trip removal of an active translation slot: clear its text instead. Default, range, formatted and unsupported entry shapes retain the existing editor restrictions.

Inspect each relevant screen with `inspect_references`, `linkType="TextList"`, paging until complete. Native links look like:

```xml
<LinkList>
  <TextList TargetID="@OpenLink"><Name>StatusChoices_1</Name></TextList>
</LinkList>
```

Use that literal name and owning control/path. A suffix does not establish which list is used. The binding validator checks an explicit list inventory, expected exact link paths and optional whole-screen preservation; it does not evaluate scripts/computed names or resolve native-ID targets. No binding editor for TextList roles is added.

## Prepare an immutable import bundle

Call `hmi_prepare_classic_export_bundle` with:

```json
{
  "sourceDirectory": "C:\\exports\\panel.zip",
  "categories": ["TextLists"],
  "folderNames": [],
  "targetVersion": "V15.1",
  "selectedTextLists": ["StatusChoices", "StatusChoices_1"],
  "repairXmlDeclaration": false
}
```

Omit `selectedTextLists` to select the entire TextLists category. Input may be a folder, its TextLists directory, or a ZIP containing one unambiguous nested TextLists root. Selection uses internal native names, never sanitized filenames. Multiple roots, duplicate identities, failed/ambiguous supplied category summaries, malformed/truncated XML and missing required companions block publication. Source inventory and archive hashes are checked again before publication. Inputs remain unchanged.

Host 0.18.3 verifies source ZIP headers, paths, file lengths, CRCs and SHA-256 inventory in streams, then reads the selected lists and their required companions directly. It never extracts unrelated screens/graphics or rebuilds the source archive's directory hierarchy. Full-archive integrity and any supplied source manifest still apply, including unselected files. `sourceAccess` records the verification counts and zero source archive members extracted. This avoids path-sensitive filesystem failures while preparing selected categories, without changing shared staging settings or Windows path policy. Native TIA import acceptance is a separate operation.

For the native error `System does not support 'utf8' encoding`, explicitly select `repairXmlDeclaration=true`. Preflight inspects raw declaration bytes before parsing. Repair replaces only that UTF-8 alias with `utf-8`, retaining BOM, quotes, whitespace and all body bytes. Valid native UTF-16 remains byte-identical. Ordinary parser success is not sufficient evidence that TIA will accept a declaration spelling. Other mismatched/unsupported encodings are rejected rather than transcoded.

The release directory contains `bundle/`, `bundle.zip`, `validation.json` and `originals/`. Only `bundle/` is passed to `hmi_preview_import_bundle`. Originals, backups, repair reports and archive verification files do not enter native import resource directories. The indexed bundle preserves native XML filenames, required non-XML companions and exact native identities. Preparation publishes atomically only after required checks pass, with folder/ZIP member/hash consistency. Failure retains a sibling `.validation-failed.json`; no candidate folder/ZIP is published.

The optional source `transfer-manifest.json` format is:

```json
{
  "format": "siemens-transfer-files-v1",
  "files": [{"path": "TextLists/StatusChoices.xml", "sha256": "<64 lowercase hex characters>", "bytes": 1234}]
}
```

The manifest declares every file under its root except itself. Member set, lengths and hashes must match before any repair. Unrecognized `manifest.json` formats are unsupported; this tool does not silently treat them as verified. A generated bundle always includes its own verified manifest; preview rechecks it. ZIP checks reject traversal, Windows-ambiguous paths, links, duplicate/case-colliding names, local/central header mismatches, bad CRCs and exceeded bounds. Stored/deflate single-disk ZIPs are supported; encrypted, ZIP64 and non-UTF-8 non-ASCII entry names are unsupported. Limits: 10,000 entries, 64 MiB per file, 512 MiB expanded, depth 32; source archive size is bounded to 600 MiB. No general archive repair is attempted.

Other category combinations keep the legacy WinForms converter. They do not claim this TextLists-only validation/repair profile. Metadata validation is also required for selected V15.1 native text-list import previews/applies. XML transfers share original-byte encoding preflight. No check modifies user safeguards.

## Native apply, evidence and interruptions

Review a hash-bound preview within the authorized scope, then apply once. Text-list preview/apply retains project/PID/API identity, original snapshot hash, candidate hash and scoped native-after export hash/fingerprint. Import uses a unique private staging directory, resolves its physical file path, checks the file and companions, and records dispatch/return/cleanup states. The short-path budget is this application's conservative profile, not a proven Siemens limit or a diagnosis of every file-not-found incident. Server visibility is checked; only a native operation establishes access by the Siemens process. Shared `McpArtifactRoot`, TEMP, safeguards and root-directory permissions are not changed.

Compile and save remain explicit separate native operations. Retain zero/nonzero errors, all warnings, diagnostic truncation status and exact scope. The workflow validator labels diagnostic rows `inherited` or `new` only against the same project/PID/HMI's complete supplied baseline; otherwise they remain `unclassified-no-comparable-baseline`. It cannot prove which actor caused a warning. Save is project-wide and may include other editors' changes. No download or runtime test is performed by these validators.

After a lost reply, inspect `get_request_status` using the original request/context and the owning session's `tia_get_status`. Preserve `preview.json`, `apply.json`, `staging.json` and exports. A returned import with unverified readback is not a completed workflow. Plans are consumed before dispatch and cannot replay. Do not retry an uncertain apply, change its request ID, release/reacquire to bypass a reservation, or automatically roll back. Reconcile actual native state before any new preview. Supported operator recovery is documented in [app host operation](APP-HOST.md#interrupted-writes-and-recovery).

The new host/staging/preparation behavior needs a host upgrade and new frontend connection. Local Comfort validation operations can reload on a compatible existing host route, but that does not add host-owned staging or bundle parameters. Package/offline tests do not establish the new route's native TIA, model-driven Codex, runtime, safety or commissioning acceptance.
