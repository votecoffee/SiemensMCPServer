# Comfort screen layout

Comfort worker 0.9.1 adds `inspect_screen_layout` and `arrange_screen_items`. They use the existing local/classic-XML routes: staging this Comfort bundle and calling `tia_reload_extensions(module="comfort")` does not replace the host or its MCP tool list. Reload compatibility is checked on 0.7.4 and 0.9.0 hosts. Successful reload invalidates outstanding previews; read the resource again afterward. See [extensions](EXTENSIONS.md).

The operations work with exported classic screen XML, prioritizing V15.1. The desktop GUI retains its transfer workflows; these layout commands are MCP extension operations. Unified uses its separate native object/property workflow.

## Select controls

1. Attach using the exact TIA process and project path, then discover the HMI ID and screen resource path.
2. Read that resource with `hmi_read_classic_resource`. Retain its XML, fingerprint and editability flag.
3. Call `tia_read_extension` with `module="comfort"`, `operation="inspect_screen_layout"` and `argumentsJson` containing the serialized object `{ "xml": <returned XML>, "offset": 0, "maxItems": 100 }`.
4. Copy each selected row's complete `selection` object. It contains an exact `xPath` and `expectedBounds`; do not substitute volatile export IDs or a different XPath expression.

Each row reports its native type, name, coordinate-container path, stored bounds and any unsupported reason. `geometryAvailable` describes XML fields, not native edit permission. A resource can remain inspection-only. Follow `nextOffset` while `hasMore` is true; use the same supplied XML for consistent local paging.

Normal screens, templates, popups, slide-ins, permanent screens and global elements are eligible when their native XML exposes supported controls. Each control must belong to `ScreenItems` and already contain scalar integer `Left`, `Top`, `Width` and `Height` fields. Missing, complex, fractional or zero-size geometry is reported as unsupported. No geometry fields are invented.

## Preview a layout change

For example, move two selected controls 20 pixels to the right using this inner request:

```json
{
  "action": "move",
  "items": [
    {
      "xPath": "<first exact path from selection>",
      "expectedBounds": { "left": 10, "top": 20, "width": 80, "height": 30 }
    },
    {
      "xPath": "<second exact path from selection>",
      "expectedBounds": { "left": 100, "top": 20, "width": 80, "height": 30 }
    }
  ],
  "deltaX": 20,
  "deltaY": 0
}
```

The paths and bounds above are placeholders; use the returned selections. Pass the JSON-serialized inner request as `argumentsJson` to `tia_preview_extension`, with `module="comfort"`, `operation="arrange_screen_items"`, `targetId` equal to the HMI ID, the exact resource `path`, and `expectedFingerprint` equal to the read resource fingerprint.

| Action | Required action fields | Behavior |
| --- | --- | --- |
| `move` | `deltaX`, `deltaY` | Translate every selected control in its own coordinate container. |
| `align` | `alignment`, `anchorXPath` | Align at least two controls to a selected anchor. Choices: `left`, `right`, `horizontalCenter`, `top`, `bottom`, `verticalCenter`. The anchor stays fixed. |
| `distribute` | `axis` | Give at least three controls equal gaps along `horizontal` or `vertical`, sorting by stored position and preserving the first/last outer edges. Equal positions retain caller order. Insufficient space for nonnegative gaps is rejected. |
| `resize` | `width` and/or `height` | Set supplied dimensions for all selected controls; other dimensions and positions stay unchanged. |

Supply only the fields for the chosen action. Align/distribute require one exact XML coordinate container; the operation does not infer transforms between layers or groups. Selecting a container together with one of its descendants is rejected. Centers and distribution round to the nearest integer pixel, with midpoints away from zero; distribution gaps can differ by one pixel.

The preview contains each selected control's before/after bounds and every exact scalar XML edit, plus the native candidate hashes and retained artifact paths. It rejects stale resource fingerprints, changed bounds, duplicate/unknown selections, unsupported geometry, out-of-range results and no-op requests. Only changed geometry scalars are authored; other XML, captions/languages, events, references and companion files pass through the existing resource staging/readback workflow.

Review the result against the authorized scope, then apply its `planId` and `candidateSha256` with `tia_apply_extension_plan`. Native apply uses the host's existing exclusive access, transaction and readback checks. It does not save. Compile and whole-project save remain separate steps within the user's authorization.

## Limits and validation

- Supplied XML: at most 16 MiB of characters, 50,000 resource elements and 64 levels.
- Inspection: 1–200 rows per call, offset 0–10,000, bounded escaped JSON payload with `payloadLimited` and a continuation offset.
- Layout: 1–100 exact selections. Positions/deltas are bounded to ±1,000,000; dimensions are 1–1,000,000. These are parser limits, not a claim about every native control's accepted range.
- Stored geometry is used without evaluating runtime dynamizations, rotation, screen clipping or collisions. A native control can impose additional restrictions during import/compile.
- Unit fixtures, real net48 workers, MCP routing and older-host reload compatibility are tested. Layout import/compile/save acceptance on an attached V15.1 project remains pending; see [validation status](NATIVE-ACCEPTANCE.md).
