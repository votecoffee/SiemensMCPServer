"""Standalone app-owned framework + production adapter + test-only IHmiSession; no native project access.

Builds a separate fixture against the supplied host. Requires the pinned framework
Python environment with the pinned framework wheel installed. All registrations/state are temporary.
"""
import argparse
import asyncio
from contextlib import AsyncExitStack
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time

from mcp import Client, StdioServerParameters
from universal_tools import __version__ as framework_version
from universal_tools.transport import list_all_tools
from universal_tools.cli import initialize, probe, process_options, recover_context, stop


def data(result):
    assert not result.is_error, result.model_dump_json()
    return result.structured_content or json.loads(result.content[0].text)


def app(result):
    return data(result)['result']


def wire(result):
    value = result.model_dump(mode="json", by_alias=True, exclude_none=True, exclude_unset=True)
    # .NET omits the optional false flag; SDK 2 routing emits it explicitly.
    # Normalize only this protocol default, retaining every other received field.
    value.setdefault("isError", False)
    return value


class AppHost:
    def __init__(self, root, payload):
        self.root = root
        self.payload = payload
        self.manager = payload/"framework/manage.py"
        self.config = root/"app-runtime.json"
        with socket.socket() as handle:
            handle.bind(('127.0.0.1', 0)); port = handle.getsockname()[1]
        self.settings = initialize(root, port)
        self.log = (root/'daemon.log').open('ab')
        self.process = None

    def start(self):
        self.process = subprocess.Popen([sys.executable, '-B', str(self.manager), 'serve', '--config', str(self.config)],
            stdin=subprocess.DEVNULL, stdout=self.log, stderr=self.log, **process_options())
        deadline = time.monotonic()+30
        while not probe(self.settings):
            assert self.process.poll() is None and time.monotonic() < deadline, 'Isolated broker startup failed'
            time.sleep(0.1)

    def stop(self):
        stop(self.root)
        deadline = time.monotonic()+10
        while probe(self.settings) and time.monotonic() < deadline:
            time.sleep(0.1)
        if self.process and self.process.poll() is None:
            try: self.process.wait(10)
            except subprocess.TimeoutExpired: self.process.terminate(); self.process.wait(10)


async def connect(stack, command, args, env=None):
    client = await stack.enter_async_context(Client(StdioServerParameters(command=str(command), args=args, env=env),
        mode="legacy", cache=None, read_timeout_seconds=90))
    assert client.session.initialize_result.protocol_version == "2025-11-25"
    return client


def events(root, name):
    path = root/'effects.jsonl'
    return [event for line in path.read_text(encoding='utf-8').splitlines()
            if (event := json.loads(line))['name'] == name] if path.exists() else []


