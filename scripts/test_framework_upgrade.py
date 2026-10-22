"""Prove SDK backend migration after evidence review; metadata only, no TIA context."""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import socket
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
import manage
from app_version import equivalent
from reconciliation import reconcile
from universal_tools.cli import initialize
from universal_tools.framework import ApplicationHost
from universal_tools.journal import WriteJournal
from universal_tools.models import BrokerError


async def exercise(old, new, root):
    state = root/'state'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
    initialize(state, port)
    publication = root/'application.json'
    def publish(executable, revision):
        value = {'schema_version': 1, 'id': 'siemens-upgrade-fixture', 'display_name': 'Siemens upgrade fixture',
            'instructions': 'Metadata-only synthetic upgrade test. No native project access.', 'backends': [{
                'revision': revision, 'artifact': {'path': str(executable), 'sha256': hashlib.sha256(executable.read_bytes()).hexdigest()},
                'service': {'schema_version': 1, 'id': 'siemens-engineering', 'display_name': 'Siemens metadata fixture',
                    'transport': {'type': 'stdio', 'command': str(executable), 'args': []}, 'allow_writes': False,
                    'protocol_mode': 'legacy', 'session_mode': 'exclusive', 'resource_id': 'synthetic-upgrade-resource'}}]}
        publication.write_text(json.dumps(value), encoding='utf-8')
    async def version(host):
        result = await host.client().invoke('describe_service', {'service_id': 'siemens-engineering'})
        assert not result.is_error
        return result.structured_content['initialization']['serverInfo']['version']
    publish(old, 'old-fixture')
    async with ApplicationHost(state, publication) as host:
        original_version = await version(host)
    request, fingerprint = 'synthetic-upgrade-request', 'a'*64
    journal = WriteJournal(state/'writes.sqlite3')
    journal.claim(request, fingerprint, 'siemens-engineering/hmi_apply_text_edits')
    journal.finish(request, 'app_error'); original = journal.status(request)
    publish(new, 'new-fixture')
    async with ApplicationHost(state, publication) as host:
        assert await version(host) == original_version
        assert host.publication_status()['backend_publications'][0]['reason'] == 'app_write_reconciliation_required'
    evidence = root/'reviewed.json'
    evidence.write_text(json.dumps({'requestId': request, 'fingerprint': fingerprint, 'outcome': 'Synthetic test only; no native effect occurred'}))
    reconcile(state, request, fingerprint, evidence, hashlib.sha256(evidence.read_bytes()).hexdigest(), 'Synthetic independent outcome reviewed', True)
    config = {'state': str(state), 'publication': str(publication), 'payloadRoot': str(new.parent.parent)}
    result = await manage.activate_reconciled(config, [request])
    expected = json.loads((new.parent.parent/'plugin.json').read_bytes())['version']
    async with ApplicationHost(state, publication) as host:
        assert equivalent(await version(host), expected)
    assert journal.status(request) == original
    try:
        journal.claim(request, fingerprint, original['tool_id'])
    except BrokerError:
        pass
    else:
        raise AssertionError('Original request could replay')
    return {**result, 'oldBackendVersion': original_version, 'reviewedBackendVersion': expected,
            'originalRequestRowUnchanged': True, 'replayBlocked': True, 'projectAttached': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('previous', type=Path)
    parser.add_argument('candidate', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='siemens-upgrade-') as temporary:
        result = asyncio.run(exercise(args.previous.resolve(), args.candidate.resolve(), Path(temporary)))
    if args.report:
        args.report.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result, indent=2))
