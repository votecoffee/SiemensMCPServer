---
name: inspect-libraries
description: Browse TIA Portal project libraries and already-open global libraries for types, versions and master-copy metadata through SiemensMCPServer. Use for engineering library inspection; excludes library updates and instance creation.
---

Read-only project and already-open global library metadata; no library changes or instance creation.

Use the selected standalone SiemensMCPServer connection throughout the operation. Discover its live tool schemas, then call `siemens_list_workflows`. Select `inspect-libraries` and call `siemens_read_workflow` with that `workflow_id`, the returned `catalog_revision` as `expected_revision`, and its `sha256` as `expected_sha256`. Read relevant supporting material through `siemens_read_reference` using the same revision and exact returned reference ID/hash. Read guides before engineering operations; guide reads need no TIA context or project attachment. If these readers are absent, report the missing host capability rather than switching routes or relying on obsolete procedures. On a stale guide pin, rediscover and review the current guide.

Then search the engineering service, describe the exact live tool and retain its invocation channel/catalog revision. Acquire `siemens-engineering` only for engineering work and retain its `context_id` through completion or reconciliation. Use explicit project/process/target identity, fingerprints and candidate hashes. A guide is guidance, never write approval. Review the exact preview and apply an authorized matching plan once; compile and save separately within scope.

After an uncertain reply, inspect the original request status and owning native session/evidence before proceeding. Never replay a consumed plan, change a request ID, release/reacquire a context or switch routes to bypass uncertainty. Release only after completion or reconciliation; release discards native selection and previews. Package or protocol checks do not establish native or runtime acceptance.
