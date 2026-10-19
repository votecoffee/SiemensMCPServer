# API compatibility

Host **0.7.4** adds flat Unified screen discovery for V16/V17, explicit V21 product-module loading and a shared project-graphics adapter for the desktop and MCP server. The MCP interface retains 51 tools. These host changes require an upgraded host and a new connection; worker reload alone cannot replace the assembly resolver or native adapters.

## Verified API profiles

The following matrix records builds, installed API metadata checks, unattached MCP protocol checks and desktop smoke tests. It does not establish native project editing on every version or device.

| Compile-time API | Engineering installation used for checks | MCP / desktop builds and checks | Unified screen layout | PLC source / cross-reference API |
| --- | --- | --- | --- | --- |
| V15.1 | V15.1 | Passed | Unified absent | Source present; cross-references absent |
| V16 | V19 compatibility API | Passed | Flat screen composition | Source present; cross-references absent |
| V17 | V19 compatibility API | Passed | Flat screen composition | Source present; cross-references absent |
| V18 | V19 compatibility API | Passed | Screens and screen groups | Source and cross-references present |
| V19 | V19 | Passed | Screens and screen groups | Source and cross-references present |
| V20 | Not checked | SDK unavailable for validation | Capability checks required | Capability checks required |
| V21 | Not checked | Migration paths implemented; build/native validation pending | Separate Unified module required | Separate STEP 7 module required |

V16–V19 expose the screen/item, button text/layout and dynamization members used by the dedicated Unified adapter. None of these inspected profiles exposes the Unified text-list editing route. Native object/service availability and Siemens restrictions still apply. Exact V15.0 compatibility has not been established. Native V16/V17/V18 installations were not exercised; their API builds were checked against the compatibility assemblies supplied with V19.

Selected Comfort V15.1 native workflows were exercised with earlier binaries. Unified editing, PLC inspection/mutation and other native project/version combinations still require their own acceptance. See [validation status](NATIVE-ACCEPTANCE.md) and [PLC inspection](PLC-INSPECTION.md).

Host 0.8.0 adds read-only [library inspection](LIBRARY-INSPECTION.md). Its MCP builds, transport and required library signatures were checked on V15.1–V19 APIs; V16–V18 again used V19 compatibility assemblies. Library metadata fields vary by API and are reported individually. This adds no new desktop library workflow, and native attached-library acceptance remains pending.

## API version versus engineering version

`TiaPortalVersion` selects the compile-time API; `TiaPortalInstallVersion` selects the installation supplying it. For example:

```powershell
dotnet build Mcp/SiemensMCPServer.Mcp.csproj -c Release -p:TiaPortalVersion=V16 -p:TiaPortalInstallVersion=V19
```

At runtime, `--tia-version` selects the engineering installation. For that example, select `V19`, provided it appears in `tia_list_versions`. The resolver checks the complete referenced assembly identity, including version and signing key. It does not treat an arbitrary newer API as compatible.

Use [build configuration](../docs/BUILDING.md) for packaging and the repeatable `build/Test-ApiProfiles.ps1` check. Reports/logs default to ignored `artifacts/` and include local paths; keep them private. Missing SDKs are reported as skipped, separately from failed installed-profile checks.

## V21 migration boundary

V21 requires a separate .NET Framework 4.8 build. The resolver uses the `net48` registry layout and loads documented WinCC, WinCC Unified and STEP 7 modules with the selected Base assembly's identity. An absent optional Unified module reports unsupported discovery; a mismatched or corrupt module remains an error. Siemens assemblies are not redistributed.

Project graphics use the legacy project property when present and the documented `GraphicsProvider` service on the modular API. A failed native lookup/read is propagated without retrying another route. Both desktop graphics transfers and MCP `$projectGraphics` use this adapter.

The migration paths have synthetic tests, but V21 SDK compilation, attachment and native graphics/Unified/PLC operations remain unverified. See [Siemens V21 migration requirements](https://docs.tia.siemens.cloud/r/en-us/v21/readme-tia-portal-openness/major-changes-for-long-term-stability-in-tia-portal-openness-v21).
