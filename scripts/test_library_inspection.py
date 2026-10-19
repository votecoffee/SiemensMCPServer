"""Check library workers and MCP routing without attaching to or editing a project."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from test_extension_reload import Client, manifest as refresh_manifest


def worker_probe(executable):
    arguments = {
        'expectedProjectPath': str(Path(tempfile.gettempdir()) / 'synthetic.ap15_1'),
        'capture': {'libraryId': 'project', 'path': 'types', 'maxRows': 2}, 'options': {},
    }
    snapshot = {
        'libraryId': 'project', 'path': 'types', 'offset': 0, 'nextOffset': 1,
        'hasMore': True, 'payloadLimited': True, 'offsetLimitReached': False,
        'rows': [{'libraryId': 'project', 'path': 'types/t:%C3%9Cber', 'kind': 'type', 'name': 'Über 😀',
                  'nativeType': 'Synthetic.LibraryType', 'canBrowse': True, 'fields': {'Author': 'テスト'},
                  'unsupportedFields': ['Namespace'], 'truncatedFields': ['Author']}],
    }
    packets = [
        {'id': 1, 'operation': '$hello', 'input': {}},
        {'id': 2, 'operation': 'browse_library', 'input': {'phase': 'validate', 'arguments': arguments}},
        {'id': 3, 'operation': 'browse_library', 'input': {'phase': 'snapshot', 'arguments': arguments, 'snapshot': snapshot}},
        {'id': 4, 'operation': 'list_libraries', 'input': {'phase': 'validate', 'arguments': arguments}},
    ]
    text = '\ufeff' + '\n'.join(json.dumps(p, ensure_ascii=False) for p in packets) + '\n'
    result = subprocess.run([str(executable), '--module=project'], input=text, capture_output=True,
        encoding='utf-8', timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, result.stderr
    replies = [json.loads(line) for line in result.stdout.splitlines()]
    assert [r['id'] for r in replies] == [1, 2, 3, 4]
    operations = replies[0]['result']['operations']
    assert {o['name'] for o in operations} == {'list_libraries', 'browse_library'}
    assert all(o['route'] == 'projectRead' and o['snapshotKind'] == 'libraryCatalog' and not o['preview'] for o in operations)
    assert replies[1]['result']['validated'] and not replies[1].get('error')
    assert replies[2]['result']['rows'][0]['name'] == 'Über 😀'
    assert replies[2]['result']['rows'][0]['fields']['Author'] == 'テスト'
    assert replies[2]['result']['hasMore'] and replies[2]['result']['payloadLimited']
    assert replies[3].get('error')


def stage(repo, bundle, root):
    result = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(repo / 'build/Install-Extension.ps1'),
        '-BundleDirectory', str(bundle), '-ExtensionRoot', str(root / 'external')], capture_output=True, encoding='utf-8', timeout=30)
    assert result.returncode == 0, result.stdout + result.stderr


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', type=Path, help='Optional 0.7.4 host to check existing PLC-worker compatibility')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    executable = args.executable.resolve()
    repo = Path(__file__).resolve().parent.parent
    registry = json.loads((executable.parent / 'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = executable.parent / 'extensions' / registry['project']
    worker_probe(bundle / 'SiemensMCPServer.ExtensionWorker.exe')
    checks = ['Real project worker accepts first-frame BOM, advertises read-only routes and preserves Unicode/page/field limits']
    with tempfile.TemporaryDirectory(prefix='tia-library-inspection-') as temporary:
        root = Path(temporary)
        client = Client(executable, root)
        try:
            tools = client.send('tools/list', {})['tools']
            assert len(tools) == 51
            before = client.call('tia_list_extensions')
            modules = {m['module']: m for m in before['modules']}
            assert set(modules) == {'comfort', 'unified', 'plc', 'project'}
            capabilities = client.call('tia_get_api_capabilities')['projectLibraries']
            assert capabilities['projectLibraryApiPresent'] and capabilities['openGlobalLibrariesApiPresent']
            arguments = {'expectedProjectPath': str(root / 'synthetic.ap15_1'), 'capture': {}, 'options': {}}
            assert 'attach' in client.rejected('tia_read_extension', module='project', operation='list_libraries', argumentsJson=json.dumps(arguments)).lower()
            arguments['capture'] = {'libraryId': 'project', 'path': 'types/t:Counter/Dependencies'}
            assert 'library' in client.rejected('tia_read_extension', module='project', operation='browse_library', argumentsJson=json.dumps(arguments)).lower()
            arguments['capture'] = {'libraryId': 'project', 'path': '', 'open': True}
            assert 'open' in client.rejected('tia_read_extension', module='project', operation='browse_library', argumentsJson=json.dumps(arguments)).lower()
            arguments['capture'] = {'libraryId': 'project', 'path': ''}
            arguments['snapshot'] = {'rows': []}
            assert 'snapshot' in client.rejected('tia_read_extension', module='project', operation='browse_library', argumentsJson=json.dumps(arguments)).lower()
            del arguments['snapshot']
            arguments['options'] = {'save': True}
            assert 'options' in client.rejected('tia_read_extension', module='project', operation='browse_library', argumentsJson=json.dumps(arguments)).lower()
            assert not client.call('tia_get_status')['attached']
            checks.append('Host keeps 51 tools, reports library APIs and rejects unattached reads, unsupported navigation, open/save options and injected snapshots')
            client.call('tia_reload_extensions', module='project')
            after = client.call('tia_list_extensions')
            project = next(m for m in after['modules'] if m['module'] == 'project')
            assert after['hostProcessId'] == before['hostProcessId'] == client.process.pid
            assert project['generation'] != modules['project']['generation']
            assert project['workerProcessId'] != modules['project']['workerProcessId']
            assert client.send('tools/list', {})['tools'] == tools
            checks.append('Project worker reload retains the host PID/tool schema and replaces its generation/process')
            bad = root / 'bad-project'
            shutil.copytree(bundle, bad)
            (bad / 'recipes.json').write_text('[{"name":"bad","xPath":"//Name","newValue":"x"}]', encoding='utf-8')
            refresh_manifest(bad)
            stage(repo, bad, root)
            rejected = client.rejected('tia_reload_extensions', module='project')
            assert 'worker failed' in rejected.lower() and 'Invalid or conflicting classic XML recipe' in rejected, rejected
            current = client.call('tia_list_extensions')
            assert next(m for m in current['modules'] if m['module'] == 'project')['generation'] == project['generation']
            assert not client.call('tia_get_status')['attached']
            checks.append('Rejected project-worker startup retains diagnostics and the previous active generation')
        finally:
            client.close()
        if args.previous_host:
            previous = root / 'previous'
            client = Client(args.previous_host.resolve(), previous)
            try:
                before = client.call('tia_list_extensions')
                previous_plc = next(m for m in before['modules'] if m['module'] == 'plc')
                plc_bundle = executable.parent / 'extensions' / registry['plc']
                stage(repo, plc_bundle, previous)
                client.call('tia_reload_extensions', module='plc')
                after = client.call('tia_list_extensions')
                plc = next(m for m in after['modules'] if m['module'] == 'plc')
                assert plc['generation'] != previous_plc['generation'] and plc['version'] == project['version']
                assert before['hostProcessId'] == after['hostProcessId']
                assert {m['module'] for m in after['modules']} == {'comfort', 'unified', 'plc'}
                assert len(client.send('tools/list', {})['tools']) == 51
                checks.append('Previous 0.7.4 host accepts the newer existing PLC worker while retaining its three-module catalog and host PID')
                registry_path = previous / 'external/active.json'
                old_selection = registry_path.read_bytes()
                stage(repo, bundle, previous)
                assert 'incompatible' in client.rejected('tia_reload_extensions', module='project').lower()
                restore = previous / 'external/restore.tmp'
                restore.write_bytes(old_selection)
                os.replace(restore, registry_path)
                assert {m['module'] for m in client.call('tia_list_extensions')['modules']} == {'comfort', 'unified', 'plc'}
                assert not client.call('tia_get_status')['attached']
                checks.append('Previous host rejects the project route; restoring its staged registry keeps the existing module catalog usable')
            finally:
                client.close()
    report = {'checks': checks, 'count': len(checks), 'executableSha256': hashlib.sha256(executable.read_bytes()).hexdigest(),
              'projectAttached': False, 'nativeLibraryInspectionAccepted': False, 'nativeWrites': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
