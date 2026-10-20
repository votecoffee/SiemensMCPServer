"""App-owned framework installation/lifecycle; no Universal Tools registration route."""
import argparse
import hashlib
from contextlib import contextmanager, nullcontext
from importlib.metadata import version
import json
import os
from pathlib import Path
import queue
import socket
import subprocess
import sys
import threading
import time
import venv

from integrity import FRAMEWORK_COMMIT, FRAMEWORK_VERSION, MCP_VERSION, atomic_json, digest, ordinary, verify_payload, verify_wheels
from reconciliation import reconcile, require_reconciled

SERVICE = 'siemens-engineering'
INSTRUCTIONS = (
    'This is the standalone SiemensMCPServer app, with no Universal Tools registration. '
    'Search and describe engineering tools, then acquire_service(service_id="siemens-engineering"). '
    'Retain the returned context_id for the entire workflow. Describe each exact tool_id and '
    'use its invocation.channel and catalog_revision. Write calls also require a unique request_id. '
    'The backend has the familiar tia_*, hmi_* and plc_* operations. Preserve explicit process, '
    'project and target IDs, fingerprints, candidate hashes and reviewed plan IDs. App safeguards '
    'remain mandatory. Apply, compile and save are separate. Never replay uncertain writes. '
    'Inspect receipts and actual native state before operator recovery. Release only after work '
    'is complete or reconciled. Metadata and initialization never authorize native work.'
)


def default_root():
    return Path(os.environ['LOCALAPPDATA'])/'SiemensMCPServer/AppHost'


