"""Disposable UTF-8 TOML fixtures only. No live Codex config or CLI calls."""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import tomllib
import unittest
from unittest.mock import patch
from codex_config_bytes import append_tables, parse, replace_if_unchanged

ORIGINAL='''# Unrelated settings: Español 😀
model = "fixture-model"
approval_policy = "on-request"
paths = ['C:\\Engineering\\Demo', 'D:\\例\\Area']
notes = """First line
Unicode: Français 日本語 😀
last line"""
literal = '\'\'\'literal \\r \\n stays literal
second line\'\'\'
sample = [nan, { flag = true, count = 2 }]
[profiles.other]
model = "unchanged"
[plugins."unrelated@local"]
enabled = true
'''
ADDITION='''# App-owned addition only
[plugins."siemens-mcp-server@siemens-mcp-server-local"]
enabled = true
'''


def inspect(raw):
    return dict(bytes=len(raw),sha256=hashlib.sha256(raw).hexdigest(),crlf=raw.count(b'\r\n'),
                crcrlf=raw.count(b'\r\r\n'),loneCr=raw.replace(b'\r\n',b'').count(b'\r'),
                loneLf=raw.replace(b'\r\n',b'').count(b'\n'))


class ConfigBytesTests(unittest.TestCase):
    def retain(self,name,before,after):
        output=os.environ.get('SIEMENS_CONFIG_FIXTURE_EVIDENCE')
        if output:
            root=Path(output);root.mkdir(parents=True,exist_ok=True)
            (root/(name+'-before.toml')).write_bytes(before);(root/(name+'-after.toml')).write_bytes(after)
            (root/(name+'-bytes.json')).write_bytes((json.dumps(dict(before=inspect(before),after=inspect(after)),indent=2)+'\n').encode())

    def test_lf_crlf_bom_unicode_multiline_and_unrelated_settings_survive_windows_publication(self):
        for ending in (b'\n',b'\r\n'):
            for bom in (b'',b'\xef\xbb\xbf'):
                with self.subTest(ending=ending,bom=bool(bom)),tempfile.TemporaryDirectory(prefix='siemens TOML 😀 ') as tmp:
                    before=bom+ORIGINAL.encode().replace(b'\n',ending)
                    after=append_tables(before,ADDITION,newline=b'\r\n')
                    path=Path(tmp)/'disposable config.toml';path.write_bytes(before)
                    replace_if_unchanged(path,before,after)
                    self.assertEqual(path.read_bytes(),after)
                    parsed=parse(after); prior=parse(before)
                    # NaN needs its own value comparison, just as TOML does.
                    parsed.pop('sample');prior.pop('sample')
                    del parsed['plugins']['siemens-mcp-server@siemens-mcp-server-local']
                    self.assertEqual(parsed,prior)
                    self.assertEqual(after.startswith(b'\xef\xbb\xbf'),bool(bom))
                    self.assertEqual(inspect(after)['loneCr'],0);self.assertEqual(inspect(after)['loneLf'],0)
                    if ending==b'\r\n':self.assertTrue(after.startswith(before))
                    self.assertEqual(list(Path(tmp).iterdir()),[path])
                    self.retain(('CRLF' if ending==b'\r\n' else 'LF')+('-BOM' if bom else ''),before,after)

    def test_platform_default_is_explicit_crlf_on_windows_and_lf_can_be_requested(self):
        before=ORIGINAL.encode();default=append_tables(before,ADDITION)
        self.assertEqual(default,append_tables(before,ADDITION,newline=b'\r\n' if os.name=='nt' else b'\n'))
        self.assertEqual(inspect(append_tables(before,ADDITION,newline=b'\n'))['crlf'],0)

    def test_malformed_crcrlf_lone_cr_and_invalid_utf8_are_not_repaired_or_published(self):
        for raw in (b'model="x"\r\r\n',b'model="x"\r',b'model="\xff"\n'):
            with self.subTest(raw=raw),tempfile.TemporaryDirectory() as tmp:
                path=Path(tmp)/'fixture.toml';path.write_bytes(raw)
                with self.assertRaises((ValueError,UnicodeError)):append_tables(raw,ADDITION)
                with self.assertRaises((ValueError,UnicodeError)):replace_if_unchanged(path,raw,b'model="x"\r\n')
                self.assertEqual(path.read_bytes(),raw)

    def test_existing_settings_cannot_be_overwritten_by_an_append(self):
        for suffix in ('model="changed"\n','[profiles.other]\nmodel="changed"\n'):
            with self.assertRaises(ValueError):append_tables(ORIGINAL.encode(),suffix)

    def test_stale_preimage_and_failed_replace_preserve_target_and_clean_private_temporary(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'fixture.toml';before=ORIGINAL.encode();after=append_tables(before,ADDITION)
            path.write_bytes(before+b'# concurrent edit\n')
            with self.assertRaises(ValueError):replace_if_unchanged(path,before,after)
            self.assertEqual(path.read_bytes(),before+b'# concurrent edit\n')
            path.write_bytes(before)
            with patch('codex_config_bytes.os.replace',side_effect=OSError('fixture refusal')):
                with self.assertRaises(OSError):replace_if_unchanged(path,before,after)
            self.assertEqual(path.read_bytes(),before);self.assertEqual(list(Path(tmp).iterdir()),[path])

    @unittest.skipUnless(os.name=='nt','Reproduce actual Windows text-mode translation')
    def test_windows_double_translation_is_reproducible_only_in_a_disposable_counterexample(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'counterexample.toml'
            path.write_text('model="fixture"\r\n',encoding='utf-8')
            raw=path.read_bytes();self.assertEqual(raw,b'model="fixture"\r\r\n')
            with self.assertRaises(ValueError):tomllib.loads(raw.decode())
            good=append_tables(b'model="fixture"\r\n',ADDITION,newline=b'\r\n')
            self.retain('double-translation-counterexample',raw,good)


if __name__=='__main__':unittest.main(verbosity=2)
