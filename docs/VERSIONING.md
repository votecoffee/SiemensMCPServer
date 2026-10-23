# Application timestamp versions

The latest fixed generated checkpoint is **26.1023.171630**, stamped in **America/New_York** from `2026-10-23T21:16:30.023932Z`, for the stable offline launcher and physical MSIX installation-path repair. New artifacts built from those generated properties share the display/package/bounded .NET identity; existing accepted installations and previously tested package bytes keep their earlier versions. Documentation/source-only checks do not install, activate or restamp a release. SDK, protocol, validation-rule and independently reviewed framework dependency versions remain separate. The small stable launcher has its own fixed ABI version, independent of the selected app release.

| Identity | Current value | Rule |
| --- | --- | --- |
| Human/Product/Informational/MCP display | `26.1023.171630` | Fully padded `YY.MMDD.HHmmss`; never generated at runtime. |
| Plugin/cache/package | `26.1023.171630` | Three numeric components with leading zeros removed for strict SemVer/PEP 440. |
| .NET Assembly/File | `26.1023.1716.30` | Bounded `YY.MMDD.HHmm.ss`, with each numeric component normalized. |

For example, display `26.0103.000507` has package identity `26.103.507` and assembly/file identity `26.103.5.7`. Midnight `00.0101.000000` maps to `0.101.0` / `0.101.0.0`. Calendar dates, leap years and clock ranges are validated. Comparisons normalize numeric components instead of comparing padded strings. The final `.NET` field retains seconds, so no second-level release information is lost. The six-digit clock is never used directly as a 16-bit assembly/file field.

## Stamp once, then build

Run the explicit release operation once when creating a checkpoint:

```powershell
.\build\Stamp-AppVersion.ps1
# To reproduce a deliberately selected instant:
.\build\Stamp-AppVersion.ps1 -Utc '2026-10-23T21:16:30.023932Z'
```

The wrapper uses Windows' New York/Eastern time-zone conversion, including daylight saving time, and passes that fixed instant to the source generator. It writes `plugin/app-version.json`, `build/AppVersion.props`, `Shared/ReleaseStamp.cs`, desktop release attributes and both plugin manifests. `Directory.Build.props` imports the generated version properties for builds. Review and commit the coherent change before producing release artifacts.

Builds, verification, packaging, startup and `manage.py version` never call the stamper or read the clock to invent a version. A changed release/checkpoint gets a new deliberate stamp; preserve previously tested package bytes and receipts. Timestamp collisions with a different recorded UTC checkpoint reject stamping. Old-to-new mappings remain private under ignored `.local/version-mappings/`; they are not public usage records.

The plugin version and cache/package directory use the canonical identity. Build metadata and installer receipts also retain the padded display and bounded assembly/file identities. Manager app/plugin reporting uses the human display; framework/SDK components retain their actual dependency versions. The pinned framework 0.20 factory has no app-version argument, so a small guarded app adapter sets its SDK low-level identity **before** initialization/discovery identity caching. It refuses a surface whose identity was already observed; it does not modify dependency package bytes or active connections.

Feature-worker handshakes read Product/Informational identity, not `AssemblyName.Version.ToString(3)`. Development-only explicit `-WorkerVersion` builds set package, informational, assembly and file values coherently; normal builds share the fixed app checkpoint. Existing historical numeric versions remain comparable. TIA native API assembly versions and extension wire protocol 1 are unchanged.

## Verification and rollout

`scripts/test_app_version.py`, `Tests/AppReleaseTests.cs` and `scripts/test_app_version.ps1` cover leading-zero normalization, invalid dates/clocks, integer/loader bounds, fixed generated identities and metadata. The PowerShell check reads actual PE Product/File/Assembly versions and verifies worker package manifests without running those executables. Python's version CLI reports the fixed JSON without starting a host or changing any files.

```powershell
py -3.13 -B plugin/framework/manage.py version
dotnet test Tests/SiemensMCPServer.Tests.csproj -c Release --filter FullyQualifiedName~AppReleaseTests
```

These are source/metadata checks. MCP initialization, packaged runtime, plugin cache and native TIA acceptance remain independent rollout gates. A runtime hold is not released by changing a version or compiling an executable. Guarded installation/verification must preserve user state, resource claims and uncertain-write journals when the application's batch is authorized.
