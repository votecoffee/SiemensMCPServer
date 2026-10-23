"""Data-only guidance and isolated candidate input tests; no runtime launch."""
import hashlib
import json
from pathlib import Path
import re
import tempfile
import unittest
from build_native_guidance import build, WORKFLOWS
from prepare_native_pilot import prepare

ROOT=Path(__file__).resolve().parents[1]


class NativePackageTests(unittest.TestCase):
    def test_maintained_workflows_have_direct_routes_complete_links_and_exact_hashes(self):
        with tempfile.TemporaryDirectory() as tmp:
            destination=Path(tmp)/'guides'; catalog=build(ROOT,destination)
            workflows=[r for r in catalog['files'] if r['kind']=='workflow']
            self.assertEqual({r['id'] for r in workflows},set(WORKFLOWS))
            for row in catalog['files']:
                path=destination/row['path']; raw=path.read_bytes()
                self.assertEqual((len(raw),hashlib.sha256(raw).hexdigest()),(row['bytes'],row['sha256']))
                for target in re.findall(r'\]\(([^)]+)\)',raw.decode()):
                    if '://' not in target and target.split('#')[0].endswith('.md'):
                        self.assertTrue((path.parent/target.split('#')[0]).is_file(),target)
                if row['kind']=='workflow':
                    self.assertNotRegex(raw.decode(),r'acquire_service|release_service|search_tools|describe_tool')
                    self.assertIn('tia_detach',raw.decode())

    @staticmethod
    def fixture(root):
        package=root/'package'; (package/'server').mkdir(parents=True)
        (package/'server/SiemensMCPServer.Mcp.exe').write_bytes(b'harmless fixture bytes, never executable')
        (package/'build-info.json').write_text(json.dumps(dict(profile='native-direct-candidate',displayVersion='26.1023.072948',sourceCommit='fixture',tiaPortalApi='V15.1',mcpStarted=False)))
        rows=[dict(path=p.relative_to(package).as_posix(),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in package.rglob('*') if p.is_file()]
        (package/'manifest.json').write_text(json.dumps(rows))
        return package

    def test_pilot_inputs_are_absolute_isolated_readonly_and_do_not_mutate_payload(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp); package=self.fixture(root); before={p:p.read_bytes() for p in package.rglob('*') if p.is_file()}
            result=prepare(package,root/'pilot'); self.assertFalse(result['started']); self.assertFalse(result['registered'])
            self.assertFalse(result['writesEnabled']); self.assertTrue(Path(result['connection']['command']).is_absolute())
            self.assertNotIn('--allow-writes',result['connection']['args'])
            policy=json.loads((root/'pilot/safeguards.json').read_bytes()); self.assertFalse(policy['McpWritesEnabled'])
            self.assertEqual(policy['MaxOwnedResources'],8)
            self.assertEqual(before,{p:p.read_bytes() for p in package.rglob('*') if p.is_file()})

    def test_tampered_unlisted_and_unsafe_payloads_refuse_before_output_creation(self):
        for mutation in ('tamper','unlisted','traversal','duplicate'):
            with self.subTest(mutation=mutation),tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp); package=self.fixture(root)
                if mutation=='tamper': (package/'server/SiemensMCPServer.Mcp.exe').write_bytes(b'changed')
                elif mutation=='unlisted': (package/'extra.txt').write_text('extra')
                else:
                    manifest=package/'manifest.json'; rows=json.loads(manifest.read_bytes())
                    if mutation=='traversal': rows[0]['path']='../outside'
                    else: rows.append(rows[0])
                    manifest.write_text(json.dumps(rows))
                with self.assertRaises(ValueError): prepare(package,root/'pilot')
                self.assertFalse((root/'pilot').exists())

    def test_pilot_state_cannot_be_written_inside_the_package(self):
        with tempfile.TemporaryDirectory() as tmp:
            package=self.fixture(Path(tmp))
            with self.assertRaises(ValueError): prepare(package,package/'pilot')
            self.assertFalse((package/'pilot').exists())


if __name__=='__main__': unittest.main(verbosity=2)
