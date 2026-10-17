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
```

Use [docs/BUILDING.md](docs/BUILDING.md) for transport and packaging checks. Verify documentation links and examples when updating documentation. Keep the root README description aligned with the plugin manifests.

Describe the problem, resulting behavior and checks in the pull request. Distinguish offline tests, installed API checks, native engineering acceptance and runtime behavior. A build or exported-file comparison does not establish native import, save/reopen, download or device behavior.

Native checks should use a dedicated test copy with a backup and restoration plan. Retain raw evidence locally; publish only a generic, appropriately scoped validation summary.
