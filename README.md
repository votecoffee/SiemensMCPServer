# SiemensMCPServer

Local MCP server for Siemens HMI and offline PLC engineering through TIA Portal Openness.

Two Windows applications are included:

- **Desktop GUI — `SiemensMCPServer.exe`:** a WinForms application for Comfort / Advanced HMI import/export, API-supported Unified transfers and offline PLC resource import/export.
- **MCP server — `SiemensMCPServer.Mcp.exe`:** connects clients such as Codex to Comfort/classic HMIs, capability-checked Unified operations and offline PLC tools, with reloadable feature workers.

| Target | Desktop GUI import/export | MCP server import/export |
| --- | --- | --- |
| Comfort / classic WinCC Comfort / Advanced | Supported | Supported |
| WinCC Unified | Supported where the installed API exposes native transfer methods | Supported where the installed API exposes native transfer methods |
| Siemens PLC software | Supported offline for API-exportable blocks, data types and tag tables | Supported offline for supported blocks, types and tag resources |

The repository and .NET solution use `SiemensMCPServer`; the plugin and MCP registration use the package ID `siemens-mcp-server`.

The current source release stamp is recorded in [app-version.json](plugin/app-version.json). Native and offline packages have separate acceptance and installation records. Both use app-owned, windowless launchers with immutable runtime selection, retained generation records and physical installation paths under Windows MSIX virtualization. Source changes, installed bytes and a chat's active connection are separate states. See [timestamp release rules](docs/VERSIONING.md).

## Table of Contents

