"""Deployment rejection controls; no TIA attachment or normal runtime changes."""
import argparse
import base64
import csv
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

FRAMEWORK = Path(__file__).resolve().parents[1]/'plugin/framework'
sys.path.insert(0, str(FRAMEWORK))
import integrity
import manage


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='siemens-deployment-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.payload = self.root/'payload'
        self.payload.mkdir()
        (self.payload/'app.txt').write_bytes(b'original')
        self.entries = [{'path': 'app.txt', 'bytes': 8,
                         'sha256': hashlib.sha256(b'original').hexdigest()}]
        self.manifest()

    def manifest(self):
        (self.payload/'manifest.json').write_text(json.dumps(self.entries), encoding='utf-8')

    def test_exact_inventory_accepts(self):
        self.assertEqual(integrity.verify_payload(self.payload), integrity.digest(self.payload/'manifest.json'))

    def test_modified_bytes_rejected(self):
        (self.payload/'app.txt').write_bytes(b'changed!')
        with self.assertRaisesRegex(ValueError, 'bytes differ'):
            integrity.verify_payload(self.payload)

    def test_unlisted_file_rejected(self):
        (self.payload/'unlisted.txt').write_bytes(b'extra')
        with self.assertRaisesRegex(ValueError, 'unexpected files'):
            integrity.verify_payload(self.payload)

    def test_case_collision_rejected(self):
        self.entries.append({**self.entries[0], 'path': 'APP.TXT'})
        self.manifest()
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            integrity.verify_payload(self.payload)

    def test_escape_rejected_before_reading(self):
        self.entries[0]['path'] = '../outside.txt'
        self.manifest()
        with self.assertRaisesRegex(ValueError, 'Unsafe'):
            integrity.verify_payload(self.payload)
        self.assertFalse((self.root/'outside.txt').exists())

    def test_linked_manifest_rejected(self):
        target = self.root/'external-manifest.json'
        target.write_bytes((self.payload/'manifest.json').read_bytes())
        (self.payload/'manifest.json').unlink()
        try:
            (self.payload/'manifest.json').symlink_to(target)
        except OSError as error:
            self.skipTest('Symbolic links unavailable: '+str(error))
        with self.assertRaisesRegex(ValueError, 'Linked'):
            integrity.verify_payload(self.payload)

    def test_bad_payload_prevents_runtime_creation_or_process(self):
        (self.payload/'app.txt').write_bytes(b'corrupt')
        runtime = self.root/'must-not-exist'
        with patch.object(manage.venv.EnvBuilder, 'create') as create, patch.object(manage.subprocess, 'run') as run:
            with self.assertRaises(ValueError):
                manage.prepare(argparse.Namespace(payload=self.payload, root=runtime))
            create.assert_not_called()
            run.assert_not_called()
        self.assertFalse(runtime.exists())

    def test_sdk_mismatch_rejected_before_import(self):
        observed = []
        real_import = __import__
        def guarded(name, *args, **kwargs):
            observed.append(name)
            return real_import(name, *args, **kwargs)
        def wrong_version(name):
            return '2.2.0' if name == 'mcp' else integrity.FRAMEWORK_VERSION
        with patch.object(manage, 'version', side_effect=wrong_version), patch('builtins.__import__', side_effect=guarded):
            with self.assertRaisesRegex(ValueError, 'pinned'):
                manage.dependencies()
        self.assertNotIn('universal_tools', observed)
        self.assertNotIn('mcp', observed)

    def test_runtime_cannot_modify_or_enclose_payload(self):
        for runtime in (self.payload/'state', self.payload.parent):
            with self.subTest(runtime=runtime), patch.object(manage, 'verify_wheels', return_value='0'*64), patch.object(manage, 'install_lock') as lock:
                with self.assertRaisesRegex(ValueError, 'separate directory trees'):
                    manage.prepare(argparse.Namespace(payload=self.payload, root=runtime))
                lock.assert_not_called()
        self.assertFalse((self.payload/'state').exists())

    def wheel(self, changed=False):
        members = {'demo/__init__.py': b'VALUE = 1\n',
                   'demo-1.0.dist-info/METADATA': b'Name: demo\nVersion: 1.0\n'}
        rows = []
        for name, body in members.items():
            checksum = base64.urlsafe_b64encode(hashlib.sha256(body).digest()).rstrip(b'=').decode()
            rows.append([name, 'sha256='+checksum, str(len(body))])
        record = 'demo-1.0.dist-info/RECORD'
        rows.append([record, '', ''])
        output = io.StringIO(newline='')
        csv.writer(output).writerows(rows)
        members[record] = output.getvalue().encode()
        if changed:
            members['demo/__init__.py'] = b'VALUE = 2\n'
        path = self.root/'demo-1.0-py3-none-any.whl'
        with zipfile.ZipFile(path, 'w') as archive:
            for name, body in members.items():
                archive.writestr(name, body)
        return path

    def test_wheel_record_accepts_complete_distribution(self):
        self.assertEqual(integrity.wheel_metadata(self.wheel()), {'name': 'demo', 'version': '1.0'})

    def test_wheel_record_detects_changed_code(self):
        with self.assertRaisesRegex(ValueError, 'RECORD mismatch'):
            integrity.wheel_metadata(self.wheel(changed=True))


if __name__ == '__main__':
    unittest.main()
