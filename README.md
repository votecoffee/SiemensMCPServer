# SiemensMCPServer

Local MCP server for Siemens HMI and offline PLC engineering through TIA Portal Openness.

SiemensMCPServer connects MCP clients such as Codex to a local TIA Portal engineering project. It provides Comfort/classic HMI editing, capability-checked Unified operations, offline PLC tools and reloadable feature workers. A separate WinForms utility supports classic HMI export and import.

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
| Comfort / classic HMI | Primary profile: V15.1. Text lists, native XML resources, scalar properties, supported object creation/deletion and bulk transfers. |
| Unified HMI | API-visible objects and supported screen properties. Text lists require the native paired-YAML API; V19 does not expose that route. |
| Siemens PLC | Offline blocks, data types, tags, native XML transfers, SCL/STL generation and compile diagnostics. |
| Desktop utility | Classic WinCC Comfort / Advanced export and import. |

The server does not provide device download, runtime control, watch or force operations. See [feature coverage](plugin/FEATURE-COVERAGE.md) and [validation status](plugin/NATIVE-ACCEPTANCE.md) for supported routes and their limits.

## Install

### Dependencies

- Windows x64 with .NET Framework 4.8.
- TIA Portal with the matching Openness API and required WinCC or STEP 7 components.
- A Windows account with Siemens Openness access, including membership in the local `Siemens TIA Openness` group.
- For source builds: a .NET 8 or newer SDK and .NET Framework 4.8 targeting support. Visual Studio can open `SiemensMCPServer.sln`.

Siemens engineering DLLs come from the installed TIA Portal environment and are not redistributed. V15.1 and V19 are build-tested profiles. Other installed APIs require their own build and validation; V21 uses a separate modular assembly family. See [build configuration](docs/BUILDING.md).

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

### CLI

Launch the server from a built package or its build output. It communicates over stdio and requires an MCP client:

```powershell
& .\Mcp\bin\Release\V15.1\net48\SiemensMCPServer.Mcp.exe --tia-version=V15.1
```

Writes are disabled by default. Add `--allow-writes` when applying changes, compiling, archiving or saving is intended. Optional `--artifact-root` and `--extensions-root` arguments choose local output directories. `TIA_PORTAL_VERSION` can also select the engineering version.

For the classic desktop utility:

```powershell
& .\bin\Release\SiemensMCPServer.exe --tia-version=V15.1
```

See [desktop usage](docs/CLASSIC-DESKTOP.md) for its screen and complete-HMI export/import workflows.

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
