"""Exercise the production host policy without attaching or invoking native writes."""
import argparse
import json
from pathlib import Path
import queue
import subprocess
import tempfile
import threading


def run(executable):
    with tempfile.TemporaryDirectory(prefix="siemens-safeguards-") as scratch:
        root = Path(scratch)
        settings = root / "safeguards.json"
        policy = {"schemaVersion": 1, "allowSave": False, "allowCompile": False,
                  "allowArchive": False, "allowReopen": False, "allowApply": False,
                  "allowExtensionReload": False, "analysisParallelism": 1,
                  "nativeQueueCapacity": 3, "analysisQueueCapacity": 2,
                  "controlQueueCapacity": 4, "workerTimeoutSeconds": 7}

        def save_policy(value):
            temporary = root / "next-policy.json"
            temporary.write_text(json.dumps(value), encoding="utf-8")
            temporary.replace(settings)

        save_policy(policy)
        for launch_writes in (False, True):
            args = [str(Path(executable).resolve()), "--settings-file=" + str(settings),
                    "--artifact-root=" + str(root / "artifacts"), "--extensions-root=" + str(root / "extensions")]
            if launch_writes:
                args.append("--allow-writes")
            process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
            messages = queue.Queue()
            errors = []
            threading.Thread(target=lambda: [messages.put(json.loads(line)) for line in process.stdout], daemon=True).start()
            threading.Thread(target=lambda: errors.extend(process.stderr), daemon=True).start()
            next_id = 0

            def send(method, params):
                nonlocal next_id
                next_id += 1
                process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params}) + "\n")
                process.stdin.flush()
                while True:
                    try:
                        packet = messages.get(timeout=25)
                    except queue.Empty as exc:
                        raise AssertionError("Host timeout: " + "".join(errors)) from exc
                    if packet.get("id") == next_id:
                        assert "error" not in packet, packet
                        return packet["result"]

            def call(name, **arguments):
                return send("tools/call", {"name": name, "arguments": arguments})

            def blocked(name, **arguments):
                result = call(name, **arguments)
                assert result.get("isError"), result
                return " ".join(c.get("text", "") for c in result.get("content", []))

            try:
                send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                    "clientInfo": {"name": "safeguards-test", "version": "1"}})
                process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}\n')
                process.stdin.flush()
                versions = call("tia_list_versions")["structuredContent"]["result"]
                assert Path(versions["safeguardsFile"]) == settings
                assert versions["launchWritePermission"] is launch_writes
                for tool, arguments in (
                    ("tia_save_project", {"expectedProjectPath": str(root / "synthetic.ap15_1")}),
                    ("tia_archive_project", {"expectedProjectPath": str(root / "synthetic.ap15_1")}),
                    ("tia_reopen_project", {"expectedProjectPath": str(root / "synthetic.ap15_1")}),
                    ("hmi_compile", {"hmiId": "synthetic"}),
                    ("plc_compile", {"plcId": "synthetic"}),
                    ("hmi_apply_text_edits", {"planId": "missing", "candidateSha256": "missing"}),
                ):
                    text = blocked(tool, **arguments)
                    assert ("Safeguards" if launch_writes else "--allow-writes") in text, text
                assert "ExtensionReload" in blocked("tia_reload_extensions", module="")
                extensions = call("tia_list_extensions")["structuredContent"]["result"]
                requests = extensions["requests"]
                assert requests["local"]["parallelism"] == 1
                assert requests["local"]["queueCapacity"] == 2
                assert requests["native"]["parallelism"] == 1
                assert requests["native"]["queueCapacity"] == 3
                assert requests["control"]["queueCapacity"] == 4
                # A saved policy is re-read without restarting the process.
                policy["mcpWritesEnabled"] = False
                policy["allowSave"] = True
                save_policy(policy)
                assert call("tia_list_versions")["structuredContent"]["result"]["writesEnabled"] is False
                text = blocked("tia_save_project", expectedProjectPath=str(root / "synthetic.ap15_1"))
                assert ("MCP writes are disabled" if launch_writes else "--allow-writes") in text, text
                policy["allowExtensionReload"] = True
                save_policy(policy)
                assert not call("tia_reload_extensions", module="").get("isError")
                # Invalid and deleted policy never silently restore permissive defaults.
                settings.write_text('{"allowSave": false, "allowSave": true}', encoding="utf-8")
                assert call("tia_list_versions").get("isError")
                settings.unlink()
                assert "disappeared" in blocked("tia_reload_extensions", module="")
                policy["mcpWritesEnabled"] = True
                policy["allowSave"] = False
                policy["allowExtensionReload"] = False
                save_policy(policy)
                assert not call("tia_list_versions").get("isError")
            finally:
                process.stdin.close()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        return {"permissionGatesBeforeNativeDispatch": True, "launchPermissionRemainsRequired": True,
                "savedPolicyReloadedWithoutRestart": True, "configuredQueuesAndWorkers": True,
                "invalidOrDeletedPolicyFailsClosed": True, "projectAttached": False, "nativeWrites": False}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("executable")
    args = parser.parse_args()
    print(json.dumps(run(args.executable), indent=2))
