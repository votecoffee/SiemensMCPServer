# Standalone app host

Plugin 0.17.0 uses framework 0.16.0, Python MCP SDK 2.3.0 and matching wire types. Its 14 frontend tools discover and invoke the existing 51 Siemens engineering operations. The C# backend retains SDK 2.2.0 and direct MCP access. This is a dedicated Siemens application: installation neither requires the Universal Tools plugin nor registers Siemens with a shared broker. The framework's `broker_status` tool reports this app host's status.

## Engineering workflow

1. Use `search_tools` and `describe_tool` to find the exact operation and its argument schema.
2. Acquire `siemens-engineering` with `acquire_service`, retain its `context_id`, and inspect `describe_service`.
3. Describe each `siemens-engineering/<operation>` and use its returned `invocation.channel` and `catalog_revision`. Pass the context and unchanged native arguments. Write-channel calls require a unique `request_id`.
4. Preserve explicit process/project/target IDs, fingerprints, candidate hashes, preview plans and the user's authorized scope. Apply, compile and save remain separate operations. Framework permission is an additional ceiling; the native safeguards still decide whether the operation is allowed.
5. Release the service when the workflow is complete or reconciled. Release closes the native adapter and discards attachment, selection and previews. Another frontend cannot use your context or plans.

The gateway serializes calls to its single native backend. Native parallel analysis remains available to direct MCP clients; gateway transport does not expose that concurrency. Compatible feature workers can still reload through `tia_reload_extensions`, preserving attachment and unsaved edits while invalidating previews. Updating the framework, SDK or native host requires an installation and new frontend processes.

## Installation and private state

Run the packaged `scripts/Install-CodexPlugin.ps1` with the verified ZIP. Python 3.13 is required. The installer verifies the complete payload and wheel RECORDs, provisions a dedicated runtime from the bundled exact dependencies with no package index access, and installs the standalone Codex plugin. Chat startup never installs dependencies. `-PrepareOnly` stages verified source without provisioning a runtime or changing Codex settings.

Default app state is `%LOCALAPPDATA%\SiemensMCPServer\AppHost`, separate from the immutable package and Codex cache. It contains the active deployment pointer, installed Python runtime, authenticated loopback configuration, app publication, ownership and write receipts. Treat that directory as private. The native GUI safeguards file and native artifact/extension locations retain their existing defaults. The normal installer enables the framework write channel; native GUI policy remains an independent ceiling. Low-level runtime preparation defaults to read-only unless `--allow-writes` is explicitly supplied.

For app administration in PowerShell:

```powershell
$siemensConfig = Join-Path $env:LOCALAPPDATA 'SiemensMCPServer\AppHost\active.json'
$siemensRuntime = Get-Content -LiteralPath $siemensConfig -Raw | ConvertFrom-Json
$siemensManager = Join-Path $siemensRuntime.payloadRoot 'framework\manage.py'
& $siemensRuntime.python -B $siemensManager status --config $siemensConfig
& $siemensRuntime.python -B $siemensManager contexts --config $siemensConfig
# After completing/releasing engineering work:
& $siemensRuntime.python -B $siemensManager stop --config $siemensConfig
```

Startup verifies the entire payload, publication and pinned module origins before serving requests. An update refuses to replace a running different deployment, retained ownership or non-completed write receipts. Finish/reconcile work, release contexts and stop this app host before updating. Prior runtime pointers and Codex configuration backups are retained. Do not edit immutable installed payloads; rebuild and reinstall them. Start a new Codex chat after installation to load updated skills and tools.

## Interrupted writes and recovery

Never replay an uncertain write or change its request ID to bypass a receipt. Inspect the receipt, retained before/after artifacts and actual native state first. The app host preserves its durable write/ownership journals across restart and blocks unsafe backend replacement. An app error is also retained conservatively; release/reacquire is not a recovery mechanism.

Recovery is an operator action after independent native reconciliation, using the pinned framework's administrative commands against this app's explicit private `state` directory. Consult that installed framework's `--help` for receipt inspection and reconciliation commands. No model-facing tool acknowledges uncertainty, retries the write or rolls back native state automatically. Retain the evidence and matching request/context identity with any administrative decision.

## Optional APIs and validation scope

Framework Tasks and Skill extensions are not registered by this app. Native engineering calls are not automatically wrapped as Tasks. The four workflow skills ship as ordinary Codex plugin skills; framework Skill publication is not required to load them.

Package tests cover integrity rejection, installed dependency health, actual frontend/native transport, ownership, result parity, native review guards, reload and durable lost-reply handling with a test provider. They do not attach to a TIA project or establish native transaction, device, runtime or model-driven chat acceptance. See [native acceptance](NATIVE-ACCEPTANCE.md) for engineering evidence and limits.
