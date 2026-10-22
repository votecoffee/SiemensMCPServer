"""Static ZIP and disposable PrepareOnly checks. Never starts MCP/Codex/services."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

from verify_plugin import validate

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = None


class OfflinePackageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='siemens-offline-package-check-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        with zipfile.ZipFile(PACKAGE) as archive:
            self.files = {entry.filename: archive.read(entry) for entry in archive.infolist()}
        self.environment = {**os.environ, 'CODEX_HOME': str(self.root/'unused-codex')}

    def prepare(self, package):
        return subprocess.run(['powershell.exe', '-NoProfile', '-File', str(ROOT/'build/Install-CodexPlugin.ps1'),
                               '-Package', str(package), '-PrepareOnly', '-InstallRoot', str(self.root/'prepared'),
                               '-AppHostRoot', str(self.root/'unused-app-host')], env=self.environment,
                              capture_output=True, text=True, encoding='utf-8', timeout=30)

    def changed(self, change):
        launch = json.loads(self.files['mcp.json'])
        change(launch['mcpServers']['siemens-mcp-server'])
        self.files['mcp.json'] = json.dumps(launch).encode()
        self.files['manifest.json'] = json.dumps([
            {'path': name, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
            for name, data in self.files.items() if name != 'manifest.json']).encode()
        target = self.root/'negative-fixture.zip'
        with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
            for name, data in self.files.items():
                archive.writestr(name, data)
        Path(str(target)+'.sha256').write_text(hashlib.sha256(target.read_bytes()).hexdigest()+'  '+target.name)
        return target

    def assert_no_runtime(self):
        self.assertFalse((self.root/'unused-app-host').exists())
        self.assertFalse((self.root/'unused-codex').exists())

    def test_static_manifest_and_four_skills(self):
        self.assertGreater(validate(self.files), 10)
        self.assertEqual(json.loads(self.files['build-info.json'])['profile'], 'direct-offline')

    def test_prepare_only_has_no_framework_or_codex_state(self):
        result = self.prepare(PACKAGE)
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertEqual(json.loads(result.stdout)['state'], 'prepared')
        self.assertFalse(json.loads(result.stdout)['settingsChanged'])
        self.assert_no_runtime()

    def test_self_consistent_package_cannot_add_startup_flags(self):
        result = self.prepare(self.changed(lambda launch: launch.update(args=['--allow-writes'])))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('fixed direct profile', result.stderr)
        self.assert_no_runtime()

    def test_self_consistent_package_cannot_select_a_shell(self):
        result = self.prepare(self.changed(lambda launch: launch.update(command='cmd.exe')))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('fixed direct profile', result.stderr)
        self.assert_no_runtime()

    def test_self_consistent_package_cannot_change_transport(self):
        result = self.prepare(self.changed(lambda launch: launch.update(type='streamable-http')))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('direct stdio', result.stderr)
        self.assert_no_runtime()

    def test_self_consistent_package_cannot_add_startup_hook(self):
        result = self.prepare(self.changed(lambda launch: launch['env'].update(DOTNET_STARTUP_HOOKS='injected.dll')))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('runtime environment differs', result.stderr)
        self.assert_no_runtime()


if __name__ == '__main__':
    PACKAGE = Path(sys.argv.pop(1)).resolve()
    unittest.main(verbosity=2)
