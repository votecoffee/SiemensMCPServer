# Validation status

This document summarizes public validation scope. Project-specific exports, screenshots, process IDs, native resource fingerprints, transcripts and machine-specific reports are retained outside the public repository.

| Validation layer | Scope |
| --- | --- |
| Offline tests | XML/YAML handling, typed edits, identity/companion checks, preview lifecycles, worker reload, rollback, hashes and path validation. |
| Build and transport checks | V15.1–V19 API profiles, stdio initialization, tool schemas and installed API metadata; V16–V18 checked with V19 compatibility APIs. Fresh package extraction is checked separately. See [compatibility](API-COMPATIBILITY.md). |
| Comfort/classic V15.1 native checks | Selected resource/text edits, transfers, guards, compile/save/reopen and attached-project worker reload have been exercised in a limited test environment. |
| Unified and PLC native writes | Separate native acceptance is still required. |
| Generated Comfort screen/tag packages | Offline generation, binding checks, worker reload and exact file materialization tested; native import/compile/save acceptance remains pending. |
| PLC readable-source and cross-reference inspection | Offline request/encoding/search/bounds tests and installed API signatures checked. Native attached-project acceptance remains pending. Native cross-reference API is present in inspected V18/V19 metadata and absent in V15.1/V16/V17. |
| Project/open global library inspection | Synthetic metadata/path/page/limit tests, V15.1–V19 library API signatures and real worker/MCP routing/reload checks pass. New native attachment/library-read acceptance remains pending. No library opening, updates or instance creation are implemented. |
| Unified V16/V17 flat screens | Synthetic catalog tests and installed screen/item/text/layout/dynamization signatures checked. Native attached-project acceptance remains pending. |
| V21 module loading and project graphics | Synthetic identity/service-routing tests pass. SDK compilation and native project acceptance remain pending. |
| Other API versions/devices | Require their own builds and native acceptance; exact V15.0, V20 and V21 are not established by these checks. Native V16/V17/V18 installations were not exercised. |
| Runtime/device behavior | Not established by engineering tests; no download, watch or force workflow is provided. |

Earlier native checks used earlier release binaries. Passing them does not certify every later binary, device category, operation or API version. Build success, native API signature checks and exported-file comparisons do not establish runtime behavior or complete project preservation.

## Known boundaries

- Classic V15.1 import retains active translation slots. Clear existing translation text instead of removing the slot.
- Classic alarm definitions, library faceplate definitions and controls outside Siemens' export surface require separate support or native engineering workflows.
- Generic native import/generation acceptance is distinct from strict candidate/readback equality. Consult each tool's documented scope and result.
- Compilation, save and reopen are separate from edit transactions. Compile diagnostics do not establish device behavior.
- Native archive creation requires a saved project; archive retrieval needs its own validation.

## Native validation procedure

Use a dedicated project copy with an archive, explicit process/project/target selection and bounded scope. Retain before/candidate/after artifacts, verify stale-plan and failure behavior, compile, restore test changes and independently check save/reopen when applicable. Do not infer preservation of objects the native API cannot export.

Keep raw evidence outside the checkout or under the ignored `.local/` directory. Public reports should describe reusable feature/version boundaries without identifying private projects or local environments. See [feature coverage](FEATURE-COVERAGE.md) for implemented routes.
