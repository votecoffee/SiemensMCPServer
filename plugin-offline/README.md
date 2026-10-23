# SiemensMCPServer direct offline profile

This standalone plugin analyzes caller-supplied exported content. It has three direct tools: `siemens_offline_catalog`, `siemens_offline_describe` and `siemens_offline_analyze`. Read the catalog and the exact operation schema first.

Supported operations cover classic HMI references, controller declarations (Rockwell L5X and PLCopen XML), binding validation, impact reports, screen/control/event/animation inspection, package/text-list validation and classic/Unified text-list translations. Supply serialized content, not file paths. Native workflow receipts are caller-supplied evidence; the server cannot establish their native acceptance.

`siemens_offline_analyze` takes `operation` and `input`, both strings. The operation's `inputSchema` returned by `siemens_offline_describe` describes the object **inside** `input`. Serialize that object once, for example `input: JSON.stringify({ analysis: { resources: [...] }, view: "references", maxItems: 1 })`. The server parses that string once; arrays, scalars, an extra `arguments` envelope and a JSON string containing another JSON string are rejected. Catalog and describe responses expose `inputContract` to distinguish the wire string from the decoded object.

Example MCP `tools/call` arguments for a small exported screen:

```json
{
  "operation": "analyze_hmi_references",
  "input": "{\"analysis\":{\"resources\":[{\"path\":\"Screens/minimal.xml\",\"xml\":\"<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Overview</Name></AttributeList></Hmi.Screen.Screen></Document>\"}]},\"view\":\"references\",\"maxItems\":1}"
}
```

`path` in a supplied resource is a logical source label; the server does not open it. A `Document` with no classic HMI resource still fails resource validation after input decoding.

This profile cannot attach to TIA, discover native processes, access controllers, modify projects, start feature workers, run executables, read/write files or start/connect to a shared host. It accepts no startup options (apart from `--version`), loads no settings files and uses no environment variable to select a different profile. Native write flags, TIA version, artifact/extension roots, recipes and shared-host resource variables cannot enable those routes. The separate engineering package retains those capabilities and its own acceptance requirements.

The package contains the Siemens plugin and four scoped skills. It has no Python, Node, framework wheel, shell wrapper, manager service or vendor assembly. Windows x64 and the .NET 8 x64 runtime are required. The apphost runs the managed assembly in its own process.

The apphost uses the Windows executable subsystem and inherited standard handles for MCP. It does not open a GUI. This prevents Windows from allocating a console host before the application can install its process Job. A bare Windows-subsystem fixture checks byte-preserving stdio and actual process creation identities; packaged MCP behavior remains a separate runtime gate.

The direct executable path and working directory use the documented `${PLUGIN_ROOT}` placeholder ([OpenAI desktop plugin configuration](https://developers.openai.com/plugins/deploy/desktop-only)). The package environment disables diagnostics/profiling/dumps and clears runtime startup hooks/additional dependencies. Installation checks both manifests against the fixed direct launcher/environment before registration. Host application configuration defaults are disabled; GUI/native settings and shared-host environment variables are never loaded by this binary.

Containment is mandatory before MCP starts: a private Windows Job permits one process and 512 MiB committed memory. Four sessions per user/logon session are admitted through retained named Mutexes before SDK startup; crash/exit releases capacity through kernel teardown. A retained parent handle is checked against the root creation time; parent death exits the root. Stdio EOF stops the host, with a five-second final drain limit. Startup failure exits without an MCP listener. No caller-owned or shared process is terminated or adopted. There are no app-launched resources requiring manager orphan cleanup.

Input is capped at 8 MiB per wire frame and 4 MiB of UTF-8 for the decoded `input` string, including surrounding whitespace and JSON escapes within that string. The outer MCP JSON string escaping counts toward the separate wire limit. Malformed JSON, non-object roots and duplicate keys are rejected before an operation runs. Replies are capped at 1 MiB; analysis uses two slots and returns busy when full. Existing operation paging and content checks also apply. These limits are fixed for this profile and cannot be widened through GUI settings or environment variables.

Build a distinct immutable package with `build/Build-OfflinePlugin.ps1`. It compiles and packages without running an MCP server or exporting a live tool catalog. Use its bundled `scripts/Install-CodexPlugin.ps1 -Package <zip>` only after the package has passed your deployment gate. The installer publishes this direct plugin without preparing or starting the framework service. `-PrepareOnly` verifies and extracts without registering or enabling it.

Source, fixture and package checks establish only those layers. A disposable MCP initialization/EOF/owner-death canary and separate Codex plugin loading check are still needed before deployment acceptance. Native engineering is outside this profile.

The `Offline.Tests` project sends actual JSON-RPC bytes through the MCP SDK's stream transport and production tool dispatch without starting the product executable or using a session slot. `scripts/test_offline_protocol.py <path-to-offline-exe>` separately tests a supplied executable's stdio and EOF; it requires one free offline session slot and never stops existing sessions. See [build and test commands](../docs/BUILDING.md#tests).
