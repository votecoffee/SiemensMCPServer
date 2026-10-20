"""Operator attestations permit upgrades without erasing original no-replay receipts."""
from datetime import UTC, datetime
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3

from integrity import digest, ordinary

FORMAT = 'siemens-request-reconciliation-v1'
ROW_FIELDS = ('request_id', 'fingerprint', 'tool_id', 'status', 'created_at', 'updated_at')


def pending_rows(state):
    journal = ordinary(Path(state)/'writes.sqlite3')
    if not journal.exists():
        return []
    with closing(sqlite3.connect(journal.as_uri()+'?mode=ro', uri=True)) as db:
        db.row_factory = sqlite3.Row
        return [dict(row) for row in db.execute("SELECT * FROM requests WHERE status!='completed'")]


def receipt_path(state, request_id):
    name = hashlib.sha256(request_id.encode('utf-8')).hexdigest()+'.json'
    return ordinary(Path(state)/'reconciliations'/name)


def valid_attestation(state, row):
    path = receipt_path(state, row['request_id'])
    if not path.exists():
        return False
    try:
        if path.stat().st_size > 64*1024:
            return False
        receipt = json.loads(path.read_bytes())
        evidence_hash = receipt['evidenceSha256']
        if not isinstance(evidence_hash, str) or len(evidence_hash) != 64 or any(c not in '0123456789abcdef' for c in evidence_hash):
            return False
        evidence = ordinary(path.parent/'evidence'/evidence_hash)
        return (receipt['format'] == FORMAT and receipt['acknowledgedOutcome'] is True
                and receipt['originalReceipt'] == {key: row[key] for key in ROW_FIELDS}
                and receipt['summary'].strip() and len(receipt['evidenceSha256']) == 64
                and all(c in '0123456789abcdef' for c in receipt['evidenceSha256'])
                and evidence.stat().st_size <= 8*1024*1024 and digest(evidence) == receipt['evidenceSha256'])
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return False


def require_reconciled(state):
    blocked = [row['request_id'] for row in pending_rows(state) if not valid_attestation(state, row)]
    if blocked:
        raise ValueError('Unreconciled write receipts prevent a runtime update: '+', '.join(blocked)
                         +'. Inspect actual app state and retain evidence before explicit reconcile-request administration.')


def reconcile(state, request_id, expected_fingerprint, evidence_path, expected_evidence_sha256, summary, acknowledged=False):
    if not acknowledged:
        raise ValueError('Reconciliation requires --acknowledge-outcome after independent app-state inspection')
    if not isinstance(request_id, str) or not 8 <= len(request_id) <= 128 or not summary or not summary.strip() or len(summary) > 4000:
        raise ValueError('Supply an exact request ID and a bounded reconciliation summary')
    if not isinstance(expected_evidence_sha256, str) or len(expected_evidence_sha256) != 64 or any(c not in '0123456789abcdef' for c in expected_evidence_sha256):
        raise ValueError('Supply the reviewed evidence SHA-256')
    state = ordinary(state)
    source = ordinary(evidence_path)
    if source.stat().st_size > 8*1024*1024:
        raise ValueError('Evidence JSON exceeds 8 MiB')
    payload = source.read_bytes()
    if hashlib.sha256(payload).hexdigest() != expected_evidence_sha256:
        raise ValueError('Evidence hash differs from the reviewed value')
    evidence = json.loads(payload)
    if not isinstance(evidence, dict) or evidence.get('requestId') != request_id or evidence.get('fingerprint') != expected_fingerprint:
        raise ValueError('Evidence must identify this exact request ID and fingerprint')
    from universal_tools.daemon import InstanceLock
    from universal_tools.ownership import OwnershipJournal
    with InstanceLock(state/'startup.lock'):
        with InstanceLock(state/'broker.lock'):
            if OwnershipJournal(state/'ownership.sqlite3').entries():
                raise ValueError('Retained ownership must be independently reconciled and recovered first')
            row = next((row for row in pending_rows(state) if row['request_id'] == request_id), None)
            if row is None or row['status'] not in {'in_progress', 'uncertain', 'app_error'} or row['fingerprint'] != expected_fingerprint or not row['tool_id'].startswith('siemens-engineering/'):
                raise ValueError('Request identity/fingerprint/status does not match the retained unresolved Siemens receipt')
            path = receipt_path(state, request_id)
            if path.exists():
                if not valid_attestation(state, row):
                    raise ValueError('An existing reconciliation differs or is damaged; retained for inspection')
                old = json.loads(path.read_bytes())
                if old['evidenceSha256'] != expected_evidence_sha256 or old['summary'] != summary:
                    raise ValueError('Existing reconciliation cannot be overwritten')
                return {'requestId': request_id, 'state': 'operator-reconciled', 'receipt': str(path), 'reused': True, 'nativeOperationPerformed': False}
            stored = ordinary(path.parent/'evidence'/expected_evidence_sha256)
            stored.parent.mkdir(parents=True, exist_ok=True)
            if stored.exists():
                if digest(stored) != expected_evidence_sha256:
                    raise ValueError('Retained evidence differs; no receipt was changed')
            else:
                with stored.open('xb') as stream:
                    stream.write(payload)
            receipt = {'format': FORMAT, 'originalReceipt': {key: row[key] for key in ROW_FIELDS},
                       'evidenceSha256': expected_evidence_sha256, 'summary': summary, 'acknowledgedOutcome': True,
                       'recordedUtc': datetime.now(UTC).isoformat(), 'originalWriteReceiptUnchanged': True,
                       'nativeOperationPerformed': False, 'replayPermitted': False}
            with path.open('x', encoding='utf-8') as stream:
                json.dump(receipt, stream, indent=2); stream.write('\n')
            return {'requestId': request_id, 'state': 'operator-reconciled', 'receipt': str(path), 'nativeOperationPerformed': False, 'originalWriteReceiptUnchanged': True, 'replayPermitted': False}
