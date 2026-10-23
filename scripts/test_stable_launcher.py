"""Windows synthetic launcher canary. Uses only supplied packages and a fresh private root.

Never edits Codex settings/cache, terminates a foreign process or opens native software.
"""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time

from test_offline_lifetime import LifetimeTests

ROOT = Path(__file__).resolve().parents[1]


class Client:
    def __init__(self, exe):
        self.process = subprocess.Popen([str(exe)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, cwd=str(exe.parent.parent), creationflags=subprocess.CREATE_NO_WINDOW)
        self.messages = queue.Queue()
        self.stderr = []
        self.sequence = 0
        def read():
            try:
                for line in self.process.stdout:
                    self.messages.put(json.loads(line))
            finally:
                self.messages.put(None)
        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=lambda: self.stderr.extend(self.process.stderr), daemon=True).start()
        try:
            self.initialized = self.send('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'stable-offline-canary', 'version': '1'}})
            self.process.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); self.process.stdin.flush()
        except BaseException:
            self.close(allow_failure=True)
            raise

    def send(self, method, params):
        self.sequence += 1
        self.process.stdin.write((json.dumps({'jsonrpc': '2.0', 'id': self.sequence, 'method': method, 'params': params}, ensure_ascii=False) + '\n').encode())
        self.process.stdin.flush()
        while True:
            message = self.messages.get(timeout=20)
            if message is None:
                raise AssertionError('Launcher closed stdout: ' + b''.join(self.stderr).decode(errors='replace'))
            if message.get('id') == self.sequence:
                assert 'error' not in message, message
                return message['result']

    def call(self, name, **arguments):
        result = self.send('tools/call', {'name': name, 'arguments': arguments})
        assert not result.get('isError'), result
        return result

    def probe(self):
        tools = {item['name']: item for item in self.send('tools/list', {})['tools']}
        assert len(tools) == 3 and tools['siemens_offline_analyze']['inputSchema']['properties']['input']['type'] == 'string'
        catalog = json.loads(self.call('siemens_offline_catalog')['content'][0]['text'])
        assert catalog['version'] == self.initialized['serverInfo']['version']
        assert catalog['inputContract']['wireType'] == 'string'
        schema = json.loads(self.call('siemens_offline_describe', operation='analyze_hmi_references')['content'][0]['text'])
        assert schema['inputSchema']['properties']['maxItems']['maximum'] == 200
        xml = '<Document><Hmi.Screen.Screen ID="0"><AttributeList><Name>Café 日本語 😀</Name></AttributeList></Hmi.Screen.Screen></Document>'
        result = self.call('siemens_offline_analyze', operation='analyze_hmi_references', input=json.dumps({'analysis': {'resources': [{'path': 'Screens/overview.xml', 'xml': xml}]}}))
        assert result['structuredContent']['result']['summary']['resources'] == 1
        return catalog['version']

    def close(self, allow_failure=False):
        if not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()  # exact Popen process handle owned by this canary
            self.process.wait(timeout=5)
            raise AssertionError('Stable root did not drain on EOF')
        finally:
            self.process.stdout.close(); self.process.stderr.close()
        if not allow_failure:
            assert self.process.returncode == 0, (self.process.returncode, self.stderr)


