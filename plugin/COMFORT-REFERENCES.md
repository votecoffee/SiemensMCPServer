# Comfort reference inspection and rebinding

Comfort worker 0.10.2 adds `inspect_references` and `rebind_references`. They show exported symbolic/native links and change exact existing symbolic names, including tag links inherited by [copied controls](COMFORT-SCREEN-ITEMS.md) and PLC symbols/connections in native tag tables.

The worker uses the existing `local` and `classicXml`/`screenText` routes. It reloads on the checked 0.10.0 host with the same executable, process and 51 MCP tools. Stage the trusted bundle and call `tia_reload_extensions(module="comfort")`; successful reload invalidates outstanding previews. Hosts earlier than 0.10.0 require the [whitespace-preserving host update](COMFORT-SCREEN-TEXTS.md#host-update). These are MCP extension operations; the desktop GUI keeps its existing transfer workflows.

## Inspect references

1. Discover the exact process/project, HMI ID and native resource path, then read with `hmi_read_classic_resource`.
2. Keep its XML, resource fingerprint and editability flag.
3. Call `tia_read_extension(module="comfort", operation="inspect_references", argumentsJson=...)`, passing a JSON-serialized object containing `xml`, optional `rootXPath`, optional `linkType`, `offset` and `maxItems`.

The catalog contains direct, unnamespaced `LinkList` children with a `TargetID`. Each row reports:

- The exact link path and role (`Tag`, `Owner`, etc.), plus `kind="openLink"` or `"nativeId"`.
- Its existing symbolic `name`, where a bounded scalar is available.
- Native owner and enclosing control paths, names and types. Owner/target/control display names longer than 512 characters or structured names are omitted.
- The exported target path/type/name for a resolvable native-ID link. These paths describe this snapshot; export IDs are never edit selectors.
- Adapter editability and a reason for protected rows. Editable rows also include a complete `selection` with `xPath`, `expectedFingerprint` and `expectedName`.

Use an exact returned owner/control path, the returned `resourceXPath`, or a control path from `inspect_screen_items` as `rootXPath` to inspect that subtree. Do not supply a free-form XPath query. `linkType` filters one exact, case-sensitive role, such as `Tag`. Follow `nextOffset` while `hasMore` is true, keeping the same XML and filters. The native host separately enforces resource editability.

Only symbolic `Tag`, `ControllerTag` and `Connection` roles are editable. A link must contain one nonblank scalar `Name`, no extra link/name metadata and `TargetID="@OpenLink"`. Ambiguous names, structured references and unknown roles remain protected. Native-ID ownership links, function links and data-type links are inspection-only. An unresolved exported native-ID reference blocks rebinding. This catalog does not analyze names embedded in scripts, expressions or other XML formats.

## Change explicit names

Build an inner request using exact returned selections:

```json
{
  "edits": [
    {
      "selection": {
        "xPath": "<exact returned link path>",
        "expectedFingerprint": "<returned link fingerprint>",
        "expectedName": "\"OriginalTag\""
      },
      "newName": "\"ReplacementTag\""
    }
  ]
}
```

The paths/fingerprints above are placeholders. `expectedName` must match the original native value exactly. `newName` is a literal replacement, including any native quoting, escaping, symbolic address or connection name. No quote normalization or search-and-replace occurs. Different native link formats may use different quoting; preserve the format of the actual resource.

For copied controls, first apply the authorized clone preview and reread the native resource. Inspect the copied control's subtree, select its intended tag links, then prepare a separate rebind preview. This keeps the original control's bindings unchanged. The offline operations can also be composed on supplied XML, but native selections must come from a fresh resource read.

The operation requires 1–200 unique selections. Each replacement is nonblank, at most 4096 characters and valid XML text. Duplicate, stale, protected and no-op edits fail before candidate mutation. Link fingerprints ignore volatile export-ID renumbering; the separate resource fingerprint binds the complete native resource and companion files.

## Preview and native validation

Pass the inner request as `argumentsJson` to `tia_preview_extension` with `module="comfort"`, `operation="rebind_references"`, the exact HMI ID as `targetId`, the **native resource path** and its **resource fingerprint**. Review the returned old/new names and owner/control paths, staged XML, companion inventory and candidate hash. XML link paths address fields inside that resource, not the native object browser.

Apply an already-authorized plan through `tia_apply_extension_plan` using the returned plan ID and candidate SHA-256. The host retains sidecars and uses its existing native transaction/readback guards. Compile and save are separate operations.

Only selected `Name` values change. IDs, link roles, other references, captions, styles, cultures and scripts retain their existing values. **A syntactically valid name does not prove that the target exists or has a compatible type.** This worker does not resolve HMI/PLC symbols, verify the connection partner or assess runtime behavior. Discover and check the intended target using native browsing and project engineering checks. Native import, compile and save/reopen acceptance for this operation remains pending.

## Bounds and checks

Inspection allows 16 MiB of supplied XML, 50,000 resource elements, 64 XML levels and 5,000 exported links. Pages contain 1–200 rows and stay within a 256 KiB escaped-JSON response budget. Root/link paths are limited to 4096 characters. Candidate XML stays within 16 MiB; link-name edits do not create or delete objects.

Unit tests cover symbolic/native distinctions, exact owner/type filters, batch validation, quoting/Unicode/XML escaping, clone-then-rebind isolation, ID normalization, preservation, payload limits and staged companion hashes. The real net48 worker/MCP check also verifies reload on an unchanged 0.10.0 host, rejection paths and preservation of the previous generation after an invalid update:

```powershell
py -3 -B -X utf8 scripts/test_comfort_references.py `
  Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe `
  --previous-host C:/path/to/0.10.0/server/SiemensMCPServer.Mcp.exe `
  --report artifacts/comfort-references.json
```

That check does not attach to TIA or establish native reference acceptance. See [validation status](NATIVE-ACCEPTANCE.md).
