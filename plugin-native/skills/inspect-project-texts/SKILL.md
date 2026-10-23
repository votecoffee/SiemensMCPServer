---
name: inspect-project-texts
description: Extract and update existing multilingual TIA project comments, descriptions, captions and text values through the direct native project-text workflow.
---

Use the selected direct Siemens connection and its live schemas. Call `siemens_list_workflows`, select `inspect-project-texts`, then call `siemens_read_workflow` with that `workflow_id`, the returned `catalog_revision` as `expected_revision`, and its exact `sha256` as `expected_sha256`. Read relevant references through `siemens_read_reference` with their returned IDs and pins. On stale pins, list and review the current guide. These readers do not attach TIA or start workers.

The engineering tool names in the guide are direct tools. Keep the owning stdio session; attach only to the explicit process/project and choose the exact target. No gateway, service acquisition or alternate route is required. If the guide readers/native tools are absent, report the missing profile rather than launching another host implicitly.

A guide grants no write approval. Preserve the user's authorized scope, review matching previews/fingerprints/candidate hashes and apply once. Compile and save are separate. After an uncertain native reply, retain artifacts and the attachment claim and inspect native state before proceeding; never replay or switch sessions to bypass uncertainty. Detach only after completion or reconciliation. Detach/EOF never saves, closes or kills user TIA. Package checks do not establish native/runtime acceptance.
