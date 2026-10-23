# UTF-8 TOML publication

The public plugin installer delegates registration to the Codex CLI. It backs up an existing config, but contains no direct TOML serializer. Root desktop commands do not access Codex configuration. Do not attribute a malformed file to those paths without a retained preimage, exact writer/version and byte evidence. Never rewrite a user's repaired config as part of a documentation/test run.

`scripts/codex_config_bytes.py` is a small explicit-path library for reusable publication tooling. It has no CLI, default Codex home, process launch or plugin-registration route. `append_tables` parses UTF-8 TOML, retains an optional BOM, rejects invalid UTF-8/lone CR/CRCRLF, requires an explicit new table and verifies that every existing parsed setting remains equal. LF/CRLF input is normalized once into explicit CRLF on Windows (LF is also selectable). Literal escape sequences and parsed multilingual/multiline values are preserved.

Publication writes those prepared **bytes** through a binary temporary file, flushes/fsyncs, rechecks the expected preimage, then replaces the explicitly supplied target. It never passes preconverted CRLF to a text-mode writer. A conflict or failed replacement is not retried and leaves no pending temporary file. Callers still need exclusive publication coordination: the repeated check is not a compare-and-swap or a lock shared with Codex.

Python Windows text mode can translate each LF to CRLF. Feeding it a string already containing CRLF produces `0D 0D 0A`. The focused test reproduces that mechanism in a disposable counterexample. It does not establish that any particular user file was written that way. Binary concatenation of CRLF input and an LF suffix instead produces mixed legal line endings, not CRCRLF; reusable publishers should still emit one deliberate convention.

Run only the disposable fixtures:

```powershell
py -3.13 -B scripts/test_codex_config_bytes.py
py -3.13 -B scripts/test_root_commands.py
```

The TOML cases cover LF/CRLF input, UTF-8 BOM, Unicode/multiline/literal values, parsed unrelated settings, stale preimages, failed replacement and malformed input refusal. Optional `SIEMENS_CONFIG_FIXTURE_EVIDENCE` points to a private output directory for exact before/after bytes, hashes and CRLF/CRCRLF/lone-CR/lone-LF counts. No normal config, Codex CLI, MCP server or TIA process is used. Keep local publication scripts and user-specific audit evidence outside public Git, and leave immutable accepted packages/cache bytes intact.
