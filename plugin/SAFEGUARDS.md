# Safeguards settings

Desktop **1.3.0** and MCP host **0.15.0** share a per-user policy. Open **Safeguards...** in the desktop toolbar, including before attaching to TIA. The property grid groups every editable policy setting and shows the remaining integrity checks as read-only entries. Select a row to read its scope and limits below the grid.

To open only the settings window, without discovering or attaching to TIA:

```powershell
.\Run.cmd -Safeguards
# Or run a built desktop executable directly:
& .\SiemensMCPServer.exe --safeguards
```

The root command needs an installed desktop 1.3.0 or newer; rerun `InstallAndRun.cmd` to update an older installation. The MCP ZIP contains the server and workers; the desktop editor is built/installed separately from the same repository.

## Editable policy

| Group | Settings | Effect |
| --- | --- | --- |
| Write permissions | Desktop writes; MCP writes; apply; compile; save; archive; reopen | Independent permission ceilings. MCP also requires the launch flag `--allow-writes` and any client authorization. |
| Restricted changes | Native object Delete; overwrite; PLC source block replacement; project translation import; feature extension reload | Restrict the corresponding reviewed action. Overwrite still requires explicit selection for each import. |
| Desktop confirmations | Confirm imports; confirm Save | Controls the desktop review dialogs. Defaults are on, with **No** selected. Disabling a dialog does not disable native validation. |
| Preview limits | Lifetime in minutes (1–1440); pending previews per workflow (1–1000) | Defaults: 30 minutes and 100 plans. MCP uses both limits in all five reviewed workflow types; desktop Unified/PLC imports use expiry. |
| Request limits | Parallel local jobs (1–8); native/local/control waiting capacities (1–1000 each); worker timeout (1–600 seconds); maximum connected MCP chats (1–16) | Defaults: 2 jobs, queues of 100/32/16, 30 seconds and 4 frontends. Scheduler/worker settings apply on host restart; the chat limit applies to new connections. Native operations always retain one STA lane. |
| Project scope | Required project path | Empty permits any explicitly selected project. A populated value pins the exact absolute project path for desktop and MCP attachment and subsequent project operations. |
| Evidence locations | Desktop import evidence folder; MCP artifact folder | Empty uses the existing per-user defaults. The MCP location applies on restart; an explicit `--artifact-root` overrides it. |

**Native object Delete** controls the engineering `delete` action. It does not classify removals inside imported XML, generated source, or specialized XML edits. Disable **Apply** to prohibit those engineering changes as a whole. Likewise, **overwrite** controls routes with an explicit overwrite flag; it does not treat every property or text edit as an overwrite operation.

The classic Comfort desktop workflows retain their documented per-object import behavior. They use the shared write/overwrite permissions, scope and confirmation settings, but do not acquire the reviewed Unified/PLC baseline/transaction pipeline merely by enabling a setting. Their batch reports and source folders retain their existing locations. See the repository's desktop guides for those workflows.

## Persistence and when changes apply

The default file is `%LOCALAPPDATA%\SiemensMCPServer\safeguards.json`. **Save settings** validates and atomically replaces the file, retaining one previous version as `safeguards.json.previous`. A stale editor cannot overwrite a policy another editor saved. **Load defaults** changes the displayed draft; **Save settings** is still required. Compatibility defaults retain existing permissions, while MCP launch authorization remains required. No project or Codex registration is changed by editing this file.

Permissions, project scope, confirmations and preview limits are read again for subsequent operations. Already dispatched native work finishes under its existing transaction rules; editing settings does not abort it. New scheduler limits, worker timeouts and the MCP artifact location require restarting/reconnecting the host. Extension reload does not rebuild the host scheduler.

Source version 0.21.0 adds **Maximum connected MCP chats**, read for each frontend admission. Lowering the ceiling keeps existing connections alive and rejects new ones until capacity is available. This bounds lightweight connections; native transactions remain serialized. Service startup is explicit. These [process-lifetime changes](LIFETIME-MANAGEMENT.md) still require isolated runtime acceptance before deployment.

Source version 0.22.0 also adds **Maximum owned software resources** (default 32, 1–256 across supervisor generations) and **Maximum processes per owned tree** (default 64, 1–256 including the supervisor). New supervisors/trees and native-host startup apply these ceilings. Limits do not erase uncertain claims or authorize native cleanup. See [resource ownership](RESOURCE-OWNERSHIP.md) for ledger/recovery and pending native acceptance.

Increasing the preview lifetime never extends an existing plan's captured deadline. Shortening it also limits older plans immediately. Reducing the pending-plan limit blocks new previews without deleting retained evidence or automatically applying old plans.

Both executables accept `--settings-file=C:\path\to\safeguards.json` for an isolated policy; pass the **same absolute file** to the GUI and server. The root launcher accepts `Run.cmd -Safeguards -SettingsFile "C:\path\to\safeguards.json"`. A different Windows user or settings path has a separate policy. The host reports the current settings file/policy and launch permission through `tia_list_versions`; `tia_list_extensions` reports the active scheduler limits.

Invalid settings fail closed instead of reverting to defaults. Restore/correct an invalid file and reload it; the GUI retains the error without replacing the file. If a running host has observed a saved file and it disappears, operations requiring policy checks fail until it is restored or the host is restarted after reviewing the policy. A never-created file uses compatibility defaults.

## Always enforced and external controls

The GUI lists these checks for inspection; they are not bypass switches:

- Exact project/target identity, baseline fingerprints and candidate hashes on reviewed routes.
- Exclusive native access, transaction/readback checks and Siemens cancellation/commit checks where the workflow provides them.
- Single-attempt, generation-bound plans and retained evidence; uncertain writes are not automatically retried.
- Verified PLC Offline state, supported native API/type limits and protected standard cycles.
- File/path/parser/package integrity protections and their bounded payload limits.
- Separate apply, compile and save; imports do not automatically save a project.

Siemens access prompts/group membership and Codex approvals belong to those applications. This editor does not alter them. A local policy cannot grant a permission denied by the host launch or client.

## Verification scope

Unit checks exercise malformed/duplicate/unknown settings, bounds, persistence, conflicting editors, revoked permissions after preview and captured expiry. The desktop smoke check edits and reloads the real property grid with an isolated file. `scripts/test_safeguards.py` checks the production stdio host's permission gates, live policy refresh and configured queue limits without attaching or invoking native writes. These checks do not renew native project acceptance for every workflow or API profile.