@contextmanager
def install_lock(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('a+b') as stream:
        if path.stat().st_size == 0:
            stream.write(b'0'); stream.flush()
        stream.seek(0)
        import msvcrt
        msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def dependencies():
    if version('universal-codex-tools') != FRAMEWORK_VERSION or version('mcp') != MCP_VERSION or version('mcp-types') != MCP_VERSION:
        raise ValueError('Install the pinned framework/SDK/wire-types versions; no dependency downgrade is permitted')
    import universal_tools, mcp
    for module in (universal_tools, mcp):
        if not Path(module.__file__).resolve().is_relative_to(Path(sys.prefix).resolve()):
            raise ValueError('Framework modules must come from the dedicated installed runtime')


def load_runtime(path):
    path = ordinary(path)
    value = json.loads(path.read_bytes())
    if value['schemaVersion'] != 1 or value['frameworkVersion'] != FRAMEWORK_VERSION:
        raise ValueError('Unsupported Siemens app runtime')
    root = ordinary(value['payloadRoot'])
    if verify_payload(root) != value['payloadManifestSha256'] or digest(root/'framework/manage.py') != value['managerSha256']:
        raise ValueError('Runtime deployment changed')
    publication = ordinary(value['publication'])
    if digest(publication) != value['publicationSha256']:
        raise ValueError('App publication changed; use an explicit app-admin update')
    if Path(sys.executable).resolve() != Path(value['python']).resolve():
        raise ValueError('Use this deployment\'s dedicated Python runtime')
    dependencies()
    return value


def backend_catalog(executable):
    process = subprocess.Popen([str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, encoding='utf-8', bufsize=1,
        creationflags=subprocess.CREATE_NO_WINDOW)
    messages = queue.Queue()
    def read():
        try:
            for line in process.stdout:
                messages.put(json.loads(line))
        except Exception as error:
            messages.put(error)
        finally:
            messages.put(None)
    threading.Thread(target=read, daemon=True).start()
    threading.Thread(target=lambda: list(process.stderr), daemon=True).start()
    def call(identifier, method, params):
        packet = {'jsonrpc': '2.0', 'method': method, 'params': params}
        if identifier is not None:
            packet['id'] = identifier
        process.stdin.write(json.dumps(packet)+'\n'); process.stdin.flush()
        if identifier is None:
            return
        while True:
            reply = messages.get(timeout=30)
            if isinstance(reply, Exception):
                raise reply
            if reply is None:
                raise EOFError('Backend exited during metadata discovery')
            if reply.get('id') == identifier:
                if 'error' in reply:
                    raise ValueError('Backend metadata discovery failed')
                return reply['result']
    try:
        hello = call(1, 'initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
            'clientInfo': {'name': 'siemens-package-catalog', 'version': '1.0'}})
        call(None, 'notifications/initialized', {})
        tools = call(2, 'tools/list', {})
        if tools.get('nextCursor') or len(tools['tools']) != 51:
            raise ValueError('Unexpected Siemens backend tool catalog')
        return {'initialization': hello, 'tools': tools['tools']}
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill(); process.wait(timeout=5)
            raise ValueError('Backend metadata process did not close')


def prepare(args):
    payload = ordinary(args.payload)
    verify_payload(payload)
    bundle = verify_wheels(payload/'framework')
    root = ordinary(args.root)
    if root.is_relative_to(payload) or payload.is_relative_to(root):
        raise ValueError('The app runtime and immutable payload must use separate directory trees')
    with install_lock(root/'install.lock'):
        runtime = root/'runtimes'/bundle[:16]
        python = runtime/'Scripts/python.exe'
        ready = runtime/'siemens-ready.json'
        if runtime.exists():
            if not ready.is_file() or json.loads(ready.read_bytes())['bundleSha256'] != bundle:
                raise ValueError('Incomplete runtime retained for inspection; use a new AppHost root')
        else:
            venv.EnvBuilder(with_pip=True).create(runtime)
            command = [str(python), '-m', 'pip', 'install', '--no-index', '--disable-pip-version-check',
                '--find-links', str(payload/'framework/wheels'), '-r', str(payload/'framework/requirements-lock.txt')]
            with (runtime/'install.log').open('w', encoding='utf-8') as log:
                subprocess.run(command, check=True, stdout=log, stderr=log)
                subprocess.run([str(python), '-m', 'pip', 'check'], check=True, stdout=log, stderr=log)
            atomic_json(ready, {'bundleSha256': bundle, 'frameworkVersion': FRAMEWORK_VERSION})
        command = [str(python), '-B', str(payload/'framework/manage.py'), 'configure',
            '--payload', str(payload), '--root', str(root)]
        if args.allow_writes:
            command.append('--allow-writes')
        for name in ('settings_file', 'artifact_root', 'extensions_root'):
            value = getattr(args, name)
            if value:
                command += ['--'+name.replace('_', '-'), str(Path(value).absolute())]
        subprocess.run(command, check=True)


def configure(args, *, _startup_locked=False):
    dependencies()
    from universal_tools.cli import initialize, probe
    from universal_tools.daemon import InstanceLock
    from universal_tools.framework import ApplicationDefinition
    from universal_tools.models import load_settings
    from universal_tools.ownership import OwnershipJournal
    payload = ordinary(args.payload)
    fingerprint = verify_payload(payload)
    root = ordinary(args.root)
    state = root/'state'
    state.mkdir(parents=True, exist_ok=True)
    launch = {'payloadRoot': str(payload), 'allowWrites': args.allow_writes,
              **{name: str(Path(getattr(args, name)).absolute()) if getattr(args, name) else None
                 for name in ('settings_file', 'artifact_root', 'extensions_root')}}
    binding = hashlib.sha256((fingerprint+json.dumps(launch, sort_keys=True)).encode('utf-8')).hexdigest()
    with nullcontext() if _startup_locked else InstanceLock(state/'startup.lock'):
        if (state/'settings.json').exists():
            settings = load_settings(state)
            if probe(settings):
                active = json.loads((root/'active.json').read_bytes())
                if active.get('deploymentBinding') == binding:
                    print(json.dumps({'state': 'framework-ready', 'config': str(root/'active.json'), 'reused': True}))
                    return
                raise ValueError('The app host is running. Finish/release work and stop this app host before upgrading; the current runtime is retained')
        else:
            with socket.socket() as handle:
                handle.bind(('127.0.0.1', 0)); port = handle.getsockname()[1]
            settings = initialize(state, port)
        with InstanceLock(state/'broker.lock'):
            if OwnershipJournal(state/'ownership.sqlite3').entries():
                raise ValueError('Retained ownership needs app outcome reconciliation before a runtime update')
            require_reconciled(state)
            settings = settings.model_copy(update={'max_request_body_bytes': 128*1024*1024})
            atomic_json(state/'settings.json', settings.model_dump(mode='json', by_alias=True))
            catalog = json.loads((payload/'framework/backend-tools.json').read_bytes())
            tools = catalog['tools']
            writes = [t['name'] for t in tools if t.get('annotations', {}).get('readOnlyHint') is not True]
            command = payload/'server/SiemensMCPServer.Mcp.exe'
            arguments = ['--allow-writes'] if args.allow_writes else []
            for name in ('settings_file', 'artifact_root', 'extensions_root'):
                value = getattr(args, name)
                if value:
                    arguments.append('--'+name.replace('_', '-')+'='+str(Path(value).absolute()))
            service = {'schema_version': 1, 'id': SERVICE, 'display_name': 'Siemens engineering',
                'description': 'TIA Portal HMI and offline PLC engineering; explicit target/review required.',
                'transport': {'type': 'stdio', 'command': str(command), 'args': arguments, 'cwd': str(command.parent)},
                'session_mode': 'exclusive', 'resource_id': 'siemens-tia-openness', 'protocol_mode': 'legacy',
                'timeout_seconds': 300, 'max_argument_bytes': 64*1024*1024,
                'allow_writes': args.allow_writes, 'allowed_write_tools': writes if args.allow_writes else []}
            publication = {'schema_version': 1, 'id': 'siemens-mcp-server', 'display_name': 'SiemensMCPServer',
                'instructions': INSTRUCTIONS, 'backends': [{'revision': 'payload-'+binding[:24],
                    'artifact': {'path': str(command), 'sha256': digest(command)}, 'service': service}]}
            ApplicationDefinition.model_validate(publication)
            release = root/'deployments'/binding[:24]
            publication_path = release/'application.json'
            if publication_path.exists() and json.loads(publication_path.read_bytes()) != publication:
                raise ValueError('An existing publication differs; retain it and use a new deployment')
            atomic_json(publication_path, publication)
            config = {'schemaVersion': 1, 'frameworkVersion': FRAMEWORK_VERSION, 'mcpPythonVersion': MCP_VERSION,
                'frameworkSourceCommit': FRAMEWORK_COMMIT, 'payloadRoot': str(payload),
                'deploymentBinding': binding, 'launchOptions': launch,
                'payloadManifestSha256': fingerprint, 'managerSha256': digest(payload/'framework/manage.py'),
                'python': sys.executable, 'state': str(state), 'publication': str(publication_path),
                'publicationSha256': digest(publication_path), 'backendTools': len(tools), 'writesEnabled': args.allow_writes}
            active = root/'active.json'
            if active.exists():
                atomic_json(root/'active.previous.json', json.loads(active.read_bytes()))
            atomic_json(active, config)
            print(json.dumps({'state': 'framework-ready', 'config': str(active), 'frameworkVersion': FRAMEWORK_VERSION,
                'mcpPythonVersion': MCP_VERSION, 'backendTools': len(tools), 'writesEnabled': args.allow_writes}))


def wait_for_instance_exit(state, timeout=35):
    from universal_tools.daemon import InstanceLock
    from universal_tools.models import BrokerError
    deadline = time.monotonic()+timeout
    while True:
        try:
            with InstanceLock(Path(state)/'broker.lock'):
                return
        except BrokerError:
            if time.monotonic() >= deadline:
                raise ValueError('Siemens app host still owns its state; no replacement was started or process forcibly stopped') from None
            time.sleep(0.1)


def ensure_running(config_path, config):
    from universal_tools.cli import probe, process_options
    from universal_tools.daemon import InstanceLock
    from universal_tools.models import load_settings
    state = Path(config['state'])
    settings = load_settings(state)
    with InstanceLock(state/'startup.lock'):
        if probe(settings):
            return
        # HTTP shutdown precedes journal/adapter teardown. Do not race the old owner.
        wait_for_instance_exit(state)
        with socket.socket() as handle:
            handle.bind((settings.host, settings.port))
        log = state/'app-host.log'
        with log.open('ab') as stream:
            child = subprocess.Popen([sys.executable, '-B', str(Path(__file__).resolve()), 'serve',
                '--config', str(config_path)], stdin=subprocess.DEVNULL, stdout=stream, stderr=stream, **process_options())
        end = time.monotonic()+35
        while time.monotonic() < end:
            if probe(settings):
                return
            if child.poll() is not None:
                raise ValueError('Siemens app host startup failed; inspect its private app-host.log')
            time.sleep(0.1)
        raise ValueError('Siemens app host startup timed out; no request was replayed')


def reconcile_upgrade(args, config):
    if not args.acknowledge_outcome or not args.reconciliation_batch or not args.payload:
        raise ValueError('Supply a reviewed --reconciliation-batch, --payload and --acknowledge-outcome')
    state = ordinary(config['state'])
    if ordinary(args.root/'state') != state:
        raise ValueError('Upgrade must retain the existing state and original replay journal')
    payload = ordinary(args.payload.resolve())
    verify_payload(payload)
    if payload != Path(__file__).resolve().parents[1]:
        raise ValueError('Run the verified candidate package manager for this payload')
    batch_path = ordinary(args.reconciliation_batch)
    if batch_path.stat().st_size > 1024*1024:
        raise ValueError('Reconciliation batch exceeds its bound')
    batch = json.loads(batch_path.read_bytes())
    if not isinstance(batch, list) or not 1 <= len(batch) <= 1000:
        raise ValueError('Supply 1..1000 exact reviewed requests')
    from universal_tools.daemon import InstanceLock
    from universal_tools.models import load_settings
    from universal_tools.ownership import OwnershipJournal
    from universal_tools.cli import probe, stop
    with InstanceLock(state/'startup.lock'):
        if OwnershipJournal(state/'ownership.sqlite3').entries():
            raise ValueError('Active/retained ownership prevents an idle-host upgrade')
        if probe(load_settings(state)):
            stop(state)
        wait_for_instance_exit(state)
        receipts = []
        for review in batch:
            receipts.append(reconcile(state, review['requestId'], review['fingerprint'], Path(review['evidence']),
                review['sha256'], review['summary'], True, _startup_locked=True))
        # Publish the new active deployment under the same startup lock. Old
        # launchers then fail their payload fingerprint check, rather than
        # restarting the retired deployment between reconciliation and install.
        configure(args, _startup_locked=True)
        return {'state': 'reconciled-upgrade-ready', 'requests': receipts, 'nativeOperationPerformed': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['prepare', 'configure', 'catalog', 'serve', 'proxy', 'status', 'stop', 'contexts', 'reconcile-request', 'reconcile-upgrade'])
    parser.add_argument('--payload', type=Path)
    parser.add_argument('--root', type=Path, default=default_root())
    parser.add_argument('--config', type=Path)
    parser.add_argument('--executable', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--allow-writes', action='store_true')
    parser.add_argument('--settings-file', type=Path)
    parser.add_argument('--artifact-root', type=Path)
    parser.add_argument('--extensions-root', type=Path)
    parser.add_argument('--request-id')
    parser.add_argument('--expected-fingerprint')
    parser.add_argument('--evidence-json', type=Path)
    parser.add_argument('--expected-evidence-sha256')
    parser.add_argument('--summary')
    parser.add_argument('--acknowledge-outcome', action='store_true')
    parser.add_argument('--reconciliation-batch', type=Path)
    args = parser.parse_args()
    if args.command == 'catalog':
        atomic_json(args.output, backend_catalog(args.executable.resolve()))
        return
    if args.command == 'prepare':
        prepare(args)
        return
    if args.command == 'configure':
        configure(args)
        return
    config_path = args.config or args.root/'active.json'
    config = load_runtime(config_path)
    from universal_tools.cli import list_contexts, probe, stop
    from universal_tools.models import load_settings
    state = Path(config['state'])
    if args.command == 'reconcile-upgrade':
        print(json.dumps(reconcile_upgrade(args, config), indent=2))
    elif args.command == 'reconcile-request':
        if not args.evidence_json:
            raise ValueError('Supply --evidence-json for the independently reviewed outcome')
        print(json.dumps(reconcile(state, args.request_id, args.expected_fingerprint, args.evidence_json,
            args.expected_evidence_sha256, args.summary, args.acknowledge_outcome), indent=2))
    elif args.command == 'serve':
        from universal_tools.framework import serve_application
        serve_application(state, Path(config['publication']))
    elif args.command == 'proxy':
        from universal_tools.surface import RemoteBackend, create_surface
        ensure_running(config_path, config)
        create_surface(RemoteBackend(load_settings(state)), name='SiemensMCPServer', instructions=INSTRUCTIONS).run(transport='stdio')
    else:
        result = {'status': lambda: probe(load_settings(state)) or {'running': False},
            'stop': lambda: stop(state), 'contexts': lambda: list_contexts(state)}[args.command]()
        if args.command == 'stop':
            wait_for_instance_exit(state)
        print(json.dumps(result, indent=2))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Siemens app framework: '+str(error), file=sys.stderr)
        raise SystemExit(1)
