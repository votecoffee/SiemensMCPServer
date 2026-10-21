"""Real net48 ZIP selection regression; synthetic files and local preparation only."""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile
import zipfile

from test_extension_reload import Client


def exercise(executable, root):
    xml = b"<Document><Engineering version='V15.1'/><Hmi.TextGraphicList.TextList ID='0'><AttributeList><Name>SelectedList</Name></AttributeList></Hmi.TextGraphicList.TextList><Asset external='path'>assets/icon.bin</Asset></Document>"
    deep = 'wrapper/ProjectGraphics/' + '/'.join(['g'*60]*5) + '/image.bin'
    marker = b'unique-unselected-archive-integrity-control'
    entries = {'wrapper/TextLists/Selected.xml': xml,
               'wrapper/TextLists/Unused.xml': xml.replace(b'SelectedList', b'UnusedList'),
               'wrapper/TextLists/assets/icon.bin': b'\x00\xff\x07', deep: marker,
               'wrapper/Screens/Unrelated.xml': b'Outside the selected native XML profile',
               'wrapper/ExportSummary.txt': b'OK     TextLists\nFAILED Screens\n'}
    source = root/'source.zip'
    with zipfile.ZipFile(source, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    client = Client(executable, root)
    arguments = {'categories': ['TextLists'], 'folderNames': [], 'selectedTextLists': ['SelectedList']}
    try:
        result = client.call('hmi_prepare_classic_export_bundle', sourceDirectory=str(source), **arguments)
        assert result['resources'] == 1 and result['validation']['canPublish']
        assert result['sourceAccess'] == {'mode': 'verified-zip-stream', 'sourceFilesVerified': len(entries),
                                          'archiveMembersVerified': len(entries), 'sourceArchiveMembersExtracted': 0}
        bundle = Path(result['artifactDirectory'])
        assert (bundle/'objects/00000/native/Selected.xml').read_bytes() == xml
        assert (bundle/'objects/00000/native/assets/icon.bin').read_bytes() == b'\x00\xff\x07'
        assert not any('Unused' in str(p) or 'ProjectGraphics' in str(p) or 'Unrelated' in str(p) for p in bundle.rglob('*'))
        assert hashlib.sha256(source.read_bytes()).hexdigest() == source_hash
        folder = root/'folder/TextLists'; folder.mkdir(parents=True)
        (folder/'Selected.xml').write_bytes(xml)
        (folder/'assets').mkdir(); (folder/'assets/icon.bin').write_bytes(b'\x00\xff\x07')
        folder_result = client.call('hmi_prepare_classic_export_bundle', sourceDirectory=str(folder.parent), **arguments)
        archive_check = next(c for c in folder_result['validation']['checks'] if c['id'] == 'bundle.archive-integrity')
        assert archive_check['status'] == 'not-run' and archive_check['applicability'] == 'not-applicable-folder-input'
        assert folder_result['validation']['canPublish']
        raw = source.read_bytes(); offset = raw.index(marker)
        corrupted = root/'corrupted.zip'; corrupted.write_bytes(raw[:offset]+b'X'+raw[offset+1:])
        error = client.rejected('hmi_prepare_classic_export_bundle', sourceDirectory=str(corrupted), **arguments)
        assert 'CRC' in error, error
        failures = list((root/'artifacts/classic-export-bundles').glob('*.validation-failed.json'))
        assert len(failures) == 1
        failed = json.loads(failures[0].read_bytes())
        assert failed['candidatePublished'] is False and failed['validation']['canPublish'] is False
        return {'hostSha256': hashlib.sha256(executable.read_bytes()).hexdigest(),
                'checks': ['selected-only payload', 'unrelated path longer than 260 characters never extracted',
                           'complete archive integrity retained', 'selected companion preserved',
                           'unselected CRC corruption blocked', 'folder archive check marked not applicable'],
                'projectAttached': False, 'nativeImportCompileSavePerformed': False}
    finally:
        client.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='szip-') as temporary:
        result = exercise(args.executable.resolve(), Path(temporary))
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result, indent=2))
