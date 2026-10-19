# Comfort screen and tag packages

The Comfort worker can assemble a classic screen/tag import bundle from native XML prototypes. It clones controls and tags while retaining exported device metadata, API headers, languages and unselected fields. It requires no TIA attachment and makes no project changes. A separate reviewed native import is still required.

These operations are available in worker **0.7.2** on the existing **0.7.1** host. Stage the worker and call `tia_reload_extensions(module="comfort")`; discover its schemas with `tia_list_extensions`. See [extension updates](EXTENSIONS.md). The host still exposes the same 51 MCP tools. Obtain the guide and file-writing helper from source or an updated package separately; worker reload does not update installed plugin files.

## Prepare native prototypes

Export a normal screen and, when needed, a tag table from the intended API/device. `hmi_read_classic_resource` returns native XML and a scalar catalog. Keep originals outside the public checkout. Supply complete `Document` XML, with exactly one `Hmi.Screen.Screen` or `Hmi.Tag.TagTable` and its `Engineering` version header.

Choose existing named controls or tags as prototypes. Each requested member is cloned into the prototype's existing composition, receives fresh IDs with internal references remapped, and can receive exact scalar edits. Layers and other template fields remain. `keepTemplateMembers` explicitly selects whether to retain the original controls/tags as well as the clones. Nested control-group replacement and dangling references to removed prototypes are rejected.

## Build through MCP

Call `tia_read_extension` with `module="comfort"`, `operation="build_screen_package"` and `argumentsJson` containing JSON of this shape. Replace the placeholder with actual exported XML; placeholder strings are not accepted as native documents.

```json
{
  "resources": [
    {
      "kind": "screen",
      "templateXml": "<complete native screen Document>",
      "name": "StationStatus",
      "keepTemplateMembers": false,
      "edits": [
        {
          "xPath": "/Document/Hmi.Screen.Screen/AttributeList/Number",
          "expectedValue": "1",
          "newValue": "23"
        }
      ],
      "members": [
        {
          "prototype": "PrototypeLabel",
          "name": "StatusLabel",
          "edits": [
            {
              "xPath": "/Document/Hmi.Screen.TextField/AttributeList/Left",
              "expectedValue": "10",
              "newValue": "100"
            }
          ]
        }
      ]
    }
  ],
  "plcSymbols": []
}
```

Use the actual prototype type and paths from its XML. Member edit paths select within a temporary `Document` wrapping that single clone; resource edit paths select within the complete exported document. Exact `expectedValue` fields reject stale assumptions. IDs, composition identity and resource-name edits are protected.

For tag tables, use `kind="tagTable"` and select existing `Hmi.Tag.Tag` prototypes by name. A member edit such as `/Document/Hmi.Tag.Tag/LinkList/ControllerTag/Name` can replace an exact exported PLC symbol. Include declarations such as `{"connection":"PLC_Link","symbol":"StationCounter","dataType":"Int"}` in `plcSymbols`. Connection and type names must match the actual exported links. Change a screen's matching HMI tag reference using its actual `LinkList/Tag/Name` path; it may belong to a nested property. Quoted references retain their native quoting, for example `"StationCount"`.

The reply's `data` contains `files`, individual UTF-8 hashes, an inventory `sha256` and an offline binding report. It includes `export-index.json`, `package-report.json` and native XML payloads, ordered with tag tables before screens. Review generated XML, destinations and the report before writing or importing it.

## Write and import the reviewed package

Save the structured reply, or its package `data`, as a local JSON file. The helper verifies the reviewed inventory hash, every file's UTF-8 hash, bundle index and safe Windows paths before creating a new destination. It never attaches to TIA.

```powershell
powershell.exe -NoProfile -File build/Write-ComfortPackage.ps1 `
  -PackageJsonPath 'C:\path\to\reviewed-reply.json' `
  -Destination 'C:\path\to\new-import-bundle' `
  -ExpectedPackageSha256 '<sha256 returned by the reviewed build>'
```

In a built plugin the helper is under `scripts/Write-ComfortPackage.ps1`. The destination must not exist; its parent must already exist. The helper refuses overwrites and writes the import index last.

1. Attach to the exact process/project and identify the intended classic HMI.
2. Read its root with `hmi_read_object(hmiId, path="")` for a fresh fingerprint.
3. Call `hmi_preview_import_bundle` with that target, empty root `path`, `expectedFingerprint`, the returned `artifactDirectory` as `sourceDirectory`, and an explicit `overwrite` choice.
4. Review the native plan and candidate hash; apply with `hmi_apply_object_plan` within the authorized scope.
5. Inspect the native result, compile with `hmi_compile`, and save separately when authorized.

Native import failures require inspecting the current project before retrying. Previewing an import does not establish that Siemens will accept every object, connection, screen number or device-specific property.

## Validate bindings

`tia_read_extension(module="comfort", operation="validate_package_bindings", argumentsJson=...)` accepts `resourceXml` (an array of complete native screen/tag documents) and the same `plcSymbols` declarations. It checks:

- Package-wide HMI tag names and screen references to those tags.
- Paired symbolic connection/controller-tag links against supplied PLC declarations.
- Matching declared PLC and exported HMI scalar types; conflicting absolute addresses are rejected.

Every referenced HMI tag must be supplied in the package XML. Declarations are caller-supplied evidence; the operation does not read a PLC, verify a native connection partner, resolve aliases or prove compiled compatibility. Reports always set `nativeBindingVerified=false`; packages always set `nativeImportAccepted=false`.

## Limits and verification

The adapter accepts sidecar-free normal screens and tag tables from one API version, targeting the HMI's root screen/tag-table collections. Limits are 1–50 resources, 500 requested members per resource and 10,000 PLC declarations, with a 4 MiB template/clone budget and a separate 4 MiB output limit. Graphic sidecars, opaque `UId` formats, unsupported link shapes and dangling native references are rejected. Use existing native transfers for content outside this scope.

Duplicate member names, HMI tag names, resource names and screen numbers within the package are checked. Existing-project collisions and device constraints require native validation. The worker clones exported prototypes rather than generating native control schemas from scratch.

Offline tests cover clone/reference/language preservation, stale edits, duplicate identities, binding mismatches and size limits. A persistent STDIO test reloads the new operations into an existing host, builds a Unicode package, materializes exact bytes and rejects hash/path tampering without attaching to TIA. Native import/compile/save acceptance of generated packages remains pending. See [validation status](NATIVE-ACCEPTANCE.md).
