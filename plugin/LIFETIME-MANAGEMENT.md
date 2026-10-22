# Process lifetimes and ownership

Source version 0.21.0 added explicit service startup, bounded connected frontends, Windows process-tree containment and a server-side shutdown fence. Version 0.22.0 pins reviewed framework 0.20.0 and adds the [durable resource ledger integration](RESOURCE-OWNERSHIP.md). In-process policy/ASGI and harmless byte-echo/parent-loss fixtures have distinct scope. Packaged MCP EOF, native cleanup and installed behavior still require separate acceptance.

## Explicit startup and connection limits

Only the operator's `manage.py start` command starts the shared service. A chat's `proxy` command connects to an already running owner or reports that it is stopped. It never starts, reconnects or restarts an owner behind that error. Installation configures the app without starting it. See [administration commands](APP-HOST.md#installation-and-private-state).

The existing state/startup locks preserve one service owner for the configured state directory. Each connected stdio frontend holds an OS file lock under the private state directory. The default ceiling is four frontends; **Maximum connected MCP chats** in the desktop Safeguards editor accepts 1–16 and is read for each new connection. `--max-frontends` supplies the installation/configuration fallback when the shared policy has no field. All sixteen slots are checked before admission, including when a lower ceiling is selected. Lowering a limit never terminates existing connections. Exit releases the slot in the OS; stale PID files are not counted as live connections.

Native requests still use one STA lane and exclusive engineering ownership. Existing analysis worker pools and queue limits remain bounded. More connected chats do not authorize concurrent native transactions, bypass leases or permit another chat to use a prepared plan.

## Private launches and user-owned attachments

On Windows, the launcher, owning Python service and native MCP backend each establish a non-inheritable, kill-on-close Job boundary before launching private descendants. The OS retains each Job handle for that process lifetime. Normal shutdown first uses the existing adapter/worker disposal. Process exit, including abnormal exit, closes the handle and terminates remaining contained descendants. Failure to establish containment rejects startup before children are created.

The bootstrap waits for an installed-runtime supervisor. That supervisor stays for the child's full lifetime, retains a handle to its immediate parent, establishes its Windows Job before child creation and records ownership before forwarding stdin. EOF closes the child's stdin and permits a bounded drain. A timeout retains recovery evidence; supervisor exit closes its own tree. Each pipe has one reader; bytes are neither parsed nor retried. The native host observes the public transport completion task and requests Generic Host shutdown on EOF. Harmless process fixtures exercise Python byte forwarding and parent-loss cleanup; full MCP/native drain behavior remains unassessed.

The separately started service is not created by a chat proxy. Closing the last frontend is therefore not authority to terminate the shared owner or another chat's work. The service remains subject to explicit shutdown and its retained ownership/write journals.

TIA Portal is attached through an existing Siemens process. This session does not launch it. Status reports `attached-user-owned`, `nativeApplicationLaunchedByThisSession=false` and `closeNativeApplicationOnDispose=false`. Disposal detaches Openness; it does not close the user's project or kill the TIA application. No global process-name search or broad Node, cmd, conhost or native-application kill is used for cleanup.

## Ownership reporting and recovery limits

Doctor reporting inspects only members of the current Windows Job, up to 256 identities. It verifies membership and records PID plus process creation FILETIME while retaining query/synchronization handles. Unavailable, truncated and unverified-member counts are explicit. External/attached applications have no cleanup authority. Tray reporting uses the same bounded inventory without contacting an engineering application.

This Job inventory is process-local. The separate 0.22 ledger retains actual supervisor generations, exact host/PID/creation/image identities, private workers, indirect frontend launchers and report-only TIA attachments. It authorizes no PID-only discovery or automatic cleanup. Existing durable engineering context and write journals remain independent; retiring a process claim never acknowledges an engineering result.

## Graceful shutdown

The authenticated `/shutdown` administration endpoint checks the exact loopback Host, bearer credential, absent browser Origin and POST method. The server holds the same admission lock used by engineering frontend invocations while checking active calls and existing ownership/outcome journals. It refuses shutdown with HTTP 409 if work is active, contexts or unreconciled outcomes remain, or the check cannot complete. Successful admission fencing rejects new calls before graceful exit is scheduled. The caller cannot turn a stale idle observation into permission to kill active work.

Shutdown never clears a lease, consumes a plan, reconciles a failed receipt or retries a native operation. Forced process loss can leave an uncertain native effect even when private processes are cleaned up. Preserve original request IDs, before/candidate/after evidence and journals; inspect the actual outcome before further changes. Tray restart remains disabled until an app-owned supervisor has separate acceptance.

## Verification

`scripts/test_lifetime_policy.py` runs entirely in process: temporary OS file locks, policy parsing, stubbed backend admission and ASGI request dispatch. It starts no MCP server, worker, HTTP listener or native app. Focused `SafeguardTests` cover the shared GUI property's bounds and property-grid visibility. Static compilation does not start the MCP executable.

Before deployment, separately authorize and run packaged MCP tests for EOF, launcher/backend/owner crashes, partial startup, full private-descendant cleanup, preserved user-owned TIA attachments, connection-limit races and shutdown admission races. Then verify the installed route. Harmless fixtures are not acceptance of native TIA shutdown or Codex reconnection. Tray restart remains disabled.
