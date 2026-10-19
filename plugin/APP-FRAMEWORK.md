# Experimental shared app framework profile

SiemensMCPServer retains its standalone Codex plugin and .NET MCP endpoint. The optional app-owned host uses the separately packaged `universal_tools.framework`, with Siemens as an out-of-process backend. Universal Tools aggregation is optional. This profile does not migrate installed plugins, registrations or native workflows.

The isolated compatibility baseline is **framework 0.14.0 / MCP Python 2.3.0**, from [broker commit 52478c3](https://github.com/votecoffee/CodexMCPBroker/tree/52478c3868b18faf821ca22fd2bc38e5d0599b9b). The Siemens .NET MCP SDK remains 2.2.0; SDK versions in different languages have separate lifecycles. No native .NET/NuGet framework binding is assumed.

## Pinned dependencies

`framework/compatibility.json` records the accepted isolated baseline and `productionAdoption: false`. `framework/requirements-lock.txt` pins the framework package and compatible Python dependencies, including `mcp==2.3.0` and `mcp-types==2.3.0`. The profile rejects an unaccepted framework, SDK or wire-types version before SDK imports or host startup. SDK 1.x is not an installation target.

Build or obtain a trusted framework 0.14.0 wheel from the pinned source. Verify its version, dependencies, license, SHA-256 and RECORD, then install it in a new dedicated environment with the lock. A locally built wheel is not a published package-feed release. Do not replace a live backend environment. For example, from an extracted Siemens package with the verified wheel in a separate wheelhouse:

```powershell
py -3.13 -m venv C:\private\siemens-framework\runtime
& 'C:\private\siemens-framework\runtime\Scripts\python.exe' -m pip install --find-links C:\private\wheelhouse -r framework/requirements-lock.txt
& 'C:\private\siemens-framework\runtime\Scripts\python.exe' -m pip check
& 'C:\private\siemens-framework\runtime\Scripts\python.exe' -B -X utf8 scripts/test_app_framework.py . --report C:\private\siemens-framework\profile.json
```

The Siemens package contains the profile, lock and preparation helper. It does not install Python, start a framework daemon or install another Codex plugin. Framework implementation comes from its normal package dependency, without a source copy or Git submodule in Siemens.

## Prepare a versioned backend publication

From a verified extracted Siemens package:

```powershell
powershell.exe -NoProfile -File scripts/New-FrameworkPublication.ps1 `
  -PluginRoot . -OutputPath 'C:\private\siemens-framework\application.json' `
  -SettingsFile 'C:\private\siemens-framework\safeguards.json' `
  -ArtifactRoot 'C:\private\siemens-framework\artifacts' `
  -ExtensionsRoot 'C:\private\siemens-framework\extensions'
```

From source, the helper is under `build/`. It validates all payload manifest members, rejects extra/linked/tampered/unsafe paths and existing output files, binds the entrypoint hash, and derives the backend revision from the payload manifest plus launch policy. Keep publications/settings outside immutable deployment directories. Full dependency/resource closure remains an app deployment responsibility; the framework's entrypoint hash is not full closure authentication.

The default publication denies writes. `-AllowedWriteTools` accepts exact outer Siemens tool names for an explicitly reviewed list, together with host launch permission. [Shared safeguards](SAFEGUARDS.md) remain an independent ceiling. The `exclusive` session mode and stable `siemens-tia-openness` resource ID preserve selection/review ownership. This backend keeps its current project and pending plans in its process, requiring exclusive ownership.

Preparation does not start a host, install dependencies or a plugin, change a live registration or connect to TIA. Use dedicated private app state, separate from any universal broker state. Do not switch a live frontend automatically.

## SDK 2 protocol boundary

The profile uses explicit legacy initialization with negotiated wire protocol `2025-11-25`, snake_case Python fields, typed pagination and alias-based MCP serialization. Two retained MCP connections exercise ownership across SDK 2 request-scoped session proxies and remain connected through backend refresh. Newer discovery/cache APIs and multi-round-trip workflows need separate acceptance.

Result comparison retains received fields, text, structured content and `_meta`, normalizing only the optional `isError` default: the .NET host omits false while SDK 2 routing emits false explicitly. Unknown top-level extension fields are outside SDK 2's contract; use `_meta` for extension data. The framework's forms-only and request-bound progress relays remain its responsibility; this Siemens profile does not establish native confirmation UI or media/progress parity.

## Updates and recovery

Complete work and release the exclusive context before refreshing a backend revision. Owned contexts defer replacement. Preserve request receipts and uncertainty barriers: refresh, resource renaming, a new request ID or a direct-route retry cannot reconcile an unknown outcome. Inspect independent app evidence before any local administrative acknowledgement.

Native skills or front-door schemas still require plugin update/reload. Chat settings such as Full access or Ask for approval are not an app authorization adapter. No model-provided mode argument bypasses Siemens safeguards, launch permission, client/broker policy or uncertain-outcome handling. The [optional broker route](BROKER-INTEGRATION.md) remains a separate assessment.

## Verification and remaining gates

The isolated profile compares direct and routed read results from the packaged .NET backend, checks ownership, retained frontend connections, deferred/applied backend publications, stale catalogs, stable frontend definitions and an app-error receipt that blocks replacement after Siemens policy denies a write. Preparation overwrite, bad-hash, extra-file and unsafe-path controls are included. Small direct/routed latency samples are recorded locally and do not establish production performance.

The separate broker profiles check workflow/reference discovery and stateful production handlers/workers with a test-only native provider. They cover multilingual results, review/hash guards, extension reload, separate compile/save/reopen calls, lost replies, durable recovery barriers and zero replay. Fake-provider results do not establish Siemens native transaction/import/compile/save behavior.

Remaining gates include standalone framework-plugin activation in Codex, actual chat isolation, native workflow acceptance, media/progress/confirmation parity, deployment closure, production performance and nested aggregation. Keep the standard standalone plugin available and live routes unchanged until applicable gates pass. See the pinned framework [design](https://github.com/votecoffee/CodexMCPBroker/blob/52478c3868b18faf821ca22fd2bc38e5d0599b9b/docs/app-framework.md) and [verification contract](https://github.com/votecoffee/CodexMCPBroker/blob/52478c3868b18faf821ca22fd2bc38e5d0599b9b/VERIFICATION.md).
