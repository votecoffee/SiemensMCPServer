"""Exercise translation snapshots, file previews, reload and old-host rejection without attaching."""
import argparse
import base64
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import zipfile
from test_extension_reload import Client
from test_library_inspection import stage


def fixture():
    main = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
    rel = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
    package = 'http://schemas.openxmlformats.org/package/2006/relationships'
    rows = []
    for row, values in enumerate([
        ['Category', 'ViewPath', 'Internal ID', 'Substitutions', 'en-US*', 'de-DE'],
        ['Comment', 'Panel/Tag/Comment', 'synthetic-stable-native-id', '', 'Ready', ''],
    ], 1):
        rows.append('<row r="'+str(row)+'">'+''.join(f'<c r="{chr(65+i)}{row}" t="inlineStr"><is><t>{v}</t></is></c>' for i, v in enumerate(values))+'</row>')
    parts = {
        '_rels/.rels': f'<Relationships xmlns="{package}"><Relationship Id="office" Type="{rel}/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        'xl/workbook.xml': f'<workbook xmlns="{main}" xmlns:r="{rel}"><sheets><sheet name="User Texts" sheetId="1" r:id="sheet"/></sheets></workbook>',
        'xl/_rels/workbook.xml.rels': f'<Relationships xmlns="{package}"><Relationship Id="sheet" Type="{rel}/worksheet" Target="worksheets/sheet.xml"/></Relationships>',
        'xl/worksheets/sheet.xml': f'<worksheet xmlns="{main}"><sheetData>'+''.join(rows)+'</sheetData></worksheet>',
        'docProps/custom.xml': '<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/custom-properties"><property name="TIA_Version"><value>1.3</value></property></Properties>',
        'retained.bin': 'Preserved native companion bytes',
    }
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED) as archive:
        for name, content in parts.items():
            archive.writestr(name, content.encode('utf-8'))
    return buffer.getvalue()


def worker(executable, packets):
    result = subprocess.run([str(executable), '--module=project'], input='\n'.join(json.dumps(p, ensure_ascii=False) for p in packets)+'\n',
        capture_output=True, encoding='utf-8', timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, result.stderr
    replies = [json.loads(line) for line in result.stdout.splitlines()]
    assert len(replies) == len(packets)
    return replies


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args(); host = args.executable.resolve(); repo = Path(__file__).resolve().parent.parent
    registry = json.loads((host.parent/'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = host.parent/'extensions'/registry['project']; executable = bundle/'SiemensMCPServer.ExtensionWorker.exe'
    data = fixture(); capture = {'sourceLanguage': 'en-US', 'targetLanguage': 'de-DE'}
    snapshot = {**capture, 'bytes': len(data), 'fileSha256': hashlib.sha256(data).hexdigest(), 'workbookBase64Url': base64.urlsafe_b64encode(data).decode().rstrip('=')}
    with tempfile.TemporaryDirectory(prefix='tia-project-translations-') as temp:
        root = Path(temp); project = str(root/'synthetic.ap15_1')
        read = {'expectedProjectPath': project, 'capture': capture, 'options': {'missingOnly': True}}
        packet = lambda i, op, content: {'id': i, 'operation': op, 'input': content}
        hello, inspected = worker(executable, [packet(1, '$hello', {}), packet(2, 'read_translation_units', {'phase': 'snapshot', 'arguments': read, 'snapshot': snapshot})])
        assert any(o['name'] == 'edit_project_texts' and o['route'] == 'projectTexts' and o['preview'] for o in hello['result']['operations'])
        unit, = inspected['result']['units']; assert unit['canPreview'] and inspected['result']['missingTranslations'] == 1
        edit = {'expectedProjectPath': project, 'capture': capture, 'edits': [{'selection': unit['targetSelection'], 'newText': '=Literal — Über 😀\r\n_x0041_'}]}
        result, = worker(executable, [packet(1, 'edit_project_texts', {'phase': 'snapshot', 'arguments': edit, 'snapshot': snapshot})])
        candidate = result['result']['candidate']; changed = base64.urlsafe_b64decode(candidate['workbookBase64Url']+'='*(-len(candidate['workbookBase64Url'])%4))
        assert hashlib.sha256(changed).hexdigest() == candidate['fileSha256']
        after, = worker(executable, [packet(1, 'read_translation_units', {'phase': 'snapshot', 'arguments': {**read, 'options': {}}, 'snapshot': candidate})])
        assert after['result']['units'][0]['targetText'] == edit['edits'][0]['newText']
        assert after['result']['units'][0]['sourceText'] == 'Ready'
        for source in (data, changed):
            with zipfile.ZipFile(io.BytesIO(source)) as z:
                assert z.read('retained.bin') == b'Preserved native companion bytes'
        workbook_path = root/'native.xlsx'; workbook_path.write_bytes(data); request_path = root/'request.json'
        request_path.write_text(json.dumps({'workbookPath': str(workbook_path), 'capture': capture, 'expectedWorkbookFingerprint': inspected['result']['workbookFingerprint'], 'edits': edit['edits']}), encoding='utf-8')
        output = root/'report'
        run = subprocess.run([str(executable), '--project-texts='+str(request_path), '--output='+str(output)], capture_output=True, encoding='utf-8', timeout=60)
        assert run.returncode == 0, run.stderr
        for entry in json.loads((output/'manifest.json').read_text())['files']:
            content = (output/entry['path']).read_bytes(); assert len(content) == entry['bytes'] and hashlib.sha256(content).hexdigest() == entry['sha256']
        assert workbook_path.read_bytes() == data
        client = Client(host, root/'current')
        try:
            tools = client.send('tools/list', {})['tools']; assert len(tools) == 51
            before = client.call('tia_list_extensions'); previous = {m['module']: m['generation'] for m in before['modules']}
            stage(repo, bundle, root/'current'); client.call('tia_reload_extensions', module='project')
            after_host = client.call('tia_list_extensions'); current = {m['module']: m['generation'] for m in after_host['modules']}
            assert before['hostProcessId'] == after_host['hostProcessId'] == client.process.pid
            assert previous['project'] != current['project'] and all(previous[m] == current[m] for m in previous if m != 'project')
            assert client.send('tools/list', {})['tools'] == tools
            assert 'attach' in client.rejected('tia_read_extension', module='project', operation='read_translation_units', argumentsJson=json.dumps(read)).lower()
            assert 'attach' in client.rejected('tia_preview_extension', module='project', operation='edit_project_texts', targetId='project', path='', expectedFingerprint=inspected['result']['workbookFingerprint'], argumentsJson=json.dumps(edit)).lower()
            assert not client.call('tia_get_status')['attached']
        finally:
            client.close()
        old_rejected = None
        if args.previous_host:
            old = Client(args.previous_host.resolve(), root/'old')
            try:
                before = old.call('tia_list_extensions'); stage(repo, bundle, root/'old')
                old.rejected('tia_reload_extensions', module='project')
                after_host = old.call('tia_list_extensions'); assert before == after_host
                old_rejected = True
            finally:
                old.close()
        report = {'workerVersion': hello['result']['version'], 'literalTranslationRoundTrip': True, 'fileManifestVerified': True, 'originalPreserved': True,
            'reloadSameHostAndTools': True, 'oldHostRejectedNewRouteWithoutReplacingWorker': old_rejected, 'projectAttached': False, 'nativeImportAcceptance': 'pending'}
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
        print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
