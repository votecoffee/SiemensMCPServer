"""Probe the Siemens plugin transport resolved by Codex. Never attach or edit a project."""
import argparse
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--codex", default=shutil.which("codex"))
    args = parser.parse_args()
    assert args.codex, "Codex CLI is not available"
    configured = subprocess.run([args.codex, "mcp", "list", "--json"], check=True, capture_output=True, text=True, encoding="utf-8", timeout=60)
    servers = json.loads(configured.stdout)
    server = next((s for s in servers if s["name"] == "siemens-mcp-server"), None)
    assert server is not None, "Codex has not exposed the installed Siemens MCP server"
    assert server["enabled"] and server["transport"]["type"] == "stdio", server
    transport = server["transport"]
    environment = transport.get("env") or {}
    plugin_root = Path(environment.get("PLUGIN_ROOT") or transport["cwd"]).resolve()
    command = Path(shutil.which(transport["command"]) or transport["command"]).resolve()
    if command.name.lower() == "cmd.exe":
        assert transport["args"] == ["/d", "/c", "server\\SiemensMCPServer.Mcp.exe", "--allow-writes"]
    else:
        assert command.is_relative_to(plugin_root)
    server_binary = plugin_root / "server/SiemensMCPServer.Mcp.exe"
    assert server_binary.is_file()
    assert all((plugin_root / "skills" / name / "SKILL.md").is_file() for name in ["edit-hmi-text", "edit-siemens-plc"])
    manifest = json.loads((plugin_root / "plugin.json").read_text(encoding="utf-8-sig"))
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

    def call(name):
        result = send("tools/call", {"name": name, "arguments": {}})
        assert not result.get("isError"), result
        return result["structuredContent"]["result"]

    try:
        initialized = send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "codex-registration-probe", "version": "1.0.0"}})
        assert initialized["serverInfo"]["version"] == manifest["version"]
        send("notifications/initialized", {}, notification=True)
        tools = {t["name"]: t for t in send("tools/list", {})["tools"]}
        assert len(tools) in (51, 54) and {"tia_reload_extensions", "tia_list_extensions", "tia_preview_extension", "tia_apply_extension_plan", "tia_archive_project", "tia_reopen_project", "hmi_preview_classic_text_list_changes", "hmi_preview_classic_behavior_changes", "plc_list_targets"} <= set(tools)
        versions = call("tia_list_versions")
        assert versions["writesEnabled"] == ("--allow-writes" in transport["args"])
        capabilities = call("tia_get_api_capabilities")
        assert capabilities["classicTextListStructureChanges"] is True and capabilities["classicAlarmDefinitions"]["available"] is False
        processes = call("tia_list_processes")
        assert call("tia_get_status")["attached"] is False
        report = {"version": manifest["version"], "codexRegistration": "enabled", "pluginRoot": str(plugin_root),
                  "server": str(server_binary), "launcher": str(command), "toolCount": len(tools), "skills": ["edit-hmi-text", "edit-siemens-plc"],
                  "configuredTransportProbe": "passed", "writesEnabled": versions["writesEnabled"], "apiVersion": capabilities["apiVersion"],
                  "nativeProcessCount": len(processes), "projectAttached": False, "projectChanged": False,
                  "chatActivation": "Start a new chat; this probe validates the installed Codex transport without making a model request."}
        if args.report:
            args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, indent=2))
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


if __name__ == "__main__":
    main()
