"""Test the real stdio endpoint. Never attach, import, compile or save a project."""
import argparse
import json
from pathlib import Path
import queue
import subprocess
import tempfile
import threading


def run(executable, native=False):
    with tempfile.TemporaryDirectory(prefix="tia-mcp-protocol-") as scratch:
        process = subprocess.Popen(
            [str(Path(executable).resolve()), "--artifact-root=" + scratch],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1,
        )
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
                try:
                    result = messages.get(timeout=20)
                except queue.Empty:
                    raise AssertionError("MCP timeout; stderr=" + "".join(errors))
                if isinstance(result, Exception):
                    raise result
                if result.get("id") == next_id:
                    if "error" in result:
                        raise AssertionError(result["error"])
                    return result["result"]

        def call(name, **arguments):
            return send("tools/call", {"name": name, "arguments": arguments})

        try:
            initialized = send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                              "clientInfo": {"name": "tia-protocol-test", "version": "0.1.0"}})
            assert initialized["serverInfo"]["name"] == "siemens-tia-openness"
            send("notifications/initialized", {}, notification=True)
            tools = {t["name"]: t for t in send("tools/list", {})["tools"]}
            expected = {"tia_list_versions", "tia_select_version", "tia_list_processes", "tia_attach", "tia_get_status",
                        "hmi_list_text_lists", "hmi_read_text_list", "hmi_preview_text_edits", "hmi_apply_text_edits",
                        "hmi_compile", "tia_save_project", "tia_archive_project", "tia_reopen_project", "tia_get_api_capabilities", "hmi_list_screens", "hmi_list_screen_items",
                        "hmi_read_unified_object", "hmi_preview_unified_edits", "hmi_apply_unified_edits",
                        "hmi_read_object", "hmi_preview_object_edits", "hmi_preview_create_object", "hmi_preview_delete_object",
                        "hmi_export_objects", "hmi_preview_import", "hmi_preview_import_bundle", "hmi_apply_object_plan",
                        "hmi_read_classic_resource", "hmi_preview_classic_xml_edits", "hmi_apply_classic_xml_edits",
                        "plc_list_targets", "plc_read_object", "plc_read_resource", "plc_export_objects", "plc_preview_object_edits",
                        "plc_preview_create_object", "plc_preview_delete_object", "plc_preview_import", "plc_preview_xml_import",
                        "plc_preview_import_bundle", "plc_preview_generate_source", "plc_apply_object_plan", "plc_compile",
                        "hmi_prepare_classic_export_bundle", "hmi_preview_classic_text_list_changes", "hmi_preview_classic_behavior_changes"}
            assert set(tools) == expected, set(tools)
            for tool in tools.values():
                assert "result" in tool["outputSchema"]["properties"], tool
            assert tools["hmi_apply_text_edits"]["annotations"]["destructiveHint"] is True
            assert tools["tia_save_project"]["annotations"]["readOnlyHint"] is False
            preview_schema = tools["hmi_preview_text_edits"]["inputSchema"]
            edit_schema = preview_schema["properties"]["edits"]["items"]
            assert {"entry", "culture", "expectedText", "newText"} <= set(edit_schema["required"]), edit_schema
            structure_change = tools["hmi_preview_classic_text_list_changes"]["inputSchema"]["properties"]["changes"]["items"]
            assert {"action", "entry"} <= set(structure_change["required"]), structure_change
            behavior_change = tools["hmi_preview_classic_behavior_changes"]["inputSchema"]["properties"]["changes"]["items"]
            assert {"action", "xPath", "expectedFingerprint"} <= set(behavior_change["required"]), behavior_change
            assert "token" not in preview_schema.get("properties", {}), "Cancellation token leaked into tool arguments"
            converter_schema = tools["hmi_prepare_classic_export_bundle"]["inputSchema"]
            assert {"sourceDirectory", "categories", "folderNames"} <= set(converter_schema["required"]), converter_schema
            assert {"exportedPath", "name"} <= set(converter_schema["properties"]["folderNames"]["items"]["required"])
            legacy = Path(scratch) / "legacy"
            (legacy / "TextLists").mkdir(parents=True)
            (legacy / "ExportSummary.txt").write_text("OK     TextLists\n", encoding="utf-8")
            original_xml = b"<Document><Hmi.TextGraphicList.TextList><AttributeList><Name>Messages</Name></AttributeList></Hmi.TextGraphicList.TextList></Document>"
            (legacy / "TextLists" / "Messages.xml").write_bytes(original_xml)
            converted = call("hmi_prepare_classic_export_bundle", sourceDirectory=str(legacy), categories=["TextLists"], folderNames=[])
            assert not converted.get("isError"), converted
            converted_data = converted["structuredContent"]["result"]
            assert converted_data["projectChanged"] is False and converted_data["resources"] == 1, converted
            assert (Path(converted_data["artifactDirectory"]) / "objects" / "00000" / "native" / "Messages.xml").read_bytes() == original_xml
            assert (legacy / "TextLists" / "Messages.xml").read_bytes() == original_xml
            versions = call("tia_list_versions")
            assert not versions.get("isError"), versions
            data = versions["structuredContent"]["result"]
            assert data["writesEnabled"] is False
            assert data["artifactRoot"] == scratch
            bad_version = call("tia_select_version", version="V999")
            assert bad_version.get("isError") is True
            blocked = call("hmi_apply_text_edits", planId="unapproved", candidateSha256="wrong")
            assert blocked.get("isError") is True and "Writes are disabled" in blocked["content"][0]["text"]
            save = call("tia_save_project", expectedProjectPath="C:\\unselected.ap15_1")
            assert save.get("isError") is True and "Writes are disabled" in save["content"][0]["text"]
            for operation in ["tia_archive_project", "tia_reopen_project"]:
                rejected = call(operation, expectedProjectPath="C:\\unselected.ap15_1")
                assert rejected.get("isError") and "Writes are disabled" in rejected["content"][0]["text"], rejected
            compile_result = call("hmi_compile", hmiId="unselected")
            assert compile_result.get("isError") is True and "Writes are disabled" in compile_result["content"][0]["text"]
            unified_blocked = call("hmi_apply_unified_edits", planId="unapproved", candidateSha256="wrong")
            assert unified_blocked.get("isError") is True and "Writes are disabled" in unified_blocked["content"][0]["text"]
            object_blocked = call("hmi_apply_object_plan", planId="unapproved", candidateSha256="wrong")
            assert object_blocked.get("isError") is True and "Writes are disabled" in object_blocked["content"][0]["text"]
            object_edit_schema = tools["hmi_preview_object_edits"]["inputSchema"]["properties"]["edits"]["items"]
            assert {"attribute", "expectedValue", "newValue"} <= set(object_edit_schema["required"]), object_edit_schema
            create_schema = tools["hmi_preview_create_object"]["inputSchema"]["properties"]["arguments"]["items"]
            assert {"name", "value"} <= set(create_schema["required"]), create_schema
            classic_schema = tools["hmi_preview_classic_xml_edits"]["inputSchema"]["properties"]["edits"]["items"]
            assert {"xPath", "expectedValue", "newValue"} <= set(classic_schema["required"]), classic_schema
            source_schema = tools["plc_preview_generate_source"]["inputSchema"]
            assert {"plcId", "expectedFingerprint", "sourceName", "sourceLanguage", "sourceCode", "allowBlockReplacement"} <= set(source_schema["required"]), source_schema
            for name, arguments in [("hmi_apply_classic_xml_edits", {"planId": "missing", "candidateSha256": "wrong"}),
                                    ("plc_apply_object_plan", {"planId": "missing", "candidateSha256": "wrong"}),
                                    ("plc_compile", {"plcId": "missing"})]:
                rejected = call(name, **arguments)
                assert rejected.get("isError") and "Writes are disabled" in rejected["content"][0]["text"], rejected
            report = {"protocol": initialized["protocolVersion"], "toolCount": len(tools), "compatibleVersions": data["compatibleEngineeringVersions"],
                      "writeGuards": "passed", "schema": "passed", "classicBundleConversion": "passed", "nativeProcessEnumeration": "not requested"}
            if native:
                capabilities = call("tia_get_api_capabilities")
                assert not capabilities.get("isError"), capabilities
                profile = capabilities["structuredContent"]["result"]
                assert profile["classicTextLists"] is True
                assert profile["comfortXmlResourceEdits"] is True and profile["plcEngineering"]["available"] is True, profile
                if profile["apiVersion"].startswith("19."):
                    assert profile["unifiedDiscovery"] and profile["unifiedScreenInspection"] and profile["unifiedScreenEdits"], profile
                    assert profile["unifiedTextListEditing"] is False, profile
                if profile["apiVersion"].startswith("15."):
                    assert profile["unifiedDiscovery"] is False, profile
                report["nativeApiCapabilities"] = profile
                processes = call("tia_list_processes")
                assert not processes.get("isError"), processes
                report["nativeProcessEnumeration"] = "passed"
                report["nativeProcessCount"] = len(processes["structuredContent"]["result"])
                status = call("tia_get_status")
                assert not status.get("isError"), status
                assert status["structuredContent"]["result"]["attached"] is False
                assert status["structuredContent"]["result"]["plcTargets"] == []
                assert call("hmi_read_text_list", hmiId="unselected", listName="missing").get("isError") is True
                assert call("hmi_read_object", hmiId="unselected", path="").get("isError") is True
                assert call("hmi_export_objects", hmiId="unselected", path="").get("isError") is True
                assert call("plc_list_targets").get("isError") is True
                assert call("plc_read_object", plcId="unselected", path="").get("isError") is True
            print(json.dumps(report, indent=2))
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable")
    parser.add_argument("--native-read-only", action="store_true")
    args = parser.parse_args()
    run(args.executable, args.native_read_only)
