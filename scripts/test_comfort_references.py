"""Check real symbolic-link workers and compatible hosts without attachment."""
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
from test_comfort_screen_items import XML, clone, named


def worker_probe(bundle):
    hello, inspected, controls = worker(bundle, [packet(1, '$hello', {}), packet(2, 'inspect_references', {'xml': XML}),
                                               packet(3, 'inspect_screen_items', {'xml': XML})])
    descriptors = {o['name']: o for o in hello['result']['operations']}
    assert descriptors['rebind_references']['route'] == 'classicXml' and descriptors['rebind_references']['snapshotKind'] == 'classicReferences'
    rows = inspected['result']['references']; assert len(rows) == 3
    tags = [r for r in rows if r['editable']]; assert len(tags) == 1 and tags[0]['linkType'] == 'Tag'
    assert next(r for r in rows if r['linkType'] == 'Owner')['targetName'] == 'A'
    edit = {'selection': tags[0]['selection'], 'newName': '"DB Ü"."Value<&😀"'}
    stale = copy.deepcopy(edit); stale['selection']['expectedName'] = 'stale'
    replies = worker(bundle, [packet(1, 'rebind_references', {'edits': [edit]}, XML),
                             packet(2, 'rebind_references', {'edits': [stale]}, XML),
                             packet(3, 'rebind_references', {'edits': [edit, edit]}, XML),
                             packet(4, 'rebind_references', {'edits': [dict(edit, unexpected=True)]}, XML),
                             packet(5, 'inspect_references', {'xml': XML, 'linkType': 'Tag', 'rootXPath': tags[0]['controlXPath']}),
                             packet(6, 'change_screen_items', {'changes': [clone(controls['result']['items'][0], 'Copy')]}, XML)])
    candidate = replies[0]['result']['candidateXml']; doc = ET.fromstring(candidate)
    expected = ET.fromstring(XML); expected.find('.//Tag/Name').text = edit['newName']
    assert ET.tostring(doc) == ET.tostring(expected)
    assert len(replies[4]['result']['references']) == 1 and all(r.get('error') for r in replies[1:4])
    cloned = replies[5]['result']; copied_xml = cloned['candidateXml']
    scope = cloned['changes']['operations'][0]['newRootXPath']
    copy_rows = worker(bundle, [packet(1, 'inspect_references', {'xml': copied_xml, 'rootXPath': scope, 'linkType': 'Tag'})])[0]['result']['references']
    copied = worker(bundle, [packet(1, 'rebind_references', {'edits': [{'selection': r['selection'], 'newName': 'CopyTag'} for r in copy_rows]}, copied_xml)])[0]
    doc = ET.fromstring(copied['result']['candidateXml'])
    assert named(doc, 'A').find('.//Tag/Name').text == 'Tag_X' and named(doc, 'Copy').find('.//Tag/Name').text == 'CopyTag'
    assert named(doc, 'Copy').find('.//b').tail == ' ' and named(doc, 'Copy').find('.//Owner').get('TargetID') == named(doc, 'Copy').get('ID')
    assert 'Deutsch 😀' in ET.tostring(named(doc, 'Copy'), encoding='unicode')
    return hello['result']['version']


def host_probe(executable, bundle, root, repo, version):
    digest = hashlib.sha256(executable.read_bytes()).hexdigest(); client = Client(executable, root)
    try:
        before = client.call('tia_list_extensions'); modules = {m['module']: m for m in before['modules']}
        tools = client.send('tools/list', {})['tools']; assert len(tools) in (51, 54)
        stage(repo, bundle, root); registry = (root/'external/active.json').read_bytes()
        client.call('tia_reload_extensions', module='comfort')
        after = client.call('tia_list_extensions'); updated = {m['module']: m for m in after['modules']}
        assert before['hostProcessId'] == after['hostProcessId'] == client.process.pid
        assert updated['comfort']['version'] == version and updated['comfort']['generation'] != modules['comfort']['generation']
        assert all(updated[m]['generation'] == modules[m]['generation'] for m in modules if m != 'comfort')
        data = client.call('tia_read_extension', module='comfort', operation='inspect_references', argumentsJson=json.dumps({'xml': XML}))['data']
        tag = next(r for r in data['references'] if r['editable'])
        request = json.dumps({'edits': [{'selection': tag['selection'], 'newName': 'CopyTag'}]})
        assert 'preview' in client.rejected('tia_read_extension', module='comfort', operation='rebind_references', argumentsJson=request).lower()
        assert 'attach' in client.rejected('tia_preview_extension', module='comfort', operation='rebind_references', targetId='unselected', path='unselected', expectedFingerprint='unselected', argumentsJson=request).lower()
        bad = root/'bad'; shutil.copytree(bundle, bad)
        (bad/'recipes.json').write_text('[{"name":"inspect_references","xPath":"//Left","newValue":"1"}]', encoding='utf-8'); manifest(bad)
        stage(repo, bad, root); assert 'Invalid or conflicting classic XML recipe' in client.rejected('tia_reload_extensions', module='comfort')
        assert next(m for m in client.call('tia_list_extensions')['modules'] if m['module'] == 'comfort')['generation'] == updated['comfort']['generation']
        assert client.call('tia_read_extension', module='comfort', operation='inspect_references', argumentsJson=json.dumps({'xml': XML}))['data']['matchedReferences'] == 3
        (root/'external/active.json').write_bytes(registry)
        assert client.send('tools/list', {})['tools'] == tools and not client.call('tia_get_status')['attached']
        assert hashlib.sha256(executable.read_bytes()).hexdigest() == digest
        return {'hostSha256': digest, 'workerVersion': version, 'hostPidUnchanged': True, 'hostBinaryUnchanged': True,
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
    bundle = executable.parent/'extensions'/registry['comfort']; version = worker_probe(bundle)
    with tempfile.TemporaryDirectory(prefix='comfort-references-') as temporary:
        root = Path(temporary); hosts = [host_probe(executable, bundle, root/'current', repo, version)]
        if args.previous_host:
            hosts.append(host_probe(args.previous_host.resolve(), bundle, root/'previous', repo, version))
    report = {'checks': ['Real net48 worker inspects native/symbolic links and changes exact symbolic names without other XML changes',
                         'Clone-then-rebind scopes changes to the copied control and preserves original links, styles and cultures',
                         'Stale/duplicate/unknown arguments rejected; read mutation and unattached previews blocked',
                         'Worker reload preserves host PID/hash/tools/other generations; invalid update preserves active worker'],
              'hosts': hosts, 'projectAttached': False, 'nativeReferenceAcceptance': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
