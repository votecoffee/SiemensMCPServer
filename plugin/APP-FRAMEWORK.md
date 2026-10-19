# Experimental shared app framework profile

SiemensMCPServer retains its fully functional standalone Codex plugin and .NET MCP endpoint. The shared-code direction is an app-owned host using the separately packaged `universal_tools.framework`, with Siemens as an out-of-process backend. Universal Tools aggregation is optional. Existing installed plugins, registrations and native workflows are not migrated by this profile.

**The target framework must use MCP Python 2.3.** The previously tested framework 0.13.0/MCP Python 1.30.0 combination is historical disposable-test evidence, not the proposed installation target. The Siemens application's existing .NET MCP SDK remains unchanged at 2.2.0; SDK versions in different languages have separate lifecycles. No native .NET/NuGet framework binding is assumed.

## Dependency acceptance gate

`framework/compatibility.json` records the required MCP Python series and currently has no accepted framework package version. `scripts/test_app_framework.py` refuses to start the compatibility run until a compatible framework package is explicitly pinned. The production package contains no older SDK dependency lock or framework installer. It does not install Python, a framework daemon or another Codex plugin.

After the updated framework package is published locally, inspect/verify its wheel version, dependencies, SHA-256 and RECORD. Pin that exact framework version and its compatible transitive dependencies in normal dependency management, then run the profile in a dedicated environment. Do not downgrade the app SDK or force incompatible dependency resolution. Explicit editable/local dependencies can support coordinated development after the compatible baseline is established; do not copy framework implementation source or use a Git submodule.

## Prepare a versioned backend publication

The preparation helper remains available independently of framework installation. From a verified extracted Siemens package:

```powershell
powershell.exe -NoProfile -File scripts/New-FrameworkPublication.ps1 `
  -PluginRoot . -OutputPath 'C:\private\siemens-framework\application.json' `
  -SettingsFile 'C:\private\siemens-framework\safeguards.json' `
  -ArtifactRoot 'C:\private\siemens-framework\artifacts' `
  -ExtensionsRoot 'C:\private\siemens-framework\extensions'
```

From source, the helper is under `build/`. It validates all payload manifest members, rejects extra/linked/tampered/unsafe paths and existing output files, binds the entrypoint hash, and derives the backend revision from the payload manifest plus launch policy. Keep publications/settings outside immutable deployment directories. Full dependency/resource closure remains an app deployment responsibility; the framework's entrypoint hash is not full closure authentication.

The default publication denies writes. `-AllowedWriteTools` accepts exact outer Siemens tool names for an explicitly reviewed list, together with host launch permission. [Shared safeguards](SAFEGUARDS.md) remain an independent ceiling. The `exclusive` session mode and stable `siemens-tia-openness` resource ID preserve selection/review ownership. Shared mode is unsuitable for this backend's process-owned current project and pending plans.

Preparation does not start a host, select a framework version, install a plugin, change a live registration or connect to TIA. Starting the updated framework requires its compatible accepted package and dedicated private app state. Never reuse an existing universal broker's state or automatically switch a live frontend.

## Stable surface, updates and recovery

The framework's discovery/invocation front door can route Siemens tools while keeping app-native targets, preview revisions, candidate hashes and native operations in the .NET domain services. A new backend publication must preserve app authorization and original result/media semantics. Native skills or front-door schemas still require plugin update/reload; arbitrary in-process DLL replacement is outside this contract.

Complete work and release the exclusive context before refreshing a backend revision. Owned contexts defer replacement. Preserve request receipts and uncertainty barriers: neither refresh, renaming a resource, a new request ID nor a direct-route retry reconciles an unknown outcome. Inspect independent app evidence before any local administrative acknowledgement.

Chat settings such as Full access or Ask for approval are not an app authorization adapter. No model-provided mode argument bypasses Siemens safeguards, launch permission, client/broker policy or uncertain-outcome handling. The [optional broker route](BROKER-INTEGRATION.md) remains a separate assessment.

## Verification and remaining gaps

The historical 0.13.0/1.30.0 disposable profile compared complete direct/routed read results, discovered the 54 Siemens tools, enforced independent ownership, deferred/applied backend publications, rejected stale catalogs, preserved fourteen front-end definitions and retained an app-error barrier after the Siemens write policy denied a request. Preparation overwrite, bad-hash, extra-file and unsafe-path controls passed. This does not establish compatibility with MCP Python 2.3.

Once a compatible framework package and dependency lock are accepted, rerun `scripts/test_app_framework.py` against a verified extracted Siemens payload. Retain explicit direct/routed comparisons and measured latency; no production performance claim follows from small local samples.

Acceptance still includes the updated framework runtime, persistent native MCP sessions across refresh, standalone framework-plugin/Codex activation, actual chat isolation, media/progress parity, native import/rollback, deployment closure and optional nested aggregation. Keep the standard standalone plugin available and live routes unchanged until applicable gates pass. The framework project's [design](https://github.com/votecoffee/CodexMCPBroker/blob/main/docs/app-framework.md) and [acceptance guidance](https://github.com/votecoffee/CodexMCPBroker/blob/main/docs/server-development/acceptance.md) describe the shared direction; each actual package must be assessed at its pinned version.
