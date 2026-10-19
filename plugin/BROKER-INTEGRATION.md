# Standalone plugin and Universal Tools integration

SiemensMCPServer continues to ship its desktop application, MCP executable and standalone Codex plugin. They work independently of [CodexMCPBroker](https://github.com/votecoffee/CodexMCPBroker). The optional broker route uses **Universal Tools** as the installed Codex plugin and the same app-owned Siemens MCP server behind it.

Host 0.14.0 adds the broker capability/workflow contribution profile. The existing 51 engineering tools retain their schemas and result envelopes. Three explicitly read-only tools publish installation metadata:

| Tool | Result |
| --- | --- |
| `tia_list_broker_capabilities` | Normalized version-1 catalog with four `workflow` hints and pinned discovery arguments |
| `tia_read_workflow` | Version-1 guide: Markdown, SHA-256, native tool references and reference readers |
| `tia_read_workflow_reference` | One embedded public guide, checked against its workflow revision and expected SHA-256 |

These tools never load Siemens APIs, start extension workers, inspect user files, attach a project, invoke confirmations, reload code or write engineering data. Responses are unwrapped normalized objects; engineering tools retain their `result` envelope. Metadata never authorizes a change. Public documentation and standalone skill bodies are embedded at build time. A host rebuild/reconnect publishes changed guides; feature-worker reload does not update them.

## Installation choices

The installer defaults to **Preserve**: reuse the selected preparation/install route from its local `integration-choice.json`; first use falls back to **Standalone** during migration assessment. Use an explicit choice to override it:

```powershell
powershell.exe -NoProfile -File scripts/Install-CodexPlugin.ps1 -Package 'C:\packages\siemens.zip' -Integration Standalone
```

It installs the Siemens plugin using the existing verified package/marketplace procedure. `-PrepareOnly` stages the source without changing Codex settings. The repository equivalent is `build/Install-CodexPlugin.ps1`. Desktop installation is independent of both Codex routes.

Host package 0.14.1 records explicit successful selections atomically, retains the previous record, and rejects concurrent installers using the same root before extraction/settings changes. A standalone dry run or broker `-PrepareOnly` cannot replace the selection. This record describes installer intent, not a live registration, accepted migration or permission to change routes during a workflow. Invalid records are retained for inspection rather than silently switching integrations.

To prepare the broker route while skipping installation of the Siemens plugin:

```powershell
powershell.exe -NoProfile -File scripts/Install-CodexPlugin.ps1 -Package 'C:\packages\siemens.zip' -Integration Broker
```

This verifies/stages the complete package and returns `broker-registration-prepared` with a generated descriptor path. It does **not** modify live broker registrations, install Universal Tools, change existing plugins or declare migration accepted. The descriptor defaults to read-only access, an empty write allowlist, a 256 KiB argument budget, a 300-second app timeout and a retained **exclusive** session. Review it and register it explicitly using the selected broker's local registration command. The broker stores the input under its required `siemens-mcp-server.json` filename.

For a separately reviewed pilot needing selection/previews/applies, create another descriptor with `scripts/New-BrokerRegistration.ps1` (`build/` in the repository), explicit `-PluginRoot`, a new `-OutputPath` and the exact `-AllowedWriteTools` array. That enables the host write flag and broker write policy for the supplied outer names. A write-capable tool still uses the broker write channel when it only selects state or exports artifacts. The mixed `tia_read_extension` wrapper retains this conservative classification. Inspect `describe_tool.invocation`; do not blanket-override the wrapper as read-only.

## Intended automatic policy after migration acceptance

Fresh installations should prefer Universal Tools and omit installation of the Siemens plugin only when the intended Codex profile has an enabled compatible Universal Tools integration, a usable accepted Siemens registration, required guidance and completed host/native acceptance for the selected workflow. If the broker is absent or unsuitable, offer standalone installation. Explicit standalone selection remains available; reinstalls preserve the recorded route.

**Presence-based automatic selection is not enabled in 0.14.0.** A plugin folder, running daemon or installed package alone does not establish readiness. Codex supports independently enabled plugins and local marketplace configuration; see [official plugin packaging guidance](https://developers.openai.com/plugins/build/plugins). Do not disable or uninstall the existing Siemens plugin merely because Universal Tools is detected. Installing both routes is an explicit choice, not the broker-mode default.

## Stateful workflow through Universal Tools

1. Refresh services and search `kind=capability` for the Siemens workflow. Describe its capability and real discovery tool using current broker revisions. The hint is not callable.
2. Acquire `siemens-mcp-server` and retain its exclusive `context_id`. Refresh the owned capability snapshot. Read the guide with its exact app revision and required references using their descriptors and hashes.
3. Describe native tools and invoke their service-qualified IDs through the indicated channel. Keep the same context across explicit API/project/target selection, read, preview, authorized apply and separate compile/save.
4. Retain app fingerprints, candidate hashes, plan IDs and evidence. Broker request IDs/catalog revisions are separate guards; none substitutes for app review tokens.
5. Finish or reconcile work before releasing the context or changing routes. Closing the stdio adapter discards process-owned attachment and preview state. Lost replies require broker recovery and app inspection; never replay an uncertain write through standalone tools.

Transient sessions lose state between calls; shared sessions cannot isolate private process selection/previews. The stable `resource_id` fences the declared resource within one broker; it cannot fence manual TIA edits, the GUI, another broker or a direct client. Keep one route per native project workflow. Current broker calls serialize per service, so they do not expose simultaneous direct-host local analyses.

Contributed guides reuse standalone instructions and public references. They are task-scoped app data, not installed native Codex skills. Local PowerShell helpers mentioned in guides remain in the staged package and require local execution; readers do not install or execute assets. Command/asset and host parity remain migration gates.

## Verification and remaining gates

Unit tests cover deterministic publication, four embedded guides, transitive references, content-bound revisions, stale hashes and unknown IDs. `scripts/test_broker_integration.py` uses an isolated current broker, two stdio frontends and the real Siemens executable. It checks discovery, workflow schema/hashes, contributed tools/references, exclusive ownership, stale rejection and write denial without installing the Siemens plugin or attaching to TIA.

Use a current broker development environment with its pinned dependencies and importable source:

```powershell
$env:PYTHONPATH = 'C:\source\CodexMCPBroker\src'
& 'C:\source\CodexMCPBroker\.local\development\Scripts\python.exe' -B -X utf8 scripts/test_broker_integration.py 'C:\packages\extracted\server\SiemensMCPServer.Mcp.exe' --broker-source 'C:\source\CodexMCPBroker'
```

This checks metadata/read-only compatibility. A separate packaged 0.14.1 V15.1 native pilot subsequently passed selected single-line English/German Unicode text-list edits, native readback, compile, save/reopen and restoration through an isolated broker. CRLF and LF candidates in the tested native body/p entries failed strict readback and rolled back; offline literal preservation does not establish native multiline support. Fresh Codex chat activation, other native workflows/families, payload/media parity and native failure recovery still need applicable acceptance before migration. Keep standalone integrations during those gates. See [native acceptance](NATIVE-ACCEPTANCE.md) and the broker's [coexistence policy](https://github.com/votecoffee/CodexMCPBroker/blob/main/docs/plugin-coexistence.md).

For the stateful adapter gate, host 0.14.1 adds an internal provider seam consumed only by the separately compiled `SiemensMCPServer.Mcp.Fixture.exe`. Production startup has no fake-provider argument. The fixture refuses loaded Siemens assemblies and substitutes `IHmiSession`; production MCP handlers, queues, `TextListWorkflow` and pure feature workers remain in use. It does not exercise native `HmiSession` target/transaction checks or Siemens import/compile/save behavior.

```powershell
& 'C:\source\CodexMCPBroker\.local\development\Scripts\python.exe' -B -X utf8 scripts/test_broker_adapter.py 'C:\packages\extracted\server\SiemensMCPServer.Mcp.exe' --previous-host 'C:\packages\previous\server\SiemensMCPServer.Mcp.exe'
```

Run from the repository with the same broker source/dependencies as above. The optional previous host must predate 0.14.1. This check compares complete direct/broker text-list read results, preserves a 49,152-character multilingual payload, rejects wrong-owner/hash/consumed/stale plans, tests reload success/failure, and keeps fixture compile/save/reopen separate. It proves release discards process-owned plans/selection and records exactly three independent fixture import effects, including one lost reply with an uncertain broker receipt. The recovery barrier survives broker restart; explicit reconciliation after inspecting that isolated test state adds no effect. No cross-route write retry occurs. These fixture results cannot establish native or Codex UI acceptance.

## App safeguard policy

Host 0.15.0 shares [desktop-editable app safeguards](SAFEGUARDS.md) for writes, operation ceilings, preview limits and request limits. These settings apply identically behind direct and broker routes. They do not edit broker service allowlists, leases or recovery barriers, and they do not infer authorization from chat approval-mode arguments. The effective permission is the intersection of app policy, launch permission and client/broker authorization.
