# Process lifetimes and ownership

Source version 0.21.0 adds explicit service startup, bounded connected frontends, Windows process-tree containment and a server-side shutdown fence. These changes have in-process policy/ASGI tests. EOF, parent exit, crash cleanup and installed behavior still require isolated runtime acceptance. The pinned framework remains 0.18.0; its process ownership information is **not a durable resource recovery ledger**. Integration with the common ledger must wait for its published package/API and app-owned acceptance.

## Explicit startup and connection limits

Only the operator's `manage.py start` command starts the shared service. A chat's `proxy` command connects to an already running owner or reports that it is stopped. It never starts, reconnects or restarts an owner behind that error. Installation configures the app without starting it. See [administration commands](APP-HOST.md#installation-and-private-state).

The existing state/startup locks preserve one service owner for the configured state directory. Each connected stdio frontend holds an OS file lock under the private state directory. The default ceiling is four frontends; **Maximum connected MCP chats** in the desktop Safeguards editor accepts 1–16 and is read for each new connection. `--max-frontends` supplies the installation/configuration fallback when the shared policy has no field. All sixteen slots are checked before admission, including when a lower ceiling is selected. Lowering a limit never terminates existing connections. Exit releases the slot in the OS; stale PID files are not counted as live connections.

Native requests still use one STA lane and exclusive engineering ownership. Existing analysis worker pools and queue limits remain bounded. More connected chats do not authorize concurrent native transactions, bypass leases or permit another chat to use a prepared plan.

## Private launches and user-owned attachments

On Windows, the launcher, owning Python service and native MCP backend each establish a non-inheritable, kill-on-close Job boundary before launching private descendants. The OS retains each Job handle for that process lifetime. Normal shutdown first uses the existing adapter/worker disposal. Process exit, including abnormal exit, closes the handle and terminates remaining contained descendants. Failure to establish containment rejects startup before children are created.

The launcher waits for its frontend and retains a handle to its immediate parent for exit observation. A closed stdin pipe allows a short cleanup grace period before exiting its own process tree. It never reads stdin alongside the protocol reader. The native host observes the public transport completion task and requests Generic Host shutdown on EOF. These paths still need isolated process-level validation; in-process tests do not establish their timing or cleanup behavior.

The separately started service is not created by a chat proxy. Closing the last frontend is therefore not authority to terminate the shared owner or another chat's work. The service remains subject to explicit shutdown and its retained ownership/write journals.

TIA Portal is attached through an existing Siemens process. This session does not launch it. Status reports `attached-user-owned`, `nativeApplicationLaunchedByThisSession=false` and `closeNativeApplicationOnDispose=false`. Disposal detaches Openness; it does not close the user's project or kill the TIA application. No global process-name search or broad Node, cmd, conhost or native-application kill is used for cleanup.

## Ownership reporting and recovery limits

Doctor reporting inspects only members of the current Windows Job, up to 256 identities. It verifies membership and records PID plus process creation FILETIME while retaining query/synchronization handles. Unavailable, truncated and unverified-member counts are explicit. External/attached applications have no cleanup authority. Tray reporting uses the same bounded inventory without contacting an engineering application.

This inventory is process-local. It does not persist ownership after a server/manager crash, recover shared leases, or authorize cleanup of a process found by PID alone. The durable common ledger is a separate pending integration. Existing durable engineering context and write journals remain intact; they do not substitute for a process/software resource ledger.

## Graceful shutdown

The authenticated `/shutdown` administration endpoint checks the exact loopback Host, bearer credential, absent browser Origin and POST method. The server holds the same admission lock used by engineering frontend invocations while checking active calls and existing ownership/outcome journals. It refuses shutdown with HTTP 409 if work is active, contexts or unreconciled outcomes remain, or the check cannot complete. Successful admission fencing rejects new calls before graceful exit is scheduled. The caller cannot turn a stale idle observation into permission to kill active work.

Shutdown never clears a lease, consumes a plan, reconciles a failed receipt or retries a native operation. Forced process loss can leave an uncertain native effect even when private processes are cleaned up. Preserve original request IDs, before/candidate/after evidence and journals; inspect the actual outcome before further changes. Tray restart remains disabled until an app-owned supervisor has separate acceptance.

## Verification

`scripts/test_lifetime_policy.py` runs entirely in process: temporary OS file locks, policy parsing, stubbed backend admission and ASGI request dispatch. It starts no MCP server, worker, HTTP listener or native app. Focused `SafeguardTests` cover the shared GUI property's bounds and property-grid visibility. Static compilation does not start the MCP executable.

Before deployment, separately authorize and run isolated tests for normal EOF, parent loss, launcher/backend/owner crashes, startup failure, nested Job membership, full private-descendant cleanup, preserved user-owned attachments, connection-limit races and shutdown admission races. Then verify the packaged and installed route. Until those checks and the common ledger integration pass, do not treat the source changes as accepted crash/restart management or installed behavior.
