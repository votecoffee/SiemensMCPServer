"""Check styled caption workers, native guards and older-host rejection without attachment."""
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


XML = ("<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>Screen Ü</Name></AttributeList><ObjectList>"
       "<Hmi.Screen.Button ID='1' CompositionName='ScreenItems'><AttributeList><Name>Button</Name></AttributeList><ObjectList>"
       "<MultilingualText CompositionName='Text'><ObjectList><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>"
       "<body><p><span style='font-weight:bold;color:red'>Hello</span> <b>world</b><br/><i/></p></body>"
       "</Text></AttributeList></MultilingualTextItem><MultilingualTextItem><AttributeList><Culture>de-DE</Culture><Text>Deutsch 😀</Text>"
       "</AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Screen.Button></ObjectList></Hmi.Screen.Screen></Document>")


def worker(bundle, packets):
    text = '\ufeff' + '\n'.join(json.dumps(p, ensure_ascii=False) for p in packets) + '\n'
    result = subprocess.run([str(bundle/'SiemensMCPServer.ExtensionWorker.exe'), '--module=comfort'], input=text,
                            capture_output=True, encoding='utf-8', timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, result.stderr
    replies = [json.loads(line) for line in result.stdout.splitlines()]
    assert [r['id'] for r in replies] == [p['id'] for p in packets]
    return replies


def packet(number, operation, arguments, snapshot=None):
    value = arguments if snapshot is None else {'snapshot': snapshot, 'arguments': arguments}
    return {'id': number, 'operation': operation, 'input': value}


def worker_probe(bundle):
    hello, inspected = worker(bundle, [packet(1, '$hello', {}), packet(2, 'inspect_screen_texts', {'xml': XML})])
    descriptors = {o['name']: o for o in hello['result']['operations']}
    assert descriptors['edit_screen_texts']['snapshotKind'] == 'classicReferences'
    assert descriptors['edit_screen_texts']['route'] == 'classicXml' and descriptors['edit_screen_texts']['preview']
    rows = inspected['result']['runs']
    assert [r['text'] for r in rows] == ['Hello', ' ', 'world', '', 'Deutsch 😀']
    edits = {'edits': [{'selection': rows[i]['selection'], 'newText': value} for i, value in [(0, 'Café < & 😀'), (1, '  '), (3, 'Italic')]]}
    stale = copy.deepcopy(edits); stale['edits'][1]['selection']['expectedTextFingerprint'] = '0'*64
    duplicate = {'edits': [edits['edits'][0], edits['edits'][0]]}
    unknown = XML.replace('<b>', '<field>').replace('</b>', '</field>')
    replies = worker(bundle, [packet(1, 'edit_screen_texts', edits, XML), packet(2, 'edit_screen_texts', stale, XML),
                             packet(3, 'edit_screen_texts', duplicate, XML), packet(4, 'inspect_screen_texts', {'xml': unknown}),
                             packet(5, 'inspect_screen_texts', {'xml': XML, 'culture': 'EN-us', 'query': 'world', 'maxRuns': 1})])
    result = replies[0]['result']; doc = ET.fromstring(result['candidateXml'])
    assert doc.find('.//span').text == 'Café < & 😀' and doc.find('.//span').get('style') == 'font-weight:bold;color:red'
    assert doc.find('.//span').tail == '  ' and doc.find('.//b').text == 'world'
    assert doc.find('.//i').text == 'Italic' and doc.find('.//br') is not None
    assert doc.findall('.//MultilingualTextItem')[1].find('AttributeList/Text').text == 'Deutsch 😀'
    assert len(result['changes']['edits']) == 3
    assert replies[1].get('error') and replies[2].get('error')
    assert not replies[3]['result']['runs'][0]['canPreview']
    assert replies[4]['result']['runs'][0]['selection']['runIndex'] == 2
    # Exercise alternate native text storage on the actual net48 runtime too.
    for storage in ('encodedBody', 'plain'):
        alternate = ET.fromstring(XML); field = alternate.find('.//MultilingualTextItem/AttributeList/Text')
        markup = ET.tostring(field.find('body'), encoding='unicode')
        for child in list(field):
            field.remove(child)
        field.text = markup if storage == 'encodedBody' else 'Original'
        alternate_xml = ET.tostring(alternate, encoding='unicode')
        row = worker(bundle, [packet(1, 'inspect_screen_texts', {'xml': alternate_xml})])[0]['result']['runs'][0]
        assert row['storage'] == storage and row['canPreview']
        changed = worker(bundle, [packet(1, 'edit_screen_texts', {'edits': [{'selection': row['selection'], 'newText': 'New ]]> & <tag> 😀'}]}, alternate_xml)])[0]
        assert not changed.get('error'), changed
        value = ET.fromstring(changed['result']['candidateXml']).find('.//MultilingualTextItem/AttributeList/Text')
        assert len(value) == 0
        if storage == 'encodedBody':
            assert ET.fromstring(value.text).find('.//span').text == 'New ]]> & <tag> 😀'
        else:
            assert value.text == 'New ]]> & <tag> 😀'
    return ['Real net48 worker preserves Unicode, markup attributes, explicit spaces, empty slots and other cultures',
            'Stale fingerprints and duplicate edits are rejected; unknown dynamic markup stays protected',
            'Culture/literal filtering retains original text-run indexes and existing host-route contract']


def stage(repo, bundle, root):
    result = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(repo/'build/Install-Extension.ps1'),
                             '-BundleDirectory', str(bundle), '-ExtensionRoot', str(root/'external')],
                            capture_output=True, encoding='utf-8', timeout=30)
    assert result.returncode == 0, result.stdout + result.stderr


