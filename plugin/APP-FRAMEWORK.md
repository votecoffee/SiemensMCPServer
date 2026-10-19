# Experimental shared app framework profile

SiemensMCPServer retains its fully functional standalone Codex plugin and .NET MCP endpoint. The primary shared-code direction is an app-owned host using the separately packaged `universal_tools.framework`, with the Siemens executable as an out-of-process backend. Universal Tools aggregation is optional; this profile does not require installing its Codex plugin or changing existing registrations.

The current profile is **experimental preparation and isolated compatibility validation**, rather than an installed replacement for the standard Siemens plugin. The framework's stable fourteen-tool discovery/invocation surface can route the existing 54 Siemens tools. App-native targets, previews, candidate hashes, write policy and native operations stay in the existing .NET services. There is no shipped native .NET/NuGet framework binding; the .NET MCP SDK is not downgraded to the framework's Python SDK.

## Pinned dependency and private state

The optional runtime/test dependency is `universal-codex-tools==0.13.0`, importing `universal_tools.framework`. The repository's `framework/requirements-lock.txt` pins its tested Windows/Python 3.13 dependency snapshot, including MCP Python 1.30.0. The MCP ZIP includes that lock for explicit local preparation; it does not contain or install Python, the framework wheel, a framework daemon or copied framework implementation.

Obtain a verified local wheel from the framework project. No public package feed is assumed. Inspect its version, dependencies, SHA-256 and wheel RECORD, then install it in a dedicated virtual environment:

```powershell
py -3.13 -m venv .local/framework
& .local/framework/Scripts/python.exe -m pip install -r framework/requirements-lock.txt --find-links 'C:\path\to\verified-wheel-directory'
& .local/framework/Scripts/python.exe -m pip check
```

For coordinated development, an explicit editable installation of the framework checkout can replace the wheel after preparing the same locked environment. Record the tested source revision and rerun the compatibility profile. Do not copy framework source into this repository, use a Git submodule, install dependencies during a tool call, or reuse a universal broker's existing state.

## Prepare a versioned backend publication

Start from a verified, freshly extracted Siemens plugin ZIP. From its root:

```powershell
powershell.exe -NoProfile -File scripts/New-FrameworkPublication.ps1 `
  -PluginRoot . -OutputPath 'C:\private\siemens-framework\application.json' `
  -SettingsFile 'C:\private\siemens-framework\safeguards.json' `
  -ArtifactRoot 'C:\private\siemens-framework\artifacts' `
  -ExtensionsRoot 'C:\private\siemens-framework\extensions'
```

From source, the helper is under `build/`. Preparation validates all payload manifest members, rejects extra/linked/tampered/unsafe paths, binds the entrypoint hash and derives the backend revision from the payload manifest plus launch policy. Existing publication files are retained and cannot be overwritten. Use immutable deployment directories and keep publications/settings outside them. The framework verifies the entrypoint on metadata/dispatch; complete dependency/resource closure remains an app deployment responsibility.

The default publication denies writes. `-AllowedWriteTools` requires exact outer Siemens tool names and enables only that reviewed list, together with the host launch flag. The [shared safeguard policy](SAFEGUARDS.md) remains an independent ceiling. The `exclusive` session mode and stable `siemens-tia-openness` resource ID retain selection/review ownership. Shared mode is unsuitable for this backend's process-owned current project and pending plans.

Preparation does not start a host, install a plugin, change a live registration or connect to TIA. An explicitly selected disposable profile can use the framework package's reference host with **dedicated private app state**:

```powershell
& .local/framework/Scripts/python.exe -m universal_tools --state .local/siemens-framework-state init --port 8894
& .local/framework/Scripts/python.exe -m universal_tools --state .local/siemens-framework-state app-serve --publication 'C:\private\siemens-framework\application.json'
```

The host owns its loopback authentication/private settings. Its package provides the `RemoteBackend`/stdio adapter for independent frontends. Do not use the universal broker's automatic-start command with app state or silently switch an existing frontend. Host startup, actual frontend/plugin packaging and route-aware guidance require explicit deployment acceptance.

## Updates, ownership and authorization

Backend deployment/publication and the native Codex skill package have separate versions. With unchanged front-end definitions, finish work and release the exclusive context before refreshing a new publication. An owned context defers replacement. Reload does not transfer another frontend's attachment or plans. Native skill/schema or framework-runtime changes still require the corresponding plugin update/reconnect; this profile does not establish arbitrary in-process DLL reload.

Retain request receipts and uncertainty barriers. An app-error receipt can block replacement even when the error appears harmless. Reconcile through the app's independent evidence before any explicit local administrative acknowledgement; neither refresh, a new backend revision, a renamed resource nor a direct-route retry is reconciliation.

Chat settings such as Full access or Ask for approval are not an app authorization adapter. No tool argument can claim those modes to bypass Siemens safeguards, launch permissions, client/broker policy or an uncertain outcome. The [broker contract](BROKER-INTEGRATION.md) remains separate from framework adoption and optional nested aggregation.

## Verification and gaps

```powershell
& .local/framework/Scripts/python.exe -B -X utf8 scripts/test_app_framework.py 'C:\packages\verified-siemens-payload' --report .local/framework-check.json
```

The test imports the pinned framework package's `ApplicationHost` around the production .NET backend in disposable state. It compares complete direct/routed read results, discovers all 54 backend tools, verifies independent owner denial, defers an owned publication, applies it after release, rejects stale catalogs, compares all fourteen front-end definitions, and verifies that the Siemens policy denial retains an app-error receipt that blocks further replacement. It also tests publication overwrite and malformed/tampered payload rejection. Direct/host timings are small local samples, not native performance or throughput acceptance.

This establishes an isolated API compatibility profile. It does **not** establish a persistent native Codex connection across refresh, chat isolation, media/progress parity, native imports/rollback, complete deployment-closure checks on every invocation, production performance, standalone framework-plugin activation, or nested universal aggregation. Keep the standard standalone plugin available and existing live routes unchanged until those applicable gates pass. See the framework project's [design](https://github.com/votecoffee/CodexMCPBroker/blob/main/docs/app-framework.md) and [acceptance guidance](https://github.com/votecoffee/CodexMCPBroker/blob/main/docs/server-development/acceptance.md).
