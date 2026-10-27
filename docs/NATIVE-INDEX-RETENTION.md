# Native lifecycle diagnostics and retention limits

The mutable backend records lifecycle stages under the app-owned
`state/launcher-stages/` directory when launched through the stable native entry.
Each file is bound to the executable PID and creation time. It contains backend
entry, installed metadata lifetime/parent identity, stop request, host stopping,
work-admission request, main-return intent and forced-drain-exit intent. It does
not record tool arguments, project contents or an observed terminal code.

The sink is asynchronous in the same process, bounded to64 events and32KiB per
root, with at most4096 bytes per event. Queue producers do no file IO. Final flush
is best effort with a25ms wait ceiling; abrupt termination or unavailable storage
can leave incomplete diagnostics. Every record says observedTerminal=UNKNOWN.
A main return or forced exit intention is not proof the process actually exited
with that code, and is never native engineering acceptance.

The approved native launcher and its assets are unchanged. It still refuses a new
generation when128 JSON claims remain in state/active, before entering the
mutable backend. Normal returned EOF archives its claim; graceful parent-stop
return can also archive a failed-or-uncertain result. Forced termination can
bypass this disposal. Completed receipt directories do not consume the active
index bound. Metadata discovery and active engineering work remain separate.

This candidate does not automatically move claims, add a bootstrap maintenance
CLI, alter128 or release engineering uncertainty. A safe automatic retention
implementation still needs a reviewed reference-consistency contract across
session-artifact writers, a defined classification for lifecycle-only evidence,
and bounded commit/restore controls. The existing resource ledger byte lock
protects resource records but is not a common lock for every session-file writer.
Moving a still-live root's own claim before Environment.Exit can also race the
immutable launcher's normal Dispose and is not implemented.

Until those guards are implemented and tested, retain original claims and all
native/resource journals. Use only an independently reviewed operator recovery
with exact process-generation, reference, file identity, ACL, timestamp, mutex,
preimage and restore evidence. Diagnostic files themselves are references and
must not be silently ignored. An outside operator remains necessary to bootstrap
an already-full index because the backend cannot run before the launcher guard.