async def exercise(broker, fixture, native, external):
    project = str(native/'FixtureProject.ap15_1')
    policy_arg = '--settings-file=' + str(native/'safeguards.json')
    env = {**os.environ, 'SIEMENS_FIXTURE_STATE': str(native)}
    counter = 0
    async with AsyncExitStack() as stack:
        proxy_args = ['-B', str(broker.manager), 'proxy', '--config', str(broker.config)]
        owner = await connect(stack, sys.executable, proxy_args)
        other = await connect(stack, sys.executable, proxy_args)
        direct = await connect(stack, fixture, ['--allow-writes', policy_arg, '--artifact-root='+str(native/'direct-artifacts'), '--extensions-root='+str(external)], env)
        outer = await list_all_tools(owner.session)
        assert len(outer) == 14
        data(await owner.call_tool('refresh_services', {}))
        context = data(await owner.call_tool('acquire_service', {'service_id': 'siemens-engineering'}))['context_id']

        async def call(tool, arguments=None, *, write=None, session=owner, request_id=None):
            nonlocal counter
            counter += 1
            description = data(await session.call_tool('describe_tool', {'tool_id': 'siemens-engineering/'+tool}))
            if write is None:
                write = description['invocation']['channel'] == 'call_write_tool'
            payload = {'tool_id': 'siemens-engineering/'+tool, 'catalog_revision': description['catalog_revision'],
                'arguments': arguments or {}, 'context_id': context}
            if write:
                payload['request_id'] = request_id or 'adapter-'+str(counter)
            return await session.call_tool('call_write_tool' if write else 'call_read_tool', payload)

        target = {'hmiId': 'fixture-hmi', 'listName': 'Messages_TL'}
        attach = {'processId': 4242, 'expectedProjectPath': project}
        app(await direct.call_tool('tia_attach', attach))
        assert app(await call('tia_attach', attach, write=True))['fixture']
        before = await call('hmi_read_text_list', target)
        direct_before = await direct.call_tool('hmi_read_text_list', target)
        assert wire(before) == wire(direct_before), {'broker': wire(before), 'direct': wire(direct_before)}
        initial = app(before)
        assert len(events(native, 'import-effect')) == 0

        async def preview(text, old=None):
            current = old or app(await call('hmi_read_text_list', target))
            value = next(v for v in current['values'] if v['entry'] == '7' and v['culture'] == 'en-US')
            return app(await call('hmi_preview_text_edits', {**target, 'expectedFingerprint': current['fingerprint'],
                'edits': [{'entry': '7', 'culture': 'en-US', 'expectedText': value['text'], 'newText': text}]}, write=True))

        def apply_args(plan):
            return {'planId': plan['planId'], 'candidateSha256': plan['candidateSha256']}

        first, overlapping = await preview('Reviewed café テスト 😀', initial), await preview('Conflicting edit', initial)
        assert (await call('hmi_apply_text_edits', apply_args(first), write=True, session=other)).is_error
        assert (await call('hmi_apply_text_edits', {**apply_args(first), 'candidateSha256': '0'*64}, write=True)).is_error
        assert len(events(native, 'import-effect')) == 0
        applied = app(await call('hmi_apply_text_edits', apply_args(first), write=True, request_id='first-apply'))
        assert applied['state'] == 'applied' and not applied['projectSaved']
        assert len(events(native, 'import-effect')) == 1 and not events(native, 'save')
        assert (await call('hmi_apply_text_edits', apply_args(first), write=True, request_id='first-apply')).is_error
        assert (await call('hmi_apply_text_edits', apply_args(first), write=True)).is_error
        assert (await call('hmi_apply_text_edits', apply_args(overlapping), write=True)).is_error
        assert (await call('hmi_apply_text_edits', apply_args(overlapping), write=True)).is_error
        assert len(events(native, 'import-effect')) == 1
        refreshed = data(await other.call_tool('refresh_services', {}))
        assert refreshed['tool_count'] >= 51
        assert app(await call('tia_get_status'))['attached']

        reload_plan = await preview('Invalidated by reload')
        original = app(await call('tia_list_extensions'))
        reloaded = app(await call('tia_reload_extensions', {'module': 'comfort'}, write=True))
        assert reloaded['previewsInvalidated']
        assert (await call('hmi_apply_text_edits', apply_args(reload_plan), write=True)).is_error
        after = app(await call('tia_list_extensions'))
        assert original['hostProcessId'] == after['hostProcessId']
        before_module = next(m for m in original['modules'] if m['module'] == 'comfort')
        after_module = next(m for m in after['modules'] if m['module'] == 'comfort')
        assert before_module['generation'] != after_module['generation']

        literal = 'Café テスト 😀 & <literal>\r\n'*2048
        large = await preview(literal)
        external.mkdir(parents=True, exist_ok=True)
        pointer = external/'active.json'
        pointer.write_text(json.dumps({'comfort': 'releases/comfort/missing'}), encoding='utf-8')
        try:
            assert (await call('tia_reload_extensions', {'module': 'comfort'}, write=True)).is_error
        finally:
            pointer.unlink()
        app(await call('hmi_apply_text_edits', apply_args(large), write=True))
        readback = await call('hmi_read_text_list', target)
        assert wire(readback) == wire(await direct.call_tool('hmi_read_text_list', target))
        values = app(readback)['values']
        assert next(v['text'] for v in values if v['culture'] == 'en-US') == literal
        assert next(v['text'] for v in values if v['culture'] == 'es-ES') == 'Conservado'
        assert len(events(native, 'import-effect')) == 2 and not events(native, 'save')
        app(await call('hmi_compile', {'hmiId': 'fixture-hmi'}, write=True))
        assert not events(native, 'save')
        assert (await call('tia_save_project', {'expectedProjectPath': project+'.wrong'}, write=True)).is_error
        app(await call('tia_save_project', {'expectedProjectPath': project}, write=True))
        assert len(events(native, 'save')) == 1
        app(await call('tia_reopen_project', {'expectedProjectPath': project}, write=True))
        assert not app(await call('tia_get_status'))['projectModified']

        abandoned = await preview('Do not adopt this plan')
        data(await owner.call_tool('release_service', {'service_id': 'siemens-engineering', 'context_id': context}))
        old_context = context
        context = data(await owner.call_tool('acquire_service', {'service_id': 'siemens-engineering'}))['context_id']
        assert context != old_context and not app(await call('tia_get_status'))['attached']
        app(await call('tia_attach', attach, write=True))
        assert (await call('hmi_apply_text_edits', apply_args(abandoned), write=True)).is_error
        assert len(events(native, 'import-effect')) == 2

        # Independent provider journal records the effect before process loss.
        lost = await preview('Effect recorded, reply lost')
        (native/'lose-reply').touch()
        assert (await call('hmi_apply_text_edits', apply_args(lost), write=True, request_id='lost-reply')).is_error
        assert len(events(native, 'import-effect')) == 3
        receipt = data(await owner.call_tool('get_request_status', {'request_id': 'lost-reply'}))
        assert receipt['status'] == 'uncertain'
        assert data(await owner.call_tool('service_session_status', {'service_id': 'siemens-engineering'}))['state'] == 'lost'
        assert (await owner.call_tool('acquire_service', {'service_id': 'siemens-engineering'})).is_error
        assert (await other.call_tool('acquire_service', {'service_id': 'siemens-engineering'})).is_error
        assert (await call('hmi_apply_text_edits', apply_args(lost), write=True, request_id='lost-reply')).is_error
        assert len(events(native, 'import-effect')) == 3
        return context, {'backend': 'test-only IHmiSession; Siemens assemblies forbidden', 'productionHandlersAndWorkers': True,
            'directFrameworkCompleteReadResultsEqual': True, 'defaultIsErrorFalseNormalized': True, 'unicodeReadbackCharacters': len(literal),
            'wrongOwnerHashConsumedAndStalePlansRejected': True, 'reloadInvalidatesAndFailedReloadRetainsPlans': True,
            'compileSaveReopenSeparate': True, 'releaseDiscardsSelectionAndPlans': True,
            'lostReplyReceipt': receipt['status'], 'independentImportEffects': 3, 'zeroReplay': True,
            'projectAttached': False, 'nativeWrites': False}


