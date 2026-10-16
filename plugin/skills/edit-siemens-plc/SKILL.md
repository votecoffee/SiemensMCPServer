---
name: edit-siemens-plc
description: Inspect and edit Siemens PLC offline engineering software through local TIA Portal Openness, including blocks, data types, tags, native XML transfers and reviewed SCL/STL generation. Use for a selected TIA engineering project; excludes online control, downloading, hardware and watch/force operations.
---

Select the installed API with the `tia_*` discovery tools and attach to the exact process/project identified by the user. Enumerate `plc_list_targets` and choose the intended PLC; never infer it from the first result. Comfort V15.1 is this plugin's primary profile, and PLC tools also build against that API. Preserve the user's authorized scope without requesting duplicate approval.

Feature workers can be updated without replacing a running 0.7.0 host. Discover versions/operations with `tia_list_extensions`, stage a trusted bundle using `Install-Extension.ps1`, and call `tia_reload_extensions(module="plc")`. Successful reload keeps the TIA connection and invalidates all previews; failed reload retains the old worker/previews. Re-read after success. Advertised PLC-source extension operations use `tia_preview_extension` with an exact offline PLC root/fingerprint and empty path, then `tia_apply_extension_plan`; native offline, replacement-scope and readback checks remain in the host. See [extensions](../../EXTENSIONS.md). Reload itself never authorizes PLC changes.

Browse `plc_read_object` from empty path. Follow returned paths for block, type, tag-table and external-source groups; generic snapshots identify the PLC using `targetId`. Use returned editable flags/types/creation arguments. Program code and structured interfaces require native XML import or source generation rather than guessed scalar properties.

For native XML, use `plc_read_resource` or `plc_export_objects`. Preview a reviewed candidate with `plc_preview_import` or `plc_preview_xml_import` into its exact composition. These require the composition fingerprint from `plc_read_object`; the resource export hash has a different scope. Preserve native schema/version, unselected objects and companions. Bundle import restores serializable resources and supported user groups; metadata snapshots alone are not a restorable PLC program. Protected block exports can contain only the public interface.

For SCL/STL, preview exact code with `plc_preview_generate_source`, a unique native source name and the PLC-root fingerprint. Native generation can replace blocks declared by that source; inspect all code and replacement scope before setting `allowBlockReplacement=true`. The source remains in the project and generation uses default groups. CPU/API language support, native generation and runtime behavior are separate checks.

Apply an authorized matching plan/hash once with `plc_apply_object_plan`. Native exports/mutations require verified Offline state. Do not connect/disconnect the CPU to satisfy this guard; report the current state and required offline transition. Run `plc_compile` after applying and report actual diagnostics. Save the whole project with `tia_save_project` only within the user's authorization; other HMI/PLC edits already present are included. Compile/save run outside edit transactions.

On stale content, reread and preview again. On native failure or uncertain transport results, inspect the current project and retained artifacts before proceeding; do not replay a consumed plan or assume rollback. Generic import/generation acceptance is not candidate content equality, compiler success, native save/reopen success or runtime validation. Do not bypass protected blocks or imply safety acceptance.

Read [the guide](../../README.md) for setup and [feature coverage](../../FEATURE-COVERAGE.md) for current boundaries. Native write-path trials remain pending; offline tests and API builds do not establish native acceptance.
