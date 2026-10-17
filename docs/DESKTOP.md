# Desktop import and export

`SiemensMCPServer.exe` discovers classic Comfort / Advanced HMI, Unified HMI and Siemens PLC software in the attached project. Select the intended target in **HMI / PLC**. The family prefix distinguishes the available workflows.

Build and launch instructions are in the [root README](../README.md#desktop-gui). Keep all DLLs and the generated `.exe.config` from the desktop output beside the executable. The MCP plugin ZIP does not contain the GUI.

## Connect

1. Open the intended project in a compatible TIA Portal instance.
2. Launch the desktop executable for that API, click **Refresh TIA**, and select the process by its project path.
3. Click **Attach** and allow the Siemens Openness access prompt if shown.
4. Select the intended **Comfort / Advanced**, **Unified**, or **PLC** target.

Comfort / Advanced selections retain the [classic screen and Complete HMI workflows](CLASSIC-DESKTOP.md). Unified and PLC selections show a resource tree, transfer actions and selection details. Expand nodes to inspect the API-visible hierarchy. **Refresh** rebuilds the current view.

## Unified and PLC exports

- **Export selected resources** exports the selected object or its supported descendants.
- **Export target resources** starts at the software target root.

Choose a parent directory. Each export creates a new timestamped folder containing `export-index.json`, per-object `object.json` metadata snapshots, and native resource folders where the API exposes an export method. Native XML, YAML, JavaScript and companion files retain their native transfer format. The index records resource destinations and failures. Metadata-only objects are not importable resources.

This export represents API-visible software resources. It is not a complete TIA project backup and does not include hardware configuration, all project-level resources or runtime data. A partial export remains available for inspection, but bundles with recorded failures are rejected for import.

## Unified and PLC imports

Back up the project before bulk imports. Use a dedicated test copy for initial native validation.

**Import native resource folder...** imports into the selected composition/object when its API exposes a supported import method. Select a folder containing one native XML resource and its sidecars, or the native Unified YAML configuration and all companions. A file export's `objects/.../native` folder is suitable. Select the destination composition, such as **Blocks**, **Types**, or **TagTables**, rather than an individual PLC member.

**Import export bundle...** reads `export-index.json` and imports its indexed resources in dependency order. Select the software target root for a whole-target export. Category exports may be imported with a narrower selection only when every destination lies inside that selection. Bundle destinations use their recorded paths within the selected target; they are not automatically rebased or renamed. Missing folder hierarchy can be recreated where the target API advertises compatible folder creation.

The importer rejects a different target family, destinations outside the selected subtree, malformed files, missing companions and failed export bundles. File sets are staged and hashed before review. A baseline export is retained under `%LOCALAPPDATA%\SiemensMCPServer\DesktopTransfers\<id>`, alongside the staged candidate. This software-resource baseline is not a full project archive.

Review the project, target, destination, resource count, overwrite choice, staged hash and local evidence directory in the confirmation dialog. **No** is the default. After confirmation, the importer rechecks the target and baseline, applies the native transfer in a Siemens transaction, and exports the result before committing. A failed native result, cancellation or readback/export error prevents the transaction from being committed. Inspect TIA and the retained evidence after an uncertain result before preparing another import.

**Overwrite existing objects** selects native XML override mode. Unified directory imports require this checkbox because the native replacement scope may affect a collection. They do not provide the same per-object no-overwrite guarantee as classic XML import.

Imports leave the project **unsaved**. Inspect the `after-export` folder and the native project, compile in TIA, review diagnostics, then click **Save project** explicitly. Native import acceptance does not assert source/export equality or runtime behavior.

## API and validation limits

| Target | Desktop scope |
| --- | --- |
| Comfort / Advanced | Existing screen and Complete HMI workflows; V15.1 remains the primary classic profile. |
| Unified | Native resource transfers only where exposed by the selected API. V19 exposes tag and script directory transfers; it has no native screen or text-list transfer route. Such objects can have metadata snapshots without importable payloads. |
| PLC | Offline XML transfers for API-exportable blocks, PLC data types and tag tables, with folder traversal. Transfers require the owning device's verified `Offline` state. External-source groups are browseable, but V15.1/V19 do not expose the generic native import/export signatures used by this GUI. SCL/STL generation remains an MCP workflow. |

The GUI shares the MCP native transfer adapters and their companion-file validation. It does not load the MCP host or feature workers. It provides no controller download, watch/force operation, hardware import or online control.

Desktop builds and offline UI/API checks cover V15.1 and V19. Native Unified/PLC GUI import, compile, save/reopen and restoration checks remain pending on dedicated test projects. See [build verification](BUILDING.md) and [validation status](../plugin/NATIVE-ACCEPTANCE.md).
