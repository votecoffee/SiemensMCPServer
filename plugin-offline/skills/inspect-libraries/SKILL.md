---
name: inspect-libraries
description: Explain the direct offline Siemens scope when library inspection is requested. Native project/global library browsing is unavailable; no library or native host may be opened by this profile.
---

Use the standalone Siemens direct offline connection. Call `siemens_offline_catalog`, then `siemens_offline_describe` for the exact operation and input schema. Supply serialized export content to `siemens_offline_analyze`. Use its paging, source hashes and explicit controller connection mappings. Preserve source files and describe missing or opaque evidence accurately.

This profile has no native engineering, file access, worker, controller or shared service route. Do not start a service, invoke another executable, change a profile, or attach to a native project to bypass a missing capability. Report operations outside the catalog as unavailable. Caller-supplied receipts do not establish native or runtime acceptance. Content analysis is read-only; native edits/import/compile/save require a separately accepted engineering deployment and explicit target.
