"""Optional pinned package profile; real .NET backend, disposable app state, no TIA attachment."""
import argparse
import asyncio
from contextlib import AsyncExitStack
from importlib.metadata import version
import json
from pathlib import Path
import socket
import statistics
import shutil
import subprocess
import tempfile
import time

def data(result):
    assert not result.is_error, result.model_dump_json()
    return result.structured_content or json.loads(result.content[0].text)


def wire(result):
    value = result.model_dump(mode="json", by_alias=True, exclude_none=True, exclude_unset=True)
    # .NET omits the optional false flag; SDK 2 routing emits it explicitly.
    # Normalize only this protocol default, retaining every other received field.
    value.setdefault("isError", False)
    return value


async def run(plugin_root):
    plugin_root = plugin_root.resolve()
    profile_path = plugin_root / "framework/compatibility.json"
    if not profile_path.exists():
        profile_path = Path(__file__).resolve().parents[1] / "framework/compatibility.json"
    profile = json.loads(profile_path.read_bytes())
    accepted = profile["acceptedFrameworkVersion"]
    if not accepted:
        raise RuntimeError("Framework acceptance is gated: publish and pin a framework package using MCP Python "
                           + profile["requiredMcpPythonSeries"] + ". No older SDK dependency will be installed or selected.")
    assert version("universal-codex-tools") == accepted, "Install the pinned framework wheel"
    assert ".".join(version("mcp").split(".")[:2]) == profile["requiredMcpPythonSeries"]
    assert version("mcp") == profile["acceptedMcpPythonVersion"], "Install the accepted MCP SDK"
    assert version("mcp-types") == profile["acceptedMcpPythonVersion"], "Install matching MCP wire types"
    from mcp import Client, StdioServerParameters
    from universal_tools.cli import initialize
    from universal_tools.framework import ApplicationHost
    from universal_tools.models import atomic_json
    from universal_tools.transport import list_all_tools
    server = plugin_root / "server/SiemensMCPServer.Mcp.exe"
    helper = plugin_root / "scripts/New-FrameworkPublication.ps1"
    if not helper.exists():  # Source-development check against an earlier verified payload.
        helper = Path(__file__).resolve().parents[1] / "build/New-FrameworkPublication.ps1"
    with tempfile.TemporaryDirectory(prefix="siemens-app-framework-") as scratch:
        root = Path(scratch)
        settings = root / "safeguards.json"
        settings.write_text(json.dumps({"schemaVersion": 1, "mcpWritesEnabled": False}), encoding="utf-8")
        publication = root / "application.json"
        prepared = subprocess.run(["powershell.exe", "-NoProfile", "-File", str(helper),
            "-PluginRoot", str(plugin_root), "-OutputPath", str(publication), "-SettingsFile", str(settings),
            "-ArtifactRoot", str(root / "artifacts"), "-ExtensionsRoot", str(root / "extensions"),
            "-AllowedWriteTools", "tia_save_project"], capture_output=True, text=True, encoding="utf-8", timeout=30)
        assert prepared.returncode == 0, prepared.stdout + prepared.stderr
        receipt = json.loads(prepared.stdout)
        assert receipt["frameworkVersion"] == accepted
        assert receipt["compatibilityAcceptance"] == profile["status"]
        assert receipt["requiredMcpPythonSeries"] == profile["requiredMcpPythonSeries"]
        first = json.loads(publication.read_bytes())
        original_publication = publication.read_bytes()
        existing = subprocess.run(prepared.args, capture_output=True, timeout=30)
        assert existing.returncode != 0 and publication.read_bytes() == original_publication
        invalid_root = root / "invalid-payload"
        shutil.copytree(plugin_root, invalid_root)
        negative_controls = []
        entries = json.loads((invalid_root / "manifest.json").read_text(encoding="utf-8-sig"))
        for failure in ("extra-file", "bad-hash", "path-escape"):
            altered = json.loads(json.dumps(entries))
            if failure == "extra-file":
                (invalid_root / "extra.txt").write_text("unmanifested", encoding="utf-8")
            elif failure == "bad-hash":
                altered[0]["sha256"] = "0" * 64
            else:
                altered[0]["path"] = "../outside"
            (invalid_root / "manifest.json").write_text(json.dumps(altered), encoding="utf-8")
            rejected_output = root / (failure + ".json")
            rejected_args = list(prepared.args)
            rejected_args[rejected_args.index("-PluginRoot") + 1] = str(invalid_root)
            rejected_args[rejected_args.index("-OutputPath") + 1] = str(rejected_output)
            rejected = subprocess.run(rejected_args, capture_output=True, timeout=30)
            assert rejected.returncode != 0 and not rejected_output.exists(), failure
            (invalid_root / "extra.txt").unlink(missing_ok=True)
            negative_controls.append(failure)
        service = first["backends"][0]["service"]
        assert service["session_mode"] == "exclusive" and service["resource_id"] == "siemens-tia-openness"
        assert service["allowed_write_tools"] == ["tia_save_project"]
        transport = service["transport"]
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        state = root / "app-state"
        initialize(state, port)
        direct_times, framework_times = [], []
        async with AsyncExitStack() as stack:
            direct = await stack.enter_async_context(Client(StdioServerParameters(
                command=transport["command"], args=transport["args"], cwd=transport["cwd"]),
                mode="legacy", cache=None, read_timeout_seconds=60))
            negotiated_protocol = direct.session.initialize_result.protocol_version
            assert negotiated_protocol == profile["wireProtocolVersion"]
            native_tool_count = len(await list_all_tools(direct.session))
            direct_reads = []
            for _ in range(3):
                start = time.perf_counter()
                direct_reads.append(await direct.call_tool("tia_list_versions", {}))
                direct_times.append((time.perf_counter() - start) * 1000)
            async with ApplicationHost(state, publication) as host, AsyncExitStack() as frontends:
                frontend = host.create_server(port=port)
                owner = await frontends.enter_async_context(Client(frontend, mode="legacy", cache=None, read_timeout_seconds=90))
                other = await frontends.enter_async_context(Client(frontend, mode="legacy", cache=None, read_timeout_seconds=90))
                assert owner.session.initialize_result.protocol_version == "2025-11-25"
                assert other.session.initialize_result.protocol_version == "2025-11-25"
                definitions = [tool.model_dump(mode="json", by_alias=True, exclude_none=True, exclude_unset=True)
                               for tool in await list_all_tools(owner.session)]
                assert len(definitions) == 14
                context = data(await owner.call_tool("acquire_service", {"service_id": service["id"]}))["context_id"]

                async def invoke(client, name, arguments=None, context_id=context, request_id=None):
                    tool_id = service["id"] + "/" + name
                    descriptor = data(await client.call_tool("describe_tool", {"tool_id": tool_id}))
                    params = {"tool_id": tool_id, "catalog_revision": descriptor["catalog_revision"],
                              "arguments": arguments or {}, "context_id": context_id}
                    if request_id:
                        params["request_id"] = request_id
                    channel = descriptor["invocation"]["channel"]
                    return await client.call_tool(channel, params)

                for direct_read in direct_reads:
                    start = time.perf_counter()
                    routed = await invoke(owner, "tia_list_versions")
                    framework_times.append((time.perf_counter() - start) * 1000)
                    assert wire(routed) == wire(direct_read), {"direct": wire(direct_read), "routed": wire(routed)}
                assert (await invoke(other, "tia_list_versions")).is_error
                old = data(await owner.call_tool("describe_tool", {"tool_id": service["id"] + "/tia_list_versions"}))
                updated = json.loads(json.dumps(first))
                updated["backends"][0]["revision"] += "-r2"
                updated["backends"][0]["service"]["transport"]["args"].append("--analysis-parallelism=1")
                atomic_json(publication, updated)
                deferred = data(await other.call_tool("refresh_services", {}))
                assert deferred["backend_publications"][0]["status"] == "deferred"
                data(await owner.call_tool("release_service", {"service_id": service["id"], "context_id": context}))
                assert data(await other.call_tool("refresh_services", {}))["backend_publications"][0]["status"] == "applied"
                assert (await owner.call_tool("call_read_tool", {"tool_id": service["id"] + "/tia_list_versions",
                    "catalog_revision": old["catalog_revision"], "arguments": {}})).is_error
                for client in (owner, other):
                    assert definitions == [tool.model_dump(mode="json", by_alias=True, exclude_none=True, exclude_unset=True)
                                           for tool in await list_all_tools(client.session)]
                context = data(await owner.call_tool("acquire_service", {"service_id": service["id"]}))["context_id"]
                blocked = await invoke(owner, "tia_save_project", {"expectedProjectPath": str(root / "synthetic.ap15_1")},
                                       context_id=context, request_id="policy-denial-1")
                assert blocked.is_error and "MCP writes are disabled in Safeguards" in blocked.model_dump_json()
                # The app's denied write remains an app-error receipt. No retry or native reconciliation is invented.
                row = host._engine.journal.status("policy-denial-1")
                assert row["status"] == "app_error"
                data(await owner.call_tool("release_service", {"service_id": service["id"], "context_id": context}))
                updated["backends"][0]["revision"] += "-r3"
                atomic_json(publication, updated)
                blocked_reload = data(await owner.call_tool("refresh_services", {}))
                assert blocked_reload["backend_publications"][0]["reason"] == "app_write_reconciliation_required"
                # End this disposable state with its original error receipt intact. Never acknowledge a real native outcome.
        return {"frameworkVersion": version("universal-codex-tools"), "mcpPythonVersion": version("mcp"),
                "wireProtocolVersion": negotiated_protocol,
                "persistentMcpFrontendsAcrossRefresh": True, "nativeBackendTools": native_tool_count,
                "completeReadResultsEqual": True, "defaultIsErrorFalseNormalized": True, "exclusiveOwnerEnforced": True,
                "ownedReloadDeferredThenApplied": True, "staleCatalogRejected": True,
                "stableFrontendDefinitions": len(definitions), "appWritePolicyPreserved": True,
                "appErrorReceiptBlocksReplacement": True,
                "publicationOverwriteRejected": True, "negativePayloadControls": negative_controls,
                "medianDirectReadMs": round(statistics.median(direct_times), 2),
                "medianFrameworkReadMs": round(statistics.median(framework_times), 2),
                "projectAttached": False, "nativeWrites": False, "liveRegistrationChanged": False,
                "codexHostAcceptance": "pending", "nativeFrameworkAcceptance": "pending"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("plugin_root", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = asyncio.run(run(args.plugin_root))
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
