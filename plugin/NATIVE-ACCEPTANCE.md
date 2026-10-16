# Native acceptance — Comfort V15.1, 2026-10-16

The user supplied an open classic test project for native testing. The session matched its exact `.ap15_1` path and process ID before attachment. One classic HMI and no PLC software were present. Tests used the actual STDIO MCP endpoint with writes enabled, through a local client recording requests, responses and artifacts. This is transport/API testing; a new Codex chat loading the installed tool set remains a separate integration trial.

The original installed 0.6.1 build exposed the V15.1 caption-wrapper gap. A 0.6.2 development build at source commit `006cb65` performed the native trials below. The release adds a guard for the translation-slot limit found by that trial; it does not relax native readback checks. Release-package verification and any later installed-build smoke results are recorded separately in the delivery evidence.

| Native operation | Result |
| --- | --- |
| Exact project attach, HMI and object discovery | Passed |
| Native compressed project archive | Passed; original archive retained with SHA-256 |
| Unsaved-state archive/reopen guards | Both rejected without discarding changes |
| Existing plain paragraph text edit | Passed; punctuation/accented text round-tripped |
| Unselected English/default/Spanish/German resources | Preserved during selected edits |
| Apply single-use and stale snapshot controls | Both rejected |
| Ordinary text-list entry add/remove | Both passed; original semantic fingerprint restored |
| Fill and clear an existing translation slot | Both passed; original fingerprint restored |
| Remove an active translation slot | Native readback differed; transaction rolled back and original fingerprint confirmed. Release rejects this V15.1 operation before staging; clear text instead |
| Native folder creation | Passed using its advertised creation signature |
| Temporary folder and contained screen deletion | Passed; original folder-composition fingerprint restored |
| Native screen import into temporary folder | Passed using a renamed/renumbered copy of an existing native export |
| Screen caption and layout XML edits | Passed strict candidate/readback equality |
| Existing event function clone/remove | Both passed strict native readback; removal restored the prior screen fingerprint |
| System cycle deletion | Rejected by the protected-object guard |
| HMI compilation, baseline and temporary changes | 0 errors, 1 pre-existing warning about script/function execution order |
| Save/reopen and caption persistence | Passed in the same TIA process |
| Final restoration, compile, save/reopen | Original text-list/screen fingerprints matched, temporary folder absent, 0 errors/1 unchanged warning, reopened project unmodified |

Before/candidate/native-after exports and JSON reports are retained outside the source repository. Temporary screen resources are removed after the trial, and restoration is checked independently before the final save/reopen. The original native screen is compared by its semantic export fingerprint, rather than volatile export IDs or project-file byte equality.

The initial trials establish selected workflows on one classic WinCC Advanced V15.1 Update 8 engineering project. The extended trials below add resource and bundle coverage. Every device/category, all event/animation kinds, classic alarm definitions, library faceplate definitions, PLC operations, Unified writes, exact V15.0, and device/runtime behavior remain outside this acceptance. No download or running-device command was issued.

## Extended Comfort resource trials

A second session used the previously allowed packaged 0.6.2 V15.1 executable against the same exact test project, with a fresh native archive before changes. Temporary copies covered internal tags, a parameterized VBScript function, a screen template, a popup, a project graphic with its EMF companion, and a graphic list. Their scalar XML edits passed strict native candidate/readback equality. Permanent-area, global-element and left slide-in grid-color edits also passed; restoring each returned its original semantic fingerprint.

The first copied tag-table import encountered existing HMI-wide tag names even though the table was in a new folder. The transaction rolled back, and a fresh read confirmed that the temporary table composition remained empty. Renaming the copied tags as well as the table allowed the import. The script copy retained parameter order and renamed its return assignment. Scripts were compiled without being executed.

The native graphic export retained `payload files/DefaultImageStream.emf`; the original, imported copy and edited native readback had the same SHA-256, `f84cf7a838eb968659b1b219fe57e193ad98863156c38eb8043e2c2c23b157f7`. A deliberately missing companion path was accepted by the old preview but never applied. Release 0.6.3 rejects missing, rooted and traversing external file references during staging/import validation, including indexed bundles with absent destination folders. The new guards were tested offline against synthetic cases and the real native export; they were not attached to TIA as a new executable during these trials. Slide-in edge identity is also protected in 0.6.3.

Native indexed-bundle imports passed separately for tags, templates, popups and scripts. Deleting a nested script folder before import exercised folder recreation and recovery of its contained script. Independent semantic comparisons matched all five resource exports across these four bundles. Generic import acceptance remains distinct from semantic equality; this trial performed the extra comparisons explicitly. Temporary changes compiled with zero errors and the same pre-existing script-order warning. All seven temporary resource fingerprints matched after save/reopen. Cleanup removed the test folders, graphic and graphic list. After final compile/save/reopen, nine original resource and six composition fingerprints matched the starting state; compilation again had zero errors/one unchanged warning, and the reopened project was unmodified. The fresh archive and extended delivery evidence were retained outside the repository.

These trials cover selected API-visible resources. Siemens' [exportable screen-object table](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/screens/overview-of-exportable-screen-objects) identifies unsupported control types, including alarm-view and trend controls. Matching native exports does not establish preservation of objects outside that export surface or runtime behavior. Classic alarm definitions and library faceplate definitions remain separate adapter gaps.

The archive implementation uses Siemens' [native project archive API](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/archiving-and-retrieving-a-project), requiring a saved project. V15.1 itself reported the 143-character archive path limit; the adapter stages the native archive in a short temporary directory before retaining the file under the chosen artifact root. Archive retrieval was not trialed.

## Attached-project extension reload trials — 0.7.0

The packaged V15.1 0.7.0 host built at source `3c7e0a1` was attached to the same exact test project after Siemens access approval. A fresh native archive preceded changes. The host process remained `37968` and its TIA process remained `52464` through a Comfort worker upgrade from 0.7.0 to 0.7.1. The upgrade used an isolated external extension registry and introduced the new `reload_grid_preset` operation without replacing the host executable or reconnecting its persistent STDIO client.

Both an existing dedicated XML preview and an existing generic extension preview were rejected after reload. A fresh read confirmed that neither stale attempt changed the original global-elements resource fingerprint. The newly discovered preset then changed only its editor `GridColor` from `0, 0, 0` to `9, 8, 7`; native readback matched the candidate exactly. Reloading the Unified worker while that Comfort edit was unsaved retained the same attachment, modified state and edited fingerprint. Comfort and PLC worker generations were unchanged by the Unified reload. This tests worker isolation, not a Unified project write.

Restoration through the dedicated XML tool returned the original semantic fingerprint, `4543f5707269650a78414d3f77d5e61c881396fa9c031490b299bda13d96c34d`. Native HMI compilation returned zero errors and the same existing script-order warning. The exact project was saved and reopened, and a fresh read again matched the original fingerprint with the project unmodified. No runtime or download operation was issued. The final release also includes the later broker-side PLC source/replacement-authorization guard from `2fc13e7`; that separate PLC change was validated offline and was not part of this native Comfort trial.

Pure-worker integration additionally covered rejected-update rollback, tampered payloads, a worker crash, a 30-second worker deadline, Unicode, unchanged MCP schemas and owned-child shutdown. Those checks used no attached project. These results establish selected native behavior through one persistent MCP client. Activation of the initial 0.7.0 host in the installed Codex plugin remains a separate migration step; subsequent worker updates use the fixed reload tools.
