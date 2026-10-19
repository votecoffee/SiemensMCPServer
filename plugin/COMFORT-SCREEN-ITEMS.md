# Comfort control cloning and removal

Comfort worker 0.10.1 adds `inspect_screen_items` and `change_screen_items`. It copies existing exported controls/groups into an explicit container in the same screen, or removes explicitly selected controls after reference checks. It uses native XML prototypes rather than constructing control schemas.

The worker uses the existing `classicXml` route and `screenText` contract. Stage the trusted Comfort bundle and call `tia_reload_extensions(module="comfort")` on host 0.10.0 or newer. Reload compatibility is checked on 0.10.0 with an unchanged host executable, PID and 51-tool interface. Earlier hosts require the [0.10.0 host update](COMFORT-SCREEN-TEXTS.md#host-update). Successful reload invalidates outstanding previews; read again afterward. These are MCP extension operations; the desktop GUI retains its existing transfer workflows.

## Inspect prototypes

1. Attach using the exact TIA process/project, discover the HMI ID and native classic screen resource path, then call `hmi_read_classic_resource`.
2. Keep its XML, resource fingerprint and editability flag.
3. Call `tia_read_extension` with `module="comfort"`, `operation="inspect_screen_items"` and `argumentsJson` containing the serialized object `{ "xml": <returned XML>, "offset": 0, "maxItems": 100 }`.

Rows contain the control name/type, exact XML path, subtree control count, source `selection` and an existing `containerXPath`. Use the selection unchanged: its fingerprint binds the full control subtree while ignoring volatile export IDs and preserving authored text whitespace. The separate native resource fingerprint binds the whole screen and its companions.

`cloneAvailable` describes the XML adapter, while `standaloneRemovalAvailable` includes reference checks for removing that subtree by itself. The native host separately enforces resource editability. `UId`-based identity/reference formats, unresolved ID references, missing control IDs/names or duplicate names make structural changes unavailable.

Use an exact returned control path as optional `rootXPath` to inspect that subtree, including its root. Follow `nextOffset` while `hasMore` is true, keeping the same XML and filter. To copy a group, collect every control row in that subtree for explicit name mappings.

## Clone a control or group

Build an inner request such as:

```json
{
  "changes": [
    {
      "action": "clone",
      "selection": {
        "xPath": "<exact source control path>",
        "expectedFingerprint": "<returned control fingerprint>"
      },
      "destinationXPath": "<returned containerXPath>",
      "names": [
        { "sourceXPath": "<exact source control path>", "newName": "NewButton" }
      ],
      "edits": [
        {
          "xPath": "/Document/Hmi.Screen.Button/AttributeList/Left",
          "expectedValue": "10",
          "newValue": "120"
        }
      ]
    }
  ]
}
```

The selection and destination above are placeholders. Copy actual returned paths/fingerprints. The optional scalar edit shown applies to a button prototype with that exact existing value; use the actual native type and XML fields. Edit paths address a `Document`-wrapped copy, not the original full screen.

Supply one name mapping for **every** control in the copied subtree, including the root and nested controls. The adapter requires names of 1–128 characters without control characters, unique across retained and new controls using a case-insensitive comparison. Repeated copies of the same prototype are allowed with distinct names. Different overlapping source subtrees, or cloning/removing the same source, require separate previews.

Destinations must be exact returned ObjectList paths already containing exported controls. Empty/new containers and cross-screen copies are outside this operation. Local geometry stays unchanged unless explicitly edited; there is no conversion between layer/group coordinate systems. A copy cannot be placed inside its own subtree or a destination removed by the batch.

Every prototype is frozen from the initial snapshot before batch changes. Native IDs are freshly allocated while all original IDs remain reserved, and internal `TargetID` links are remapped. Events, scripts, styles, language slots, graphic references and other XML are retained. External ID links and symbolic names retain their existing values: **review inherited actions and bindings**. Names embedded in scripts or symbolic links are not automatically rewritten to copied control names.

Optional edits change existing scalar values only. They cannot override mapped control names or change IDs, `TargetID` links, composition, culture identities or external-file metadata flags. Companion paths still have to resolve inside the staged resource. Native field validity remains subject to import/compile checks.

## Remove controls

For removal, supply only `action="remove"` and the complete returned `selection`; omit destination, names and edits. Removing a group includes its descendants. A batch may remove several disjoint subtrees together.

Removal is blocked when retained exported objects refer to removed IDs, or when a retained `@OpenLink` has an exact native/quoted Name matching a removed control name. References between subtrees removed together are permitted, so standalone removal can be blocked while an explicitly scoped combined removal is valid. Embedded code strings, computed names and other symbolic reference formats are not analyzed.

## Preview, apply and validate

Pass the JSON-serialized inner request as `argumentsJson` to `tia_preview_extension` with `module="comfort"`, `operation="change_screen_items"`, the HMI ID as `targetId`, the **native resource path**, and the read **resource fingerprint** as `expectedFingerprint`. XML control/container paths are selectors inside that resource, not native object-browser paths.

The preview reports the selected sources, mappings/edits, affected control counts, new copy paths, retained open-link/external-ID-link counts, candidate hashes and artifact locations. Review the candidate and inherited behavior against the authorized scope, then apply the matching plan/hash through `tia_apply_extension_plan`. Existing native exclusive access, transactions, readback checks and generation invalidation apply. Compile and whole-project save remain separate authorized operations; apply does not save.

Limits are 16 MiB of supplied/candidate XML characters, 50,000 resource elements, 64 XML levels, 2,000 exported controls, 1–200 catalog rows per page, 1–50 changes per preview, 500 copied controls, 5,000 elements per prototype and a 4 MiB aggregate prototype/copy budget. Catalog replies have an escaped JSON budget and continuation offset. Each copy permits up to 100 scalar edits with paths/values bounded to 4096 characters.

Unit and real net48 worker tests cover cloning, group naming, frozen batches, references, preservation, companions and preview guards. MCP tests verify reload on the existing 0.10.0 host and rollback after a rejected worker update. Native control import/compile/save acceptance remains pending. Objects absent from Siemens' native export surface cannot be assessed through export/readback equality; see [validation status](NATIVE-ACCEPTANCE.md).
