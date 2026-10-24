# Runtime tag monitoring feasibility

No live tag snapshot/watch tool is shipped. Direct offline reports `runtimeMonitoringAvailable: false`; engineering XML and PLC reads do not provide HMI-internal runtime values.

Siemens documents OPC DA and OPC UA Data Access servers for RT Advanced PC/Panel PC. V15.1 lists DA ProgID `OPC.SimaticHMI.CoRtHmiRTm` and example UA URL `opc.tcp://[HostName]:4870`, not observed endpoints. [WinCC Engineering V15.1 — Communication, pages 18–19](https://cache.industry.siemens.com/dl/files/215/109755215/att_940509/v1/WCC_Communication_en-US.pdf#page=18).

Server enablement changes runtime configuration/deployment. Siemens states that connecting a client can start the HMI OPC server; DA needs DCOM launch/activation rights. Read-only therefore does not prove no-start. No runtime endpoint, COM object or project was probed for this research. [Same manual, pages 23–24](https://cache.industry.siemens.com/dl/files/215/109755215/att_940509/v1/WCC_Communication_en-US.pdf#page=23).

Exact internal-tag/array/index exposure remains unverified. Edition/build, configuration, licensing/security and deployed-project identity need acceptance. RT Professional and Unified require separate adapters; their APIs do not prove classic support. Data Access alone does not establish system-event availability.

## Shared service proposal

One `RuntimeMonitorService` with transport-specific `IRuntimeObservationBackend` implementations would serve GUI/MCP outside the closed offline profile. Proposed capabilities, currently unavailable:

- Exact process creation/image, endpoint/server/certificate and edition/build identity where observable; missing deployed-image binding returns `identityUnverified`.
- Explicit selected internal/connected tag snapshots using observed node IDs/types/ranks/index support. No guessed namespaces, PLC substitution, mirror tags or instrumentation.
- Bounded watch start/read/stop with tag/interval/duration/sample/memory/queue limits, revised intervals, sequences, drops, disconnects and stop reasons. Dispose only owned clients/subscriptions; preserve runtime and other clients.
- Canonical JSONL and derived CSV evidence with identities, selected mapping, settings, timestamps, sequences and hashes. Private watch sets stay outside public Git.

Separate value, raw quality/status, missing/error and connection state. Retain source/server timestamps and add observation time. OPC UA distinguishes these fields, permits unavailable source timestamps and makes bad-quality values unusable. [OPC Foundation, DataValue](https://reference.opcfoundation.org/specs/OPC-10000-4/7.11).

A new observation/Good status does not prove new process acquisition. Classify demonstrated/cached/unknown/unavailable freshness; never turn missing values into zero. Multi-tag reads are not inherently atomic: use serial/commit-token pre/post coherence with bounded retries where available, otherwise disclose unknown coherence. Initial scope would permit approved browse/read/subscribe only, without writes, method calls, acknowledgements, activation, trust-store edits or security downgrades.

## Acceptance proposal

Review one exact disposable runtime/project, endpoint/security configuration, already-running server evidence and deployed-image binding. If server enablement, exposure changes, scripts/mirrors or download is needed, prepare a separate source-bound change/rollback proposal before mutation.

Demonstrate internal scalar, connected tag and each required array/index shape against a native reference view. Check missing/unsupported tags, Bad/Uncertain quality, unchanged values, timestamp semantics, disconnect/reconnect, cancellation and JSONL/CSV evidence. Confirm stop preserves external runtime and other clients. Events need a separately verified edition/version-specific source. Event codes/static handler counts cannot alone establish overflow. Mock/offline tests do not satisfy native acceptance.
