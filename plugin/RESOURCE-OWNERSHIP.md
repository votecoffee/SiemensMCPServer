# Resource ownership and recovery

Source version 0.22.0 adopts the reviewed shared framework 0.20.0 record contract. Each app retains its own plugin and native skills; the independent Windows manager inspects the common private ledger without a universal Codex connection. Source and harmless-process checks do not establish packaged MCP transport, native TIA, dirty-document or installed Codex acceptance.

## Supervisors and records

Explicit `start` launches `service_supervisor.py`; that retained supervisor uses the suspended, streamless Windows background profile for the shared HTTP host. Its native recovery barrier is durable before creation and its exact identity is bound before resume. A separate OS supervisor lock prevents duplicate startup and remains held through verified tree closure. The `serve` entrypoint requires the live supervisor's exact resource/process or Python-redirector identity; it cannot be invoked as an unsupervised alternative. Retained native/shared-host barriers block another start until reviewed recovery. The temporary start CLI never owns the surviving host's ledger record.

The Windows Codex bootstrap waits for `frontend_stdio.py` in the dedicated runtime. This retained supervisor owns its proxy for the proxy's entire lifetime. It establishes a kill-on-close Job before creating the child, records a launch reservation and binds its exact identity before forwarding stdin. Windows Python redirectors and other verifiable frontend Job descendants receive separate report-only private claims. Membership sampling is bounded; the OS Job covers descendants between snapshots. No global executable-name discovery or adoption is used.

Bootstrap admission also holds an early OS slot before starting app-created wrappers. Proxy admission separately bounds all actual connections, including direct proxy clients. Both use the GUI chat ceiling, so rejected connection storms cannot create an unbounded set of dedicated-runtime helpers. A Windows redirector is distinguished from the real Python supervisor in identities and parent-exit checks.

The app's native publication uses `native_stdio.py`, another retained supervisor of the exact verified packaged backend. It keeps the original byte stream and waits for that backend. It is not a temporary registration helper. A native recovery barrier is durable before child creation. Original process-handle exit permits a process-only retirement attestation that retains the barrier; it never proves a native operation succeeded or rolled back. Shared app contexts, write journals and no-replay guards remain authoritative.

The .NET native host is the actual supervisor of its pure feature workers. `Mcp/ResourceOwnership.cs` implements the language-neutral JSON, byte-lock, atomic replacement, identity, revision and receipt contract because the shared package has no .NET binding. A reservation precedes worker creation; the host's Job contains the child at creation. The worker's retained process handle supplies its creation/image identity. No worker request is sent until binding is durable. Cleanup intent is flushed before normal/forced worker disposal; confirmed exit completes the receipt, while an unverified exit retains uncertainty. Another retained claim blocks cleanup.

TIA is an attachment to an existing user process. Its record is `ownership="attached"`, `cleanup_mode="report"`, with an explicit native recovery barrier. Normal disposal releases the Openness handle and then the claim. It never closes or kills the user's TIA project/application. Shared/report-only resources remain protected. No app route exposes automatic orphan cleanup or arbitrary launch commands.

Records retain the real supervisor generation and host/PID/creation FILETIME/normalized image identities. The common registry defaults to `%LOCALAPPDATA%\CodexMCPManager\Registry\resources`; an explicit `SIEMENS_MCP_MANAGEMENT_REGISTRY` is shared by the supervisors, native host and manager. Normal default resolution uses the user's Windows Local AppData folder. Commands, environment values, project contents and credentials are excluded from records.

## Limits and shutdown

The desktop Safeguards editor exposes **Maximum connected MCP chats** (default 4, 1–16), **Maximum owned software resources** (default 32 non-retired claims per app across generations, 1–256) and **Maximum processes per owned tree** (default 64, 1–256, including the supervisor). New trees/admissions read their limits. Native scheduler changes still require host restart. A lower setting does not kill an existing connection or clear a retained claim. History is separately bounded to 4,096 records; no automatic deletion is provided.

**MCP stdio EOF drain seconds** is also editable (default 15, 1–600). New supervisors capture this deadline. Timeout preserves the native barrier/uncertain journals and closes the supervisor's private tree; it does not prove a transaction had no effect.

The shared background `resources.launch` profile creates a suspended, atomically assigned Job, but it does not inherit standard streams. Siemens keeps its SDK stdio profile and uses the explicit-registration contract with an already established inherited Job and a gate on request forwarding. CPU startup can precede identity binding; a failure in that interval leaves an unresolved intent and closes the private tree on supervisor exit. It does not guess a PID or claim that the suspended-background profile was used.

Graceful shared-host shutdown still fences admission and refuses active calls, retained engineering contexts, missing journals and unreconciled outcomes. EOF/parent death may forcibly end a private process tree; retained journals and native barriers preserve the need for inspection. Kernel containment cannot undo a transaction already dispatched to external TIA. Tray restart remains disabled pending app-owned native recovery acceptance.

## Reviewed retirement of an old claim

The manager can plan/confirm eligible private orphan cleanup through its supported resource UI/API. Live owners, attached/shared/report-only claims, unknown/foreign identities, other claims and native recovery barriers remain blocked. Review the manager's current record/revision and retained evidence first. Never change a cleanup request ID to bypass an uncertain receipt.

After independent inspection, the app-only `reconcile-resource` administration command can retire a departed owner's claim. Supply a private JSON object with `resource_id`, `expected_revision` and an independent `summary`. Hash those exact reviewed bytes, then invoke the installed manager as shown in [app administration](APP-HOST.md#installation-and-private-state):

```powershell
& $siemensRuntime.python -B $siemensManager reconcile-resource --config $siemensConfig `
  --resource-id '<exact resource ID>' --expected-revision '<reviewed resource revision>' `
  --evidence-json 'C:\private\reviewed-resource.json' --expected-evidence-sha256 '<reviewed file SHA-256>' `
  --acknowledge-outcome
```

The command retains the exact evidence bytes and an app attestation. The framework independently refuses a live/unknown old owner, a live/unknown private resource, a stale revision or a previously retired claim. Existing cleanup receipts and barriers remain in the record. This command terminates no process, adopts no live native instance and changes no engineering write journal. It is not an MCP tool or automatic startup recovery policy.

## Verification boundaries

Focused .NET tests use fake process drivers and private temporary files. Framework 0.20's actual `ResourceRecord` model accepts the emitted records and matches their Unicode/integer/fractional timestamp revision hashes. Python stub tests verify EOF timeout, evidence-bound retirement, live-process refusal and unchanged native barriers/journals. Harmless Windows fixtures use plain byte echo and idle Python children/grandchildren to verify EOF, exit codes and Job teardown after supervisor/parent loss. They contain no MCP server or engineering application.

Packaging, installed stdio/Skills probes, full native worker integration, protected TIA attachment preservation, dirty-project recovery and native transaction acceptance remain separate pending checks. The reviewed wheel is a source dependency change; adopting it does not migrate a live registration or install/enable a plugin.