- [Background](#background)
- [Install](#install)
- [Usage](#usage)
- [Development](#development)
- [API](#api)
- [Maintainers](#maintainers)
- [Contributing](#contributing)
- [License](#license)

## Background

The server exposes engineering operations through the Model Context Protocol (MCP). Clients inspect an explicit project and target, prepare a preview, review its diff and apply the matching candidate hash. Native writes use Siemens transactions and readback checks. Compile and save are separate operations.

| Area | Scope |
| --- | --- |
| Comfort / classic HMI | Primary profile: V15.1. Styled caption-run edits, text lists, native XML resources, reviewed control/behavior copying and removal, layout and reference changes, scalar properties, supported object creation/deletion, prototype-based screen/tag packages and bulk transfers. |
| Unified HMI | API-visible objects and supported screen properties. Text lists require the native paired-YAML API; V19 does not expose that route. |
| Siemens PLC | Offline blocks, data types, tags, native XML transfers, SCL/STL generation, readable-source export/search, API-supported cross-references and compile diagnostics. |
| Desktop utility | Comfort / Advanced screens and resources; Unified native transfers where available; offline PLC XML resources. |
| Libraries | Read-only project/open global library metadata: folders, types, versions and master-copy contents. No library updates or instance creation. |
| Project texts | Active-language discovery, native XLSX extraction/search, language-aware comment/description/text records and reviewed existing-translation updates. Native import acceptance pending. |

The server does not provide device download, runtime control, watch or force operations. See [feature coverage](plugin/FEATURE-COVERAGE.md) and [validation status](plugin/NATIVE-ACCEPTANCE.md) and the [RT Advanced V15.1 monitoring feasibility](docs/RUNTIME-TAG-MONITORING.md) for supported routes and their limits.

The direct native host includes explicit detach, four revision/hash-pinned guidance tools, retained parent identity, bounded EOF drain and Windows Job process/memory limits. The GUI exposes the shared Safeguards policy. Exact worker and TIA attachment claims retain ownership and uncertainty; no automatic cleanup of user-owned TIA or write replay is enabled. A packaged Comfort V15.1 pilot passed a selected screen edit, exact readback, compile, save/reopen, restoration and attached worker reload. See the [validation scope](plugin/NATIVE-ACCEPTANCE.md); other operations and families need their own acceptance.

[Comfort TextLists preparation](plugin/CLASSIC-TEXT-LIST-WORKFLOW.md) validates original XML encoding, reviewed entry/translation deltas and supplied native evidence. Selected TextLists stream from verified ZIPs without extracting unrelated graphics/screens. Required checks gate publication. Literal CR/LF preservation in exported XML does not establish that the native HMI accepts every multiline value.

| MCP profile | Exposure | Dependency and acceptance boundary |
| --- | --- | --- |
| [Direct offline](plugin-offline/README.md) | Three tools / seventeen supplied-content operations; four scoped skills | Windows/.NET 8; no TIA, worker or framework service. Bounded offline acceptance. |
| [Direct native](plugin-native/README.md) | 52 engineering/lifecycle tools plus four guidance/doctor readers; four native skills | Windows/.NET Framework 4.8 and matching Openness API. Selected Comfort V15.1 native acceptance; Unified/PLC acceptance remains separate. |
| [Earlier optional framework profile](plugin/APP-HOST.md) | App-owned discovery/context/receipt frontend to native backends and dynamic guidance | Separately pinned Python framework; direct-profile acceptance does not validate this route. |

The [CodexMCPBroker project](https://github.com/votecoffee/CodexMCPBroker) provides a **shared MCP application framework** and a **separate Windows management app**. Siemens keeps its standalone MCP server, application-owned Codex plugin and skills. There is no universal plugin. Direct Siemens profiles do not depend on the framework; the earlier framework profile retains its reviewed dependency pins. The independent manager is optional, is not installed by Siemens' root commands, and cannot infer native project state. Direct-native doctor reports management/restart unavailable. See [implementation architecture](docs/ARCHITECTURE.md) and [profile-specific guidance](plugin/CURRENT-GUIDANCE.md).

## Install

### Dependencies

- Windows x64 with .NET Framework 4.8.
- TIA Portal with the matching Openness API and required WinCC or STEP 7 components.
- A Windows account with Siemens Openness access, including membership in the local `Siemens TIA Openness` group.
- For source builds: a .NET 8 or newer SDK and .NET Framework 4.8 targeting support. Visual Studio can open `SiemensMCPServer.sln`.

Siemens engineering DLLs come from the installed TIA Portal environment and are not redistributed. V15.1, V16, V17, V18 and V19 API builds pass MCP/desktop checks; V16–V18 were checked using V19 compatibility APIs. Native project acceptance is narrower. V20/V21 SDK validation remains pending; V21 uses a separate modular assembly family. See the [compatibility matrix](plugin/API-COMPATIBILITY.md) and [build configuration](docs/BUILDING.md).

### Build and install the plugin

For analysis of exported content without TIA, use the separate [direct offline profile](plugin-offline/README.md):

```powershell
powershell.exe -NoProfile -File build/Build-OfflinePlugin.ps1
```

That package contains three direct tools and four scoped Siemens skills, requires Windows x64/.NET 8, and has no framework service, Python runtime, native attachment or controller route. It analyzes supplied XML/L5X/PLCopen content, HMI references/bindings/impact and classic/Unified text-list translations. Its installer skips framework preparation. Static/package checks and a disposable MCP/Codex loading check are separate acceptance steps. The engineering package described below remains a separate profile.

Install/select the reviewed offline package with `.\InstallMcp.cmd -Package 'C:\packages\reviewed-offline.zip' -NoPause`. This stages the standalone plugin and selects retained bytes behind a stable app-owned launcher/cwd outside the checkout and disposable plugin caches. It does not start MCP or edit Codex settings. Publish the returned stable binding through one serialized host-settings update, then check a fresh connection. See [launcher deployment and verification](docs/STABLE-LAUNCHER.md). The root `InstallAndRun.cmd` and `Run.cmd` remain desktop GUI commands.

For `siemens_offline_analyze`, pass `input` as one JSON string encoding the described operation object. `siemens_offline_describe.inputSchema` applies to the decoded object; the [offline guide](plugin-offline/README.md) includes an exact call example and input limits.

The [native direct candidate](plugin-native/README.md) restores the existing TIA engineering implementation through a standalone stdio host, with 56 declared tools, four native workflow skills, revision-pinned guidance, explicit detach and bounded private-worker/parent/EOF lifetimes. Build it without starting any runtime:

```powershell
powershell.exe -NoProfile -File build/Build-NativePlugin.ps1 -TiaPortalVersion V15.1
```

This candidate has no gateway or shared-service dependency. Normal installation requires an acceptance receipt bound to the exact package for disposable native edits, compile, save/reopen and restoration. The [native launcher guide](docs/NATIVE-LAUNCHER.md) describes installation, editable safeguards and extension staging. Building a candidate preserves the accepted offline installation. Source builds and package hashes alone do not establish native engineering acceptance.

The earlier framework engineering profile remains available for existing deployments. Build that separate profile for the installed API:

```powershell
git clone https://github.com/votecoffee/SiemensMCPServer.git
cd SiemensMCPServer
powershell.exe -NoProfile -File build/Build-Plugin.ps1 -TiaPortalVersion V15.1 -FrameworkWheel 'C:\packages\universal_codex_tools-0.20.0-py3-none-any.whl'
```

The build prints the ZIP path and creates its SHA-256 sidecar under `artifacts/`. Pass that exact ZIP to the installer, keeping the sidecar beside it:

```powershell
powershell.exe -NoProfile -File build/Install-CodexPlugin.ps1 -Package 'C:\path\to\plugin.zip'
```

The installer installs the standalone Siemens Codex plugin with its app-owned framework gateway (framework 0.20.0 / MCP Python 2.3.0). Python 3.13 is required; exact dependencies are installed offline from the verified bundle into a dedicated per-user runtime. Use `-PrepareOnly` to verify and stage a package without changing Codex settings. Load the installed tools and skills in a new Codex chat. See the [plugin guide](plugin/README.md).

For that optional profile, `universal-codex-tools` / `universal_tools` identify the versioned [shared framework dependency](https://github.com/votecoffee/CodexMCPBroker), not a universal plugin. The framework repository's newer source does not upgrade this app's reviewed wheel automatically. The [separate Windows manager](https://github.com/votecoffee/CodexMCPBroker/blob/main/docs/service-management.md) has its own installer/runtime and needs no Codex connection.

Fresh installations and upgrades always install the standalone plugin. Obsolete integration-choice files do not affect installation. Concurrent installers using one root are rejected before settings changes.

Version 0.18.2 also retains [evidence-backed request reconciliation](plugin/APP-HOST.md#reconciled-requests-and-upgrades) for upgrades after independently reviewed interrupted work. Original error/uncertainty receipts and replay protection remain intact; the installer does not acknowledge outcomes automatically.

## Usage

### Desktop GUI

Double-click [InstallAndRun.cmd](InstallAndRun.cmd) in the project root, or run:

```powershell
.\InstallAndRun.cmd
```

It builds the V15.1 desktop GUI, installs it under `%LOCALAPPDATA%\SiemensMCPServer\Desktop`, adds **SiemensMCPServer (V15.1)** to the Start menu with an application icon, and opens the GUI. No administrator access is required for the app installation. Building requires a .NET 8 or newer SDK, .NET Framework 4.8 targeting support and the selected installed Openness API. Later, use the Start menu shortcut or [Run.cmd](Run.cmd) to launch without rebuilding.

For another installed API, use `.\InstallAndRun.cmd -TiaPortalVersion V19`. Add `-TiaPortalInstallVersion V19` when an older API is supplied by that installation; `-NoShortcut` skips the Start menu entry. Each update uses a fresh application folder and retains earlier versions. These commands install the GUI only; Codex plugin setup remains separate. See [desktop installation options](docs/DESKTOP.md#install-and-launch).

Both root commands accept `-Help` and `-NoPause`. Use `InstallAndRun.cmd -InstallOnly` to install without opening the GUI, and `Run.cmd -CheckOnly` to verify the selected installation without launching it. Quoted paths with spaces/Unicode are forwarded through the shared command wrapper; failures return a nonzero exit code.

Open the project in TIA Portal, attach to its process and select a target labeled **Comfort / Advanced**, **Unified** or **PLC**. Classic targets retain selected-screen/folder export, all-screen export, screen XML import, screens-only import and complete-HMI export/import. Unified and PLC targets show a resource browser with selected/whole-target exports, native resource-folder imports and export-bundle imports. Imports leave the project unsaved; inspect and compile before saving explicitly.

**Unified transfers depend on the selected API. V19 supports native tag/script transfers, but has no native screen or text-list transfer route. PLC transfers require a verified Offline state.** See [desktop usage](docs/DESKTOP.md) and [classic screen workflows](docs/CLASSIC-DESKTOP.md). The plugin ZIP packages the MCP server and workers; install the desktop application separately with the command above. Its generated DLLs and configuration stay beside the executable.

### Safeguards settings

Desktop 1.3.0 adds **Safeguards...** for the shared desktop/MCP policy: write permissions, native deletion and overwrite ceilings, import/Save confirmations, preview expiry, request limits, project scope and evidence folders. Open the editor without connecting to TIA with `Run.cmd -Safeguards`. Host 0.15.0 reads the same per-user policy before subsequent protected operations; scheduler changes require a host restart. MCP still requires `--allow-writes` and client permission. Mandatory integrity checks are visible and remain enforced. See [all settings and their exact scope](plugin/SAFEGUARDS.md).

### CLI

Launch the server from a built package or its build output. It communicates over stdio and requires an MCP client:

```powershell
& .\Mcp\bin\Release\V15.1\net48\SiemensMCPServer.Mcp.exe --tia-version=V15.1
```

Writes are disabled by default. Add `--allow-writes` when applying changes, compiling, archiving or saving is intended. Optional `--artifact-root` and `--extensions-root` arguments choose local output directories. `TIA_PORTAL_VERSION` can also select the engineering version.

### Engineering workflow

1. Open the intended project in TIA Portal.
2. Discover compatible APIs and processes; attach using the exact process ID and project path. Allow the Siemens access prompt if shown.
3. Inspect the returned HMI or PLC target and its capabilities.
4. Read the resource, preview changes and review the diff and candidate hash.
5. Apply the matching plan once, inspect native readback, compile and save explicitly.

An MCP client can begin with these discovery calls:

```text
tia_list_versions()
tia_list_processes()
tia_get_api_capabilities()
```

Direct native clients keep one owning stdio connection: attachments, preview plans and worker generations belong to that host. Read the current guide, attach to an explicit process/project and invoke the direct live tool schemas. A transport that starts a fresh host for every call cannot carry an attach/preview/apply sequence. Explicit `tia_detach` releases the Openness attachment and previews without saving or closing user TIA. Preserve uncertain outcomes and inspect native state; do not replay an old plan or switch hosts to bypass retained evidence.

Only the earlier framework profile requires explicit [app-service startup](plugin/APP-HOST.md#installation-and-private-state) and acquisition of `siemens-engineering`. Keep that context for the workflow. A stopped service is reported without automatic launch/restart. Direct profiles require no service/context acquisition. The desktop **Safeguards...** editor controls the native connection ceiling and Job memory along with the other shared policy settings.

### Reload feature workers

Stage a trusted worker bundle, then call `tia_reload_extensions` for its module. Successful reload preserves the TIA attachment and invalidates outstanding previews; reread before another edit. Host changes require a host upgrade. See [extension development and updates](plugin/EXTENSIONS.md).

Comfort extensions build screen/tag packages from native XML prototypes and validate supplied PLC bindings. Review generated files before native import; see the [Comfort package workflow](plugin/COMFORT-PACKAGES.md).

PLC extensions export readable native SCL/STL/DB/UDT source, search it and read API-supported cross-references. They require explicit PLC/object paths and verified Offline state; see [PLC inspection](plugin/PLC-INSPECTION.md).

Project extensions provide bounded [library metadata browsing](plugin/LIBRARY-INSPECTION.md) and [multilingual project texts](plugin/PROJECT-TEXTS.md). Version 0.12.0 extracts comments, descriptions, captions and text values by category/path and explicit active culture pair, then previews exact translation updates with native workbook evidence. Source-language changes require opt-in; apply uses a fresh snapshot and native import/readback. The companion `build/Edit-ProjectTexts.ps1` extracts complete retained workbooks or prepares disk-only candidates. The bundled [library](plugin/skills/inspect-libraries/SKILL.md) and [project-text](plugin/skills/inspect-project-texts/SKILL.md) skills describe selection and paging.

The native host supports [concurrent requests](plugin/CONCURRENCY.md). Direct MCP clients can run pure local analysis in parallel worker processes (two jobs by default, configurable with `--analysis-parallelism=1..8`); native operations and transactions retain one FIFO STA lane. The standalone framework gateway currently serializes calls to its native backend. Queued native requests can be cancelled before dispatch, and `tia_list_extensions` reports lane limits, active/queued work and worker IDs. Reload drains active generations; conflicting edits retain their fingerprint guards.

The project-text import route requires a 0.12.0 host upgrade/reconnect once; later translation logic can reload on the same contract. V15.1 native extraction has been exercised; import/rollback and Unified/PLC category acceptance remain pending. The desktop GUI does not yet expose this project-text workflow. See [API compatibility](plugin/API-COMPATIBILITY.md) and [native validation status](plugin/NATIVE-ACCEPTANCE.md).

Comfort workers support [layout](plugin/COMFORT-LAYOUT.md), [styled captions](plugin/COMFORT-SCREEN-TEXTS.md), [control/group copies](plugin/COMFORT-SCREEN-ITEMS.md), [symbolic tag/connection rebinding](plugin/COMFORT-REFERENCES.md) and [event/animation copies with explicit owner links](plugin/COMFORT-BEHAVIORS.md). The 0.10.4 host/worker fixes native V15.1 `ObjectName` controls and `#ID` references, including import readback comparisons. Upgrade/reconnect the host before staging this Comfort bundle; subsequent compatible workers can reload through the same `classicReferences` contract. See [native XML compatibility](plugin/EXTENSIONS.md#classic-native-xml).

Comfort worker 0.10.5 adds layout for horizontal/vertical lines with one zero extent, updates their primary endpoints and dependent bounds together, exposes control `TabIndex` values for reviewed copies, and preserves exported composition positions when copying behaviors. Inconsistent line geometry is rejected before scalar-edit staging. These worker changes reload on host 0.10.4 or newer. The 0.10.5 host additionally owns runtime shutdown cleanup and records failed readback artifact paths; obtaining those host changes requires an upgrade/reconnect.

Comfort worker 0.11.0 adds [project reference analysis](plugin/HMI-REFERENCE-ANALYSIS.md), Rockwell L5X/PLCopen declaration import, member/type/array/alias binding checks and proposed-change impact reports. A companion command reads complete export folders and large controller files directly. These read-only operations reload on host 0.10.4 or newer; native connection partners and runtime behavior require separate checks.

## Development

Run the offline tests from the repository root:

```powershell
dotnet test Tests/SiemensMCPServer.Tests.csproj -c Release -p:RestoreLockedMode=true
dotnet test Offline.Tests/SiemensMCPServer.Offline.Tests.csproj -c Release -p:RestoreLockedMode=true
dotnet test Launcher.Tests/SiemensMCPServer.Launcher.Tests.csproj -c Release -p:RestoreLockedMode=true
```

[Build configuration](docs/BUILDING.md) covers API selection, desktop/MCP builds, packaging and protocol checks. Offline tests do not establish native project or runtime acceptance.

Keep machine-specific notes, project exports, acceptance evidence, logs and customer data outside the checkout or under the ignored `.local/` directory. `artifacts/`, IDE caches and build outputs are also ignored. See [contribution guidelines](CONTRIBUTING.md).

## API

The installed plugin exposes 14 stable discovery/invocation tools backed by 51 Siemens engineering operations. The native .NET executable also retains its 51 direct MCP tools. See [gateway usage and lifecycle](plugin/APP-HOST.md). Their descriptions and JSON schemas are available through MCP `tools/list`.

- `tia_*`: API/process discovery, project attachment, status, archive/save/reopen and extension discovery/reload.
- `siemens_*`: current workflow/reference discovery and pinned reads, plus a read-only capability/doctor report; no engineering context is required.
- `hmi_*`: Comfort/classic and capability-checked Unified inspection, previews, edits, transfers and compilation.
- `plc_*`: offline PLC inspection, previews, transfers, source generation and compilation.

See the [plugin guide](plugin/README.md), [feature coverage](plugin/FEATURE-COVERAGE.md) and [workflow skills](plugin/skills) for operation-specific requirements. No OpenAI API key is required to connect this local server; the repository does not include a model-calling application or public HTTP service.

## Maintainers

[@votecoffee](https://github.com/votecoffee).

## Contributing

Questions and bug reports are welcome through [GitHub Issues](https://github.com/votecoffee/SiemensMCPServer/issues). Pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), use sanitized fixtures, run relevant checks and state native-validation limits.

## License

[MIT](LICENSE) © 2026 votecoffee. Packaged dependency notices are in [plugin/licenses](plugin/licenses). Siemens software and SDK assemblies are supplied separately by Siemens.
