# Build configuration

Build on Windows x64 with a .NET 8 or newer SDK, .NET Framework 4.8 targeting support and the required installed Siemens Openness API. Visual Studio can open `SiemensMCPServer.sln`; the desktop project can also be built independently.

## MCP server and desktop utility

The default compile-time API is V15.1:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V15.1
dotnet build SiemensMCPServer.csproj -c Release -p:TiaPortalVersion=V15.1
```

The MCP build stages bundled workers automatically. Use `-p:StageFeatureExtensions=false` only when a separate build step supplies them.

For a per-user desktop install, run [InstallAndRun.cmd](../InstallAndRun.cmd) from the checkout. It builds/publishes the GUI, creates the Start menu shortcut and opens it. [Run.cmd](../Run.cmd) launches the installed selection afterward. See [desktop installation](DESKTOP.md#install-and-launch) for API/profile and path options. These commands leave Codex plugin settings alone.

The desktop executable embeds the supplied PLC/HMI icon in `assets/SiemensMCPServer.ico`. Its eight PNG sizes are retained under `assets/siemens-plc`. To rebuild the ICO from those unchanged frames on Windows:

```powershell
powershell.exe -NoProfile -File build/New-AppIcon.ps1
```

## Select an installed API

`TiaPortalVersion` chooses the build's API. `TiaPortalInstallVersion` chooses the installation supplying it; newer installations may retain older APIs:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V17 -p:TiaPortalInstallVersion=V20
```

An explicit API directory can be supplied instead:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V17 '-p:TiaPublicApiDir=C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V17'
```

V15.1–V20 use the classic assembly family. V21 uses separate modular `net48` Base/WinCC assemblies and requires the STEP 7 module for PLC support in both applications. Unified discovery explicitly loads the optional `Siemens.Engineering.WinCCUnified.dll` module when installed:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V21
```

These examples require the corresponding installed API. V15.1–V19 are build-tested API profiles; V16–V18 were checked with compatibility assemblies supplied by V19. V20/V21 SDK and native validation remain pending. Pre-V21 and V21 builds are not interchangeable at runtime. Missing API assemblies produce build errors; Siemens engineering DLLs are not copied into packages. See the [compatibility matrix](../plugin/API-COMPATIBILITY.md).

## Runtime selection

The resolver reads `HKEY_LOCAL_MACHINE\SOFTWARE\Siemens\Automation\Openness` and selects installations exposing the API version referenced by the executable. The desktop UI prompts when several compatible versions exist. MCP clients use `tia_list_versions` and `tia_select_version`, or supply `--tia-version` at launch. `TIA_PORTAL_VERSION` is also supported. Changing the loaded engineering API requires a host restart.

For an API V16 build supplied by Portal V19, the runtime engineering selection is `V19`. The resolver validates full assembly identities and uses only the `net48` registry layout for V21. It does not select a .NET 8 assembly for this .NET Framework host.

## Package and verify

```powershell
powershell.exe -NoProfile -File build/Build-Plugin.ps1 -TiaPortalVersion V15.1 -FrameworkWheel 'C:\packages\universal_codex_tools-0.18.0-py3-none-any.whl'
py -3 -X utf8 scripts/verify_plugin.py 'C:\path\to\plugin.zip'
```

The package contains the stdio host, workers, plugin manifests, skills, public plugin documentation, dependency notices, source identity and hashes. Only Git-tracked public inputs are eligible for packaging and source inventory. Ignored/private folders and untracked drafts are excluded. A fresh ZIP and SHA-256 sidecar are written under `artifacts/`.

## Tests

```powershell
dotnet test Tests/SiemensMCPServer.Tests.csproj -c Release -p:RestoreLockedMode=true
py -3 -X utf8 scripts/test_mcp_protocol.py Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe
```

The protocol probe starts a local stdio host and never attaches to a project. Adding `--native-read-only` checks installed API capabilities and process enumeration without attaching or editing. Contract checks load installed API metadata without creating a TIA instance:

```powershell
powershell.exe -NoProfile -File scripts/test_native_contracts.ps1 -Version V15.1 -ServerDirectory Mcp/bin/Release/V15.1/net48
```

Offline and metadata checks do not establish native project or runtime acceptance. See [validation status](../plugin/NATIVE-ACCEPTANCE.md). Keep machine-specific evidence outside the public tree or under `.local/`.

The desktop installer check publishes into isolated `.local` folders and creates test shortcuts outside the real Start menu. It verifies install/update preservation, launch-path handling, shortcut arguments/icon, dependency files, missing-SDK rejection and altered-receipt/executable rejection without opening the GUI or attaching to TIA:

