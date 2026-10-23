# Current workflow guidance and host features

The selected profile determines its tools and entry points. The [direct offline profile](../plugin-offline/README.md) has three supplied-content tools and four scoped offline skills. The [native direct candidate](../plugin-native/README.md) declares 52 engineering/lifecycle tools plus the four readers below; its native bootstraps use direct live schemas and require no service/context acquisition. Its doctor reports management/restart unavailable. Build and harmless fixture checks do not activate or accept that native profile.

The sections below describe the earlier optional Python framework profile and its context/HTTP publication mechanisms. [CodexMCPBroker](https://github.com/votecoffee/CodexMCPBroker) is the shared framework plus a separate Windows management app; neither replaces Siemens' own plugin/skills or installs a universal plugin. Direct-profile guidance remains data-only and revision/hash pinned; optional HTTP Skills support is not inferred for the direct host.

Host/plugin 0.20.0 keeps four focused packaged bootstraps: HMI, offline PLC, library inspection and project texts. Their names/descriptions/triggers remain unchanged. Detailed procedures live in `guidance/<workflow>/SKILL.md`; supporting public references remain the root Markdown documents in this plugin. Those are the canonical maintained sources. Data-only bundle preparation generates self-contained reference views, resource manifests and hashes from them; generated snapshots are not parallel manually maintained instructions.

## Selected routes

The earlier framework Codex stdio connection has fourteen framework discovery/invocation tools plus four read-only app tools:

| Tool | Purpose |
| --- | --- |
| `siemens_list_workflows` | Current workflow identities, catalog revision, guide byte hashes and sizes. |
| `siemens_read_workflow` | Exact guide bytes and supporting-reference manifest, pinned by workflow ID, `expected_revision` and `expected_sha256`. |
| `siemens_read_reference` | One exact reference ID/hash from that same revision. |
| `siemens_doctor` | Read-only capability/profile report with explicit implemented, tested and blocked boundaries. |

These reads use the already selected app host. They do not acquire an engineering context, load a native project, attach TIA or contact controllers. Discover the ordinary tool schemas first. Read the selected guide and relevant references, then search/describe the exact current engineering tools. Acquire `siemens-engineering` only for engineering work. Keep the same context, route, native identities, reviewed plan and fingerprints through completion/reconciliation. Guide content never supplies write approval or changes policy.

The same app's authenticated loopback HTTP profile additionally composes `SkillCatalog` through `ApplicationHost.create_server(skills=...)`: `skills/list`, `skills/get`, `resources/read` and `resources/directory/read` are wired. The fixed extension identity is `io.modelcontextprotocol/skills`. Four real skill packages and complete SHA-256 manifests are published under `skill://`. Directory/catalog cursors bind their revision. The stdio proxy does **not** advertise that extension: its live guidance path is the ordinary tools above. A compatible client may deliberately select the app-host HTTP profile before starting a workflow; do not switch routes during an operation. No universal broker registration is added.

SDK resource views and ordinary tool reads share one immutable catalog snapshot, byte-for-byte. Resource reads are side-effect-free. Only bounded UTF-8 Markdown data is accepted; contributed Python, scripts, hidden resources, links and files outside the reviewed manifest are not loaded or executed. The selected importer profile limits publication to five uniquely named skills and bounded complete manifests. URI reads against an old protocol manifest must verify its digest and refetch discovery after a change; ordinary reads reject stale revision/hash pins directly.

## Publish a reviewed guide update

Editing source files alone does not affect a running host or its native worker. Prepare a new data-only bundle outside the source using the verified installed manager and its dedicated runtime:

```powershell
& $siemensRuntime.python -B $siemensManager prepare-guidance --config $siemensConfig `
  --source 'C:\reviewed\plugin' --output 'C:\private\guide-bundle'
```

Review the source, generated files and returned `manifest_sha256`. Read `siemens_list_workflows` for the current `catalog_revision`. An operator can then publish through the private authenticated app-admin endpoint:

```powershell
& $siemensRuntime.python -B $siemensManager publish-guidance --config $siemensConfig `
  --bundle 'C:\private\guide-bundle' --expected-sha256 '<reviewed manifest SHA-256>' `
  --expected-revision '<current catalog revision>'
```

This command is not an MCP/model-facing mutation tool. It validates and snapshots every declared byte, then calls atomic `catalog.publish(..., expected_revision=...)`. Add/update/remove changes appear coherently to connected readers. Stale revisions, mismatched hashes, invalid frontmatter and extra files reject publication and retain the previous catalog. No native session, preview, worker generation, journal or engineering effect is changed. Resource-list/generation notifications are best-effort hints; reconnecting clients refetch.

If a publication reply is lost, refetch the current catalog before another publish. Cancellation after the catalog commits does not undo it; the content, revision and source-bundle hash remain coherent even when notification delivery is interrupted.

**Runtime overrides are session publications.** The receipt explicitly reports `persistent=false`: an owning-host restart restores guidance from the installed canonical payload. Retain reviewed bundles and republish deliberately, or rebuild/reinstall the app to make a source update the installed default. No implicit file watching, background publication or arbitrary extension-code reload is enabled.

The initial host update/reconnect is needed for the new readers and extension composition. Subsequent guide/reference publication needs no native backend replacement or worker reload. Changes to packaged bootstrap metadata or plugin tools still need plugin installation and a new chat. OpenAI's [skill import documentation](https://developers.openai.com/plugins/build/skills#import-a-skill-from-mcp) describes submission-time snapshots, not native runtime fetching by Codex/ChatGPT. Protocol tests do not establish native Codex skill activation.

## Feature and acceptance boundaries

| Feature | Selected integration and evidence boundary |
| --- | --- |
| Process lifetimes and ownership | Source version 0.22.0 integrates framework 0.20's durable ledger, retained stdio supervisors, native worker/attachment records, explicit start and GUI limits. Record/revision interoperability, policy/admission and harmless byte-echo/owner-loss fixtures have separate checks. Full MCP/native/installed acceptance remains pending. See [resource ownership](RESOURCE-OWNERSHIP.md). |
| Current guides, references and Skills publication | Enabled and tested with connected clients, complete content/manifest parity, stale pins/cursors, add/remove, failed publication and refetch. HTTP extension and stdio tool profiles are assessed separately. |
| Native request stages | Enabled: each requesting tool can receive monotonic stage units for queued/dispatched/returned or terminal failure/cancellation. Total/percentage is omitted. Native internal object counts and percent complete are not instrumented. Delivery is optional and request-bound; tests use a fake native adapter. |
| Cancellation and structured status | Existing queued cancellation removes work before dispatch. Active native execution is never thread-aborted. `tia_list_extensions` retains lane snapshots, cancellation-requested flags and the latest 50 terminal rows when notifications are absent. Rows are bounded, process-local telemetry with lane correlation IDs, not durable MCP operation receipts. An after-dispatch failure/cancellation leaves effect uncertainty until actual evidence is inspected. |
| Tool catalog notifications and TTL caching | The same exclusive C# backend uses assessed `auto` negotiation with modern watching and private five-second definition hints. The framework cache is bounded to that retained connection and TTL; detached copies prevent caller mutation. Tests compare twenty metadata listings with and without caching and require a forced fresh check/invalidation to refetch. Existing `broker://catalog` and guide-resource notifications are hints for compatible clients. Native tool definitions are fixed for the server lifetime; worker-operation data still requires explicit inspection. Legacy fallback requires explicit refresh, and missing hints disable caching. Every actual invocation rechecks current schema/effect, policy, ownership and revision. This does not cache engineering results or approvals. |
| Tasks | Blocked: no durable TIA job identity, owner-bound native-job adapter, lost-start reconciliation or demonstrated native job survival. Ordinary synchronous tools and their no-replay journal remain the route. Generic calls are not wrapped as Tasks. |
| Approval continuations | Blocked: no native target/preview-bound resolver adapter. Ordinary authorization, preview hashes, app safeguards and Siemens permission remain independent. No approval prompt is added to metadata reads. |
| Scoped resource artifacts | Guidance has explicit complete, hash-bound data resources. Engineering report/export resource publication is blocked by the absence of an owner-bound retained artifact registry. Existing engineering readers/receipts remain available; no arbitrary file-path resource reader is exposed. |
| Tray restart, Redis, remote OAuth and Apps | Tray reporting remains enabled with restart disabled pending an app-owned safe supervisor. Replication and unrelated remote/UI profiles stay outside this local single-owner workflow. |

Synthetic/source/package and installed transport checks do not establish a native project edit, native host skill loading, controller/runtime behavior or safety acceptance. A client disconnect or progress/cancellation event never proves rollback or completion. Preserve request IDs, receipts and retained evidence; do not retry an uncertain write or clear another chat's fence.
