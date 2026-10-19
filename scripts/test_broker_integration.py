"""Exercise Siemens through an isolated Universal Tools broker; never attach to TIA.

Run with the broker's Python environment (mcp/jsonschema/universal_tools installed).
No real broker registrations, Codex settings or plugin installations are changed.
"""
import argparse
import asyncio
from contextlib import AsyncExitStack
import hashlib
import json
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

import jsonschema
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from universal_tools.cli import initialize, probe, process_options, stop


def data(result):
    assert not result.isError, result.model_dump_json()
    return result.structuredContent or json.loads(result.content[0].text)


async def check(state, broker_source):
    async with AsyncExitStack() as stack:
        async def connect():
            pipes = await stack.enter_async_context(stdio_client(StdioServerParameters(
                command=sys.executable, args=['-m', 'universal_tools', '--state', str(state), 'proxy'])))
            session = await stack.enter_async_context(ClientSession(*pipes))
            await session.initialize()
            return session
        owner, other = await connect(), await connect()
        assert len((await owner.list_tools()).tools) == 14
        refreshed = data(await owner.call_tool('refresh_services', {}))
        matches = data(await owner.call_tool('search_tools', {'query': 'workflow', 'kind': 'capability'}))['matches']
        assert len(matches) == 4 and all(not item['callable'] for item in matches), (matches, refreshed)
        context = data(await owner.call_tool('acquire_service', {'service_id': 'siemens-mcp-server'}))['context_id']
        assert (await other.call_tool('acquire_service', {'service_id': 'siemens-mcp-server'})).isError
        data(await owner.call_tool('refresh_capabilities', {'service_id': 'siemens-mcp-server', 'context_id': context}))
        matches = data(await owner.call_tool('search_tools', {'query': 'workflow', 'kind': 'capability'}))['matches']

        async def read(tool, arguments, session=owner):
            tool_id = 'siemens-mcp-server/' + tool
            description = data(await session.call_tool('describe_tool', {'tool_id': tool_id}))
            return await session.call_tool('call_read_tool', {'tool_id': tool_id, 'catalog_revision': description['catalog_revision'],
                'arguments': arguments, 'context_id': context})

        schema = json.loads((broker_source/'examples/workflow.schema.json').read_text(encoding='utf-8'))
        guides, references = 0, 0
        for match in matches:
            capability = data(await owner.call_tool('describe_capability', {'service_id': 'siemens-mcp-server',
                'capability_id': match['capability_id'], 'capability_revision': match['capability_revision']}))
            discovery = capability['discovery']; arguments = discovery['arguments']
            guide = data(await read('tia_read_workflow', arguments))
            jsonschema.Draft202012Validator(schema).validate(guide)
            instructions = guide['instructions']
            assert hashlib.sha256(instructions['text'].encode('utf-8')).hexdigest() == instructions['sha256']
            for tool in guide['tools']:
                data(await owner.call_tool('describe_tool', {'tool_id': 'siemens-mcp-server/'+tool['tool']}))
            for reference in guide['references']:
                content = data(await read(reference['read']['tool'], reference['read']['arguments']))
                assert hashlib.sha256(content['content'].encode('utf-8')).hexdigest() == reference['sha256']
                references += 1
            assert (await read('tia_read_workflow', {**arguments, 'expectedRevision': 'stale'})).isError
            assert (await read('tia_read_workflow', arguments, other)).isError
            guides += 1
        # The normal app result envelope and target write policy remain unchanged.
        versions = data(await read('tia_list_versions', {}))['result']
        assert versions['writesEnabled'] is False
        attach = data(await owner.call_tool('describe_tool', {'tool_id': 'siemens-mcp-server/tia_attach'}))
        assert attach['invocation']['channel'] == 'call_write_tool' and attach['invocation']['registration_blocker'], attach['invocation']
        assert (await read('tia_attach', {'processId': 1, 'expectedProjectPath': 'not-a-project'})).isError
        data(await owner.call_tool('release_service', {'service_id': 'siemens-mcp-server', 'context_id': context}))
        return {'workflowGuides': guides, 'referenceReadbacks': references, 'staleGuidesRejected': True,
                'competingFrontendBlocked': True, 'writesDisabled': True, 'brokerTools': 14,
                'standalonePluginRequiredByTest': False, 'projectAttached': False, 'nativeWrites': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--broker-source', required=True, type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='siemens-broker-') as temporary:
        state = Path(temporary)
        with socket.socket() as socket_handle:
            socket_handle.bind(('127.0.0.1', 0)); port = socket_handle.getsockname()[1]
        settings = initialize(state, port)
        descriptor = {'schema_version': 1, 'id': 'siemens-mcp-server', 'display_name': 'SiemensMCPServer',
            'transport': {'type': 'stdio', 'command': str(args.executable.resolve()),
                'args': ['--artifact-root='+str(state/'artifacts'), '--extensions-root='+str(state/'extensions')]},
            'allow_writes': False, 'allowed_write_tools': [], 'session_mode': 'exclusive', 'resource_id': 'siemens-tia-openness',
            'timeout_seconds': 60, 'capability_catalog': {'tool': 'tia_list_broker_capabilities', 'arguments': {}}}
        (state/'services/siemens-mcp-server.json').write_text(json.dumps(descriptor), encoding='utf-8')
        with (state/'daemon.log').open('wb') as log:
            process = subprocess.Popen([sys.executable, '-m', 'universal_tools', '--state', str(state), 'serve'],
                stdin=subprocess.DEVNULL, stdout=log, stderr=log, **process_options())
            try:
                deadline = time.monotonic()+30
                while not probe(settings):
                    assert process.poll() is None and time.monotonic() < deadline, 'Isolated broker did not start'
                    time.sleep(0.1)
                report = asyncio.run(check(state, args.broker_source.resolve()))
            except BaseException:
                print((state/'daemon.log').read_text(encoding='utf-8', errors='replace'), file=sys.stderr)
                raise
            finally:
                stop(state)
                deadline = time.monotonic()+10
                while probe(settings) and time.monotonic() < deadline:
                    time.sleep(0.1)
                if process.poll() is None:
                    process.terminate()
                    try: process.wait(10)
                    except subprocess.TimeoutExpired: process.kill(); process.wait(10)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
