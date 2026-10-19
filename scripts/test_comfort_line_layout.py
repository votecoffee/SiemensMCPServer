"""Check zero-extent Line layout on real net48 workers and compatible persistent hosts."""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile
import time
import xml.etree.ElementTree as ET

from test_extension_reload import Client, alive
from test_comfort_screen_texts import packet, stage, worker


def item(kind, name, left, width, height):
    return (f"<{kind} CompositionName='ScreenItems'><AttributeList><ObjectName>{name}</ObjectName>"
            f"<Left>{left}</Left><Top>20</Top><Width>{width}</Width><Height>{height}</Height><LineColor>1, 2, 3</LineColor>"
            f"</AttributeList></{kind}>")


XML = ("<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Lines</Name></AttributeList><ObjectList>"
       + item('Hmi.Screen.Line', 'Horizontal', 10, 80, 0) + item('Hmi.Screen.Line', 'Vertical', 110, 0, 80)
       + item('Hmi.Screen.Line', 'Diagonal', 210, 40, 80) + item('Hmi.Screen.Button', 'Button', 310, 80, 30)
       + "</ObjectList></Hmi.Screen.Screen></Document>")


def probe(bundle):
    hello, inspection = worker(bundle, [packet(1, '$hello', {}), packet(2, 'inspect_screen_layout', {'xml': XML})])
    rows = inspection['result']['items']; assert all(r['geometryAvailable'] for r in rows)
    assert [r['name'] for r in rows] == ['Horizontal', 'Vertical', 'Diagonal', 'Button']
    choices = [r['selection'] for r in rows]
    moved, resized, forbidden, collapsed = worker(bundle, [
        packet(1, 'arrange_screen_items', {'action': 'move', 'items': choices[:3], 'deltaX': 5, 'deltaY': -7}, XML),
        packet(2, 'arrange_screen_items', {'action': 'resize', 'items': [choices[2]], 'width': 0}, XML),
        packet(3, 'arrange_screen_items', {'action': 'resize', 'items': [choices[1], choices[3]], 'width': 0}, XML),
        packet(4, 'arrange_screen_items', {'action': 'resize', 'items': [choices[0]], 'width': 0}, XML)])
    assert not moved.get('error') and not resized.get('error')
    original = ET.fromstring(XML); result = ET.fromstring(moved['result']['candidateXml'])
    for node in original.iter('Hmi.Screen.Line'):
        node.find('AttributeList/Left').text = str(int(node.findtext('AttributeList/Left')) + 5)
        node.find('AttributeList/Top').text = '13'
    assert ET.tostring(original) == ET.tostring(result)
    diagonal = list(ET.fromstring(resized['result']['candidateXml']).iter('Hmi.Screen.Line'))[2]
    assert diagonal.findtext('AttributeList/Width') == '0' and diagonal.findtext('AttributeList/Height') == '80'
    assert forbidden.get('error') and collapsed.get('error')
    operation = next(o for o in hello['result']['operations'] if o['name'] == 'arrange_screen_items')
    assert operation['snapshotKind'] == 'classicReferences'
    return hello['result']['version']


def host_probe(executable, bundle, root, repo, version):
    digest = hashlib.sha256(executable.read_bytes()).hexdigest(); client = Client(executable, root); owned = set(); outcome = {}
    host_version = tuple(int(n) for n in client.records[0]['response']['result']['serverInfo']['version'].split('.')[:3])
    try:
        before = client.call('tia_list_extensions'); initial = {m['module']: m for m in before['modules']}
        owned.update(m['workerProcessId'] for m in initial.values())
        tools = client.send('tools/list', {})['tools']; assert len(tools) == 51
        stage(repo, bundle, root); client.call('tia_reload_extensions', module='comfort')
        after = client.call('tia_list_extensions'); updated = {m['module']: m for m in after['modules']}
        owned.update(m['workerProcessId'] for m in updated.values())
        assert before['hostProcessId'] == after['hostProcessId'] == client.process.pid
        assert updated['comfort']['version'] == version and updated['comfort']['generation'] != initial['comfort']['generation']
        assert all(updated[m]['generation'] == initial[m]['generation'] for m in initial if m != 'comfort')
        result = client.call('tia_read_extension', module='comfort', operation='inspect_screen_layout', argumentsJson=json.dumps({'xml': XML}))['data']
        assert all(r['geometryAvailable'] for r in result['items'])
        request = json.dumps({'action': 'move', 'items': [result['items'][0]['selection']], 'deltaX': 1, 'deltaY': 0})
        assert 'preview' in client.rejected('tia_read_extension', module='comfort', operation='arrange_screen_items', argumentsJson=request).lower()
        assert not client.call('tia_get_status')['attached'] and client.send('tools/list', {})['tools'] == tools
        assert hashlib.sha256(executable.read_bytes()).hexdigest() == digest
        outcome.update({'hostSha256': digest, 'workerVersion': version, 'hostPidAndBinaryUnchanged': True, 'toolsAndOtherModulesUnchanged': True})
        return outcome
    finally:
        client.close()
        outcome['workersStoppedBeforeHostExit'] = not any(alive(pid) for pid in owned)
        if host_version >= (0, 10, 5):
            assert outcome['workersStoppedBeforeHostExit'], 'Host must dispose owned workers before exiting'
        for _ in range(40):
            if not any(alive(pid) for pid in owned):
                break
            time.sleep(0.05)
        assert not any(alive(pid) for pid in owned), 'Owned workers still running after host shutdown'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--bundle-directory', type=Path)
    parser.add_argument('--previous-host', action='append', type=Path, default=[], help='Compatible host using classicReferences (0.10.4+)')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args(); executable = args.executable.resolve(); repo = Path(__file__).resolve().parent.parent
    registry = json.loads((executable.parent/'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = args.bundle_directory.resolve() if args.bundle_directory else executable.parent/'extensions'/registry['comfort']
    version = probe(bundle)
    with tempfile.TemporaryDirectory(prefix='comfort-line-layout-') as temporary:
        root = Path(temporary); hosts = [host_probe(executable, bundle, root/'current', repo, version)]
        for index, old in enumerate(args.previous_host):
            hosts.append(host_probe(old.resolve(), bundle, root/f'old-{index}', repo, version))
    report = {'checks': ['Real net48 worker moves horizontal/vertical/diagonal native Lines and preserves other XML',
                         'Resize permits one zero extent only for the exact Line type; collapsed/mixed-invalid batches rejected',
                         'Compatible host reload retains PID/hash/schemas/other modules; mutation through read tool rejected'],
              'hosts': hosts, 'projectAttached': False, 'nativeImportAcceptance': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
