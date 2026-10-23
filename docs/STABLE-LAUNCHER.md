# App-owned offline launcher

The direct offline installer selects retained immutable runtime bytes behind `SiemensMCPServer.Launcher.exe` in a persistent application directory. The launcher and cwd do not depend on the source checkout, build output or Codex plugin cache. This deployment preserves the standalone Siemens plugin and four offline skills. It adds no framework, manager service, native engineering capability or controller access.

Build a reviewed offline ZIP, then run:

```powershell
.\InstallMcp.cmd -Package 'C:\packages\reviewed-offline.zip' -NoPause
```

The default application root is `%USERPROFILE%\AppData\Local\SiemensMCPServer\Mcp`. `-InstallRoot` selects another persistent application directory. `-ExpectedSelectionSha256 <hash-or-absent>` enforces a reviewed prior pointer. `-Help` explains the command. Installation does not start a server, register a Codex plugin or edit host settings. New offline packages also redirect normal `Install-CodexPlugin.ps1` installation through this stable installer; `-PrepareOnly` remains extraction-only.

The app-owned installation contains:

```text
SiemensMCPServer.Launcher.exe / .dll / .deps.json / .runtimeconfig.json
launcher/current.json
launcher/snapshots/<sha256>.json
runtimes/<version>-<package-hash-prefix>/
retained-packages/plugins/<immutable-package>/
CodexMarketplace/plugins/<derived-standalone-plugin>/
state/active/<generation>.json
state/receipts/<generation>/claim.json + outcome.json
```

The fixed launcher ABI version is separate from the selected application's New York release stamp. The launcher runs the selected managed entry point **in the same windowless process**, using an isolated assembly load context and the selected dependency closure. It preserves original standard handles and Unicode JSON lines. It launches no shell, child server, conhost, Node process or helper. Protocol output belongs to MCP; lifecycle diagnostics go to stderr.

The launcher verifies the bounded pointer, snapshot hash, exact runtime inventory and every payload hash. It rejects duplicate keys, unknown selection fields, path escapes, linked paths, missing files and extra/tampered payloads. Read leases retain selected bytes until that process ends. Ordinary startup never installs dependencies, fetches updates, selects the newest directory or falls back to another profile.

MCP startup uses the same surviving process's existing one-process / 512 MiB Windows Job, four-session user/logon admission and retained exact parent handle. A process-local identity-checked fence lets the backend reuse that containment without taking a second admission slot. EOF retains the bounded drain; parent death exits the root. `--version` verifies the selection and invokes only the backend's existing version branch without opening MCP or taking a session slot.

Each admitted generation records its owner and parent PID, creation FILETIME, canonical image path and filesystem image identity, plus the selected snapshot/version. Reconciliation uses creation time and image file identity rather than comparing logical MSIX and physical path strings. It retains live and uncertain peers, retires only positively dead generations, preserves the original claim and writes a separate outcome. It never kills a process by PID/name or adopts user/shared software. This offline profile has no write intent to replay or native resources to clean up.

Updates verify a separate runtime before publishing a pointer. A filesystem sharing lock serializes publishers even through path aliases; the installer rechecks the expected pointer and uses atomic replacement with an exact prior-state backup. Existing processes retain their selected closure and claims until close. Old runtime/package bytes and receipts remain. Differing stable launcher ABI bytes refuse an ordinary update; replacing the entry assets requires a separately reviewed maintenance boundary rather than overwriting a running launcher.

The installer stages a derived standalone marketplace/plugin whose MCP command/cwd are absolute stable paths. Original packages and existing caches remain untouched. Host settings publication is a separate serialized operation. `scripts/prepare_stable_binding.py --config <explicit-TOML> --receipt <installation-JSON> --output <new-private-directory>` retains an exact config backup, current route/hash and proposed command/args/cwd/env; it never writes the config. The publisher must refuse stale bytes, preserve unrelated tables/comments/BOM/newline convention and use validated binary TOML publication. Keep one effective enabled Siemens route and disable an unresolved duplicate plugin route if the host cannot resolve its template. Never repair this by editing immutable plugin caches.

The [CodexMCPBroker framework and separate Windows manager](https://github.com/votecoffee/CodexMCPBroker) remain independent. No universal plugin exists and this offline profile needs neither component. The earlier optional framework pins and native pilot fences remain unchanged. Without a management endpoint, status is **Not reporting** and restart is **unavailable**. Installed, selected, running and current-chat versions are separate observations.

## Verification

```powershell
dotnet test Launcher.Tests/SiemensMCPServer.Launcher.Tests.csproj -c Release -p:RestoreLockedMode=true
dotnet test Offline.Tests/SiemensMCPServer.Offline.Tests.csproj -c Release -p:RestoreLockedMode=true
py -3.13 -B scripts/test_stable_launcher.py 'C:\packages\prior-compatible.zip' 'C:\packages\new.zip' 'C:\private\new-launcher-canary'
```

The launcher unit tests cover runtime leases/tampering, bounded strict selection, logical/physical image identity and live/dead/uncertain claim handling. Set `SIEMENS_TEST_MSIX_PID` to a positively identified running Codex process to exercise its actual Windows image identity without opening another app. The synthetic executable canary uses a copied CMD checkout with spaces/Unicode, removes its own disposable cache, updates with a live peer, checks exact owner death/reconnect, protocol/Unicode reads, EOF and selection/payload/stale-publication refusal. It needs a free offline session slot and never stops existing sessions or changes the admission limit.

Retain exact evidence before host activation. A package/compiler check, metadata version or SDK-only stream test does not prove an actual launcher MCP connection. Full slots are a bounded refusal, not authorization to terminate peers. After publication, verify actual current tools and four skills in a fresh/restarted Codex chat. Native, GUI, controller and shared-host acceptance do not follow from launcher acceptance.
