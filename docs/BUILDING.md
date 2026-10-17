# Build configuration

Build on Windows x64 with a .NET 8 or newer SDK, .NET Framework 4.8 targeting support and the required installed Siemens Openness API. Visual Studio can open `SiemensMCPServer.sln`; the desktop project can also be built independently.

## MCP server and desktop utility

The default compile-time API is V15.1:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V15.1
dotnet build SiemensMCPServer.csproj -c Release -p:TiaPortalVersion=V15.1
```

The MCP build stages bundled workers automatically. Use `-p:StageFeatureExtensions=false` only when a separate build step supplies them.

## Select an installed API

`TiaPortalVersion` chooses the build's API. `TiaPortalInstallVersion` chooses the installation supplying it; newer installations may retain older APIs:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V17 -p:TiaPortalInstallVersion=V20
```

An explicit API directory can be supplied instead:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V17 '-p:TiaPublicApiDir=C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V17'
```

V15.1–V20 use the classic assembly family. V21 uses separate modular `net48` Base/WinCC assemblies and requires the STEP 7 module for the MCP PLC tools:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V21
```

These examples require the corresponding installed API. V15.1 and V19 are build-tested profiles; V21 and other profiles need separate validation. Pre-V21 and V21 builds are not interchangeable at runtime. Missing API assemblies produce build errors; Siemens engineering DLLs are not copied into packages.

## Runtime selection

The resolver reads `HKEY_LOCAL_MACHINE\SOFTWARE\Siemens\Automation\Openness` and selects installations exposing the API version referenced by the executable. The desktop UI prompts when several compatible versions exist. MCP clients use `tia_list_versions` and `tia_select_version`, or supply `--tia-version` at launch. `TIA_PORTAL_VERSION` is also supported. Changing the loaded engineering API requires a host restart.

## Package and verify

```powershell
powershell.exe -NoProfile -File build/Build-Plugin.ps1 -TiaPortalVersion V15.1
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
