"""Probe the Siemens plugin transport resolved by Codex. Never attach or edit a project."""
import argparse
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--codex", default=shutil.which("codex"))
    parser.add_argument("--plugin-root", type=Path, help="Probe an isolated extracted payload instead of the normal Codex profile")
    parser.add_argument("--framework-config", type=Path, help="Explicit isolated app-host config for a packaged probe")
    args = parser.parse_args()
    assert args.codex, "Codex CLI is not available"
    if args.plugin_root:
        source = args.plugin_root.resolve()
        transport = json.loads((source/".mcp.json").read_bytes())["mcpServers"]["siemens-mcp-server"]
        transport = {**transport, "cwd": str(source)}
    else:
        configured = subprocess.run([args.codex, "mcp", "list", "--json"], check=True, capture_output=True, text=True, encoding="utf-8", timeout=60)
        servers = json.loads(configured.stdout)
        server = next((s for s in servers if s["name"] == "siemens-mcp-server"), None)
        assert server is not None, "Codex has not exposed the installed Siemens MCP server"
        assert server["enabled"] and server["transport"]["type"] == "stdio", server
        transport = server["transport"]
    environment = transport.get("env") or {}
    if args.framework_config:
        environment["SIEMENS_MCP_APPHOST"] = str(args.framework_config.resolve())
    plugin_root = Path(environment.get("PLUGIN_ROOT") or transport["cwd"]).resolve()
    command = Path(shutil.which(transport["command"]) or transport["command"]).resolve()
    server_binary = plugin_root / "server/SiemensMCPServer.Mcp.exe"
    assert server_binary.is_file()
    skills = ["edit-hmi-text", "edit-siemens-plc", "inspect-libraries", "inspect-project-texts"]
    assert all((plugin_root / "skills" / name / "SKILL.md").is_file() for name in skills)
    manifest = json.loads((plugin_root / "plugin.json").read_text(encoding="utf-8-sig"))
    gateway = tuple(map(int, manifest["version"].split("."))) >= (0, 17, 0)
    framework_version = json.loads((plugin_root/"framework/wheels.json").read_bytes())["frameworkVersion"] if gateway else None
    if gateway:
        assert transport["args"] == ["-3.13", "-B", "framework/launch.py"]
    else:
        assert command.name.lower() == "cmd.exe"
        assert transport["args"] == ["/d", "/c", "server\\SiemensMCPServer.Mcp.exe", "--allow-writes"]
    process = subprocess.Popen([str(command), *transport["args"]], cwd=transport.get("cwd"),
                               env={**os.environ, **environment}, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
    messages = queue.Queue()
    errors = []

    def read():
        try:
            for line in process.stdout:
                messages.put(json.loads(line))
        except Exception as exc:
            messages.put(exc)

    threading.Thread(target=read, daemon=True).start()
    threading.Thread(target=lambda: errors.extend(process.stderr), daemon=True).start()
    next_id = 0

    def send(method, params, notification=False):
        nonlocal next_id
        next_id += 1
        packet = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notification:
            packet["id"] = next_id
        process.stdin.write(json.dumps(packet) + "\n")
        process.stdin.flush()
        if notification:
            return
        while True:
            result = messages.get(timeout=30)
            if isinstance(result, Exception):
                raise result
            if result.get("id") == next_id:
                assert "error" not in result, result
                return result["result"]

    context = None
    def raw(name, arguments):
        return send("tools/call", {"name": name, "arguments": arguments})

    def unwrapped(result):
        assert not result.get("isError"), result
        return result["structuredContent"]

    def call(name):
        if gateway:
            tool_id = "siemens-engineering/"+name
            description = unwrapped(raw("describe_tool", {"tool_id": tool_id}))
            parameters = {"tool_id": tool_id, "catalog_revision": description["catalog_revision"],
                          "context_id": context, "arguments": {}}
            channel = description["invocation"]["channel"]
            if channel == "call_write_tool":
                parameters["request_id"] = str(uuid.uuid4())
            result = raw(channel, parameters)
        else:
            result = raw(name, {})
        return unwrapped(result)["result"]

    try:
        initialized = send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "codex-registration-probe", "version": "1.0.0"}})
        assert initialized["serverInfo"]["version"] == (framework_version if gateway else manifest["version"])
        send("notifications/initialized", {}, notification=True)
        tools = {t["name"]: t for t in send("tools/list", {})["tools"]}
        if gateway:
            assert len(tools) == 14
            unwrapped(raw("refresh_services", {}))
            service = unwrapped(raw("describe_service", {"service_id": "siemens-engineering"}))
            assert service["initialization"]["serverInfo"]["version"] == manifest["version"]
            context = unwrapped(raw("acquire_service", {"service_id": "siemens-engineering"}))["context_id"]
        else:
            assert len(tools) == 51 and {"tia_reload_extensions", "hmi_apply_text_edits", "plc_list_targets"} <= set(tools)
        versions = call("tia_list_versions")
        assert versions["writesEnabled"] == (versions["launchWritePermission"] and versions["safeguards"]["mcpWritesEnabled"])
        capabilities = call("tia_get_api_capabilities")
        assert capabilities["classicTextListStructureChanges"] is True and capabilities["classicAlarmDefinitions"]["available"] is False
        processes = call("tia_list_processes")
        assert call("tia_get_status")["attached"] is False
        report = {"version": manifest["version"], "codexRegistration": "enabled", "pluginRoot": str(plugin_root),
                  "server": str(server_binary), "launcher": str(command), "toolCount": len(tools), "skills": skills,
                  "configuredTransportProbe": "passed", "frameworkVersion": framework_version,
                  "backendEngineeringTools": 51, "writesEnabled": versions["writesEnabled"], "apiVersion": capabilities["apiVersion"],
                  "nativeProcessCount": len(processes), "projectAttached": False, "projectChanged": False,
                  "chatActivation": "Start a new chat; this probe validates the installed Codex transport without making a model request."}
        if args.report:
            args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, indent=2))
    finally:
        try:
            if context is not None and process.poll() is None:
                unwrapped(raw("release_service", {"service_id": "siemens-engineering", "context_id": context}))
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


if __name__ == "__main__":
    main()
