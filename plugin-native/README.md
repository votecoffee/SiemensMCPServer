# SiemensMCPServer native direct candidate

This candidate restores the existing local TIA Portal Openness tools through one direct stdio host, with four Siemens workflow skills and data-only, revision-pinned guidance readers. It has no gateway, Python service or universal plugin dependency. Comfort V15.1 is the first native pilot; Unified and PLC acceptance remain separate.

Build it with `build/Build-NativePlugin.ps1`. Building publishes binaries and package bytes only. It never starts the MCP host, workers, TIA or a service, and never changes an installed plugin. The installer deliberately refuses this candidate profile until coordinated runtime/native acceptance. Keep the accepted offline installation available.

The package contains the 51 existing engineering tools, explicit `tia_detach`, and four guidance/doctor tools. Call `siemens_list_workflows`, then read the chosen workflow with its catalog revision and SHA256. Invoke engineering tools directly using their live schemas. No service/context acquisition is required. Tools are declarations until the packaged host has passed a real disposable MCP probe.

`scripts/prepare_native_pilot.py --package <extracted-package> --output <new-private-directory>` verifies the full payload manifest and writes an absolute-path, read-only connection description, isolated settings, ledger and artifact locations. It does not register or start anything. This avoids relying on client expansion of plugin path placeholders. Relocation requires generating a fresh description; preserve the original package hash and receipts. Do not merge it into normal Codex settings or enable a second Siemens route during a pilot.

The pilot policy comes from `scripts/fixtures/native-pilot-policy.json`, whose exact camelCase property names are checked with the production strict settings reader. It disables desktop/MCP writes and apply/compile/save/archive/reopen, with one frontend, eight owned resources/processes and 1024 MiB aggregate Job memory. A policy parse refusal occurs before MCP/worker startup; preserve that receipt and correct the source rather than weakening the settings parser.

Start with metadata/guidance and pure worker discovery in a disposable MCP client. After that passes, manually open a fresh disposable TIA V15.1 project and attach using its exact PID and project path; browse/export and explicitly detach. `tia_detach`, EOF and parent loss never save, close or terminate user-owned TIA. A native mutation/restore pilot needs its own reviewed target/candidate and readback evidence. Customer projects and controller/runtime operations are outside this candidate pilot.

The root owns a bounded inherited Windows Job and exact durable worker/attachment claims. Parent identity, connected-session admission, process count, Job memory and EOF drain are bounded; request/resource limits remain GUI editable. Interrupted native work leaves its attachment/journal evidence and cannot be retried automatically. Another host cannot attach through a retained claim for the same TIA process. The candidate does not report to a shared manager or provide tray restart.

Existing feature-worker reload remains available through `tia_list_extensions` / `tia_reload_extensions`; it preserves the attachment, invalidates previews on success and does not authorize writes. Launch writes remain disabled in the generated pilot binding. Production write ceilings, explicit targets, preview fingerprints, candidate hashes, native transactions and uncertainty checks remain in the shared implementation.

Siemens DLLs are supplied by the installed matching TIA environment and are not redistributed. The native host and pure workers target Windows x64/.NET Framework 4.8 with Windows-subsystem executables. MIT license: [LICENSE](../LICENSE).
