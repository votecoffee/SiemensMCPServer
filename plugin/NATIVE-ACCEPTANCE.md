# Validation status

This document summarizes public validation scope. Project-specific exports, screenshots, process IDs, native resource fingerprints, transcripts and machine-specific reports are retained outside the public repository.

| Validation layer | Scope |
| --- | --- |
| Offline tests | XML/YAML handling, typed edits, identity/companion checks, preview lifecycles, worker reload, rollback, hashes and path validation. |
| Concurrent requests and transaction scheduling | Lane/pool unit tests and multiplexed real MCP checks cover parallel local jobs, bounded queues, pre-dispatch cancellation, native-lane metadata responsiveness, generation draining, response isolation and worker cleanup. The integration test remains unattached; native transaction scheduling under simultaneous submissions and external-client contention require separate native trials. |
| Build and transport checks | V15.1–V19 API profiles, stdio initialization, tool schemas and installed API metadata; V16–V18 checked with V19 compatibility APIs. Fresh package extraction is checked separately. See [compatibility](API-COMPATIBILITY.md). |
| Comfort/classic V15.1 native checks | Selected resource/text edits, transfers, guards, compile/save/reopen and attached-project worker reload have been exercised in a limited test environment. |
| Unified and PLC native writes | Separate native acceptance is still required. |
| Generated Comfort screen/tag packages | Offline generation, binding checks, worker reload and exact file materialization tested; native import/compile/save acceptance remains pending. |
| Comfort screen-layout operations | Unit/real net48 worker/MCP checks pass. On host 0.10.4 with worker 0.10.5, selected V15.1 control movement and endpoint-aware Line movement/resize passed exact native readback, compile and save/reopen. Other control/version cases need separate acceptance. |
| Comfort static caption-run edits | Plain/embedded/encoded rich text, styles, escaping, empty slots, language and whitespace/hash tests pass. A selected embedded styled caption passed V15.1 native edit/readback, compile and save/reopen on host 0.10.4 with worker 0.10.5. Other text modes/control types remain separately testable. |
| Comfort control/group cloning and removal | Unit/real-worker/MCP checks pass. Selected V15.1 button/group clones passed native readback, compile and save/reopen. Removal of those copied subtrees passed exact readback and restored the pre-clone resource fingerprint. An inherited duplicate TabIndex mismatch rolled back; a fresh preview with an explicit distinct index passed. |
| Comfort reference inspection/rebinding | Unit/real-worker/MCP checks pass. A selected copied control tag-array element rebind passed V15.1 native readback, compile and save/reopen after independent target/type/bounds inspection. The worker does not itself resolve target existence/types, connection partners, script strings or computed references. Connection/ControllerTag native rebinding remains pending. |
| Comfort behavior copying/removal and owner links | Unit/real-worker/MCP checks pass. Selected V15.1 event replacement retained exported composition order and passed exact readback, compile and save/reopen after correcting a test parameter rejected by compilation. Explicit external Owner/Control mapping and native animation-copy acceptance remain pending. |
| Comfort native XML compatibility | Native ObjectName/#ID adapter and host 0.10.4 readback were exercised by the selected V15.1 cases above. Worker 0.10.5 reloaded on the same attached host with unsaved edits retained; obsolete plans were rejected. Host 0.10.5 shutdown and candidate guards have offline/protocol coverage, with separate native attachment acceptance pending. |
| PLC readable-source and cross-reference inspection | Offline request/encoding/search/bounds tests and installed API signatures checked. Native attached-project acceptance remains pending. Native cross-reference API is present in inspected V18/V19 metadata and absent in V15.1/V16/V17. |
| Project/open global library inspection | Synthetic metadata/path/page/limit tests, V15.1–V19 library API signatures and real worker/MCP routing/reload checks pass. New native attachment/library-read acceptance remains pending. No library opening, updates or instance creation are implemented. |
| Multilingual project-text export/update | V15.1 native extraction has been exercised; exact schema-1.3 updates, Unicode, blank targets, source opt-in, metadata/hash preservation, file candidates, routing and reload have static tests. Native import/readback/rollback and Unified/PLC category trials remain pending. API builds do not establish category completeness or runtime acceptance. |
| Unified V16/V17 flat screens | Synthetic catalog tests and installed screen/item/text/layout/dynamization signatures checked. Native attached-project acceptance remains pending. |
| V21 module loading and project graphics | Synthetic identity/service-routing tests pass. SDK compilation and native project acceptance remain pending. |
| Other API versions/devices | Require their own builds and native acceptance; exact V15.0, V20 and V21 are not established by these checks. Native V16/V17/V18 installations were not exercised. |
| Runtime/device behavior | Not established by engineering tests; no download, watch or force workflow is provided. |
| Project HMI/declaration analysis | Unit and net48 worker/file-command/reload checks cover L5X/PLCopen import, static graphs, binding checks and hash-bound impacts. A fresh complete V15.1 API-visible export and supplied L5X were analyzed read-only using explicit WinCC addressing; native project content remained unmodified. Native partners, unresolved predefined types, opaque code and runtime remain separate. Raw reports/source hashes stay outside this repository. |

