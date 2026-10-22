"""Fixed timestamp identity and normalization checks; no app/SDK transport is started."""
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
from types import SimpleNamespace
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'plugin/framework'))
from app_version import assembly_version, canonical, equivalent, release, stamp_surface, timestamp


class VersionTests(unittest.TestCase):
    def test_date_clock_leading_zero_and_loader_boundaries(self):
        for display, package, assembly in (
            ('26.0103.000507','26.103.507','26.103.5.7'),
            ('00.0101.000000','0.101.0','0.101.0.0'),
            ('24.0229.235959','24.229.235959','24.229.2359.59'),
            ('26.1231.235959','26.1231.235959','26.1231.2359.59')):
            with self.subTest(display=display):
                self.assertEqual(canonical(display),package)
                self.assertTrue(equivalent(display,package))
                self.assertEqual(timestamp(package)[0],display)
                self.assertEqual(assembly_version(display),assembly)
                self.assertTrue(all(0<=int(x)<=65534 for x in assembly.split('.')))
                self.assertRegex(package,r'^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')
        from packaging.version import Version
        self.assertEqual(Version('26.0103.000507'),Version('26.103.507'))

    def test_invalid_dates_clocks_and_versions_are_rejected(self):
        for value in ('26.0230.000000','25.0229.000000','26.1301.000000','26.0101.240000','26.0101.126000','26.0101.000060'):
            with self.subTest(value=value),self.assertRaises(ValueError):timestamp(value)
        for value in ('26.1.1.1','26.-1.1','26.1022.173616\n','26.1022.2147483648'):
            with self.subTest(value=value),self.assertRaises(ValueError):canonical(value)

    def test_one_fixed_stamp_matches_all_generated_source_identities(self):
        info=release()
        for name in ('plugin/plugin.json','plugin/.codex-plugin/plugin.json'):
            self.assertTrue(equivalent(json.loads((ROOT/name).read_text())['version'],info['display_version']))
        props=(ROOT/'build/AppVersion.props').read_text()
        for key,value in [('Version',info['package_version']),('AssemblyVersion',info['assembly_version']),('FileVersion',info['file_version']),('InformationalVersion',info['display_version'])]:
            self.assertIn(f'<{key}>{value}</{key}>',props)
        self.assertIn('"'+info['display_version']+'"',(ROOT/'Shared/ReleaseStamp.cs').read_text())
        self.assertIn('AssemblyVersion("'+info['assembly_version']+'")',(ROOT/'Properties/ReleaseVersionInfo.cs').read_text())

    def test_version_cli_does_not_restamp_or_create_runtime(self):
        paths=[ROOT/'plugin/app-version.json',ROOT/'build/AppVersion.props',ROOT/'Shared/ReleaseStamp.cs']
        before=[hashlib.sha256(p.read_bytes()).hexdigest() for p in paths]
        process=subprocess.run([sys.executable,'-B',str(ROOT/'plugin/framework/manage.py'),'version'],capture_output=True,text=True,timeout=10)
        self.assertEqual(process.returncode,0,process.stderr)
        self.assertEqual(json.loads(process.stdout),release())
        self.assertEqual(before,[hashlib.sha256(p.read_bytes()).hexdigest() for p in paths])

    def test_surface_identity_changes_before_observation_only(self):
        low=SimpleNamespace(version='0.20.0',name='Siemens')
        server=SimpleNamespace(_lowlevel_server=low)
        stamp_surface(server,release()['display_version'])
        self.assertEqual(low.version,release()['display_version'])
        low._server_info_stamp_source={'version':low.version}
        with self.assertRaises(ValueError):stamp_surface(server,low.version)


if __name__=='__main__':unittest.main()
