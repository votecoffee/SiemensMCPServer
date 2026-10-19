"""Exercise Comfort package generation/reload/materialization without attaching to TIA."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

from test_extension_reload import Client

SCREEN = "<Document><Engineering version='V15.1'/><Hmi.Screen.Screen ID='0'><AttributeList><Name>TemplateScreen</Name><Number>1</Number></AttributeList><ObjectList><Hmi.Screen.IOField ID='1' CompositionName='ScreenItems'><AttributeList><Name>Field</Name><Left>10</Left></AttributeList><LinkList><Tag TargetID='@OpenLink'><Name>\"TemplateTag\"</Name></Tag></LinkList></Hmi.Screen.IOField></ObjectList></Hmi.Screen.Screen></Document>"
TABLE = "<Document><Engineering version='V15.1'/><Hmi.Tag.TagTable ID='0'><AttributeList><Name>TemplateTable</Name></AttributeList><ObjectList><Hmi.Tag.Tag ID='1' CompositionName='Tags'><AttributeList><Name>TemplateTag</Name></AttributeList><LinkList><Connection TargetID='@OpenLink'><Name>PLC_Link</Name></Connection><ControllerTag TargetID='@OpenLink'><Name>Counter</Name></ControllerTag><DataType TargetID='@OpenLink'><Name>Int</Name></DataType></LinkList></Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--worker-root', required=True, type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    registry = json.loads((args.worker_root/'active.json').read_text(encoding='utf-8-sig'))
    bundle = (args.worker_root/registry['comfort']).resolve()
    checks = []
    with tempfile.TemporaryDirectory(prefix='comfort-package-') as temporary:
        root = Path(temporary)
        client = Client(args.executable.resolve(), root)
        try:
            initial = client.call('tia_list_extensions')
            before = {m['module']: m for m in initial['modules']}
            tools_before = client.send('tools/list', {})['tools']
            installed = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(repo/'build/Install-Extension.ps1'),
                '-BundleDirectory', str(bundle), '-ExtensionRoot', str(root/'external')], capture_output=True, text=True, encoding='utf-8')
            assert installed.returncode == 0, installed.stdout+installed.stderr
            client.call('tia_reload_extensions', module='comfort')
            after = client.call('tia_list_extensions')
            comfort = next(m for m in after['modules'] if m['module'] == 'comfort')
            assert initial['hostProcessId'] == after['hostProcessId'] == client.process.pid
            assert comfort['generation'] != before['comfort']['generation']
            assert client.send('tools/list', {})['tools'] == tools_before
            assert {'build_screen_package', 'validate_package_bindings'} <= {o['name'] for o in comfort['operations']}
            checks.append('Worker reload adds operations with unchanged host PID and fixed MCP tools')
            edit = {'xPath':'//Tag/Name', 'expectedValue':'"TemplateTag"', 'newValue':'"NewTag"'}
            spec = {'resources':[
                {'kind':'screen','templateXml':SCREEN,'name':'New Screen Ü','keepTemplateMembers':False,'members':[{'prototype':'Field','name':'Value','edits':[edit]}]},
                {'kind':'tagTable','templateXml':TABLE,'name':'New Table','keepTemplateMembers':False,'members':[{'prototype':'TemplateTag','name':'NewTag'}]}
            ], 'plcSymbols':[{'connection':'PLC_Link','symbol':'Counter','dataType':'Int'}]}
            reply = client.call('tia_read_extension', module='comfort', operation='build_screen_package', argumentsJson=json.dumps(spec, ensure_ascii=False))
            package = reply['data']
            assert package['validation']['valid'] and not package['validation']['nativeBindingVerified']
            checks.append('Real net48 worker builds a Unicode package with offline binding checks')
            writer = repo/'build/Write-ComfortPackage.ps1'

            def write(payload, destination, expected=None, accepted=False):
                input_path = root/'reply.json'
                input_path.write_text(json.dumps({'data':payload}, ensure_ascii=False), encoding='utf-8')
                result = subprocess.run(['powershell.exe','-NoProfile','-File',str(writer),'-PackageJsonPath',str(input_path),
                    '-Destination',str(destination),'-ExpectedPackageSha256',expected or payload['sha256']], capture_output=True, text=True, encoding='utf-8')
                assert (result.returncode == 0) == accepted, result.stdout+result.stderr
                return result

            destination = root/'output'
            write(package, destination, accepted=True)
            for file in package['files']:
                actual = (destination/file['path']).read_bytes()
                assert actual == file['content'].encode('utf-8')
                assert hashlib.sha256(actual).hexdigest() == file['sha256']
            index = json.loads((destination/'export-index.json').read_text())
            assert index['entries'][0]['nativeType'].endswith('Hmi.Tag.TagTable')
            assert index['entries'][1]['path'].endswith('New%20Screen%20%C3%9C')
            checks.append('Materializer preserves exact UTF8 payload hashes and dependency order')
            write(package, destination)
            write(package, root/'wrong-hash', '0'*64)
            assert not (root/'wrong-hash').exists()
            tampered = copy.deepcopy(package); tampered['files'][0]['content'] += 'tamper'
            write(tampered, root/'tamper'); assert not (root/'tamper').exists()
            checks.append('Existing destinations, wrong reviewed hashes and tampered content are rejected')

            def rehash(payload):
                payload['sha256'] = hashlib.sha256('\n'.join(sorted(f["path"]+':'+f['sha256'] for f in payload['files'])).encode()).hexdigest()

            for number, bad_path in enumerate(('../escape.xml', 'C:/escape.xml', 'objects/CON/payload.xml', 'objects/x./payload.xml')):
                invalid = copy.deepcopy(package); invalid['files'][0]['path'] = bad_path; rehash(invalid)
                output = root/f'unsafe-{number}'; write(invalid, output); assert not output.exists()
            invalid = copy.deepcopy(package); invalid['files'][1]['path'] = invalid['files'][0]['path'].upper(); rehash(invalid)
            write(invalid, root/'duplicate'); assert not (root/'duplicate').exists()
            checks.append('Traversal, rooted paths, Windows aliases and case-folded duplicate paths are rejected before writes')
            spec['plcSymbols'][0]['dataType'] = 'Bool'
            message = client.rejected('tia_read_extension', module='comfort', operation='build_screen_package', argumentsJson=json.dumps(spec))
            assert 'type differs' in message
            report = client.call('tia_read_extension', module='comfort', operation='validate_package_bindings', argumentsJson=json.dumps({'resourceXml':[SCREEN], 'plcSymbols':[]}))['data']
            assert not report['valid'] and report['errors']
            checks.append('Mismatched PLC types and unresolved screen references fail through MCP')
        finally:
            client.close()
    report = {'checks':checks,'count':len(checks),'projectAttached':False,'nativeImportAccepted':False,'hostBinaryReplaced':False}
    if args.report:
        args.report.write_text(json.dumps(report,indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report,indent=2))


if __name__ == '__main__':
    main()