def host_probe(executable, bundle, root, repo, old=False):
    before_hash = hashlib.sha256(executable.read_bytes()).hexdigest()
    client = Client(executable, root)
    try:
        initial = client.call('tia_list_extensions'); modules = {m['module']: m for m in initial['modules']}
        tools = client.send('tools/list', {})['tools']; assert len(tools) == 51
        stage(repo, bundle, root)
        if old:
            message = client.rejected('tia_reload_extensions', module='comfort')
            assert 'incompatible' in message.lower(), message
            after = client.call('tia_list_extensions')
            assert next(m for m in after['modules'] if m['module'] == 'comfort')['generation'] == modules['comfort']['generation']
            assert client.call('tia_read_extension', module='comfort', operation='inspect_xml', argumentsJson=json.dumps({'xml': XML, 'editable': True}))['data']['name'] == 'Screen Ü'
            (root/'external/active.json').write_text('{}', encoding='utf-8')
        else:
            client.call('tia_reload_extensions', module='comfort')
            after = client.call('tia_list_extensions'); current = next(m for m in after['modules'] if m['module'] == 'comfort')
            assert current['generation'] != modules['comfort']['generation']
            rows = client.call('tia_read_extension', module='comfort', operation='inspect_screen_texts', argumentsJson=json.dumps({'xml': XML}))['data']['runs']
            request = json.dumps({'edits': [{'selection': rows[0]['selection'], 'newText': 'Changed'}]})
            message = client.rejected('tia_read_extension', module='comfort', operation='edit_screen_texts', argumentsJson=request)
            assert 'preview' in message.lower(), message
            message = client.rejected('tia_preview_extension', module='comfort', operation='edit_screen_texts', targetId='unselected', path='unselected', expectedFingerprint='unselected', argumentsJson=request)
            assert 'attach' in message.lower(), message
            good_registry = (root/'external/active.json').read_bytes()
            bad = root/'bad'; shutil.copytree(bundle, bad)
            (bad/'recipes.json').write_text('[{"name":"inspect_screen_texts","xPath":"//Left","newValue":"1"}]', encoding='utf-8'); manifest(bad)
            stage(repo, bad, root)
            message = client.rejected('tia_reload_extensions', module='comfort')
            assert 'Invalid or conflicting classic XML recipe' in message, message
            assert next(m for m in client.call('tia_list_extensions')['modules'] if m['module'] == 'comfort')['generation'] == current['generation']
            assert client.call('tia_read_extension', module='comfort', operation='inspect_screen_texts', argumentsJson=json.dumps({'xml': XML}))['data']['matchedRuns'] == 5
            (root/'external/active.json').write_bytes(good_registry)
        assert client.send('tools/list', {})['tools'] == tools
        assert initial['hostProcessId'] == after['hostProcessId'] == client.process.pid
        assert not client.call('tia_get_status')['attached']
        assert hashlib.sha256(executable.read_bytes()).hexdigest() == before_hash
        return {'hostSha256': before_hash, 'fixedTools': len(tools), 'hostPidUnchanged': True, 'hostBinaryUnchanged': True,
                'olderHostRejectedNewContract': old, 'failedReloadPreservedActiveGeneration': True}
    finally:
        client.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', action='append', type=Path, default=[])
    parser.add_argument('--report', type=Path)
    args = parser.parse_args(); executable = args.executable.resolve()
    repo = Path(__file__).resolve().parent.parent
    registry = json.loads((executable.parent/'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = executable.parent/'extensions'/registry['comfort']
    checks = worker_probe(bundle); hosts = []
    with tempfile.TemporaryDirectory(prefix='comfort-screen-texts-') as temporary:
        root = Path(temporary)
        hosts.append(host_probe(executable, bundle, root/'current', repo))
        for index, old in enumerate(args.previous_host):
            hosts.append(host_probe(old.resolve(), bundle, root/f'old-{index}', repo, old=True))
    checks += ['New host reload retains PID/binary and fixed tools; rejected worker update preserves working caption inspection',
               'Mutation via read tool and native preview without explicit attachment remain blocked']
    if args.previous_host:
        checks.append('Older hosts reject the current classic-XML contract and retain their previous working Comfort generation')
    report = {'checks': checks, 'count': len(checks), 'hosts': hosts, 'projectAttached': False, 'nativeCaptionAcceptance': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
