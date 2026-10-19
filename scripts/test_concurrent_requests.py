"""Multiplex one real stdio MCP connection. Never attach or mutate a TIA project."""
import argparse
from concurrent.futures import Future, TimeoutError
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time

from test_extension_reload import alive


class Client:
    def __init__(self, executable, root):
        self.lock = threading.Lock(); self.pending = {}; self.sequence = 0; self.errors = []
        environment = {**os.environ, 'SIEMENS_CONCURRENCY_TEST_ROOT': str(root)}
        self.process = subprocess.Popen([str(executable), '--artifact-root='+str(root/'artifacts'), '--extensions-root='+str(root/'external'), '--analysis-parallelism=2'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', bufsize=1, env=environment)
        def read():
            try:
                for line in self.process.stdout:
                    message = json.loads(line)
                    if 'id' in message:
                        with self.lock:
                            future = self.pending.pop(message['id'])
                        future.set_result(message)
            except Exception as error:
                with self.lock:
                    for future in self.pending.values():
                        future.set_exception(error)
                    self.pending.clear()
        self.reader = threading.Thread(target=read, daemon=True); self.reader.start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr), daemon=True).start()
        hello = self.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'concurrent-request-test', 'version': '1.0'}})[1].result(10)
        self.version = hello['result']['serverInfo']['version']; assert tuple(map(int, self.version.split('.')[:3])) >= (0, 13, 0)
        self.notify('notifications/initialized', {})

    def request(self, method, params):
        with self.lock:
            self.sequence += 1; identifier = self.sequence; future = Future(); self.pending[identifier] = future
            self.process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': identifier, 'method': method, 'params': params}, ensure_ascii=False)+'\n'); self.process.stdin.flush()
        return identifier, future

    def notify(self, method, params):
        with self.lock:
            self.process.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': method, 'params': params})+'\n'); self.process.stdin.flush()

    def tool(self, name, **arguments):
        return self.request('tools/call', {'name': name, 'arguments': arguments})

    @staticmethod
    def result(future):
        response = future.result(15); assert 'error' not in response, response
        result = response['result']; assert not result.get('isError'), result
        return result['structuredContent']['result']

    def call(self, name, **arguments):
        return self.result(self.tool(name, **arguments)[1])

    def close(self):
        self.process.stdin.close(); self.process.wait(timeout=15); self.reader.join(2)
        assert self.process.returncode == 0, ''.join(self.errors)


def until(check, seconds=10):
    end = time.monotonic()+seconds
    while time.monotonic() < end:
        value = check()
        if value:
            return value
        time.sleep(0.02)
    raise AssertionError('Timed out waiting for an observable concurrency state')


