"""Check real control clone/remove workers and current host compatibility without attachment."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import xml.etree.ElementTree as ET

from test_extension_reload import Client, manifest
from test_comfort_screen_texts import packet, stage, worker


def button(name, identity):
    return f"<Hmi.Screen.Button ID='{identity}' CompositionName='ScreenItems'><AttributeList><Name>{name}</Name><Left>10</Left></AttributeList></Hmi.Screen.Button>"


XML = ("<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Screen Ü</Name></AttributeList><ObjectList>"
       "<Hmi.Screen.ScreenLayer ID='1' CompositionName='Layers'><AttributeList><Name>L1</Name></AttributeList><ObjectList>"
       "<Hmi.Screen.Button ID='2' CompositionName='ScreenItems'><AttributeList><Name>A</Name><Left>10</Left></AttributeList><ObjectList>"
       "<MultilingualText ID='3' CompositionName='Text'><ObjectList><MultilingualTextItem ID='4'><AttributeList><Culture>en-US</Culture><Text>"
       "<body><p><b style='font-weight:bold'>Start</b> <i>ready</i></p></body></Text></AttributeList></MultilingualTextItem>"
       "<MultilingualTextItem ID='5'><AttributeList><Culture>de-DE</Culture><Text>Deutsch 😀</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>"
       "<Hmi.Event.Event ID='6' CompositionName='Events'><LinkList><Owner TargetID='2'/><Layer TargetID='1'/><Tag TargetID='@OpenLink'><Name>Tag_X</Name></Tag></LinkList></Hmi.Event.Event>"
       "</ObjectList></Hmi.Screen.Button><Hmi.Screen.Group ID='7' CompositionName='ScreenItems'><AttributeList><Name>Group</Name></AttributeList><ObjectList>"
       + button('G', '8') + button('H', '9') + "</ObjectList></Hmi.Screen.Group></ObjectList></Hmi.Screen.ScreenLayer>"
       "<Hmi.Screen.ScreenLayer ID='A' CompositionName='Layers'><AttributeList><Name>L2</Name></AttributeList><ObjectList>"
       + button('C', 'B') + "</ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>")


def clone(row, name, destination=None):
    return {'action': 'clone', 'selection': row['selection'], 'destinationXPath': destination or row['containerXPath'],
            'names': [{'sourceXPath': row['xPath'], 'newName': name}]}


def named(doc, name):
    return next(e for e in doc.iter() if e.findtext('AttributeList/Name') == name)


def worker_probe(bundle):
    hello, inspected = worker(bundle, [packet(1, '$hello', {}), packet(2, 'inspect_screen_items', {'xml': XML})])
    descriptors = {o['name']: o for o in hello['result']['operations']}
    assert descriptors['change_screen_items']['route'] == 'classicXml' and descriptors['change_screen_items']['snapshotKind'] == 'classicReferences'
    rows = {r['name']: r for r in inspected['result']['items']}; assert len(rows) == 5
    first = clone(rows['A'], 'Copy Ü', rows['C']['containerXPath'])
    first['edits'] = [{'xPath': '/Document/Hmi.Screen.Button/AttributeList/Left', 'expectedValue': '10', 'newValue': '60'}]
    group = clone(rows['Group'], 'NewGroup', rows['C']['containerXPath'])
    group['names'] += [{'sourceXPath': rows[n]['xPath'], 'newName': 'New'+n} for n in ('G', 'H')]
    stale = copy.deepcopy(first); stale['selection']['expectedFingerprint'] = '0'*64
    bad_link = copy.deepcopy(first); bad_link['edits'] = [{'xPath': '//Layer/@TargetID', 'expectedValue': '1', 'newValue': 'A'}]
    replies = worker(bundle, [packet(1, 'change_screen_items', {'changes': [first, group, clone(rows['A'], 'Another')]}, XML),
                             packet(2, 'change_screen_items', {'changes': [stale]}, XML),
                             packet(3, 'change_screen_items', {'changes': [clone(rows['A'], 'a')]}, XML),
                             packet(4, 'change_screen_items', {'changes': [bad_link]}, XML),
                             packet(5, 'change_screen_items', {'changes': [{'action': 'remove', 'selection': rows['Group']['selection']}]}, XML),
                             packet(6, 'inspect_screen_items', {'xml': XML, 'rootXPath': rows['Group']['xPath'], 'offset': 1, 'maxItems': 1})])
    result = replies[0]['result']; doc = ET.fromstring(result['candidateXml']); copied = named(doc, 'Copy Ü')
    assert copied.findtext('AttributeList/Left') == '60' and named(doc, 'A').findtext('AttributeList/Left') == '10'
    assert copied.find('.//Owner').get('TargetID') == copied.get('ID')
    assert copied.find('.//Layer').get('TargetID') == '1' and copied.find('.//Tag/Name').text == 'Tag_X'
    assert copied.find('.//b').tail == ' ' and copied.find('.//b').get('style') == 'font-weight:bold'
    assert 'Deutsch 😀' in ET.tostring(copied, encoding='unicode')
    assert len(list(named(doc, 'NewGroup').iter('Hmi.Screen.Button'))) == 2
    assert named(doc, 'NewG') is not None and named(doc, 'NewH') is not None and named(doc, 'Another') is not None
    ids = [e.get('ID') for e in doc.iter() if e.get('ID') is not None]; assert len(ids) == len(set(ids))
    assert all(e.get('TargetID') == '@OpenLink' or e.get('TargetID') in ids for e in doc.iter() if e.get('TargetID') is not None)
    assert all(r.get('error') for r in replies[1:4]), replies
    removed = ET.fromstring(replies[4]['result']['candidateXml']); assert all(e.findtext('AttributeList/Name') not in ('Group', 'G', 'H') for e in removed.iter())
    assert replies[5]['result']['items'][0]['name'] == 'G' and replies[5]['result']['hasMore']
    linked = XML.replace('<Name>C</Name>', "<Name>C</Name><Dependency TargetID='2'/>")
    linked_rows = worker(bundle, [packet(1, 'inspect_screen_items', {'xml': linked})])[0]['result']['items']
    linked_by_name = {r['name']: r for r in linked_rows}; assert not linked_by_name['A']['standaloneRemovalAvailable']
    remove_a = {'action': 'remove', 'selection': linked_by_name['A']['selection']}
    remove_c = {'action': 'remove', 'selection': linked_by_name['C']['selection']}
    ref_checks = worker(bundle, [packet(1, 'change_screen_items', {'changes': [remove_a]}, linked),
                                 packet(2, 'change_screen_items', {'changes': [remove_a, remove_c]}, linked)])
    assert ref_checks[0].get('error') and not ref_checks[1].get('error')
    return ['Real net48 worker clones controls/groups/repeated prototypes with explicit names and unique native IDs',
            'Styles, spaces, cultures, events and external links are preserved; optional edits stay scoped',
            'Stale selections, name collisions and native-link edits fail; retained references block removal']


def host_probe(executable, bundle, root, repo):
    digest = hashlib.sha256(executable.read_bytes()).hexdigest(); client = Client(executable, root)
    try:
        before = client.call('tia_list_extensions'); modules = {m['module']: m for m in before['modules']}
        tools = client.send('tools/list', {})['tools']; assert len(tools) in (51, 54)
        stage(repo, bundle, root); good_registry = (root/'external/active.json').read_bytes()
        client.call('tia_reload_extensions', module='comfort')
        after = client.call('tia_list_extensions'); updated = {m['module']: m for m in after['modules']}
        assert before['hostProcessId'] == after['hostProcessId'] == client.process.pid
        assert updated['comfort']['generation'] != modules['comfort']['generation']
        expected_version = worker(bundle, [packet(1, '$hello', {})])[0]['result']['version']
        assert updated['comfort']['version'] == expected_version
        assert all(updated[m]['generation'] == modules[m]['generation'] for m in modules if m != 'comfort')
        rows = client.call('tia_read_extension', module='comfort', operation='inspect_screen_items', argumentsJson=json.dumps({'xml': XML}))['data']['items']
        assert len(rows) == 5
        request = json.dumps({'changes': [clone(rows[0], 'Copy')]})
        assert 'preview' in client.rejected('tia_read_extension', module='comfort', operation='change_screen_items', argumentsJson=request).lower()
        assert 'attach' in client.rejected('tia_preview_extension', module='comfort', operation='change_screen_items', targetId='unselected', path='unselected', expectedFingerprint='unselected', argumentsJson=request).lower()
        bad = root/'bad'; shutil.copytree(bundle, bad)
        (bad/'recipes.json').write_text('[{"name":"inspect_screen_items","xPath":"//Left","newValue":"1"}]', encoding='utf-8'); manifest(bad)
        stage(repo, bad, root); assert 'Invalid or conflicting classic XML recipe' in client.rejected('tia_reload_extensions', module='comfort')
        assert next(m for m in client.call('tia_list_extensions')['modules'] if m['module'] == 'comfort')['generation'] == updated['comfort']['generation']
        assert client.call('tia_read_extension', module='comfort', operation='inspect_screen_items', argumentsJson=json.dumps({'xml': XML}))['data']['matchedItems'] == 5
        (root/'external/active.json').write_bytes(good_registry)
        assert client.send('tools/list', {})['tools'] == tools and not client.call('tia_get_status')['attached']
        assert hashlib.sha256(executable.read_bytes()).hexdigest() == digest
        return {'hostSha256': digest, 'workerVersion': updated['comfort']['version'], 'hostPidUnchanged': True, 'hostBinaryUnchanged': True,
                'fixedTools': len(tools), 'otherModulesUnchanged': True, 'failedReloadPreservedGeneration': True}
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args(); executable = args.executable.resolve(); repo = Path(__file__).resolve().parent.parent
    registry = json.loads((executable.parent/'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = executable.parent/'extensions'/registry['comfort']; checks = worker_probe(bundle); hosts = []
    with tempfile.TemporaryDirectory(prefix='comfort-screen-items-') as temporary:
        root = Path(temporary); hosts.append(host_probe(executable, bundle, root/'current', repo))
        if args.previous_host:
            hosts.append(host_probe(args.previous_host.resolve(), bundle, root/'previous', repo))
    checks += ['Persistent host reload retains PID, executable, unchanged tool schemas and other module generations',
               'Rejected update preserves working generation; mutation through read tools or unattached previews is blocked']
    report = {'checks': checks, 'count': len(checks), 'hosts': hosts, 'projectAttached': False, 'nativeControlAcceptance': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
