# Stable native entry and deployment

The native implementation targets .NET Framework 4.8. Its app-owned `SiemensMCPServer.NativeLauncher.exe` uses a separate Framework AppDomain with the selected immutable runtime's application base and binding redirects, in the same OS process and inherited stdio pair. It does not use the .NET 8 offline loader or its one-process limit. The native host owns the parent/EOF fence, session admission, private-worker Job and exact attachment/resource ledger.

The native launcher has a fixed ABI identity independent of app timestamps. It verifies a strict hash-pinned selection, exact bounded runtime inventory, ordinary contained paths and open read leases. A pointer update affects new roots; live roots retain their selected generation. The runtime AppDomain remains until process exit because its retained unmanaged parent wait cannot be unloaded reliably after SDK EOF. There is no child MCP process or transport-forwarding wrapper.

`build/Install-StableNativeMcp.ps1` stages verified native packages under a persistent app-owned root, resolves physical MSIX paths from the opened publication lock, retains immutable runtimes/selection backups, and derives a standalone plugin with four native skills and an absolute stable command/cwd. Existing launcher asset bytes must match. The installer never starts MCP/TIA, edits Codex settings or changes immutable Codex caches.

Normal selection requires an exact-package receipt for metadata, pure workers, disposable native edit/readback, compile, save/reopen/restore and no outstanding uncertainty:

```powershell
.\InstallMcp.cmd -Native -Package 'C:\packages\native.zip' -AcceptanceReceipt 'C:\private\accepted.json' -AllowWrites -NoPause
```

`-PilotOnly` requires a distinct explicit private installation root and does not authorize normal publication. Launch writes remain off unless selected explicitly. Initial pilot policy uses one frontend, one analysis lane, eight owned resources/processes and 1024 MiB Job memory. Normal defaults allow four frontends, two analysis lanes and 64 shared durable resource claims; each new root still defaults to an eight-process/1024 MiB Job. Exact retained TIA claims prevent competing attachments. Existing policy files are preserved rather than silently enabling writes or raising limits.

Use the returned `safeguardsFile` with the desktop GUI's `--safeguards --settings-file=<path>` to edit native policy. Launch permission, policy, explicit native target, preview/fingerprint/candidate checks and uncertainty barriers apply together. Compile/save/reopen remain separate operations. Attached external TIA is never assigned to the worker Job or stopped by detach/EOF. A disposable fixture that creates its own TIA needs a separately reviewed budget and exact ownership; the worker budget is not proof that TIA itself fits in 1 GiB.

After native acceptance, publish exactly one resolved host route with serialized settings ownership, byte backup, validated TOML, compare-and-swap and readback. Preserve unrelated settings, active old offline roots, rollback packages and uncertainty records. Verify the installed stable route's actual tool schemas, pinned guide and disposable edit/readback after plugin/loader selection. CLI discovery is not proof of an already-open chat's activation. Unified/PLC/runtime acceptance remains target-specific.