```powershell
powershell.exe -NoProfile -File scripts/test_desktop_installer.ps1 -ReportPath artifacts/desktop-installer.json
```

Pass `-Version V19` for that API, or `-Version V17 -TiaPortalInstallVersion V19` for a compatibility SDK supplied by the installed V19 environment. Installer checks cover V15.1 and V19; GUI/native transfer acceptance remains a separate result.

Run the installed API matrix without attaching to a project:

```powershell
powershell.exe -NoProfile -File build/Test-ApiProfiles.ps1
```

The script finds matching APIs in installed Portal directories, builds each available profile, checks native signatures and MCP transport, and runs desktop build/smoke checks. Missing SDKs are reported separately; an installed profile failure returns a nonzero exit code. Reports and command logs default to ignored `artifacts/`. Invoke `& .\build\Test-ApiProfiles.ps1 -Versions V16,V17,V18` directly from PowerShell to restrict profiles. Use `-AutomationRoot` for a different installation root or `-SkipDesktop`/`-SkipProtocol` for narrower checks. A skipped check is not a pass.

For individual metadata checks against a compatibility API, pass `-TiaPortalInstallVersion V19` or `-TiaPublicApiDir`. For a desktop smoke test, pass the matching runtime installation using `-TiaPortalInstallVersion V19`.

The Comfort package integration probe installs a staged worker into an isolated extension root, reloads it in one persistent host, generates synthetic screen/tag XML and checks file materialization and rejection controls. It never attaches to a project:

```powershell
powershell.exe -NoProfile -File build/Build-Extensions.ps1 -Destination artifacts/comfort-package-probe -Modules comfort
py -3 -B -X utf8 scripts/test_comfort_package.py Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe --worker-root artifacts/comfort-package-probe
```

This probe can also run against an existing 0.7.1 host executable to check compatibility with a newer worker. See the [Comfort package guide](../plugin/COMFORT-PACKAGES.md) for supported input and native acceptance limits.

The PLC inspection probe exercises the real worker with synthetic source/reference snapshots and the host's catalog, native API capability report, unattached-read rejection, PLC reload and rejected-startup diagnostics. It does not attach or perform native source/reference reads:

```powershell
py -3 -B -X utf8 scripts/test_plc_inspection.py Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe
py -3 -B -X utf8 scripts/test_plc_inspection.py Mcp/bin/Release/V19/net48/SiemensMCPServer.Mcp.exe
```

Use optional `--previous-host` with a pre-0.7.3 host executable that retains its bundled workers to check rejection of the new PLC route without losing the previous running generation. See [PLC inspection](../plugin/PLC-INSPECTION.md) for version and native acceptance boundaries.

The library probe exercises the real project worker, native API-member reporting, strict/unattached read rejection, worker reload and rejected-startup rollback. It does not attach or read a native library:

```powershell
py -3 -B -X utf8 scripts/test_library_inspection.py Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe
```

Optional `--previous-host` accepts a 0.7.4 executable with its bundled workers to verify that the newer existing PLC module still loads on that host. The new project module requires host 0.8.0; see [library inspection](../plugin/LIBRARY-INSPECTION.md).

The project-text probe uses a synthetic XLSX with the real net48 worker and checks Unicode/cell paging/search, hash failures, host preflight, reload and optional rejection/rollback on a 0.8.0 host. It never attaches or performs a native text export:

```powershell
py -3 -B -X utf8 scripts/test_project_texts.py Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe
```

Use `--previous-host` with an extracted 0.8.0 executable retaining its bundled project worker for the snapshot-kind upgrade check. See [project-text inspection](../plugin/PROJECT-TEXTS.md) for capture limits and native acceptance boundaries.

## Desktop verification

The desktop project uses SDK-style .NET Framework 4.8 builds and locked NuGet dependencies. Its native transfer adapter source is shared with MCP, without a reference to the MCP executable or host dependencies.

```powershell
dotnet build SiemensMCPServer.csproj -c Release -p:TiaPortalVersion=V15.1 -p:RestoreLockedMode=true
powershell.exe -NoProfile -STA -File scripts/test_desktop_smoke.ps1 -DesktopDirectory bin/Release -Version V15.1
powershell.exe -NoProfile -File scripts/test_native_contracts.ps1 -Version V15.1 -ServerDirectory bin/Release -ExecutableName SiemensMCPServer.exe
```

Use a separate output/intermediate directory when retaining a second GUI profile:

