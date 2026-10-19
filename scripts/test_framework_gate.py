"""Check the pending MCP 2.3 gate without importing an SDK or starting a process."""
import asyncio
import builtins
import json
from pathlib import Path
import runpy
import tempfile
import unittest
from unittest.mock import patch


class FrameworkGateTests(unittest.TestCase):
    def check_gate(self, accepted, versions, exception):
        module = runpy.run_path(str(Path(__file__).with_name("test_app_framework.py")))
        run = module["run"]
        run.__globals__["version"] = lambda name: versions[name]
        normal_import = builtins.__import__

        def reject_sdk(name, *args, **kwargs):
            if name.split(".")[0] in {"mcp", "universal_tools"}:
                self.fail("An unaccepted SDK was imported")
            return normal_import(name, *args, **kwargs)

        with tempfile.TemporaryDirectory(prefix="siemens-framework-gate-") as scratch:
            root = Path(scratch)
            (root / "framework").mkdir()
            (root / "framework/compatibility.json").write_text(json.dumps({
                "acceptedFrameworkVersion": accepted, "requiredMcpPythonSeries": "2.3"}), encoding="utf-8")
            with patch("builtins.__import__", side_effect=reject_sdk), patch("subprocess.run", side_effect=AssertionError("No process may start")):
                with self.assertRaises(exception):
                    asyncio.run(run(root))

    def test_unaccepted_framework_stops_before_import_or_start(self):
        self.check_gate(None, {}, RuntimeError)

    def test_wrong_framework_version_stops_before_import_or_start(self):
        self.check_gate("test-compatible-version", {"universal-codex-tools": "test-older-version"}, AssertionError)

    def test_older_mcp_stops_before_import_or_start(self):
        self.check_gate("test-compatible-version", {"universal-codex-tools": "test-compatible-version", "mcp": "1.30.0"}, AssertionError)


if __name__ == "__main__":
    unittest.main()
