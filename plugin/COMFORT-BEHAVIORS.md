# Comfort events and animation prototypes

Comfort worker 0.10.3 adds paged `inspect_behaviors` and extends `change_behaviors` with explicit external `Owner`/`Control` ID-link mappings. It copies existing native event/animation/binding objects, or removes selected behaviors after checking retained native-ID references. It does not construct behavior schemas or execute scripts.

The operations use existing `local` and whitespace-preserving `classicXml`/`screenText` routes. The new worker reloads on the checked 0.10.0 host with unchanged PID, executable, 51 tool schemas and other module generations. Stage the trusted bundle and call `tia_reload_extensions(module="comfort")`. Successful reload invalidates outstanding previews. Older hosts require the [0.10.0 host update](COMFORT-SCREEN-TEXTS.md#host-update).

## Inspect prototypes

Read the exact classic native resource with `hmi_read_classic_resource`, retaining its XML, HMI ID, native resource path, resource fingerprint and editability flag. Call `tia_read_extension` with `module="comfort"`, `operation="inspect_behaviors"` and an `argumentsJson` object containing the returned `xml` plus optional `rootXPath`, `offset` and `maxItems`.

Each `objects` row includes the prototype's exact `xPath`, `fingerprint`, native type/composition, owner path/type/name, clone-relative root path, clone availability and standalone removal availability. The separate whole-resource fingerprint remains required for native previews.

`ownerLinks` describes eligible external native-ID references in direct `LinkList` children named `Owner` or `Control`. Each entry has a **clone-relative** `xPath`, the **original-resource** `expectedTargetXPath`, and target type/name. Internal links are automatically remapped during cloning; symbolic links and unsupported roles are not offered as owner maps. `ownerLinkCatalogLimited` reports omitted mapping rows; inspect the original XML if necessary. An omitted map retains its original external target.

Use an exact native object path returned by this catalog, `inspect_screen_items` or `inspect_references` as `rootXPath` to scope inspection to that subtree. Follow `nextOffset` while `hasMore` is true, keeping the same XML and scope. Nodes nested inside a behavior are also prototypes; selecting a parent includes its descendants.

## Copy with explicit ownership

Build an inner request using actual returned paths and fingerprints:

```json
{
  "changes": [
    {
      "action": "clone",
      "xPath": "<prototype path in original resource>",
      "expectedFingerprint": "<prototype fingerprint>",
      "destinationXPath": "<destination owner path in original resource>",
      "referenceTargets": [
        {
          "xPath": "<clone-relative Owner/Control path from ownerLinks>",
          "expectedTargetXPath": "<original target path from ownerLinks>",
          "newTargetXPath": "<new target path in original resource>"
        }
      ],
      "edits": [
        {
          "xPath": "/Document/Hmi.Event.Event/AttributeList/Name",
          "expectedValue": "Press",
          "newValue": "Release"
        }
      ]
    }
  ]
}
```

The name edit is an example for a prototype with that exact native field/value. Use the actual clone-relative paths for other types. Destination owners must have the same native type as the original owner and at most one existing `ObjectList`; an absent list is created. Copying to different owner types is outside this adapter because event availability/compositions can differ.

Owner maps require exact returned link paths, exact current target paths and new targets of the same native type with an exported ID. They apply only to existing, simple external `Owner`/`Control` ID links. Duplicate, stale, internal, symbolic, extra-metadata and no-op mappings are rejected. Maps are optional: without them, external targets retain their original values, which may point to the source control. Review that ownership explicitly.

Every prototype is frozen before batch changes. Copies receive fresh IDs while all original IDs remain reserved, and internal ID links are remapped. Clones are inserted before removals. A later copy cannot accidentally absorb an earlier insertion, and deletion cannot free an ID for accidental reuse.

Optional scalar edits address the `Document`-wrapped clone. They cannot change IDs, `TargetID` links, compositions, culture identities or external-file metadata flags. Supported owner changes use `referenceTargets`; symbolic tag/connection names can be reviewed through [reference rebinding](COMFORT-REFERENCES.md). Styles, authored text whitespace, scripts and unmapped references retain their existing values unless an allowed scalar is explicitly edited.

## Remove dependent behaviors

For removal, use `action="remove"`, the exact prototype `xPath` and `expectedFingerprint`; omit destination, edits and owner maps. A batch may remove several disjoint subtrees together. References between removed subtrees are allowed, while a retained exported object's ID link to any removed node blocks the batch. Thus standalone removal may be unavailable while a complete explicitly selected dependent set can be removed together.

Duplicate or overlapping source subtrees require separate previews. A clone destination or mapped target cannot be removed by the batch. `UId` formats, missing behavior IDs and unresolved exported native-ID references block structural edits. Script strings, computed/symbolic behavior references and objects absent from native export are not analyzed.

## Preview and apply

Pass the JSON-serialized inner request to `tia_preview_extension` with `module="comfort"`, `operation="change_behaviors"`, the exact HMI ID as `targetId`, the **native resource path**, and the read **resource fingerprint**. Review every clone/removal, owner mapping, scalar edit, staged XML, companion file and candidate hash. XML paths inside this guide are selectors within the exported resource.

Apply an already-authorized preview through `tia_apply_extension_plan` with its plan ID and candidate SHA-256. The host retains sidecars and uses existing native exclusive-access, transaction and readback guards. Compile and save remain separate. Native import/compile/save acceptance for this operation is pending; metadata/pure-worker checks do not establish runtime event behavior or native validity of a copied configuration.

The fixed `hmi_preview_classic_behavior_changes` tool retains its original schema. It receives the improved batch/preservation checks, while the additional `referenceTargets` fields are advertised through the extension catalog. Existing root/first-page `behaviorCatalog` inspection remains available; `inspect_behaviors` supplies bounded paging and owner-map metadata. These are MCP features; the GUI retains its transfer workflows.

## Limits and verification

Input/candidate XML is bounded to 16 MiB, 50,000 resource elements, 64 levels and 5,000 behavior records. Inspection pages contain 1–200 rows within a 256 KiB escaped-JSON budget. Owner-link catalogs contain at most 50 eligible rows and may be shorter to fit their payload budget. Paths are bounded to 4096 characters.

A preview accepts 1–100 disjoint operations. Clones allow at most 5,000 elements per prototype, 10,000 copied elements and 4 MiB of copied XML per batch. Each clone permits up to 100 scalar edits and 50 owner-link maps. Scalar paths/values are bounded to 4096 characters.

Unit tests cover frozen batches, reserved IDs, explicit ownership, internal links, retained scripts/styles/spaces, dependent removals, stale/invalid mappings, atomic failure and page bounds. The real net48 worker/MCP probe also checks older-host reload and native guards without attachment:

```powershell
py -3 -B -X utf8 scripts/test_comfort_behaviors.py `
  Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe `
  --previous-host C:/path/to/0.10.0/server/SiemensMCPServer.Mcp.exe `
  --report artifacts/comfort-behaviors.json
```

See [validation status](NATIVE-ACCEPTANCE.md) for the remaining native boundaries.
