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
| Comfort / classic HMI | Primary profile: V15.1. Text lists, native XML resources, scalar properties, supported object creation/deletion, prototype-based screen/tag packages and bulk transfers. |
| Unified HMI | API-visible objects and supported screen properties. Text lists require the native paired-YAML API; V19 does not expose that route. |
| Siemens PLC | Offline blocks, data types, tags, native XML transfers, SCL/STL generation, readable-source export/search, API-supported cross-references and compile diagnostics. |
| Desktop utility | Comfort / Advanced screens and resources; Unified native transfers where available; offline PLC XML resources. |

The server does not provide device download, runtime control, watch or force operations. See [feature coverage](plugin/FEATURE-COVERAGE.md) and [validation status](plugin/NATIVE-ACCEPTANCE.md) for supported routes and their limits.

## Install

### Dependencies

- Windows x64 with .NET Framework 4.8.
- TIA Portal with the matching Openness API and required WinCC or STEP 7 components.
- A Windows account with Siemens Openness access, including membership in the local `Siemens TIA Openness` group.
- For source builds: a .NET 8 or newer SDK and .NET Framework 4.8 targeting support. Visual Studio can open `SiemensMCPServer.sln`.

Siemens engineering DLLs come from the installed TIA Portal environment and are not redistributed. V15.1, V16, V17, V18 and V19 API builds pass MCP/desktop checks; V16–V18 were checked using V19 compatibility APIs. Native project acceptance is narrower. V20/V21 SDK validation remains pending; V21 uses a separate modular assembly family. See the [compatibility matrix](plugin/API-COMPATIBILITY.md) and [build configuration](docs/BUILDING.md).

### Build and install the plugin

Build the package for the installed API:

```powershell
git clone https://github.com/votecoffee/SiemensMCPServer.git
cd SiemensMCPServer
powershell.exe -NoProfile -File build/Build-Plugin.ps1 -TiaPortalVersion V15.1
```

The build prints the ZIP path and creates its SHA-256 sidecar under `artifacts/`. Pass that exact ZIP to the installer, keeping the sidecar beside it:

```powershell
powershell.exe -NoProfile -File build/Install-CodexPlugin.ps1 -Package 'C:\path\to\plugin.zip'
```

Use `-PrepareOnly` to verify and stage a package without changing Codex settings. Load the installed tools and skills in a new Codex chat. The [plugin guide](plugin/README.md) covers client setup, direct MCP registration and migration from the former `siemens-tia-openness` identity.

## Usage

### Desktop GUI

Build and launch the WinForms application independently of the MCP server:

```powershell
dotnet build SiemensMCPServer.csproj -c Release -p:TiaPortalVersion=V15.1
& .\bin\Release\SiemensMCPServer.exe --tia-version=V15.1
```

Open the project in TIA Portal, attach to its process and select a target labeled **Comfort / Advanced**, **Unified** or **PLC**. Classic targets retain selected-screen/folder export, all-screen export, screen XML import, screens-only import and complete-HMI export/import. Unified and PLC targets show a resource browser with selected/whole-target exports, native resource-folder imports and export-bundle imports. Imports leave the project unsaved; inspect and compile before saving explicitly.

**Unified transfers depend on the selected API. V19 supports native tag/script transfers, but has no native screen or text-list transfer route. PLC transfers require a verified Offline state.** See [desktop usage](docs/DESKTOP.md) and [classic screen workflows](docs/CLASSIC-DESKTOP.md). The plugin ZIP packages the MCP server and workers; build the desktop executable separately with the command above. Keep its generated DLLs and configuration beside the executable.

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

Keep a persistent MCP connection for engineering workflows: attachments, preview plans and worker generations belong to the running host. A transport that starts a fresh host for each call cannot carry an attach/preview/apply sequence. Inspect current native state and retained artifacts before retrying an uncertain write.

### Reload feature workers

Stage a trusted worker bundle, then call `tia_reload_extensions` for its module. Successful reload preserves the TIA attachment and invalidates outstanding previews; reread before another edit. Host changes require a host upgrade. See [extension development and updates](plugin/EXTENSIONS.md).

Comfort worker 0.7.2 adds offline screen/tag package generation from native XML prototypes and validation of supplied PLC bindings. These operations load through the existing 0.7.1 host. Review the generated package before native import; see the [Comfort package workflow](plugin/COMFORT-PACKAGES.md).

The 0.7.2 host adds Unified compile-service lookup within the HMI's owning device when its software has no compiler. Results report the selected native scope. This broker change requires a host upgrade/reconnect; native Unified compile acceptance remains pending.

Host/worker 0.7.3 adds PLC `read_source`, `find_in_source` and `cross_references` through `tia_read_extension`. These use explicit PLC/object paths and verified Offline state. Native readable-source export supports SCL/STL, data blocks and PLC types; native cross-references require a matching API and object service (present in inspected V18/V19 APIs, absent in V15.1/V16/V17). The new broker read route requires a host upgrade/reconnect. Native project acceptance remains pending; see [PLC inspection](plugin/PLC-INSPECTION.md).

Host 0.7.4 supports flat Unified screen discovery on V16/V17 and prepares explicit V21 module loading and project-graphics service access. It requires a host upgrade/reconnect. V15.1–V19 API builds are checked; V20/V21 native acceptance remains pending. See [API compatibility](plugin/API-COMPATIBILITY.md).

## Development

Run the offline tests from the repository root:

```powershell
dotnet test Tests/SiemensMCPServer.Tests.csproj -c Release -p:RestoreLockedMode=true
```

[Build configuration](docs/BUILDING.md) covers API selection, desktop/MCP builds, packaging and protocol checks. Offline tests do not establish native project or runtime acceptance.

Keep machine-specific notes, project exports, acceptance evidence, logs and customer data outside the checkout or under the ignored `.local/` directory. `artifacts/`, IDE caches and build outputs are also ignored. See [contribution guidelines](CONTRIBUTING.md).

## API

The server currently exposes 51 tools. Their descriptions and JSON schemas are available through MCP `tools/list`.

- `tia_*`: API/process discovery, project attachment, status, archive/save/reopen and extension discovery/reload.
- `hmi_*`: Comfort/classic and capability-checked Unified inspection, previews, edits, transfers and compilation.
- `plc_*`: offline PLC inspection, previews, transfers, source generation and compilation.

See the [plugin guide](plugin/README.md), [feature coverage](plugin/FEATURE-COVERAGE.md) and [workflow skills](plugin/skills) for operation-specific requirements. No OpenAI API key is required to connect this local server; the repository does not include a model-calling application or public HTTP service.

## Maintainers

[@votecoffee](https://github.com/votecoffee).

## Contributing

Questions and bug reports are welcome through [GitHub Issues](https://github.com/votecoffee/SiemensMCPServer/issues). Pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), use sanitized fixtures, run relevant checks and state native-validation limits.

## License

[MIT](LICENSE) © 2026 votecoffee. Packaged dependency notices are in [plugin/licenses](plugin/licenses). Siemens software and SDK assemblies are supplied separately by Siemens.