Earlier native checks used earlier release binaries. Passing them does not certify every later binary, device category, operation or API version. Build success, native API signature checks and exported-file comparisons do not establish runtime behavior or complete project preservation.

## Comfort worker 0.10.5 native checks

The current checks used an unchanged 0.10.4 host and reloadable 0.10.5 Comfort workers on a dedicated classic V15.1 project copy. A native archive preceded edits. Temporary popup copies bounded the scope; retained candidate/native fingerprints matched for the cases above. The final authored test state compiled with zero errors and one pre-existing warning, then retained both resource fingerprints across save/reopen. Copied controls were removed, temporary resources were deleted, and the original resource/composition fingerprints were independently checked after cleanup and save/reopen.

Two failed trials informed the implementation. A duplicate interactive TabIndex was reassigned by native import; strict readback rejected it and independent inspection confirmed rollback. A Line edit that changed dependent bounds without primary endpoints was rejected by import, followed by TIA exiting. The saved baseline was reopened and verified before continuing. The corrected endpoint-aware move/resize passed native readback, compile and save/reopen; an inconsistent scalar preview was then rejected before native import with unchanged resource content. The exit sequence is observed evidence, not a crash-dump diagnosis or a guarantee against other native failures.

A copied event's test change to the popup X parameter passed XML readback but failed native compilation because it placed a full-width popup outside the screen. Restoring that parameter produced the error-free compile above. Native readback equality does not establish valid function arguments or runtime behavior; compilation remains a separate required engineering check.

These selected checks do not establish every control, caption storage mode, behavior type, explicit owner-link mapping, generated package, companion format, API version or device. Runtime behavior and device deployment remain outside this validation.

## Known boundaries

- Classic V15.1 import retains active translation slots. Clear existing translation text instead of removing the slot.
- Classic alarm definitions, library faceplate definitions and controls outside Siemens' export surface require separate support or native engineering workflows.
- Generic native import/generation acceptance is distinct from strict candidate/readback equality. Consult each tool's documented scope and result.
- Compilation, save and reopen are separate from edit transactions. Compile diagnostics do not establish device behavior.
- Native archive creation requires a saved project; archive retrieval needs its own validation.

## Native validation procedure

Use a dedicated project copy with an archive, explicit process/project/target selection and bounded scope. Retain before/candidate/after artifacts, verify stale-plan and failure behavior, compile, restore test changes and independently check save/reopen when applicable. Do not infer preservation of objects the native API cannot export.

Keep raw evidence outside the checkout or under the ignored `.local/` directory. Public reports should describe reusable feature/version boundaries without identifying private projects or local environments. See [feature coverage](FEATURE-COVERAGE.md) for implemented routes.
