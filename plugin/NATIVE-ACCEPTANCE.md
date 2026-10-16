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

This is selected workflow acceptance on one classic WinCC Advanced V15.1 Update 8 engineering project. It does not establish every device/category, binary companion round trip, all event/animation kinds, bulk import, classic alarm definitions, library faceplate definitions, PLC operations, Unified writes, exact V15.0, or device/runtime behavior. No download or running-device command was issued.

The archive implementation uses Siemens' [native project archive API](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/archiving-and-retrieving-a-project), requiring a saved project. V15.1 itself reported the 143-character archive path limit; the adapter stages the native archive in a short temporary directory before retaining the file under the chosen artifact root. Archive retrieval was not trialed.
