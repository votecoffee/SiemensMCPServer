"""Disposable explicit-input proposal checks; no actual host configuration discovery."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from prepare_stable_binding import prepare


class ProposalTests(unittest.TestCase):
    def test_exact_binary_backup_unrelated_settings_and_no_config_write(self):
        for newline in (b'\n', b'\r\n'):
            with self.subTest(newline=newline), tempfile.TemporaryDirectory(prefix='Siemens proposal 日本語 ') as temporary:
                root = Path(temporary)
                runtime = root / 'runtimes/fixture'; runtime.mkdir(parents=True)
                payload = runtime / 'SiemensMCPServer.Offline.dll'; payload.write_bytes(b'nonexecutable fixture')
                exe = root / 'SiemensMCPServer.Launcher.exe'; exe.write_bytes(b'nonexecutable launcher fixture')
                snapshot = json.dumps({'runtime': 'runtimes/fixture', 'files': [{'path': payload.name, 'sha256': hashlib.sha256(payload.read_bytes()).hexdigest()}]}).encode()
                digest = hashlib.sha256(snapshot).hexdigest()
                snapshots = root / 'launcher/snapshots'; snapshots.mkdir(parents=True)
                (snapshots / (digest + '.json')).write_bytes(snapshot)
                selected = json.dumps({'schemaVersion': 1, 'snapshotSha256': digest}).encode()
                (root / 'launcher/current.json').write_bytes(selected)
                receipt = root / 'receipt.json'
                receipt.write_bytes(json.dumps(dict(state='selected', profile='direct-offline', version='26.1023.170559', settingsChanged=False,
                    command=str(exe), args=[], cwd=str(root), env={}, runtime=str(runtime), selectionSha256=hashlib.sha256(selected).hexdigest(),
                    snapshotSha256=digest, packageSha256='a'*64, pluginSource=str(root/'plugin'), marketplaceRoot=str(root/'marketplace'))).encode())
                config = root / 'fixture-config.toml'
                before = b'\xef\xbb\xbf' + 'name = "Café 日本語"\ntext = """line one\n[mcp_servers.fake]\nline three"""\n[mcp_servers.siemens-mcp-server]\ncommand = "old.exe"\n[mcp_servers.peer]\ncommand = "peer.exe"'.encode().replace(b'\n', newline)
                config.write_bytes(before)
                result = prepare(config, receipt, root / 'proposal')
                self.assertEqual(config.read_bytes(), before)
                self.assertEqual((root/'proposal/config-before.toml').read_bytes(), before)
                self.assertEqual(result['expectedRoute'], {'command': 'old.exe'})
                self.assertEqual(result['expectedConfigSha256'], hashlib.sha256(before).hexdigest())
                self.assertEqual(result['proposedRoute']['command'], str(exe))
                self.assertFalse(result['settingsChanged'])
                self.assertEqual(result['managementStatus'], 'Not reporting')
                (root/'launcher/current.json').write_bytes(b'{}')
                with self.assertRaisesRegex(ValueError, 'selection changed'):
                    prepare(config, receipt, root / 'refused-proposal')
                self.assertFalse((root/'refused-proposal').exists())


if __name__ == '__main__':
    unittest.main(verbosity=2)
