# Comfort screen text runs

Host/worker 0.10.0 adds `comfort:inspect_screen_texts` and `comfort:edit_screen_texts`. These operations inspect existing multilingual classic screen captions and edit selected static text runs while preserving native formatting and other languages. Comfort V15.1 is the primary profile. Unified retains its separate caption/property workflow; text lists retain their specialized tools.

## Host update

Install host 0.10.4 or newer and reconnect before installing the current Comfort worker. It preserves all whitespace inside authored `Text` subtrees in resource hashes and behavior fingerprints, distinguishing `<b>A</b> <i>B</i>` from `<b>A</b><i>B</i>`. It also resolves native `#ID` links when comparing freshly exported readback XML.

The preview operation declares `route="classicXml"`, `snapshotKind="classicReferences"`. This is a compatibility marker for the shared hash contract. Older hosts reject the entire current Comfort bundle and retain their previous active generation. Subsequent workers using this contract can reload through the existing five extension tools. See [native XML compatibility](EXTENSIONS.md#classic-native-xml).

## Inspect captions

1. Attach using the exact process/project, discover the HMI ID and classic screen resource path, then call `hmi_read_classic_resource`.
2. Retain the returned XML, resource fingerprint and editability flag.
3. Call `tia_read_extension` with `module="comfort"`, `operation="inspect_screen_texts"` and `argumentsJson` containing the serialized inspection request below.

```json
{
  "xml": "<XML returned by hmi_read_classic_resource>",
  "culture": "en-US",
  "query": "Ready",
  "caseSensitive": false,
  "offset": 0,
  "maxRuns": 100,
  "maxCharacters": 1024
}
```

Only `xml` is required. Empty `culture` includes all exported language slots; the filter is case-insensitive but selections retain the exact native culture. Empty `query` includes all runs. Search is literal within each run; it does not join words across formatting or paragraph boundaries. Results can include protected content.

Each row reports the text field's XML `xPath`, owner path/name, text property, culture, storage mode, formatting fingerprint, zero-based `runIndex`, bounded text, preview eligibility and any unsupported reason. Supported, unclipped rows include a complete `selection` object. `canPreview` describes the XML adapter; the host separately enforces native resource editability.

Follow `nextOffset` while `hasMore` is true, keeping the same supplied XML and filters for consistent paging. A clipped row has no editable selection. Increase `maxCharacters` up to 4096 to inspect a complete bounded run before editing.

## Preview and apply

Copy each returned selection without changing its path, culture, fingerprint, run index or old text. Build this inner request:

```json
{
  "edits": [
    {
      "selection": {
        "xPath": "<exact returned Text field path>",
        "culture": "en-US",
        "expectedTextFingerprint": "<returned Text field fingerprint>",
        "runIndex": 1,
        "expectedText": "Ready"
      },
      "newText": "Ready & waiting"
    }
  ]
}
```

The selection above is a placeholder; copy the actual returned object. Pass the JSON-serialized inner request as `argumentsJson` to `tia_preview_extension` with `module="comfort"`, `operation="edit_screen_texts"`, `targetId` equal to the HMI ID, the native resource `path` and the resource's `expectedFingerprint`. The Text field fingerprint and resource fingerprint serve different scopes and cannot be interchanged. XML owner paths are inspection selectors, not native resource paths.

One batch can edit several runs in the same caption or across captions/cultures in the selected screen. Each replacement is literal text: `<`, `&` and markup-looking strings are escaped rather than creating formatting or dynamic fields. Existing empty text slots can be filled. The operation never adds a language, paragraph, formatting element, tag binding or event.

The preview reports exact old/new run edits, affected Text fields, native candidate hashes and retained artifacts. It rejects unknown/duplicate selections, stale formatting or culture, changed old text, unsupported content, invalid XML characters, oversized values and no-op requests before creating an applicable plan. Review the result against the authorized scope, then apply with `tia_apply_extension_plan`. Compile and whole-project save remain separate authorized operations; apply does not save.

## Supported content and limits

- Plain scalar text, embedded `body` markup and body markup stored as an escaped scalar retain their storage mode. The encoded form can normalize XML serialization while preserving elements, attributes and text values.
- Existing paragraphs, line breaks, multiple inline runs and empty text leaves are supported. Supported inline elements are `span`, `b`, `strong`, `i`, `em`, `u`, `font`, `sub`, `sup` and empty `br`. Static attributes are `style`, `color`, `face`, `size`, `align`, `dir`, `lang` and `xml:space`.
- Unknown/namespaced markup, dynamic fields, references, comments/processing instructions in caption markup, invalid structure, ambiguous properties/cultures and behavior-owned text remain inspection-only. Markup-looking scalar content that cannot be recognized as a body document is protected.
- At most 16 MiB of supplied XML characters, 50,000 resource elements, 64 XML levels and 20,000 inspected runs; formatted captions allow 512 elements and 32 nested levels. Caption content beyond 65,536 characters is protected.
- Inspection returns 1–200 rows per call with an escaped JSON budget and `payloadLimited` continuation. Edits allow 1–200 selections and at most 4096 characters per old/new run.
- Stored text is inspected without evaluating runtime bindings, rendering or language completeness. Native control restrictions still apply.

Unit tests cover preservation, escaping, whitespace-only changes and staged candidates. Real net48 worker/MCP checks cover Unicode, filtering, reload rollback and older-host rejection. Attached-project caption import/compile/save acceptance remains pending; see [validation status](NATIVE-ACCEPTANCE.md).