```powershell
dotnet build SiemensMCPServer.csproj -c Release -p:TiaPortalVersion=V19 -p:RestoreLockedMode=true -p:OutputPath=bin/Release/V19/ -p:IntermediateOutputPath=obj/V19/Release/
powershell.exe -NoProfile -STA -File scripts/test_desktop_smoke.ps1 -DesktopDirectory bin/Release/V19 -Version V19
powershell.exe -NoProfile -File scripts/test_native_contracts.ps1 -Version V19 -ServerDirectory bin/Release/V19 -ExecutableName SiemensMCPServer.exe
```

The smoke script constructs the form without process enumeration, uses synthetic target families to check view selection, and verifies disabled actions without an attachment. Native contract checks inspect installed method signatures only. Neither script attaches to a project or performs native imports/exports. The optional smoke `-ScreenshotPath` renders an invisible test form; keep output outside the public tree or under `.local/`.

## Safeguards policy checks

```powershell
py -3 -B -X utf8 scripts/test_safeguards.py Mcp/bin/Release/V15.1/net48/SiemensMCPServer.Mcp.exe
powershell.exe -NoProfile -STA -File scripts/test_desktop_smoke.ps1 -DesktopDirectory bin/Release -Version V15.1
```

These use isolated settings files. They verify production-host permission gates before native dispatch, live policy refresh, configured scheduling, malformed/deleted policy rejection, and save/reload through the real desktop property grid. They do not attach or invoke native project writes. Unit tests cover preview expiry, revoked permissions after preview and stale-editor rejection. See [Safeguards](../plugin/SAFEGUARDS.md).

## Standalone adapter verification

```powershell
py -3 -B -X utf8 scripts/test_adapter_integration.py 'C:\packages\extracted\server\SiemensMCPServer.Mcp.exe'
py -3 -B -X utf8 scripts/test_codex_installer.py 'C:\packages\siemens.zip'
```

The adapter profile builds a separate test executable with a fake `IHmiSession` below the production handlers, queues and feature workers. It uses only Python's standard library, temporary settings and fake project state; Siemens assemblies are prohibited. Checks cover exact multilingual readback, plan ownership/hash/staleness, successful/failed reload, separate compile/save/reopen, process restart and a lost reply without replay. Native transactions and actual Codex chat behavior need their own acceptance.

The installer profile uses disposable Codex homes and fake CLI commands to verify standalone installation, repeats, obsolete choice files, concurrent admission and package rejection controls. It never changes the normal Codex profile.

## App-owned framework build

Plugin 0.20.0 pins `universal-codex-tools==0.18.0`, MCP Python 2.3.0 and matching wire types in `plugin/framework/requirements-lock.txt`. Build or obtain a verified wheel from framework commit `7d8a0ccdc06598313061147ee8774837118a8861`, then supply it through `-FrameworkWheel`. Packaging checks its RECORD/metadata and collects the exact binary dependency wheels. Runtime installation uses `--no-index` and the bundled wheelhouse. The C# SDK remains 2.2.0.

The reviewed framework wheel SHA-256 is `0c1084b424f079957ebd3bec763e609564aaef64cd1fedee78b61a45bfbc24ac`. Build and runtime bundle checks enforce those exact bytes as well as the source commit and dependency pins. Management integration is part of the owning Python host, leaving the native TIA profiles intact. Run `scripts/test_service_management.py` and the existing framework integrity/lifecycle/reconciliation/adapter checks in the pinned test runtime. Keep synthetic registries separate from the real manager's registry. The native restart feature remains disabled; tests never restart a live engineering service through the tray.

For host 0.20.0 also run `scripts/test_guidance.py`, the request-lane unit tests and the packaged adapter/installed-connection probes. Canonical procedures are under `plugin/guidance/`; root public plugin Markdown references are projected into data-only snapshots. Packaged `plugin/skills/` files remain stable entrypoints. [Current guidance](../plugin/CURRENT-GUIDANCE.md) documents the ordinary tools, the separate HTTP Skills profile, explicit publication, session-only overrides and precise optional-feature blockers. No native project is needed for guide discovery or protocol tests.

Use `scripts/test_framework_integrity.py` for deployment/bundle rejection controls and the pinned framework environment for `scripts/test_framework_adapter.py <extracted>/server/SiemensMCPServer.Mcp.exe`. The latter exercises two independent app frontends, complete direct/routed results, review guards, reload, receipts and a lost reply with a durable restart barrier, using a test-only native provider. Actual native acceptance remains separate. See [app lifecycle](../plugin/APP-HOST.md).
