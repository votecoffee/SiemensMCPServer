"""Check real Comfort layout workers and hot reload without attaching to TIA."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

from test_extension_reload import Client, manifest


def item(name, left, width):
    return (f"<Hmi.Screen.Button CompositionName='ScreenItems'><AttributeList><Name>{name}</Name>"
            f"<Left>{left}</Left><Top>20</Top><Width>{width}</Width><Height>30</Height></AttributeList>"
            "<ObjectList><MultilingualText><ObjectList><MultilingualTextItem><AttributeList>"
            "<Culture>de-DE</Culture><Text>Ü &amp; 😀</Text></AttributeList></MultilingualTextItem>"
            "</ObjectList></MultilingualText></ObjectList></Hmi.Screen.Button>")


XML = ("<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList>"
       "<Name>Layout Ü</Name><Width>800</Width></AttributeList><ObjectList>"
       + item('A', 10, 20) + item('B', 51, 10) + item('C', 110, 30)
       + "</ObjectList></Hmi.Screen.Screen></Document>")


def worker(bundle, packets):
    payload = '\ufeff' + '\n'.join(json.dumps(p, ensure_ascii=False) for p in packets) + '\n'
    result = subprocess.run([str(bundle/'SiemensMCPServer.ExtensionWorker.exe'), '--module=comfort'],
                            input=payload, capture_output=True, encoding='utf-8', timeout=30,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, result.stderr
    replies = [json.loads(line) for line in result.stdout.splitlines()]
    assert [r['id'] for r in replies] == [p['id'] for p in packets]
    return replies


def packet(number, operation, arguments, snapshot=False):
    value = {'arguments': arguments, 'snapshot': XML} if snapshot else arguments
    return {'id': number, 'operation': operation, 'input': value}


def probe_worker(bundle):
    hello, inspection = worker(bundle, [packet(1, '$hello', {}), packet(2, 'inspect_screen_layout', {'xml': XML})])
    operations = {o['name']: o for o in hello['result']['operations']}
    assert operations['arrange_screen_items']['route'] == 'classicXml' and operations['arrange_screen_items']['preview']
    assert operations['inspect_screen_layout']['route'] == 'local' and not operations['inspect_screen_layout']['preview']
    rows = inspection['result']['items']
    assert rows[0]['name'] == 'A' and all(i['geometryAvailable'] for i in rows)
    selections = [i['selection'] for i in rows]
    move = {'action': 'move', 'items': selections[:2], 'deltaX': 5, 'deltaY': -7}
    stale = copy.deepcopy(move); stale['items'][1]['expectedBounds']['left'] += 1
    requests = [
        packet(1, 'arrange_screen_items', move, True),
        packet(2, 'arrange_screen_items', {'action': 'distribute', 'items': selections, 'axis': 'horizontal'}, True),
        packet(3, 'arrange_screen_items', {'action': 'align', 'items': selections, 'anchorXPath': selections[0]['xPath'], 'alignment': 'right'}, True),
        packet(4, 'arrange_screen_items', {'action': 'resize', 'items': selections, 'height': 40}, True),
        packet(5, 'arrange_screen_items', stale, True),
        packet(6, 'arrange_screen_items', {**move, 'items': [selections[0], selections[0]]}, True),
        packet(7, 'arrange_screen_items', {**move, 'width': 40}, True),
        packet(8, 'inspect_screen_layout', {'xml': XML, 'maxItems': 1, 'offset': 1}),
    ]
    replies = worker(bundle, requests)
    def fields(reply, field):
        return [int(i.findtext('AttributeList/' + field)) for i in ET.fromstring(reply['result']['candidateXml']).iter('Hmi.Screen.Button')]
    assert fields(replies[0], 'Left') == [15, 56, 110] and fields(replies[0], 'Top') == [13, 13, 20]
    assert fields(replies[1], 'Left') == [10, 65, 110]
    assert fields(replies[2], 'Left') == [10, 20, 0]
    assert fields(replies[3], 'Height') == [40, 40, 40]
    assert all(r.get('error') for r in replies[4:7]), replies
    assert replies[7]['result']['items'][0]['name'] == 'B' and replies[7]['result']['hasMore']
    for reply in replies[:4]:
        assert 'Ü &amp; 😀' in reply['result']['candidateXml']
        assert reply['result']['changes']['edits']
        assert ET.fromstring(reply['result']['candidateXml']).findtext('Hmi.Screen.Screen/AttributeList/Width') == '800'
    return ['Real net48 worker advertises existing routes and accepts first-frame BOM/Unicode',
            'Move, align, equal-gap distribution and resize preserve unrelated screen fields and translations',
            'Stale bounds, duplicate selections and contradictory fields fail; inspection paging retains selections']


def probe_host(executable, bundle, root, repo):
    executable_hash = hashlib.sha256(executable.read_bytes()).hexdigest()
    client = Client(executable, root)
    try:
        initial = client.call('tia_list_extensions')
        before = next(m for m in initial['modules'] if m['module'] == 'comfort')
        tools = client.send('tools/list', {})['tools']; assert len(tools) in (51, 54)
        def stage(source):
            result = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(repo/'build/Install-Extension.ps1'),
                                     '-BundleDirectory', str(source), '-ExtensionRoot', str(root/'external')],
                                    capture_output=True, encoding='utf-8', timeout=30)
            assert result.returncode == 0, result.stdout + result.stderr
        stage(bundle)
        good_registry = (root/'external/active.json').read_bytes()
        client.call('tia_reload_extensions', module='comfort')
        loaded = client.call('tia_list_extensions')
        after = next(m for m in loaded['modules'] if m['module'] == 'comfort')
        assert after['generation'] != before['generation'] and after['workerProcessId'] != before['workerProcessId']
        assert loaded['hostProcessId'] == initial['hostProcessId'] == client.process.pid
        assert client.send('tools/list', {})['tools'] == tools
        assert 'arrange_screen_items' in {o['name'] for o in after['operations']}
        reply = client.call('tia_read_extension', module='comfort', operation='inspect_screen_layout', argumentsJson=json.dumps({'xml': XML}, ensure_ascii=False))
        assert reply['data']['totalItems'] == 3
        arguments = {'action': 'move', 'items': [i['selection'] for i in reply['data']['items']], 'deltaX': 1, 'deltaY': 0}
        message = client.rejected('tia_read_extension', module='comfort', operation='arrange_screen_items', argumentsJson=json.dumps(arguments))
        assert 'preview' in message.lower(), message
        message = client.rejected('tia_preview_extension', module='comfort', operation='arrange_screen_items', targetId='unselected',
                                  path='unselected', expectedFingerprint='unselected', argumentsJson=json.dumps(arguments))
        assert 'attach' in message.lower(), message
        bad = root/'bad'; shutil.copytree(bundle, bad)
        (bad/'recipes.json').write_text('[{"name":"inspect_screen_layout","xPath":"//Left","newValue":"1"}]', encoding='utf-8')
        manifest(bad); stage(bad)
        message = client.rejected('tia_reload_extensions', module='comfort')
        assert 'Invalid or conflicting classic XML recipe' in message, message
        assert next(m for m in client.call('tia_list_extensions')['modules'] if m['module'] == 'comfort')['generation'] == after['generation']
        assert client.call('tia_read_extension', module='comfort', operation='inspect_screen_layout', argumentsJson=json.dumps({'xml': XML}))['data']['totalItems'] == 3
        (root/'external/active.json').write_bytes(good_registry)
        assert not client.call('tia_get_status')['attached']
        assert hashlib.sha256(executable.read_bytes()).hexdigest() == executable_hash
        return {'hostSha256': executable_hash, 'workerVersion': after['version'], 'fixedTools': len(tools),
                'hostPidUnchanged': True, 'hostBinaryUnchanged': True, 'rejectedReloadPreservedGeneration': True}
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--worker-root', required=True, type=Path)
    parser.add_argument('--previous-host', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    registry = json.loads((args.worker_root/'active.json').read_text(encoding='utf-8-sig'))
    bundle = (args.worker_root/registry['comfort']).resolve()
    checks = probe_worker(bundle)
    hosts = []
    with tempfile.TemporaryDirectory(prefix='comfort-layout-') as temporary:
        root = Path(temporary)
        for number, executable in enumerate([args.executable] + ([args.previous_host] if args.previous_host else [])):
            hosts.append(probe_host(executable.resolve(), bundle, root/str(number), repo))
    checks += ['Persistent hosts load new layout operations with unchanged PID, binary and unchanged tool schemas',
               'MCP refuses mutation through read tools and native preview without attachment',
               'Rejected conflicting worker recipe retains active generation and functioning layout inspection']
    report = {'checks': checks, 'count': len(checks), 'hosts': hosts, 'projectAttached': False,
              'nativeImportAccepted': False, 'nativeCompileSaveAccepted': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
