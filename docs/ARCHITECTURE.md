# Implementation architecture

SiemensMCPServer owns its Windows applications, native engineering behavior, Codex plugin and skills. The [CodexMCPBroker repository](https://github.com/votecoffee/CodexMCPBroker) supplies a reusable framework and a separately installed Windows management app. Neither replaces this application's plugin. There is no universal plugin or requirement to adopt the framework for the direct profiles.

## Profiles and responsibilities

| Component | Implementation | Responsibility |
| --- | --- | --- |
| Desktop utility | Root WinForms project and shared native adapters | Comfort/Advanced import/export, API-supported Unified/offline PLC transfers and the shared Safeguards editor. It does not require an MCP connection. |
| Direct offline | `Offline/`, `plugin-offline/` | Three direct tools over seventeen closed supplied-content operations; no native APIs, process launcher, worker or shared service route. |
| Direct native candidate | `Mcp/Program.cs`, `Mcp/McpTools.cs`, `plugin-native/` | Existing engineering implementation, explicit detach and four guidance readers: 56 declared tools. Native/Codex installation acceptance remains pending. |
| Native execution | `OpennessRuntime`, `RequestLane`, `HmiSession` and workflows | One FIFO STA owns attachment and transactions; pure analysis uses separate bounded worker lanes. GUI/native mutations stay in the shared adapters and workflow implementations. |
| Feature updates | `Extensions/`, `ExtensionManager`, `ExtensionPlanStore` | Pure feature workers and explicit reviewed reload. A successful reload retains attachment and invalidates previews; a failed reload retains the prior generation. |
| Guidance | Maintained `plugin/guidance/`, `NativeGuidance`, `scripts/build_native_guidance.py` | Data-only projection into direct-native guides/references with revision and exact SHA256 pins. Four small native skill entry points select these readers. |
| Optional earlier framework host | `plugin/framework/` | Application-owned Python runtime, discovery/context/receipt lifecycle and optional management reporting. Uses its own reviewed wheel/pins; not a direct-profile dependency. |

## Native ownership and uncertain work

The direct root captures its own and its parent's PID, creation FILETIME and image, retains a query/synchronize parent handle, and holds session admission until exit. It joins a private inherited Windows Job before creating feature workers. Job process count, aggregate memory and EOF drain are bounded and GUI editable for new hosts. Windows-subsystem executables avoid console helpers appearing before containment. A harmless net48 fixture verifies stdio, parent/EOF handling, admission and private-child cleanup; it is not a packaged MCP/TIA acceptance test.

`ResourceOwnership` reserves durable claims before worker startup, binds original process identities before sending work and records cleanup intent/results. Failed unbound launches retain uncertainty and never authorize a PID-based kill. TIA is a report-only attachment to user-owned software, not a private child to terminate. Another host cannot attach through a retained claim for the same TIA process. Detach, EOF and parent loss never save, close or kill user TIA. Hung drain closes only the private Job; native journals/attachment claims remain for inspection.

Write launch permission and the current GUI policy are independent ceilings. Reviewed routes retain explicit project/target identity, preview fingerprints, candidate hashes, consumed plans, native transactions/readback and evidence. Compile/save are separate. Neither a transport retry nor a feature reload clears uncertainty. See [resource ownership](../plugin/RESOURCE-OWNERSHIP.md), [Safeguards](../plugin/SAFEGUARDS.md) and [native acceptance scope](../plugin/NATIVE-ACCEPTANCE.md).

## Framework and management boundaries

The shared framework is a versioned package dependency in the optional Python profile. Its retained distribution/import names are `universal-codex-tools` and `universal_tools`. This application's reviewed optional profile pins framework 0.20.0 and MCP Python 2.3.0; the C# native host uses SDK 2.2.0. A newer framework repository release is not proof of application migration or native acceptance. Verify new wheel bytes, locks, protocol/ownership contracts and app behavior before adopting it.

The [Windows management app](https://github.com/votecoffee/CodexMCPBroker/blob/main/docs/service-management.md) has its own installer/runtime and requires no Codex plugin or MCP connection. Reporting and guarded resource cleanup require explicit app integration. Direct-native doctor reports management/restart unavailable; it does not claim the optional Python profile's reporting. The manager must not treat attached user TIA, unknown identities, shared claims or uncertain native work as disposable processes.

## Installer and configuration boundary

Root `InstallAndRun.cmd` / `Run.cmd` call `build/Invoke-DesktopCommand.ps1` for the GUI only. `-Help`, `-NoPause`, `-InstallOnly` and `-CheckOnly` support unattended/disposable checks without a GUI launch. These commands never modify Codex configuration or install the independent manager.

The plugin installer verifies packages and delegates marketplace/plugin registration to the Codex CLI. It does not serialize TOML directly. `-PrepareOnly` stages verified bytes without invoking that registration. Native-candidate activation remains held. Reusable explicit-path config publication tooling is documented in [UTF-8 TOML publication](CONFIG-PUBLICATION.md); it never discovers or opens a normal config implicitly.
