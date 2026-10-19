"""Check real PLC worker/host read routes without attaching to or changing a project."""
import argparse
import hashlib
import json
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading

from test_extension_reload import Client, manifest as refresh_manifest


class Worker:
    def __init__(self, executable):
        self.process = subprocess.Popen([str(executable), '--module=plc'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding='utf-8', bufsize=1, creationflags=subprocess.CREATE_NO_WINDOW)
        self.messages = queue.Queue()
        def read():
            for line in self.process.stdout:
                self.messages.put(json.loads(line))
        threading.Thread(target=read, daemon=True).start()
        self.counter = 0

    def call(self, operation, value, rejected=False):
        self.counter += 1
        self.process.stdin.write(('\ufeff' if self.counter == 1 else '') + json.dumps({'id':self.counter, 'operation':operation, 'input':value}, ensure_ascii=False)+'\n')
        self.process.stdin.flush()
        reply = self.messages.get(timeout=30)
        assert reply['id'] == self.counter, reply
        assert bool(reply.get('error')) == rejected, reply
        return reply.get('error') if rejected else reply['result']

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill(); self.process.wait()
            raise AssertionError('Worker shutdown timed out')
        assert self.process.returncode == 0, self.process.stderr.read()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--previous-host', type=Path, help='Optional pre-0.7.3 host to check rejection/rollback of the new PLC route')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    executable = args.executable.resolve()
    extensions = executable.parent/'extensions'
    registry = json.loads((extensions/'active.json').read_text(encoding='utf-8-sig'))
    bundle = extensions/registry['plc']
    manifest = json.loads((bundle/'extension.json').read_text(encoding='utf-8-sig'))
    checks = []
    worker = Worker(bundle/manifest['entryPoint'])
    request = {'plcId':'devices/Controller/CPU', 'path':'p:BlockGroup/p:Blocks/n:Example',
        'capture':{'withDependencies':False}, 'options':{}}
    try:
        catalog = {o['name']:o for o in worker.call('$hello', {})['operations']}
        assert {catalog[name]['snapshotKind'] for name in ('read_source','find_in_source','cross_references')} == {'source','crossReferences'}
        assert all(catalog[name]['route'] == 'plcRead' and not catalog[name]['preview'] for name in ('read_source','find_in_source','cross_references'))
        source = {'text':'Count := Count + 1;\r\n// COUNT Über 😀\nEnd', 'fileSha256':'synthetic-hash'}
        request['options'] = {'maxCharacters':5}
        assert worker.call('read_source', {'phase':'validate','arguments':request})['validated']
        read = worker.call('read_source', {'phase':'snapshot','snapshot':source,'arguments':request})
        assert read['text'] == 'Count' and read['truncated']
        preserved = worker.call('read_source', {'phase':'snapshot','snapshot':{'text':'\ufeffText'},'arguments':request})
        assert preserved['text'] == '\ufeffText'
        request['options'] = {'query':'über 😀','caseSensitive':False,'maxMatches':1}
        search = worker.call('find_in_source', {'phase':'snapshot','snapshot':source,'arguments':request})
        assert search['totalMatches'] == 1 and search['matches'][0]['line'] == 2
        assert search['matches'][0]['column'] == 10 and 'Über 😀' in search['matches'][0]['context']
        request['options']['query'] = ''
        worker.call('find_in_source', {'phase':'validate','arguments':request}, rejected=True)
        checks.append('Real net48 worker accepts a first-frame UTF8 BOM, validates options and returns bounded Unicode source/search positions')
        references = {'plcId':request['plcId'],'path':request['path'], 'filter':'AllObjects', 'truncated':True,'fieldsTruncated':False,
            'rows':[{'source':{'Name':'Counter'},'reference':{'Name':'Main'},'location':{'Access':'Read'},'sourceAncestry':[]}]}
        request['capture'] = {'filter':'AllObjects','maxRows':10}; request['options'] = {}
        result = worker.call('cross_references', {'phase':'snapshot','snapshot':references,'arguments':request})
        assert result['truncated'] and result['rows'][0]['location']['Access'] == 'Read'
        checks.append('Cross-reference worker retains native display fields and truncation flags')
    finally:
        worker.close()
    with tempfile.TemporaryDirectory(prefix='plc-inspection-') as temporary:
        root = Path(temporary)
        client = Client(executable, root)
        try:
            catalog = client.call('tia_list_extensions')
            plc = next(m for m in catalog['modules'] if m['module'] == 'plc')
            tools = client.send('tools/list', {})['tools']
            assert len(tools) in (51, 54)
            assert next(t for t in tools if t['name'] == 'tia_read_extension')['annotations']['readOnlyHint'] is False
            assert {'read_source','find_in_source','cross_references'} <= {o['name'] for o in plc['operations']}
            capability = client.call('tia_get_api_capabilities')
            inspection = capability['plcInspection']
            assert inspection['readableSourceApiPresent'] and inspection['verifiedOfflineRequired']
            api_major = int(capability['apiVersion'].split('.')[0])
            if api_major in (15, 16, 17):
                assert inspection['nativeCrossReferenceApiPresent'] is False
            elif api_major in (18, 19):
                assert inspection['nativeCrossReferenceApiPresent'] is True
            request['capture'] = {'withDependencies':False}; request['options'] = {}
            rejected = client.rejected('tia_read_extension', module='plc', operation='read_source', argumentsJson=json.dumps(request))
            assert 'attach' in rejected.lower(), rejected
            request['capture']['destination'] = 'unexpected'
            assert 'destination' in client.rejected('tia_read_extension', module='plc', operation='read_source', argumentsJson=json.dumps(request))
            request['capture'] = {'withDependencies':False}; request['snapshot'] = {'text':'client injection'}
            assert 'snapshot' in client.rejected('tia_read_extension', module='plc', operation='read_source', argumentsJson=json.dumps(request))
            assert not client.call('tia_get_status')['attached']
            client.call('tia_reload_extensions', module='plc')
            after = client.call('tia_list_extensions')
            assert after['hostProcessId'] == catalog['hostProcessId'] == client.process.pid
            assert next(m for m in after['modules'] if m['module'] == 'plc')['generation'] != plc['generation']
            assert client.send('tools/list', {})['tools'] == tools
            checks.append('MCP preserves engineering schemas, reports API limits, rejects unattached reads/injected snapshots and reloads PLC in one host')
            generation = next(m for m in after['modules'] if m['module'] == 'plc')['generation']
            bad_bundle = root/'bad-bundle'; shutil.copytree(bundle, bad_bundle)
            (bad_bundle/'recipes.json').write_text('[{"name":"bad","xPath":"//Name","newValue":"x"}]',encoding='utf-8')
            refresh_manifest(bad_bundle)
            install = Path(__file__).resolve().parent.parent/'build/Install-Extension.ps1'
            staged = subprocess.run(['powershell.exe','-NoProfile','-File',str(install),'-BundleDirectory',str(bad_bundle),
                '-ExtensionRoot',str(root/'external')],capture_output=True,text=True,encoding='utf-8')
            assert staged.returncode == 0, staged.stdout+staged.stderr
            failure = client.rejected('tia_reload_extensions',module='plc')
            assert 'worker failed' in failure.lower() and 'Invalid or conflicting classic XML recipe' in failure, failure
            after = client.call('tia_list_extensions')
            assert next(m for m in after['modules'] if m['module'] == 'plc')['generation'] == generation
            checks.append('Rejected worker startup retains bounded stderr diagnostics and the previous running generation')
        finally:
            client.close()
        if args.previous_host:
            previous = root/'previous'; previous.mkdir()
            client = Client(args.previous_host.resolve(), previous)
            try:
                before = client.call('tia_list_extensions')
                generation = next(m for m in before['modules'] if m['module'] == 'plc')['generation']
                install = Path(__file__).resolve().parent.parent/'build/Install-Extension.ps1'
                staged = subprocess.run(['powershell.exe','-NoProfile','-File',str(install),'-BundleDirectory',str(bundle),
                    '-ExtensionRoot',str(previous/'external')], capture_output=True,text=True,encoding='utf-8')
                assert staged.returncode == 0, staged.stdout+staged.stderr
                assert 'incompatible' in client.rejected('tia_reload_extensions',module='plc').lower()
                after = client.call('tia_list_extensions')
                assert next(m for m in after['modules'] if m['module'] == 'plc')['generation'] == generation
                assert after['hostProcessId'] == before['hostProcessId']
                checks.append('Previous host rejects the new native route and preserves its running PLC worker/generation')
            finally:
                client.close()
    report = {'checks':checks,'count':len(checks),'executableSha256':hashlib.sha256(executable.read_bytes()).hexdigest(),
        'projectAttached':False,'nativeSourceExportAccepted':False,'nativeCrossReferenceReadAccepted':False,'engineeringWrites':False}
    if args.report:
        args.report.write_text(json.dumps(report,indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report,indent=2))


if __name__ == '__main__':
    main()
