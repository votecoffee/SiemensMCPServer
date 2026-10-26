"""Source-only archive packaging checks. No Siemens or MCP process is started."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import pack_native_assets as assets


class NativeAssetPackagingTests(unittest.TestCase):
    def setUp(self):
        self.scratch=tempfile.TemporaryDirectory(prefix='siemens-pack-assets-')
        self.addCleanup(self.scratch.cleanup)
        self.root=Path(self.scratch.name);self.source=self.root/'source';self.source.mkdir()
        self.member=self.source/'guide.md';self.member.write_bytes('# Español 😀\n'.encode())
        self.destination=self.root/'assets.zip'

    def test_exact_inventory_body_readback_and_source_preservation(self):
        before=self.member.read_bytes()
        result=assets.pack(self.source,self.destination,'native-guidance')
        with zipfile.ZipFile(self.destination) as archive:
            index=json.loads(archive.read(assets.INDEX))
            self.assertEqual(index['purpose'],'native-guidance')
            self.assertEqual(index['files'],[dict(path='guide.md',bytes=len(before),sha256=hashlib.sha256(before).hexdigest())])
            self.assertEqual(archive.read('guide.md'),before)
        self.assertEqual(self.member.read_bytes(),before)
        self.assertEqual(result['sha256'],hashlib.sha256(self.destination.read_bytes()).hexdigest())
        with self.assertRaises(ValueError):assets.pack(self.source,self.destination,'native-guidance')

    def test_containment_reserved_paths_and_empty_inventory(self):
        with self.assertRaises(ValueError):assets.pack(self.source,self.source/'inside.zip','extensions')
        with self.assertRaises(ValueError):assets.pack(self.source,self.destination,'other')
        for name in ('../escape','folder\\escape','CON.txt','name.','name ','Siemens.Engineering.dll','runtime.whl','launch.py'):
            with self.subTest(name=name),self.assertRaises(ValueError):assets.relative(name)
        self.member.unlink()
        with self.assertRaises(ValueError):assets.pack(self.source,self.destination,'extensions')
        self.assertFalse(self.destination.exists())

    def test_source_growth_is_bounded_before_hashing(self):
        with patch.object(assets,'MEMBER_LIMIT',4),self.assertRaises(ValueError):
            assets.read_member(self.member)
        self.assertFalse(self.destination.exists())

    def test_mid_pack_change_preserves_partial_archive_and_original_source(self):
        original=assets.read_member;reads=0
        def changing(path):
            nonlocal reads
            reads+=1
            return original(path) if reads==1 else b'changed'
        with patch.object(assets,'read_member',changing),self.assertRaisesRegex(ValueError,'changed during packaging'):
            assets.pack(self.source,self.destination,'extensions')
        self.assertTrue(self.destination.exists())
        self.assertEqual(self.member.read_bytes(),'# Español 😀\n'.encode())
        with self.assertRaises(ValueError):assets.pack(self.source,self.destination,'extensions')

    def test_reserved_index_is_rejected(self):
        (self.source/assets.INDEX).write_bytes(b'{}')
        with self.assertRaises(ValueError):assets.pack(self.source,self.destination,'extensions')
        self.assertFalse(self.destination.exists())

    def test_compressed_archive_bound_retains_unpublished_output(self):
        with patch.object(assets,'MEMBER_LIMIT',256),self.assertRaisesRegex(ValueError,'compressed bound'):
            assets.pack(self.source,self.destination,'extensions')
        self.assertTrue(self.destination.exists())
        self.assertTrue(self.member.exists())


if __name__=='__main__':unittest.main()
