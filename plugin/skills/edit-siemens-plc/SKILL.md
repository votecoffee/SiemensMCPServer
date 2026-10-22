---
name: edit-siemens-plc
description: Inspect and edit Siemens PLC offline engineering software through local TIA Portal Openness, including readable source, cross-references, native XML transfers and reviewed source generation. Use for a selected TIA engineering project; excludes online control, downloading, hardware and watch/force operations.
---

Offline Siemens PLC engineering only; no online control, download, hardware, watch or force operations.

Use the selected standalone SiemensMCPServer connection throughout the operation. If the application service is stopped, report that state; do not start or restart it implicitly from a chat. Discover its live tool schemas, then call `siemens_list_workflows`. Select `edit-siemens-plc` and call `siemens_read_workflow` with that `workflow_id`, the returned `catalog_revision` as `expected_revision`, and its `sha256` as `expected_sha256`. Read relevant supporting material through `siemens_read_reference` using the same revision and exact returned reference ID/hash. Read guides before engineering operations; guide reads need no TIA context or project attachment. If these readers are absent, report the missing host capability rather than switching routes or relying on obsolete procedures. On a stale guide pin, rediscover and review the current guide.

Then search the engineering service, describe the exact live tool and retain its invocation channel/catalog revision. Acquire `siemens-engineering` only for engineering work and retain its `context_id` through completion or reconciliation. Use explicit project/process/target identity, fingerprints and candidate hashes. A guide is guidance, never write approval. Review the exact preview and apply an authorized matching plan once; compile and save separately within scope.

After an uncertain reply, inspect the original request status and owning native session/evidence before proceeding. Never replay a consumed plan, change a request ID, release/reacquire a context or switch routes to bypass uncertainty. Release only after completion or reconciliation; release discards native selection and previews. Package or protocol checks do not establish native or runtime acceptance.
