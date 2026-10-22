---
name: inspect-libraries
description: Browse TIA Portal project libraries and already-open global libraries for types, versions and master-copy metadata through SiemensMCPServer. Use for engineering library inspection; excludes library updates and instance creation.
---

The standalone Codex plugin uses an app-owned framework gateway. Its engineering discovery tools reach the Siemens operations named below; those operation names are backend names. Search with `search_tools`, inspect each `describe_tool` result, and acquire `siemens-engineering` with `acquire_service`. Keep the returned `context_id` for this complete workflow. Invoke the exact `siemens-engineering/<operation>` through its returned `invocation.channel`, with the current `catalog_revision`, unchanged engineering arguments and context. Write-channel calls also need a fresh unique `request_id`; metadata never grants app permission. Inspect `describe_service` after acquisition. Complete/reconcile work before `release_service`; it closes the native adapter and discards its selection/previews. Never release/reacquire or change request IDs to bypass an uncertain outcome. See [app host operation and recovery](../../APP-HOST.md).

Use the SiemensMCPServer project extension for read-only library inspection. Inspect `tia_list_extensions` for the `project` module and its operation schemas; it requires host 0.8.0 or newer. `tia_get_api_capabilities().projectLibraries` reports API-member presence, separately from native project acceptance.

Use the exact process and project path when attaching. Read the attached path from `tia_get_status`. Call `tia_read_extension` with `module="project"`, `operation="list_libraries"` and JSON arguments containing `expectedProjectPath`, `capture={}` and `options={}`. This lists the project library and already-open global libraries; it does not open library files.

For `operation="browse_library"`, keep the same project path and supply `capture={"libraryId":"<returned ID>","path":""}` to read its root. Follow returned paths to folders, types, versions and master-copy content descriptions. Global IDs identify native library paths, not content hashes. Do not substitute HMI/PLC object paths or follow parent, dependency or instance handles.

Use `capture.maxRows` and the returned `nextOffset` to page. `hasMore`, `payloadLimited`, `offsetLimitReached` and per-row `truncatedFields` describe incomplete results; `unsupportedFields` means the API lacks a member. Paging follows native composition order and is not a stable snapshot. Rediscover after library changes, and do not treat incomplete results as proof that an item is absent or unused.

These tools inspect metadata only. They do not expose faceplate definitions, instantiate master copies, update/release types, save libraries or establish runtime compatibility. Read the [library guide](../../LIBRARY-INSPECTION.md) for field/version limits and native acceptance status.
