"""App-owned ledger inspection and reviewed retirement; no start/kill/replay route."""
import hashlib
import json
import os
from pathlib import Path

from integrity import ordinary
from universal_tools.management import private_directory
from universal_tools.resources import ResourceLedger, TERMINAL

APP_ID = 'siemens-mcp-server'


def require_startup_clear(ledger):
    if any(r.app_id == APP_ID and r.state not in TERMINAL and (r.recovery_barrier or r.kind == 'app-host') for r in ledger.records()):
        raise ValueError('Retained shared-host/native resource barriers require reviewed recovery before another service start')


def verify_shared_host(ledger=None):
    resource_id = os.environ.get('SIEMENS_SHARED_HOST_RESOURCE')
    if not resource_id:
        raise ValueError('Shared hosting requires the explicit start command and a retained resource supervisor')
    ledger = ledger or ResourceLedger(os.environ.get('SIEMENS_MCP_MANAGEMENT_REGISTRY'))
    record = ledger.get(resource_id)
    if (record.app_id != APP_ID or record.kind != 'app-host' or record.state != 'active'
            or record.containment != 'windows_job' or not record.recovery_barrier or not record.process
            or ledger.driver.state(record.owner.model_dump()) != 'alive'):
        raise ValueError('Shared-host ownership/generation is not verifiable')
    current = ledger.driver.capture(os.getpid())
    # The background profile may have a Windows Python redirector as root.
    root = record.process.model_dump()
    if current != root and ledger.driver.capture(os.getppid()) != root:
        raise ValueError('Shared-host creation identity differs from the supervised process/redirector')
    return record


def summary(ledger=None):
    ledger = ledger or ResourceLedger(os.environ.get('SIEMENS_MCP_MANAGEMENT_REGISTRY'))
    records = [r for r in ledger.records() if r.app_id == APP_ID]
    active = [r for r in records if r.state not in TERMINAL]
    return {'record_count': len(records), 'unretired_count': len(active),
            'private_count': sum(r.ownership == 'private' for r in active),
            'attached_or_shared_count': sum(r.ownership != 'private' for r in active),
            'recovery_barrier_count': sum(bool(r.recovery_barrier) for r in active),
            'uncertain_cleanup_count': sum(bool(r.receipt and r.receipt.outcome in {'dispatching', 'uncertain'}) for r in active),
            'native_state_probed': False, 'automatic_cleanup': False}


def reconcile_reviewed(resource_id, expected_revision, evidence_path, expected_sha256, acknowledged, *, ledger=None):
    if acknowledged is not True:
        raise ValueError('Resource retirement requires explicit --acknowledge-outcome and reviewed evidence')
    path = ordinary(evidence_path)
    with path.open('rb') as stream:
        raw = stream.read(65537)
    if len(raw) > 65536 or hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise ValueError('Reviewed resource evidence hash/size mismatch')
    evidence = json.loads(raw)
    if (not isinstance(evidence, dict) or evidence.get('resource_id') != resource_id
            or evidence.get('expected_revision') != expected_revision
            or not isinstance(evidence.get('summary'), str) or not 1 <= len(evidence['summary']) <= 512):
        raise ValueError('Evidence must name the exact resource/revision and independent inspection summary')
    ledger = ledger or ResourceLedger(os.environ.get('SIEMENS_MCP_MANAGEMENT_REGISTRY'))
    record = ledger.get(resource_id)
    if record.app_id != APP_ID or record.revision != expected_revision:
        raise ValueError('Resource identity/revision changed; original claim was retained')
    retained = ordinary(ledger.directory/'siemens-evidence')
    private_directory(retained)
    destination = retained/(expected_sha256+'.json')
    try:
        with destination.open('xb') as stream:
            stream.write(raw); stream.flush(); os.fsync(stream.fileno())
    except FileExistsError:
        if ordinary(destination).read_bytes() != raw:
            raise ValueError('Retained resource evidence differs; it was not overwritten') from None
    # This is an operator attestation actor, not an owner/adoption of a live .NET
    # process. The API independently refuses another live generation or a private
    # process that has not already exited. Receipts/barriers are preserved.
    actor = ledger.owner(APP_ID)
    result = actor.reconcile(resource_id, expected_revision,
        f'Reviewed evidence SHA256 {expected_sha256}; retained under siemens-evidence/{expected_sha256}.json')
    actor.close()
    return {'resource_id': result.resource_id, 'revision': result.revision, 'state': result.state,
            'evidence_sha256': expected_sha256, 'native_operation_performed': False,
            'process_terminated': False, 'engineering_journals_changed': False}
