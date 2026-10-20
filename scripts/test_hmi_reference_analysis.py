"""Check controller import, HMI analysis/impact, file reports, and compatible host reload. Never attach to TIA."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import time

from test_extension_reload import Client, alive
from test_comfort_screen_texts import packet, stage, worker

L5X = ("<RSLogix5000Content TargetType='Controller' SoftwareRevision='35.00'><Controller Name='Demo'>"
       "<DataTypes><DataType Name='Axis'><Members><Member Name='Position' DataType='DINT' Dimension='0'/></Members></DataType></DataTypes>"
       "<Tags><Tag Name='Axes' TagType='Base' DataType='Axis' Dimensions='4'/><Tag Name='Alias' TagType='Alias' DataType='DINT' AliasFor='Axes[2].Position'/></Tags>"
       "<Programs><Program Name='Main'><Tags><Tag Name='Local' TagType='Alias' DataType='DINT' AliasFor='Alias'/></Tags></Program></Programs>"
       "</Controller></RSLogix5000Content>")
TABLE = ("<Document><Hmi.Tag.TagTable ID='0'><AttributeList><Name>Tags</Name></AttributeList><ObjectList>"
         "<Hmi.Tag.Tag ID='1'><AttributeList><Name>HMI\\Position</Name><LogicalAddress>Main:Local</LogicalAddress><AddressAccessMode>Absolute</AddressAccessMode></AttributeList>"
         "<LinkList><Connection TargetID='@OpenLink'><Name>Rockwell</Name></Connection><DataType TargetID='@OpenLink'><Name>DInt</Name></DataType></LinkList>"
         "</Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>")
SCREEN = ("<Document><Hmi.Screen.Screen ID='0'><AttributeList><Name>Overview Ü</Name></AttributeList><ObjectList>"
          "<Hmi.Screen.Button ID='1' CompositionName='ScreenItems'><AttributeList><ObjectName>Start</ObjectName></AttributeList><ObjectList>"
          "<Hmi.Event.Event ID='2' CompositionName='Events'><AttributeList><Name>Press</Name></AttributeList><LinkList><Tag TargetID='@OpenLink'><Name>HMI\\Position</Name></Tag></LinkList>"
          "</Hmi.Event.Event></ObjectList></Hmi.Screen.Button></ObjectList></Hmi.Screen.Screen></Document>")
ANALYSIS = {
    'resources': [{'hmiId': 'hmi', 'path': 'tags', 'xml': TABLE}, {'hmiId': 'hmi', 'path': 'screens/overview', 'xml': SCREEN}],
    'controllerDocuments': [{'id': 'plc', 'format': 'rockwell-l5x', 'content': L5X}],
    'connectionMappings': [{'hmiId': 'hmi', 'connection': 'Rockwell', 'controllerId': 'plc', 'addressSyntax': 'wincc-rockwell', 'expectedControllerName': 'Demo'}],
    'exportReportedComplete': True}
CHANGE = {'kind': 'controllerSymbol', 'controllerId': 'plc', 'name': 'Axes'}
OPERATIONS = ['import_controller_declarations', 'analyze_hmi_references', 'validate_hmi_bindings', 'reference_impact']


def worker_probe(bundle):
    hello, imported, analyzed, validated = worker(bundle, [
        packet(1, '$hello', {}), packet(2, OPERATIONS[0], {'controllerDocuments': ANALYSIS['controllerDocuments'], 'maxItems': 1}),
        packet(3, OPERATIONS[1], {'analysis': ANALYSIS}), packet(4, OPERATIONS[2], {'analysis': ANALYSIS})])
    catalog = {o['name']: o for o in hello['result']['operations']}
    assert all(catalog[n]['route'] == 'local' and not catalog[n]['preview'] for n in OPERATIONS)
    assert imported['result']['page']['totalItems'] == 3 and imported['result']['page']['hasMore']
    rows = analyzed['result']['page']['items']; consumer = next(r for r in rows if r['linkType'] == 'Tag')
    assert consumer['status'] == 'valid' and consumer['resolution']['canonicalSymbol'] == 'Axes[2].Position'
    assert consumer['controlName'] == 'Start' and any('Event:Press' in context for context in consumer['context'])
    digest = analyzed['result']['summary']['analysisSha256']
    assert validated['result']['summary']['analysisSha256'] == digest
    impact, stale, filesystem = worker(bundle, [
        packet(1, OPERATIONS[3], {'analysis': ANALYSIS, 'expectedAnalysisSha256': digest, 'changes': [CHANGE]}),
        packet(2, OPERATIONS[3], {'analysis': ANALYSIS, 'expectedAnalysisSha256': '0'*64, 'changes': [CHANGE]}),
        packet(3, OPERATIONS[1], {'exportDirectory': 'untrusted-path'})])
    assert impact['result']['affectedReferences'] == 2 and stale.get('error') and filesystem.get('error')
    return hello['result']['version']


def file_probe(bundle, root, large):
    export = root/'export'; export.mkdir(); entries = []
    for name, xml in [('tags', TABLE), ('screen', SCREEN)]:
        native = export/'objects'/name/'native'; native.mkdir(parents=True); (native/'payload.xml').write_text(xml, encoding='utf-8')
        entries.append({'path': name, 'sourceDirectory': native.relative_to(export).as_posix()})
    (export/'export-index.json').write_text(json.dumps({'format': 'tia-engineering-export-v1', 'family': 'classic', 'targetId': 'hmi', 'path': '', 'entries': entries, 'failures': []}), encoding='utf-8')
    controller = root/'controller.L5X'
    controller.write_text(L5X + ('<!--'+'x'*(25*1024*1024)+'-->' if large else ''), encoding='utf-8')
    source_hash = hashlib.sha256(controller.read_bytes()).hexdigest()
    request = root/'request.json'; output = root/'report'
    request.write_text(json.dumps({'exportDirectory': str(export), 'controllerFiles': [{'id': 'plc', 'format': 'rockwell-l5x', 'path': str(controller)}],
                                  'connectionMappings': ANALYSIS['connectionMappings'], 'changes': [CHANGE]}), encoding='utf-8')
    result = subprocess.run([str(bundle/'SiemensMCPServer.ExtensionWorker.exe'), '--analyze-project='+str(request), '--output='+str(output)],
                            capture_output=True, encoding='utf-8', timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout)['summary']['bindingStatuses']['valid'] == 1
    for item in json.loads((output/'manifest.json').read_text(encoding='utf-8-sig'))['files']:
        data = (output/item['path']).read_bytes(); assert len(data) == item['bytes'] and hashlib.sha256(data).hexdigest() == item['sha256']
    assert (output/'impact.csv').exists() and hashlib.sha256(controller.read_bytes()).hexdigest() == source_hash
    return {'controllerBytes': controller.stat().st_size, 'manifestVerified': True, 'sourcesPreserved': True}


def host_probe(executable, bundle, root, repo, version):
    digest = hashlib.sha256(executable.read_bytes()).hexdigest(); client = Client(executable, root); owned = set()
    try:
        before = client.call('tia_list_extensions'); initial = {m['module']: m for m in before['modules']}
        owned.update(m['workerProcessId'] for m in initial.values()); tools = client.send('tools/list', {})['tools']; assert len(tools) == 51
        stage(repo, bundle, root); client.call('tia_reload_extensions', module='comfort')
        after = client.call('tia_list_extensions'); updated = {m['module']: m for m in after['modules']}
        owned.update(m['workerProcessId'] for m in updated.values())
        assert before['hostProcessId'] == after['hostProcessId'] == client.process.pid
        assert updated['comfort']['version'] == version and initial['comfort']['generation'] != updated['comfort']['generation']
        assert all(updated[m]['generation'] == initial[m]['generation'] for m in initial if m != 'comfort')
        assert all(any(o['name'] == n for o in updated['comfort']['operations']) for n in OPERATIONS)
        result = client.call('tia_read_extension', module='comfort', operation=OPERATIONS[1], argumentsJson=json.dumps({'analysis': ANALYSIS}))['data']
        assert result['summary']['bindingStatuses']['valid'] == 1
        imported = client.call('tia_read_extension', module='comfort', operation=OPERATIONS[0], argumentsJson=json.dumps({'controllerDocuments': ANALYSIS['controllerDocuments']}))['data']
        assert imported['page']['totalItems'] == 3
        validated = client.call('tia_read_extension', module='comfort', operation=OPERATIONS[2], argumentsJson=json.dumps({'analysis': ANALYSIS}))['data']
        assert validated['summary']['analysisSha256'] == result['summary']['analysisSha256']
        impact = client.call('tia_read_extension', module='comfort', operation=OPERATIONS[3],
                            argumentsJson=json.dumps({'analysis': ANALYSIS, 'expectedAnalysisSha256': result['summary']['analysisSha256'], 'changes': [CHANGE]}))['data']
        assert impact['affectedReferences'] == 2
        assert 'fingerprint' in client.rejected('tia_read_extension', module='comfort', operation=OPERATIONS[3],
                            argumentsJson=json.dumps({'analysis': ANALYSIS, 'expectedAnalysisSha256': '0'*64, 'changes': [CHANGE]})).lower()
        assert not client.call('tia_get_status')['attached'] and client.send('tools/list', {})['tools'] == tools
        assert hashlib.sha256(executable.read_bytes()).hexdigest() == digest
        return {'hostSha256': digest, 'workerVersion': version, 'hostPidAndBinaryUnchanged': True, 'toolsAndOtherModulesUnchanged': True}
    finally:
        client.close()
        for _ in range(40):
            if not any(alive(pid) for pid in owned): break
            time.sleep(0.05)
        assert not any(alive(pid) for pid in owned)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', action='append', type=Path, default=[])
    parser.add_argument('--large-file', action='store_true')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args(); executable = args.executable.resolve(); repo = Path(__file__).resolve().parent.parent
    registry = json.loads((executable.parent/'extensions/active.json').read_text(encoding='utf-8-sig'))
    bundle = executable.parent/'extensions'/registry['comfort']; version = worker_probe(bundle)
    with tempfile.TemporaryDirectory(prefix='hmi-reference-analysis-') as temporary:
        root = Path(temporary); files = file_probe(bundle, root, args.large_file)
        hosts = [host_probe(executable, bundle, root/'current', repo, version)]
        hosts += [host_probe(old.resolve(), bundle, root/f'old-{i}', repo, version) for i, old in enumerate(args.previous_host)]
    report = {'workerVersion': version, 'fileCommand': files, 'hosts': hosts, 'projectAttached': False, 'nativeWrites': False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