async def barrier_after_restart(broker):
    async with AsyncExitStack() as stack:
        client = await connect(stack, sys.executable, ['-B', str(broker.manager), 'proxy', '--config', str(broker.config)])
        assert (await client.call_tool('acquire_service', {'service_id': 'siemens-engineering'})).is_error


async def previous_host_rejects_worker(previous, current, root):
    """An old host must retain its old worker/generation instead of losing CRs."""
    external = root/'previous-external'
    registry = json.loads((current.parent/'extensions/active.json').read_text(encoding='utf-8-sig'))
    async with AsyncExitStack() as stack:
        client = await connect(stack, previous, ['--artifact-root='+str(root/'previous-artifacts'), '--extensions-root='+str(external)])
        before = app(await client.call_tool('tia_list_extensions', {}))
        comfort = next(m for m in before['modules'] if m['module'] == 'comfort')
        shutil.copytree(current.parent/'extensions'/registry['comfort'], external/'releases/comfort/literal-text')
        (external/'active.json').write_text(json.dumps({'comfort': 'releases/comfort/literal-text'}), encoding='utf-8')
        assert (await client.call_tool('tia_reload_extensions', {'module': 'comfort'})).is_error
        after = app(await client.call_tool('tia_list_extensions', {}))
        retained = next(m for m in after['modules'] if m['module'] == 'comfort')
        assert retained['generation'] == comfort['generation'] and retained['workerProcessId'] == comfort['workerProcessId']
        result = app(await client.call_tool('tia_read_extension', {'module': 'comfort', 'operation': 'inspect_xml',
            'argumentsJson': json.dumps({'xml': '<Document><Hmi.Screen.Screen><AttributeList><Name>Retained</Name></AttributeList></Hmi.Screen.Screen></Document>', 'editable': True})}))
        assert result['data']['name'] == 'Retained'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--report', type=Path)
    parser.add_argument('--previous-host', type=Path, help='Verify a pre-0.14.1 host rejects the new Comfort worker atomically')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    with tempfile.TemporaryDirectory(prefix='siemens-adapter-') as temporary:
        root = Path(temporary); fixture_dir = root/'fixture'; fixture_dir.mkdir()
        host = args.executable.resolve()
        for source in host.parent.iterdir():
            if source.is_file() and source.suffix in {'.dll', '.exe', '.config', '.json'}:
                assert not source.name.startswith('Siemens.Engineering')
                shutil.copy2(source, fixture_dir/source.name)
        shutil.copytree(host.parent/'extensions', fixture_dir/'extensions')
        build = subprocess.run(['dotnet', 'build', str(repo/'scripts/fixtures/SiemensMCPServer.Mcp.Fixture.csproj'), '-c', 'Release',
            '-p:HostDirectory='+str(host.parent), '--nologo', '-v', 'minimal', '-o', str(fixture_dir)], capture_output=True, text=True, timeout=90)
        assert build.returncode == 0, build.stdout+build.stderr
        fixture = fixture_dir/'SiemensMCPServer.Mcp.Fixture.exe'
        shutil.copy2(host.with_suffix('.exe.config'), fixture.with_suffix('.exe.config'))
        native = root/'test-native'; native.mkdir()
        policy = native/'safeguards.json'
        policy.write_text(json.dumps({'schemaVersion': 1, 'mcpWritesEnabled': True}), encoding='utf-8')
        external = root/'external'
        os.environ['SIEMENS_FIXTURE_STATE'] = str(native)
        payload = host.parent.parent
        assert json.loads((payload/'framework/wheels.json').read_bytes())['frameworkVersion'] == framework_version
        broker = AppHost(root/'app-state', payload)
        registration = {'schema_version': 1, 'id': 'siemens-engineering', 'display_name': 'Siemens adapter fixture',
            'transport': {'type': 'stdio', 'command': str(fixture), 'args': ['--allow-writes', '--settings-file='+str(policy), '--artifact-root='+str(root/'artifacts'), '--extensions-root='+str(external)],
                'env_vars': ['SIEMENS_FIXTURE_STATE']}, 'allow_writes': True,
            'allowed_write_tools': ['tia_attach', 'hmi_read_text_list', 'hmi_preview_text_edits', 'hmi_apply_text_edits', 'tia_reload_extensions', 'hmi_compile', 'tia_save_project', 'tia_reopen_project'],
            'session_mode': 'exclusive', 'resource_id': 'synthetic-siemens-resource', 'timeout_seconds': 60,
            'protocol_mode': 'legacy'}
        publication = broker.root/'application.json'
        value = {'schema_version': 1, 'id': 'siemens-mcp-server-fixture', 'display_name': 'Siemens fixture',
            'instructions': 'Isolated fake-native fixture only; preserve targets, reviewed candidates and receipts.',
            'backends': [{'revision': 'fixture-v1', 'artifact': {'path': str(fixture), 'sha256': hashlib.sha256(fixture.read_bytes()).hexdigest()}, 'service': registration}]}
        publication.write_text(json.dumps(value), encoding='utf-8')
        config = {'schemaVersion': 1, 'frameworkVersion': framework_version, 'python': sys.executable,
            'payloadRoot': str(payload), 'payloadManifestSha256': hashlib.sha256((payload/'manifest.json').read_bytes()).hexdigest(),
            'managerSha256': hashlib.sha256(broker.manager.read_bytes()).hexdigest(),
            'state': str(broker.root), 'publication': str(publication),
            'publicationSha256': hashlib.sha256(publication.read_bytes()).hexdigest()}
        broker.config.write_text(json.dumps(config), encoding='utf-8')
        try:
            broker.start()
            context, report = asyncio.run(exercise(broker, fixture, native, external))
            broker.stop(); broker.start()
            asyncio.run(barrier_after_restart(broker))
            broker.stop()
            assert len(events(native, 'import-effect')) == 3
            assert 'Effect recorded, reply lost' in (native/'native-state.xml').read_text(encoding='utf-8')
            # Synthetic state inspected above; acknowledge only this isolated fixture.
            recover_context(broker.root, 'siemens-engineering', context, acknowledged=True)
            assert len(events(native, 'import-effect')) == 3
            report.update({'durableBarrierAfterRestart': True, 'fixtureReconciliationAddsNoEffect': True,
                'hostSha256': hashlib.sha256(host.read_bytes()).hexdigest(), 'frameworkVersion': framework_version})
            if args.previous_host:
                asyncio.run(previous_host_rejects_worker(args.previous_host.resolve(), host, root))
                report['previousHostRejectedNewWorkerAndRetainedGeneration'] = True
        except BaseException:
            print((broker.root/'daemon.log').read_text(encoding='utf-8', errors='replace'), file=sys.stderr)
            raise
        finally:
            broker.stop(); broker.log.close()
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
