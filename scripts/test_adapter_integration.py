"""Exercise production standalone handlers/workers with a test-only native provider.

No Python packages, Siemens assemblies, real projects or controllers are used.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading


class Client:
    def __init__(self, fixture, native, external, name):
        self.sequence = 0
        self.messages = queue.Queue()
        self.errors = []
        self.process = subprocess.Popen([
            str(fixture), '--allow-writes', '--settings-file='+str(native/'safeguards.json'),
            '--artifact-root='+str(native/name), '--extensions-root='+str(external)],
            env={**os.environ, 'SIEMENS_FIXTURE_STATE': str(native)}, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8',
            bufsize=1, creationflags=subprocess.CREATE_NO_WINDOW)

        def read():
            try:
                for line in self.process.stdout:
                    self.messages.put(json.loads(line))
            except Exception as error:
                self.messages.put(error)
            finally:
                self.messages.put(None)

        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr), daemon=True).start()
        hello = self.send('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
            'clientInfo': {'name': 'standalone-adapter-fixture', 'version': '1.0'}})
        assert hello['protocolVersion'] == '2025-11-25'
        self.send('notifications/initialized', {}, notification=True)
        assert len(self.send('tools/list', {})['tools']) == 51

    def send(self, method, params, notification=False):
        self.sequence += 1
        packet = {'jsonrpc': '2.0', 'method': method, 'params': params}
        if not notification:
            packet['id'] = self.sequence
        self.process.stdin.write(json.dumps(packet, ensure_ascii=False)+'\n')
        self.process.stdin.flush()
        if notification:
            return
        while True:
            reply = self.messages.get(timeout=30)
            if reply is None:
                raise EOFError('Fixture exited before the reply')
            if isinstance(reply, Exception):
                raise reply
            if reply.get('id') == self.sequence:
                assert 'error' not in reply, reply
                return reply['result']

    def call(self, tool, arguments=None):
        return self.send('tools/call', {'name': tool, 'arguments': arguments or {}})

    def app(self, tool, arguments=None):
        result = self.call(tool, arguments)
        assert not result.get('isError'), result
        return result['structuredContent']['result']

    def close(self, lost=False):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
            raise AssertionError('Fixture did not close')
        assert self.process.returncode == (91 if lost else 0), ''.join(self.errors)


def effects(native, name):
    path = native/'effects.jsonl'
    return [event for line in path.read_text(encoding='utf-8').splitlines()
            if (event := json.loads(line))['name'] == name] if path.exists() else []


def exercise(fixture, native, external):
    project = str(native/'FixtureProject.ap15_1')
    attach = {'processId': 4242, 'expectedProjectPath': project}
    target = {'hmiId': 'fixture-hmi', 'listName': 'Messages_TL'}
    owner = Client(fixture, native, external, 'owner')
    peer = Client(fixture, native, external, 'peer')
    lost_reply = False
    try:
        assert owner.app('tia_attach', attach)['fixture']
        peer.app('tia_attach', attach)
        assert owner.call('hmi_read_text_list', target) == peer.call('hmi_read_text_list', target)
        initial = owner.app('hmi_read_text_list', target)

        def preview(text, previous=None):
            current = previous or owner.app('hmi_read_text_list', target)
            value = next(v for v in current['values'] if v['entry'] == '7' and v['culture'] == 'en-US')
            return owner.app('hmi_preview_text_edits', {**target, 'expectedFingerprint': current['fingerprint'],
                'edits': [{'entry': '7', 'culture': 'en-US', 'expectedText': value['text'], 'newText': text}]})

        def apply_args(plan):
            return {'planId': plan['planId'], 'candidateSha256': plan['candidateSha256']}

        first = preview('Reviewed café テスト 😀', initial)
        overlapping = preview('Conflicting edit', initial)
        assert peer.call('hmi_apply_text_edits', apply_args(first)).get('isError')
        assert owner.call('hmi_apply_text_edits', {**apply_args(first), 'candidateSha256': '0'*64}).get('isError')
        assert not effects(native, 'import-effect')
        applied = owner.app('hmi_apply_text_edits', apply_args(first))
        assert applied['state'] == 'applied' and not applied['projectSaved']
        for plan in (first, overlapping, overlapping):
            assert owner.call('hmi_apply_text_edits', apply_args(plan)).get('isError')
        assert len(effects(native, 'import-effect')) == 1 and not effects(native, 'save')

        reload_plan = preview('Invalidated by reload')
        before = owner.app('tia_list_extensions')
        assert owner.app('tia_reload_extensions', {'module': 'comfort'})['previewsInvalidated']
        assert owner.call('hmi_apply_text_edits', apply_args(reload_plan)).get('isError')
        after = owner.app('tia_list_extensions')
        assert before['hostProcessId'] == after['hostProcessId']
        assert next(m for m in before['modules'] if m['module'] == 'comfort')['generation'] != next(
            m for m in after['modules'] if m['module'] == 'comfort')['generation']

        literal = 'Café テスト 😀 & <literal>\r\n'*2048
        large = preview(literal)
        external.mkdir(parents=True, exist_ok=True)
        pointer = external/'active.json'
        pointer.write_text(json.dumps({'comfort': 'releases/comfort/missing'}), encoding='utf-8')
        try:
            assert owner.call('tia_reload_extensions', {'module': 'comfort'}).get('isError')
        finally:
            pointer.unlink()
        owner.app('hmi_apply_text_edits', apply_args(large))
        readback = owner.call('hmi_read_text_list', target)
        assert readback == peer.call('hmi_read_text_list', target)
        values = readback['structuredContent']['result']['values']
        assert next(v['text'] for v in values if v['culture'] == 'en-US') == literal
        assert next(v['text'] for v in values if v['culture'] == 'es-ES') == 'Conservado'
        assert len(effects(native, 'import-effect')) == 2 and not effects(native, 'save')
        owner.app('hmi_compile', {'hmiId': 'fixture-hmi'})
        assert not effects(native, 'save')
        assert owner.call('tia_save_project', {'expectedProjectPath': project+'.wrong'}).get('isError')
        owner.app('tia_save_project', {'expectedProjectPath': project})
        assert len(effects(native, 'save')) == 1
        owner.app('tia_reopen_project', {'expectedProjectPath': project})
        assert not owner.app('tia_get_status')['projectModified']

        abandoned = preview('Do not adopt this plan')
        owner.close()
        owner = Client(fixture, native, external, 'replacement')
        assert not owner.app('tia_get_status')['attached']
        owner.app('tia_attach', attach)
        assert owner.call('hmi_apply_text_edits', apply_args(abandoned)).get('isError')
        assert len(effects(native, 'import-effect')) == 2
        lost = preview('Effect recorded, reply lost')
        (native/'lose-reply').touch()
        try:
            owner.call('hmi_apply_text_edits', apply_args(lost))
        except EOFError:
            lost_reply = True
        else:
            raise AssertionError('Fixture must lose its reply after the independent effect')
        assert len(effects(native, 'import-effect')) == 3
        assert 'Effect recorded, reply lost' in (native/'native-state.xml').read_text(encoding='utf-8')
    finally:
        owner.close(lost=lost_reply)
        peer.close()
    replacement = Client(fixture, native, external, 'after-loss')
    try:
        assert not replacement.app('tia_get_status')['attached']
        assert len(effects(native, 'import-effect')) == 3
        replacement.app('tia_attach', attach)
        assert replacement.call('hmi_apply_text_edits', apply_args(lost)).get('isError')
        assert len(effects(native, 'import-effect')) == 3
    finally:
        replacement.close()
    return {'backend': 'test-only IHmiSession; Siemens assemblies forbidden',
        'productionHandlersAndWorkers': True, 'completeIndependentReadResultsEqual': True,
        'unicodeReadbackCharacters': len(literal), 'foreignProcessHashConsumedAndStalePlansRejected': True,
        'reloadInvalidatesAndFailedReloadRetainsPlans': True, 'compileSaveReopenSeparate': True,
        'restartDiscardsSelectionAndPlans': True, 'lostReplyObserved': lost_reply,
        'independentImportEffects': 3, 'zeroReplay': True, 'projectAttached': False, 'nativeWrites': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    host = args.executable.resolve()
    repo = Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory(prefix='siemens-adapter-') as temporary:
        root = Path(temporary)
        fixture_dir = root/'fixture'; fixture_dir.mkdir()
        for source in host.parent.iterdir():
            if source.is_file() and source.suffix in {'.dll', '.exe', '.config', '.json'}:
                assert not source.name.startswith('Siemens.Engineering')
                shutil.copy2(source, fixture_dir/source.name)
        shutil.copytree(host.parent/'extensions', fixture_dir/'extensions')
        build = subprocess.run(['dotnet', 'build', str(repo/'scripts/fixtures/SiemensMCPServer.Mcp.Fixture.csproj'),
            '-c', 'Release', '-p:HostDirectory='+str(host.parent), '--nologo', '-v', 'minimal', '-o', str(fixture_dir)],
            capture_output=True, text=True, encoding='utf-8', timeout=90)
        assert build.returncode == 0, build.stdout+build.stderr
        fixture = fixture_dir/'SiemensMCPServer.Mcp.Fixture.exe'
        shutil.copy2(host.with_suffix('.exe.config'), fixture.with_suffix('.exe.config'))
        native = root/'test-native'; native.mkdir()
        (native/'safeguards.json').write_text(json.dumps({'schemaVersion': 1, 'mcpWritesEnabled': True}), encoding='utf-8')
        report = exercise(fixture, native, root/'external')
        report['hostSha256'] = hashlib.sha256(host.read_bytes()).hexdigest()
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
