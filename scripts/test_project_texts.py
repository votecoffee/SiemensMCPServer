"""Probe project-text workbook transport and host guards; never attach to TIA."""
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


def workbook(budget=False):
    main = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
    rel = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
    package = 'http://schemas.openxmlformats.org/package/2006/relationships'
    parts = {
        '_rels/.rels': f'<Relationships xmlns="{package}"><Relationship Id="office" Type="{rel}/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        'xl/workbook.xml': f'<workbook xmlns="{main}" xmlns:r="{rel}"><sheets><sheet name="Texts" sheetId="1" r:id="s"/></sheets></workbook>',
        'xl/_rels/workbook.xml.rels': f'<Relationships xmlns="{package}"><Relationship Id="s" Type="{rel}/worksheet" Target="worksheets/sheet.xml"/><Relationship Id="ss" Type="{rel}/sharedStrings" Target="sharedStrings.xml"/></Relationships>',
        'xl/sharedStrings.xml': f'<sst xmlns="{main}"><si><r><t xml:space="preserve">Über 😀 </t></r><r><t>Über</t></r></si></sst>',
        'xl/worksheets/sheet.xml': f'<worksheet xmlns="{main}"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="str"><f>HYPERLINK("https://example.invalid/REMOTE")</f><v>Cache</v></c></row></sheetData></worksheet>',
    }
    if budget:
        text = 'x' * 4096
        parts['xl/sharedStrings.xml'] = f'<sst xmlns="{main}"><si><t>{text}</t></si></sst>'
        cells = (f'<c t="s"><f>{text}</f><v>0</v></c>') * 100
        parts['xl/worksheets/sheet.xml'] = f'<worksheet xmlns="{main}"><sheetData><row r="1">{cells}</row></sheetData></worksheet>'
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED) as archive:
        for path, text in parts.items():
            archive.writestr(path, text.encode('utf-8'))
    return buffer.getvalue()


