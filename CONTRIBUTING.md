# Contributing

Use [GitHub Issues](https://github.com/votecoffee/SiemensMCPServer/issues) for questions, reproducible bugs and feature proposals. Pull requests are welcome.

## Public repository contents

Commit reusable source, tests, build tooling and public documentation. Use synthetic or sanitized test fixtures.

Keep private project names and paths, customer data, native project files, screenshots, acceptance transcripts, hashes identifying private resources and machine-specific notes outside the checkout or under `.local/`. That directory is ignored in its entirety. Do not force-add private files. Build output belongs in the ignored `bin/`, `obj/` and `artifacts/` directories; Python caches and test-result directories are also ignored.

Before committing, inspect `git status --short` and the complete staged diff. Exclude IDE state, compiled binaries, native engineering exports, credentials and local environment details. Git ignore rules do not remove already-tracked files; removing a file in a new commit does not erase earlier commits.

## Validation

Run the offline tests for changes to the engineering adapters:

```powershell
dotnet test Tests/SiemensMCPServer.Tests.csproj -c Release -p:RestoreLockedMode=true
dotnet test Offline.Tests/SiemensMCPServer.Offline.Tests.csproj -c Release -p:RestoreLockedMode=true
```

Use [docs/BUILDING.md](docs/BUILDING.md) for transport and packaging checks. Verify documentation links and examples when updating documentation. Keep the root README description aligned with the plugin manifests.

[Architecture](docs/ARCHITECTURE.md) records which profile owns each behavior and its acceptance boundary. The shared framework and separate management app live in [CodexMCPBroker](https://github.com/votecoffee/CodexMCPBroker); this repository retains its own standalone server, Codex plugin and skills. Do not replace them with a universal plugin or upgrade reviewed dependency pins as part of a documentation change.

Run `scripts/test_root_commands.py` for CMD quoting/forwarding, help, install-only/check-only and failure propagation in a disposable copied checkout. Run `scripts/test_codex_config_bytes.py` for byte-exact UTF-8 TOML publication. These fixtures do not access a normal Codex config or start an engineering application. Reusable publication code must emit explicit CRLF as binary bytes on Windows, preserve parsed unrelated settings, reject malformed/stale inputs and retain evidence without automatic repair/retry. See [configuration publication](docs/CONFIG-PUBLICATION.md).

Describe the problem, resulting behavior and checks in the pull request. Distinguish offline tests, installed API checks, native engineering acceptance and runtime behavior. A build or exported-file comparison does not establish native import, save/reopen, download or device behavior.

Native checks should use a dedicated test copy with a backup and restoration plan. Retain raw evidence locally; publish only a generic, appropriately scoped validation summary.
