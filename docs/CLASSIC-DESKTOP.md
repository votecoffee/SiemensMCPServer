# Classic desktop utility

The WinForms executable handles classic WinCC Comfort / Advanced projects. Build and launch it using the instructions in [the root README](../README.md#usage) and [build configuration](BUILDING.md). Unified and PLC engineering tools are provided by the MCP server.

## Typical use

1. Start a supported TIA Portal version and open the project.
2. Start this program.
3. If prompted, select the TIA Portal version to use.
4. Click **Refresh TIA**.
5. Select the correct process and click **Attach**.
6. Accept the Siemens Openness access prompt if shown.
7. Select the HMI target.

## Screen-only functions

- **Export selected**: exports one screen or a selected screen folder.
- **Export all screens**: recursively exports all normal screens.
- **Import screen XML...**: imports one or more screen XML files into the selected screen folder.
- **Import Screens Only**: imports only normal screens from a Complete HMI export while leaving the other HMI categories untouched.

## Export Complete HMI

Creates a timestamped export folder with these categories:

```text
ProjectGraphics\
Connections\
Cycles\
VBScripts\
TagTables\
TextLists\
GraphicLists\
ScreenTemplates\
PopupScreens\
SlideInScreens\
PermanentArea\
Screens\
ExportSummary.txt
```

Project graphics are exported to individual subfolders because exported XML may reference sidecar image files by relative path.

## Import Complete HMI

Select the root folder created by **Export Complete HMI**. The importer restores data in dependency-aware order:

1. Project graphics
2. Connections
3. Cycles
4. VB scripts
5. HMI tag tables
6. Text lists
7. Graphic lists
8. Screen templates
9. Pop-up screens
10. Slide-in screens
11. Permanent area
12. Normal screens

Folder hierarchy is recreated where the Openness API exposes folders.

### Overwrite behavior

The **Overwrite existing objects** checkbox controls the general import mode:

- unchecked: `ImportOptions.None`
- checked: `ImportOptions.Override`

Cycles are intentionally different: existing cycles are not overwritten. Only missing cycles are imported with `ImportOptions.None`, because standard cycles can expose attributes that are not editable.

### Project save behavior

Imports do **not** automatically save the TIA project. After import:

1. inspect the modified HMI,
2. compile it in TIA Portal,
3. review warnings and errors,
4. click **Save project** only when satisfied.

The importer writes an `ImportSummary-YYYYMMDD-HHMMSS.txt` report into the selected export root. Screens-only import writes a corresponding `ScreenImportSummary-YYYYMMDD-HHMMSS.txt`.

## Why the import order matters

Classic HMI objects can reference one another. Typical dependencies include:

- HMI tags referencing connections and acquisition cycles
- tag event handlers calling VB scripts
- text lists referencing HMI tags
- screens referencing tags, text lists, graphic lists, project graphics, templates, pop-ups, scripts, and other screens

For that reason, Complete HMI import uses dependency order rather than alphabetical folder order.

## Important limitations

- The desktop UI targets classic WinCC (`Siemens.Engineering.Hmi.HmiTarget`). The MCP server additionally discovers Unified `HmiSoftware` and exposes only the operations supported by its loaded API; see the plugin guide for version and native-validation limits.
- Pre-V21 and V21 builds use different Siemens assembly families and are not interchangeable at runtime.
- Screen imports require a compatible HMI device type and valid screen dimensions/numbers.
- Integrated HMI connections are not exportable through this classic connection export mechanism.
- Complete HMI import is safest as a round trip into the same project/device family. Cross-project imports can still depend on external PLC objects, libraries, authorization configuration, styles, or project-level resources that are not represented by individual HMI XML files.
- Project text translation XLSX export/import is intentionally not included because Siemens restricts re-import of exported project texts to the project from which they originated.
- Always archive or back up the TIA project before doing bulk imports.