def install(package, root, expected='', entry=None):
    # Exercise the real root CMD from another cwd, including quoted Unicode paths.
    parts = [str(entry or ROOT / 'InstallMcp.cmd'), '-Package', str(package), '-InstallRoot', str(root), '-NoPause']
    if expected:
        parts += ['-ExpectedSelectionSha256', expected]
    assert not any('"' in part or '\r' in part or '\n' in part for part in parts)
    command = 'cmd.exe /d /s /c "' + ' '.join('"' + part + '"' for part in parts) + '"'
    result = subprocess.run(command, cwd=root.parent,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, (result.returncode, result.stdout, result.stderr)
    pointer = json.loads((root / 'launcher/current.json').read_bytes())
    return json.loads((root / 'launcher/snapshots' / (pointer['snapshotSha256'] + '.json')).read_bytes())


def metadata_refusals(root, selected):
    exe = root / 'SiemensMCPServer.Launcher.exe'
    def version(expected):
        result = subprocess.run([str(exe), '--version'], capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
        assert result.returncode == expected, (result.returncode, result.stderr)
        if expected == 0:
            assert result.stdout.decode().strip() == selected['version']
        else:
            assert not result.stdout
    version(0)
    pointer = root / 'launcher/current.json'
    original = pointer.read_bytes()
    pointer.write_bytes(b'{}')
    try:
        version(1)
    finally:
        pointer.write_bytes(original)
    payload = root / selected['runtime'] / 'SiemensMCPServer.Offline.dll'
    data = payload.read_bytes()
    payload.write_bytes(b'tampered')
    try:
        version(1)
    finally:
        payload.write_bytes(data)
    snapshot = root / 'launcher/snapshots' / (json.loads(original)['snapshotSha256'] + '.json')
    held = snapshot.with_suffix('.held')
    snapshot.rename(held)
    try:
        version(1)
    finally:
        held.rename(snapshot)
    # Native junction fixture: the target and link are both inside this new root.
    runtime = root / selected['runtime']
    held_runtime = runtime.with_name(runtime.name + '.held')
    runtime.rename(held_runtime)
    try:
        environment = {**os.environ, 'SIEMENS_TEST_LINK': str(runtime), 'SIEMENS_TEST_TARGET': str(held_runtime)}
        made = subprocess.run(['powershell.exe', '-NoProfile', '-Command',
            'New-Item -ItemType Junction -Path $env:SIEMENS_TEST_LINK -Target $env:SIEMENS_TEST_TARGET | Out-Null'],
            env=environment, capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
        assert made.returncode == 0, made.stderr
        try:
            version(1)
        finally:
            os.rmdir(runtime)  # remove only the fixture junction, never its target
    finally:
        held_runtime.rename(runtime)
    version(0)
    return 'version, missing/tampered selection, payload tamper and junction refusal passed'


def run(old_package, new_package, output, metadata_only=False):
    output.mkdir(parents=True, exist_ok=False)
    root = output / 'application 日本語 & spaces'
    cache = output / 'disposable plugin cache'
    cache.mkdir()
    copied = cache / old_package.name
    shutil.copy2(old_package, copied); shutil.copy2(Path(str(old_package) + '.sha256'), Path(str(copied) + '.sha256'))
    checkout = output / 'checkout with spaces & 日本語'
    (checkout / 'build').mkdir(parents=True)
    shutil.copy2(ROOT / 'InstallMcp.cmd', checkout / 'InstallMcp.cmd')
    for name in ('Install-StableMcp.ps1', 'Install-CodexPlugin.ps1', 'Version-Common.ps1'):
        shutil.copy2(ROOT / 'build' / name, checkout / 'build' / name)
    old = install(copied, root, 'absent', checkout / 'InstallMcp.cmd')
    shutil.rmtree(cache)  # only the fresh positively identified fixture cache
    exe = root / 'SiemensMCPServer.Launcher.exe'
    refusal_report = metadata_refusals(root, old)
    original = (root / 'launcher/current.json').read_bytes()
    # Idempotent reinstallation must leave the selected bytes unchanged.
    install(old_package, root, hashlib.sha256(original).hexdigest())
    assert (root / 'launcher/current.json').read_bytes() == original
    if metadata_only:
        result = {'state': 'metadata-and-installation-passed', 'version': old['version'], 'cacheRemoval': 'passed',
            'rootCmd': 'real install from copied checkout with spaces/Unicode and another cwd', 'idempotent': True,
            'refusals': refusal_report, 'mcpProtocol': 'not run; requires an available admission slot', 'settingsChanged': False}
        (output / 'report.json').write_bytes(json.dumps(result, indent=2).encode())
        return result
    peer = Client(exe)
    try:
        assert peer.probe() == old['version']
        claim_path = next((root / 'state/active').glob('*.json'))
        original_claim = claim_path.read_bytes()
        owner = json.loads(original_claim)['owner']
        assert owner['pid'] == peer.process.pid
        assert LifetimeTests.observed_children(owner) == []
        # Fill only available slots with our own harmless clients. Existing peers
        # count toward the unchanged ceiling and are never stopped or adopted.
        extra = []
        denied = False
        try:
            for _ in range(4):
                try:
                    extra.append(Client(exe))
                except AssertionError as error:
                    assert 'capacity is unavailable' in str(error), str(error)
                    denied = True
                    break
            assert denied, 'Expected the fixed four-session admission ceiling'
        finally:
            for client in extra:
                client.close()
        # A live selected payload is held against writes/deletion until this root exits.
        payload = root / old['runtime'] / 'SiemensMCPServer.Offline.dll'
        try:
            with payload.open('r+b'):
                raise AssertionError('Live immutable payload was writable')
        except PermissionError:
            pass
        prior = (root / 'launcher/current.json').read_bytes()
        new = install(new_package, root, hashlib.sha256(prior).hexdigest())
        assert new['packageSha256'] != old['packageSha256']
        assert claim_path.read_bytes() == original_claim
        assert json.loads((root / 'launcher/current.json').read_bytes())['snapshotSha256'] != json.loads(original_claim)['snapshotSha256']
        assert peer.probe() == old['version']  # update never rebinds the live generation
        assert peer.process.poll() is None
    finally:
        peer.close()
    peer = Client(exe)
    try:
        assert peer.probe() == new['version']
        claimed = json.loads(next((root / 'state/active').glob('*.json')).read_bytes())
        assert claimed['snapshotSha256'] == json.loads((root / 'launcher/current.json').read_bytes())['snapshotSha256']
        assert LifetimeTests.observed_children(claimed['owner']) == []
        # Crash this exact owned root; reconnect must reconcile its retained claim.
        crashed_generation = claimed['generation']
        peer.process.kill(); peer.process.wait(timeout=10)
    finally:
        peer.close(allow_failure=True)
    peer = Client(exe)
    try:
        assert peer.probe() == new['version']
        outcome = json.loads((root / 'state/receipts' / crashed_generation / 'outcome.json').read_bytes())
        assert outcome['state'] == 'owner-exited-reconciled'
    finally:
        peer.close()
    # Failed publication preserves the exact prior pointer.
    original = (root / 'launcher/current.json').read_bytes()
    command = ['powershell.exe', '-NoProfile', '-File', str(ROOT / 'build/Install-StableMcp.ps1'), '-Package', str(new_package), '-InstallRoot', str(root), '-ExpectedSelectionSha256', '0' * 64, '-NoPause']
    refused = subprocess.run(command, capture_output=True, timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    assert refused.returncode != 0 and (root / 'launcher/current.json').read_bytes() == original
    # Bad pointer and payload refuse startup with no protocol output or fallback.
    pointer = root / 'launcher/current.json'
    pointer.write_bytes(b'{}')
    refused = subprocess.run([str(exe), '--version'], capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    assert refused.returncode == 1 and not refused.stdout
    pointer.write_bytes(original)
    runtime = root / new['runtime']
    data = (runtime / 'SiemensMCPServer.Offline.dll').read_bytes()
    (runtime / 'SiemensMCPServer.Offline.dll').write_bytes(b'tampered')
    refused = subprocess.run([str(exe), '--version'], capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    assert refused.returncode == 1 and not refused.stdout
    (runtime / 'SiemensMCPServer.Offline.dll').write_bytes(data)
    peer = Client(exe)
    try:
        assert peer.probe() == new['version']
    finally:
        peer.close()
    assert not list((root / 'state/active').glob('*.json'))
    result = {'state': 'passed', 'oldVersion': old['version'], 'newVersion': new['version'], 'stableCommand': str(exe),
        'cacheRemoval': 'passed', 'activePeerUpdate': 'passed; old peer readback before orderly close',
        'unicodeStdio': 'passed', 'actualChildren': [], 'exactOwnerDeathReconnect': 'passed', 'eof': 'exit 0',
        'tamperRefusal': 'passed', 'stalePublicationRefusal': 'passed', 'boundedAdmission': 'four-session ceiling, live peers preserved', 'settingsChanged': False}
    (output / 'report.json').write_bytes(json.dumps(result, indent=2).encode())
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('old_package', type=Path); parser.add_argument('new_package', type=Path); parser.add_argument('output', type=Path)
    parser.add_argument('--metadata-only', action='store_true', help='Exercise installation/version/refusal without opening an MCP session')
    args = parser.parse_args()
    print(json.dumps(run(args.old_package.resolve(), args.new_package.resolve(), args.output.resolve(), args.metadata_only), indent=2))
