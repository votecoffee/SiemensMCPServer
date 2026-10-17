# SiemensMCPServer

Local MCP server for Siemens HMI and offline PLC engineering through TIA Portal Openness, with reloadable feature workers and a classic WinCC Comfort / Advanced WinForms utility.

## Codex / MCP integration

The repository also contains a local .NET Framework 4.8 MCP server with 51 tools, including five fixed tools for reloadable feature workers. Comfort/classic V15.1 is the primary profile, with dedicated native XML scalar editing, text-list editing, object/resource tools, legacy WinForms export conversion and bulk transfers. Unified retains version-specific capabilities. Siemens PLC tools add offline blocks/data types/tags, native XML import/export, SCL/STL generation and compile diagnostics. Mutations use hash-bound previews and Siemens transactions; saving remains explicit. See [feature coverage](plugin/FEATURE-COVERAGE.md) for implemented routes and native-validation limits and [extension updates](plugin/EXTENSIONS.md) for upgrades without restarting the host. Selected Comfort V15.1 native tests have passed; see [native acceptance](plugin/NATIVE-ACCEPTANCE.md) for scope and remaining trials. The WinForms desktop UI remains classic-only.

See [the plugin guide](plugin/README.md) for supported scope, build and installation commands, and the native acceptance boundary. Build a distributable plugin with `powershell -NoProfile -File build/Build-Plugin.ps1 -TiaPortalVersion V15.1`. The server is an additional executable; the existing desktop workflow remains available.

The source is version-neutral across the classic monolithic Openness API used by TIA Portal V15.1 through V20. The build selects the API version to compile against, and the application resolves that API at runtime from an installed TIA Portal version.

## Supported TIA Portal versions

- **V15.1 through V20:** supported through the classic monolithic `Siemens.Engineering.dll` / `Siemens.Engineering.Hmi.dll` API.
- **V21:** supported as a separate V21 build using the modular `Siemens.Engineering.Base.dll`, `Siemens.Engineering.WinCC.dll`, and `Siemens.Engineering.WinCC.Extension.dll` assemblies.

Siemens preserves older Openness APIs in several later pre-V21 TIA releases. That allows one classic build to work with more than one installed engineering version when the referenced API is present. V21 is an API/assembly boundary: a V21 build is intended for V21, while pre-V21 builds use the legacy assembly family.

## Requirements

- A supported TIA Portal version with TIA Portal Openness installed
- WinCC Comfort / Advanced for classic HMI projects
- Visual Studio with the .NET Framework 4.8 targeting pack
- Windows user in the local `Siemens TIA Openness` group
- TIA Portal running with the project already open

The Siemens DLL references use `Copy Local = False`; Siemens DLLs are not redistributed.

## Build

Open `SiemensMCPServer.sln`, restore NuGet packages, and build. Both application executables target x64. The MCP project also requires a .NET 8 or newer SDK; building the original `SiemensMCPServer.csproj` alone does not require the MCP SDK packages.

The default compile-time API is V15.1. To build against another installed Openness API, set `TiaPortalVersion`:

```bat
msbuild SiemensMCPServer.sln /restore /p:Configuration=Release /p:TiaPortalVersion=V17
```

TIA Portal V21 uses its modular net48 assemblies automatically:

```bat
msbuild SiemensMCPServer.sln /restore /p:Configuration=Release /p:TiaPortalVersion=V21
```

If the API DLL comes from a newer installed TIA version, also set `TiaPortalInstallVersion`. For example, TIA Portal V20 can provide its retained V17 API:

```bat
msbuild SiemensMCPServer.sln /restore /p:Configuration=Release /p:TiaPortalVersion=V17 /p:TiaPortalInstallVersion=V20
```

You can also point directly at a PublicAPI directory:

```bat
msbuild SiemensMCPServer.sln /restore /p:Configuration=Release /p:TiaPublicApiDir="C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V17"
```

The build fails with a clear message if the requested `Siemens.Engineering.dll` or `Siemens.Engineering.Hmi.dll` cannot be found.

### Runtime version selection

At startup the program reads the Siemens Openness registry tree:

```text
HKEY_LOCAL_MACHINE\SOFTWARE\Siemens\Automation\Openness
```

The registry uses technical version keys such as `20.0` and API-version subkeys such as `20.0.0.0`. The program converts those to user-facing labels such as `V20` and identifies installations that contain the exact API assembly version referenced by the build.

- One compatible version: it is selected automatically.
- Multiple compatible versions: the application prompts you to choose one.
- No compatible versions: the application explains which Openness API the build expects.

For unattended launch, select the engineering version explicitly:

```bat
SiemensMCPServer.exe --tia-version=V18
```

or set:

```text
TIA_PORTAL_VERSION=V18
```

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