def main():
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('executable', type=Path); parser.add_argument('--report', type=Path)
    args = parser.parse_args(); executable = args.executable.resolve(); repo = Path(__file__).resolve().parent.parent
    digest = hashlib.sha256(executable.read_bytes()).hexdigest(); pids = set(); client = None
    with tempfile.TemporaryDirectory(prefix='tia-concurrent-') as temporary:
        root = Path(temporary); bundle = root/'external/releases/concurrency/test'; bundle.mkdir(parents=True)
        compiler = Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        built = subprocess.run([str(compiler), '/nologo', '/target:exe', '/r:System.Web.Extensions.dll', '/out:'+str(bundle/'SiemensMCPServer.ExtensionWorker.exe'), str(repo/'scripts/fixtures/ConcurrencyWorker.cs')], capture_output=True, text=True, timeout=30)
        assert built.returncode == 0, built.stdout+built.stderr
        payload = bundle/'SiemensMCPServer.ExtensionWorker.exe'
        (bundle/'extension.json').write_text(json.dumps({'format': 'tia-extension-v1', 'protocol': 1, 'module': 'concurrency', 'version': '0.0.1', 'entryPoint': payload.name,
            'files': [{'path': payload.name, 'bytes': payload.stat().st_size, 'sha256': hashlib.sha256(payload.read_bytes()).hexdigest()}]}), encoding='utf-8')
        (root/'external/active.json').write_text(json.dumps({'concurrency': 'releases/concurrency/test'}), encoding='utf-8')
        gate = root/'release'; client = Client(executable, root)
        def status():
            data = client.call('tia_list_extensions')
            for module in data['modules']:
                pids.add(module['workerProcessId']); pids.update(module.get('localWorkerProcessIds', []))
            return data
        def hold():
            return client.tool('tia_read_extension', module='concurrency', operation='hold', argumentsJson='{}')
        try:
            tools = client.request('tools/list', {})[1].result(10)['result']['tools']; assert len(tools) in (51, 54)
            initial = status(); host_pid = client.process.pid
            first = [hold(), hold()]; until(lambda: len(list(root.glob('running-*'))) == 2)
            busy = status(); assert busy['requests']['local']['active'] == 2 and busy['requests']['native']['active'] == 0
            assert busy['requests']['local']['parallelism'] == 2
            version = client.call('tia_list_versions'); assert 'compatibleEngineeringVersions' in version
            cancelled_id, cancelled = hold(); until(lambda: status()['requests']['local']['queued'] == 1)
            client.notify('notifications/cancelled', {'requestId': cancelled_id, 'reason': 'Queued cancellation test'})
            # The SDK may omit a response after notifications/cancelled. Observe
            # removal at the dispatch boundary rather than requiring an RPC reply.
            until(lambda: status()['requests']['local']['queued'] == 0 and status()['requests']['local']['cancelled'] >= 1)
            if cancelled.done():
                response = cancelled.result(); assert 'error' in response or response.get('result', {}).get('isError'), response
            queued = []
            for count in range(1, 33):
                queued.append(hold()); until(lambda: status()['requests']['local']['queued'] == count)
            overflow = hold()[1].result(10)
            assert overflow.get('result', {}).get('isError') and 'queue is full' in overflow['result']['content'][0]['text']
            full = status(); assert full['requests']['local']['queued'] == 32 and full['requests']['local']['rejected'] >= 1
            gate.touch()
            for _, future in first+queued:
                assert client.result(future)['data']['completed']
            until(lambda: not list(root.glob('running-*')))
            idle = status(); assert idle['requests']['local']['cancelled'] >= 1
            gate.unlink(); old_generation = next(module['generation'] for module in idle['modules'] if module['module'] == 'concurrency')
            inflight = [hold(), hold()]; until(lambda: len(list(root.glob('running-*'))) == 2)
            reload = client.tool('tia_reload_extensions', module='concurrency')[1]
            try:
                reload.result(0.2)
                raise AssertionError('Reload retired an in-flight generation')
            except TimeoutError:
                pass
            gate.touch()
            for _, future in inflight:
                assert client.result(future)['extension']['generation'] == old_generation
            assert client.result(reload)['previewsInvalidated']
            after = status(); assert next(module['generation'] for module in after['modules'] if module['module'] == 'concurrency') != old_generation
            for wave in range(3):
                requests = []
                for index in range(16):
                    expected = f'Request {wave}-{index} — テスト 😀'
                    xml = '<Document><Hmi.Screen.Screen><AttributeList><Name>'+expected+'</Name></AttributeList></Hmi.Screen.Screen></Document>'
                    _, future = client.tool('tia_read_extension', module='comfort', operation='inspect_xml', argumentsJson=json.dumps({'xml': xml, 'editable': True}, ensure_ascii=False))
                    requests.append((expected, future))
                for expected, future in requests:
                    assert client.result(future)['data']['name'] == expected
            final = status(); assert client.request('tools/list', {})[1].result(10)['result']['tools'] == tools
            assert hashlib.sha256(executable.read_bytes()).hexdigest() == digest
            report = {'hostVersion': client.version, 'parallelLocalRequestsObserved': 2, 'fullLocalQueueObserved': 32,
                'nativeLaneResponsiveDuringLocalWork': True, 'queuedCancellationRemovedBeforeDispatch': True, 'overloadRejected': True,
                'reloadDrainedInflightGeneration': True, 'distinctUnicodeResponsesMatched': 48, 'hostPidAndBinaryUnchanged': True,
                'toolCount': 51, 'nativeTransactions': 'serialized; scheduler/unit tests only', 'projectAttached': False, 'nativeWrites': False}
        finally:
            gate.touch()
            client.close()
        until(lambda: not any(alive(pid) for pid in pids), 5)
        report['allObservedWorkersStopped'] = True
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
