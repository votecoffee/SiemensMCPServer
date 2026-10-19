"""Check real behavior workers and unchanged host reload without a TIA attachment."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import xml.etree.ElementTree as ET

from test_extension_reload import Client
from test_comfort_screen_texts import packet, stage, worker
from test_comfort_screen_items import XML, named


BEHAVIORS = XML.replace("<Hmi.Event.Event ID='6' CompositionName='Events'>",
    "<Hmi.Event.Event ID='6' CompositionName='Events'><AttributeList><Name>Press</Name><Text><body><p><b>Start</b> <i>ready</i></p></body></Text></AttributeList>"
    "<ObjectList><Hmi.Event.Function ID='C' CompositionName='Functions'><AttributeList><Name>SetTag</Name><Code>Tag_X = 1</Code></AttributeList></Hmi.Event.Function></ObjectList>")
BEHAVIORS = BEHAVIORS.replace("<Owner TargetID='2'/>", "<Function TargetID='C'/><Owner TargetID='2'/>")
BEHAVIORS = BEHAVIORS.replace('<Name>C</Name><Left>10</Left></AttributeList>',
    "<Name>C</Name><Left>10</Left></AttributeList><ObjectList><Hmi.Event.Event ID='D' CompositionName='Events'><AttributeList><Name>Dependent</Name></AttributeList><LinkList><Event TargetID='6'/></LinkList></Hmi.Event.Event></ObjectList>")


def change(row, destination=None):
    value = {'action': 'clone' if destination else 'remove', 'xPath': row['xPath'], 'expectedFingerprint': row['fingerprint']}
    if destination:
        value['destinationXPath'] = destination
    return value


def worker_probe(bundle):
    hello, inspected, controls = worker(bundle, [packet(1, '$hello', {}), packet(2, 'inspect_behaviors', {'xml': BEHAVIORS}),
                                               packet(3, 'inspect_screen_items', {'xml': BEHAVIORS})])
    descriptors = {o['name']: o for o in hello['result']['operations']}
    assert descriptors['change_behaviors']['snapshotKind'] == 'screenText' and descriptors['inspect_behaviors']['route'] == 'local'
    rows = inspected['result']['objects']; assert len(rows) == 3
    assert not rows[0]['standaloneRemovalAvailable']
    destination = next(r for r in controls['result']['items'] if r['name'] == 'C')['xPath']
    clone = change(rows[0], destination)
    clone['referenceTargets'] = [dict(rows[0]['ownerLinks'][0], newTargetXPath=destination)]
    clone['referenceTargets'][0] = {k: clone['referenceTargets'][0][k] for k in ('xPath','expectedTargetXPath','newTargetXPath')}
    clone['edits'] = [{'xPath': '/Document/Hmi.Event.Event/AttributeList/Name', 'expectedValue': 'Press', 'newValue': 'CopiedPress'}]
    stale = copy.deepcopy(clone); stale['referenceTargets'][0]['expectedTargetXPath'] = destination
    scalar_bypass = copy.deepcopy(clone); scalar_bypass['referenceTargets'] = []
    scalar_bypass['edits'] = [{'xPath': '//Owner/@TargetID', 'expectedValue': '2', 'newValue': 'B'}]
    replies = worker(bundle, [packet(1, 'change_behaviors', {'changes': [clone]}, BEHAVIORS),
                             packet(2, 'change_behaviors', {'changes': [stale]}, BEHAVIORS),
                             packet(3, 'change_behaviors', {'changes': [scalar_bypass]}, BEHAVIORS),
                             packet(4, 'change_behaviors', {'changes': [change(rows[0])]}, BEHAVIORS),
                             packet(5, 'change_behaviors', {'changes': [change(rows[0]), change(rows[2])]}, BEHAVIORS),
                             packet(6, 'inspect_behaviors', {'xml': BEHAVIORS, 'offset': 1, 'maxItems': 1})])
    doc = ET.fromstring(replies[0]['result']['candidateXml']); copied = named(doc, 'CopiedPress')
    assert copied.find('LinkList/Owner').get('TargetID') == named(doc, 'C').get('ID')
    assert copied.find('LinkList/Function').get('TargetID') == copied.find('ObjectList/Hmi.Event.Function').get('ID')
    assert copied.find('.//Tag/Name').text == 'Tag_X' and copied.find('.//Code').text == 'Tag_X = 1'
    assert copied.find('.//b').tail == ' ' and named(doc, 'Press').find('LinkList/Owner').get('TargetID') == '2'
    assert all(r.get('error') for r in replies[1:4])
    assert not list(ET.fromstring(replies[4]['result']['candidateXml']).iter('Hmi.Event.Event'))
    assert replies[5]['result']['objects'][0]['nativeType'] == 'Hmi.Event.Function' and replies[5]['result']['hasMore']
    return hello['result']['version'], clone


def host_probe(executable, bundle, root, repo, version, clone):
    digest = hashlib.sha256(executable.read_bytes()).hexdigest(); client = Client(executable, root)
    try:
        before = client.call('tia_list_extensions'); initial = {m['module']: m for m in before['modules']}
        tools = client.send('tools/list', {})['tools']; assert len(tools) == 51
        stage(repo, bundle, root); client.call('tia_reload_extensions', module='comfort')
        after = client.call('tia_list_extensions'); updated = {m['module']: m for m in after['modules']}
        assert before['hostProcessId'] == after['hostProcessId'] == client.process.pid
        assert updated['comfort']['version'] == version and updated['comfort']['generation'] != initial['comfort']['generation']
        assert all(updated[m]['generation'] == initial[m]['generation'] for m in initial if m != 'comfort')
        data = client.call('tia_read_extension', module='comfort', operation='inspect_behaviors', argumentsJson=json.dumps({'xml': BEHAVIORS}))['data']
        assert data['matchedObjects'] == 3 and data['objects'][0]['ownerLinks']
        args = json.dumps({'changes': [clone]})
        assert 'preview' in client.rejected('tia_read_extension', module='comfort', operation='change_behaviors', argumentsJson=args).lower()
        assert 'attach' in client.rejected('tia_preview_extension', module='comfort', operation='change_behaviors', targetId='unselected', path='unselected', expectedFingerprint='unselected', argumentsJson=args).lower()
        assert client.send('tools/list', {})['tools'] == tools and not client.call('tia_get_status')['attached']
        assert hashlib.sha256(executable.read_bytes()).hexdigest() == digest
        return {'hostSha256': digest, 'workerVersion': version, 'hostPidUnchanged': True, 'hostBinaryUnchanged': True,
                'fixedToolsAndSchemasUnchanged': True, 'otherModulesUnchanged': True}
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args(); executable = args.executable.resolve(); repo = Path(__file__).resolve().parent.parent
    registry = json.loads((executable.parent/'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = executable.parent/'extensions'/registry['comfort']; version, clone = worker_probe(bundle)
    with tempfile.TemporaryDirectory(prefix='comfort-behaviors-') as temporary:
        root = Path(temporary); hosts = [host_probe(executable, bundle, root/'current', repo, version, clone)]
        if args.previous_host:
            hosts.append(host_probe(args.previous_host.resolve(), bundle, root/'previous', repo, version, clone))
    report = {'checks': ['Real net48 worker clones an event/function subtree and explicitly remaps its external owner',
                         'Internal IDs, original owner, symbolic tags, scripts and styled spaces are preserved',
                         'Stale owner maps and scalar link bypass rejected; combined dependent removals succeed',
                         'Worker reload preserves host PID/hash/tool schemas and other generations; unattached writes blocked'],
              'hosts': hosts, 'projectAttached': False, 'nativeBehaviorAcceptance': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