def worker_probe(executable):
    data = workbook()
    args = {'expectedProjectPath': str(Path(tempfile.gettempdir()) / 'synthetic.ap15_1'),
            'capture': {'sourceLanguage': 'en-US', 'targetLanguage': 'de-DE'}, 'options': {'maxCells': 1, 'maxCharacters': 6}}
    snapshot = {'sourceLanguage': 'en-US', 'targetLanguage': 'de-DE', 'bytes': len(data),
                'fileSha256': hashlib.sha256(data).hexdigest(), 'workbookBase64Url': base64.urlsafe_b64encode(data).decode().rstrip('=')}
    search = {**args, 'options': {'query': 'über', 'caseSensitive': False, 'maxMatches': 1}}
    formula = {**args, 'options': {'query': 'REMOTE', 'caseSensitive': True}}
    bad = {**snapshot, 'fileSha256': 'tamper'}
    large = workbook(budget=True)
    large_snapshot = {**snapshot, 'bytes': len(large), 'fileSha256': hashlib.sha256(large).hexdigest(),
                      'workbookBase64Url': base64.urlsafe_b64encode(large).decode().rstrip('=')}
    large_args = {**args, 'options': {'maxCells': 100, 'maxCharacters': 4096}}
    packets = [
        {'id': 1, 'operation': '$hello', 'input': {}},
        {'id': 2, 'operation': 'read_project_texts', 'input': {'phase': 'validate', 'arguments': args}},
        {'id': 3, 'operation': 'read_project_texts', 'input': {'phase': 'snapshot', 'arguments': args, 'snapshot': snapshot}},
        {'id': 4, 'operation': 'find_in_project_texts', 'input': {'phase': 'snapshot', 'arguments': search, 'snapshot': snapshot}},
        {'id': 5, 'operation': 'find_in_project_texts', 'input': {'phase': 'snapshot', 'arguments': formula, 'snapshot': snapshot}},
        {'id': 6, 'operation': 'read_project_texts', 'input': {'phase': 'snapshot', 'arguments': args, 'snapshot': bad}},
        {'id': 7, 'operation': 'read_project_texts', 'input': {'phase': 'snapshot', 'arguments': large_args, 'snapshot': large_snapshot}},
    ]
    result = subprocess.run([str(executable), '--module=project'],
        input='\ufeff' + '\n'.join(json.dumps(p, ensure_ascii=False) for p in packets) + '\n',
        capture_output=True, encoding='utf-8', timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, result.stderr
    replies = [json.loads(line) for line in result.stdout.splitlines()]
    assert [r['id'] for r in replies] == list(range(1, 8))
    assert {'projectTexts', 'projectLanguages'} <= {op['snapshotKind'] for op in replies[0]['result']['operations']}
    assert replies[1]['result']['validated']
    cells = replies[2]['result']
    assert cells['cells'][0]['text'] == 'Über ' and cells['cells'][0]['textTruncated']
    assert cells['hasMore'] and cells['nextOffset'] == 1
    found = replies[3]['result']
    assert found['totalMatches'] == 2 and found['truncated'] and '😀' in found['matches'][0]['context']
    assert replies[4]['result']['totalMatches'] == 0
    assert 'hash mismatch' in replies[5]['error'].lower()
    page = replies[6]['result']
    assert page['payloadLimited'] and page['hasMore'] and 0 < len(page['cells']) < 100
    assert page['nextOffset'] == len(page['cells'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', type=Path, help='Optional 0.8.0 host for new snapshot-kind rejection/rollback')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    executable = args.executable.resolve()
    repo = Path(__file__).resolve().parent.parent
    registry = json.loads((executable.parent / 'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = executable.parent / 'extensions' / registry['project']
    worker_probe(bundle / 'SiemensMCPServer.ExtensionWorker.exe')
    checks = ['Real net48 worker decodes native-format XLSX snapshots, shared rich text, Unicode paging/search, formula caches and hash mismatch rejection']
    with tempfile.TemporaryDirectory(prefix='tia-project-text-probe-') as temporary:
        root = Path(temporary)
        client = Client(executable, root)
        try:
            tools = client.send('tools/list', {})['tools']
            assert len(tools) in (51, 54)
            catalog = client.call('tia_list_extensions')
            project = next(m for m in catalog['modules'] if m['module'] == 'project')
            assert {'list_project_languages', 'read_project_texts', 'find_in_project_texts'} <= {o['name'] for o in project['operations']}
            capabilities = client.call('tia_get_api_capabilities')['projectTexts']
            assert capabilities['nativeExportApiPresent'] and capabilities['activeLanguageApiPresent'] and capabilities['nativeImportApiPresent'] and capabilities['importImplemented']
            request = {'expectedProjectPath': str(root / 'synthetic.ap15_1'), 'capture': {}, 'options': {}}
            assert 'attach' in client.rejected('tia_read_extension', module='project', operation='list_project_languages', argumentsJson=json.dumps(request)).lower()
            request['capture'] = {'sourceLanguage': 'en-US', 'targetLanguage': 'de-DE'}
            assert 'attach' in client.rejected('tia_read_extension', module='project', operation='read_project_texts', argumentsJson=json.dumps(request)).lower()
            request['capture']['targetLanguage'] = 'en-US'
            assert 'distinct' in client.rejected('tia_read_extension', module='project', operation='read_project_texts', argumentsJson=json.dumps(request)).lower()
            request['capture']['targetLanguage'] = 'de-DE'
            request['capture']['destination'] = str(root / 'unrequested.xlsx')
            assert 'destination' in client.rejected('tia_read_extension', module='project', operation='read_project_texts', argumentsJson=json.dumps(request)).lower()
            del request['capture']['destination']
            request['snapshot'] = {'workbookBase64Url': 'client injection'}
            assert 'snapshot' in client.rejected('tia_read_extension', module='project', operation='read_project_texts', argumentsJson=json.dumps(request)).lower()
            del request['snapshot']
            request['options'] = {'query': '', 'caseSensitive': True}
            assert 'query' in client.rejected('tia_read_extension', module='project', operation='find_in_project_texts', argumentsJson=json.dumps(request)).lower()
            assert not client.call('tia_get_status')['attached']
            checks.append('Host retains engineering schemas and rejects unattached reads, ambiguous languages, output-path overrides, client snapshots and invalid search before capture')
            client.call('tia_reload_extensions', module='project')
            after = client.call('tia_list_extensions')
            assert after['hostProcessId'] == catalog['hostProcessId'] == client.process.pid
            assert next(m for m in after['modules'] if m['module'] == 'project')['generation'] != project['generation']
            assert client.send('tools/list', {})['tools'] == tools
            checks.append('Project worker reload retains host PID and MCP tool schemas with the added operations')
        finally:
            client.close()
        if args.previous_host:
            previous = root / 'previous'
            client = Client(args.previous_host.resolve(), previous)
            try:
                before = client.call('tia_list_extensions')
                project = next(m for m in before['modules'] if m['module'] == 'project')
                stage(repo, bundle, previous)
                assert 'incompatible' in client.rejected('tia_reload_extensions', module='project').lower()
                after = client.call('tia_list_extensions')
                assert next(m for m in after['modules'] if m['module'] == 'project')['generation'] == project['generation']
                assert after['hostProcessId'] == before['hostProcessId']
                checks.append('0.8.0 host rejects new snapshot kinds and retains its previous project worker/generation')
            finally:
                client.close()
    report = {'checks': checks, 'count': len(checks), 'executableSha256': hashlib.sha256(executable.read_bytes()).hexdigest(),
              'projectAttached': False, 'nativeProjectTextExportAccepted': False, 'projectTextsImported': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
