# Standalone app host

Plugin 0.18.2 uses framework 0.17.0, Python MCP SDK 2.3.0 and matching wire types. Its 14 frontend tools discover and invoke the existing 51 Siemens engineering operations. The C# backend retains SDK 2.2.0 and direct MCP access. This is a dedicated Siemens application: installation neither requires the Universal Tools plugin nor registers Siemens with a shared broker. The framework's `broker_status` tool reports this app host's status.

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

The Windows launcher waits for its frontend and returns that process's exit status. The client can therefore track the connection lifetime until shutdown. Verification checks this lifetime and explicitly releases its test context before reporting success.

Stopping waits for the host's OS-owned state lock to be released, after HTTP shutdown and adapter/journal teardown. Startup also waits for that lock before launching a replacement. A busy owner times out safely; it is never forcibly killed and native work is not replayed.

## Interrupted writes and recovery

Never replay an uncertain write or change its request ID to bypass a receipt. Inspect the receipt, retained before/after artifacts and actual native state first. The app host preserves its durable write/ownership journals across restart and blocks unsafe backend replacement. An app error is also retained conservatively; release/reacquire is not a recovery mechanism.

Use `get_request_status` with the original request ID and context; retain its exact completed/app-error/uncertain result. `tia_get_status` in the owning context also reports the current session's pending text-list previews and last apply attempt, project modified state and artifact paths. Retained `apply.json` and `staging.json` distinguish preflight/not-dispatched, native dispatch, a returned import, and matched or unverified readback. Neither a timeout nor a missing reply proves that a transaction had no effect. Inspect current native state and retained before/candidate/native-after files before preparing any further change.

Recovery is an operator action after independent native reconciliation. After releasing unrelated work and stopping this Siemens app host with the administration command above, the pinned framework supports the following command. Replace the placeholder with the exact retained context ID; this clears its reservation and records operator reconciliation without performing any native operation:

```powershell
& $siemensRuntime.python -B -m universal_tools --state $siemensRuntime.state recover-context siemens-engineering --context-id '<retained-context-id>' --acknowledge-outcome
```

The OS state lock prevents recovery against a running host. Preserve journals and original request/context IDs: reservation recovery does not erase an uncertain write receipt or make replay safe. No model-facing tool acknowledges uncertainty, retries the write or rolls back native state automatically. Keep the reconciliation evidence with the operator's decision. Local [workflow receipt validation](CLASSIC-TEXT-LIST-WORKFLOW.md) can check supplied native evidence without acquiring TIA or acknowledging recovery.

### Reconciled requests and upgrades

Host/plugin 0.18.1 adds stopped-host `reconcile-request` administration. Session recovery and request reconciliation are separate: an old `app_error`, `uncertain` or interrupted `in_progress` receipt can still block an upgrade after its session was recovered. Independently inspect the actual outcome, retain exact request ID/fingerprint and native evidence, and write a private evidence JSON containing at least `requestId` and `fingerprint`. Include the observed outcome and supporting artifact hashes. Then explicitly acknowledge that reviewed outcome:

```powershell
& $siemensRuntime.python -B $siemensManager reconcile-request --config $siemensConfig `
  --request-id '<original-request-id>' --expected-fingerprint '<original-request-fingerprint>' `
  --evidence-json 'C:\private\reviewed-outcome.json' --expected-evidence-sha256 '<reviewed-evidence-file-hash>' `
  --summary 'Independent outcome review and evidence retained; original request must not replay' --acknowledge-outcome
```

When upgrading an older host that lacks this command, first stage the verified new package with the installer’s `-PrepareOnly` option, then use its `framework/manage.py` as `$siemensManager` with the existing dedicated Python runtime and config. The host must be stopped and all ownership reconciled first. Keep the normal state directory; do not create a fresh one to bypass receipts.

The command retains a separate immutable copy of the reviewed evidence and an attestation bound to all original request fields. It does not change the original write-journal row, mark a failed request successful, perform native work, retry or roll back. Repeating the original request ID remains blocked. An upgrade accepts only an exact attestation with intact retained evidence; another unresolved request, changed receipt, damaged/missing evidence or a running host still blocks it. This records an operator assertion; hashes do not authenticate native provenance. The installer never generates acknowledgments automatically.

Existing chat connections may try to restart the idle host during an update. For an evidence-reviewed upgrade, the candidate manager also supports `reconcile-upgrade`: it holds the startup lock across the idle-host stop, exact request attestations and publication of the new deployment. It refuses retained ownership and keeps the same state directory and original replay journal. Use a private batch JSON array with `{requestId,fingerprint,evidence,sha256,summary}` for each reviewed request (`evidence` is the exact JSON file path):

```powershell
& $siemensRuntime.python -B $siemensManager reconcile-upgrade --config $siemensConfig `
  --root "$env:LOCALAPPDATA\SiemensMCPServer\AppHost" --payload '<verified-candidate-plugin-source>' `
  --reconciliation-batch 'C:\private\reviewed-requests.json' --acknowledge-outcome --allow-writes
```

Then rerun the normal installer against that same verified package to update the plugin selection. `--allow-writes` retains the usual installation ceiling; native safeguards remain independent. Old launchers reject a mismatched active deployment rather than restarting retired code. This command never forces a process to exit or acknowledges evidence automatically.

Version 0.18.2 also invokes the framework's supported `ApplicationHost.acknowledge_reload_outcome` app-admin API for those exact reviewed receipts. This preserves the original request rows and allows the framework to publish the new backend. The command verifies the actual backend initialization version against the candidate plugin, rather than treating updated plugin files as proof that the backend changed. Discovery during this check only initializes/lists metadata; it does not acquire a TIA context or run project operations.

## Optional APIs and validation scope

Framework Tasks and Skill extensions are not registered by this app. Native engineering calls are not automatically wrapped as Tasks. The four workflow skills ship as ordinary Codex plugin skills; framework Skill publication is not required to load them.

Framework 0.17 adds optional notification, federation, UI, authentication and shared-store profiles. This app keeps the authenticated loopback, single-owner process model:

| Framework feature | Siemens integration |
| --- | --- |
| Tasks and push notifications | Deferred until an app-owned durable job adapter can reconcile the actual engineering operation. Existing synchronous calls retain native receipts and uncertainty barriers. |
| Skills discovery and live catalogs | Four packaged Codex skills remain the entrypoints; no downstream skill sources are configured. Native skill updates still require plugin installation and a new chat. |
| Resources, prompts and completion relay | No relay sources are configured. The current backend exposes engineering tools; no implicit target acquisition or downstream forwarding is added. |
| MCP Apps | The desktop GUI remains available. No web UI extension or iframe rendering is claimed. |
| OAuth, service grants and DPoP | No remote endpoint is configured. The dedicated local host retains its loopback authentication and per-frontend ownership. |
| Redis and replicated native SDK hosting | Not enabled or installed in the application runtime. One process owns the TIA adapter, journals and resource reservations; live engineering sessions are not portable between replicas. |

Package tests cover integrity rejection, installed dependency health, actual frontend/native transport, ownership, result parity, native review guards, reload and durable lost-reply handling with a test provider. They do not attach to a TIA project or establish native transaction, device, runtime or model-driven chat acceptance. See [native acceptance](NATIVE-ACCEPTANCE.md) for engineering evidence and limits.
