"""Stopped-host operator evidence permits upgrades while keeping no-replay history."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
from reconciliation import reconcile, require_reconciled, receipt_path
from universal_tools.daemon import InstanceLock
from universal_tools.journal import WriteJournal
from universal_tools.models import BrokerError
from universal_tools.ownership import OwnershipJournal
import manage


class ReconciliationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='siemens-reconciliation-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.state = self.root/'state'; self.state.mkdir()
        self.request = 'synthetic-request-0001'
        self.fingerprint = 'a'*64
        self.journal = WriteJournal(self.state/'writes.sqlite3')
        self.journal.claim(self.request, self.fingerprint, 'siemens-engineering/hmi_apply_text_edits')
        self.journal.finish(self.request, 'app_error')
        self.original = self.journal.status(self.request)
        self.evidence = self.root/'evidence.json'
        self.evidence.write_text(json.dumps({'requestId': self.request, 'fingerprint': self.fingerprint, 'outcome': 'Synthetic independent readback matches original'}))
        self.hash = hashlib.sha256(self.evidence.read_bytes()).hexdigest()

    def record(self, **overrides):
        args = dict(state=self.state, request_id=self.request, expected_fingerprint=self.fingerprint,
                    evidence_path=self.evidence, expected_evidence_sha256=self.hash,
                    summary='Synthetic outcome independently inspected; original request must never replay', acknowledged=True)
        return reconcile(**{**args, **overrides})

    def test_upgrade_requires_explicit_ack_and_preserves_original_no_replay_receipt(self):
        with self.assertRaisesRegex(ValueError, 'Unreconciled'):
            require_reconciled(self.state)
        with self.assertRaisesRegex(ValueError, 'acknowledge'):
            self.record(acknowledged=False)
        result = self.record()
        self.assertFalse(result['replayPermitted'])
        require_reconciled(self.state)
        self.assertEqual(self.journal.status(self.request), self.original)
        with self.assertRaisesRegex(BrokerError, 'already claimed'):
            self.journal.claim(self.request, self.fingerprint, self.original['tool_id'])
        self.assertTrue(self.record()['reused'])

    def test_receipt_matches_exact_evidence_hash_request_and_row_state(self):
        for overrides in ({'request_id': 'another-request-0002'}, {'expected_fingerprint': 'b'*64}, {'expected_evidence_sha256': 'b'*64}):
            with self.assertRaises(ValueError):
                self.record(**overrides)
        self.record()
        self.journal.finish(self.request, 'uncertain')
        with self.assertRaisesRegex(ValueError, 'Unreconciled'):
            require_reconciled(self.state)

    def test_evidence_and_existing_attestation_cannot_be_overwritten_or_removed(self):
        self.record()
        self.evidence.write_text('unrelated replacement source')
        require_reconciled(self.state)  # Retained immutable copy is used.
        with self.assertRaisesRegex(ValueError, 'overwritten'):
            reconcile(self.state, self.request, self.fingerprint,
                      self.state/'reconciliations/evidence'/self.hash, self.hash, 'Different summary', True)
        (self.state/'reconciliations/evidence'/self.hash).write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'Unreconciled'):
            require_reconciled(self.state)

    def test_running_host_lock_rejects_administration(self):
        with InstanceLock(self.state/'broker.lock'):
            with self.assertRaises(BrokerError):
                self.record()
        self.assertFalse(receipt_path(self.state, self.request).exists())

    def test_retained_context_blocks_request_administration(self):
        ownership = OwnershipJournal(self.state/'ownership.sqlite3')
        with ownership.connect() as db:
            db.execute("INSERT INTO contexts VALUES (?, ?, ?, ?, 'lost', 'fixture', 0, 0)",
                       ('siemens-engineering', 'synthetic-context-0001', 'owner', 'manifest'))
        with self.assertRaisesRegex(ValueError, 'Retained ownership'):
            self.record()
        self.assertEqual(self.journal.status(self.request), self.original)

    def test_invalid_or_missing_evidence_never_resolves_receipt(self):
        for data in ({'requestId': 'wrong', 'fingerprint': self.fingerprint}, [], {}):
            self.evidence.write_text(json.dumps(data))
            self.hash = hashlib.sha256(self.evidence.read_bytes()).hexdigest()
            with self.assertRaises(ValueError):
                self.record()
        self.assertEqual(self.journal.status(self.request), self.original)

    def test_one_reconciled_request_does_not_release_another(self):
        self.record()
        self.journal.claim('synthetic-request-0002', 'b'*64, 'siemens-engineering/hmi_export_objects')
        with self.assertRaisesRegex(ValueError, 'synthetic-request-0002'):
            require_reconciled(self.state)

    def test_damaged_attestation_blocks_upgrade(self):
        self.record()
        receipt_path(self.state, self.request).write_text('{truncated')
        with self.assertRaisesRegex(ValueError, 'Unreconciled'):
            require_reconciled(self.state)

    def test_atomic_upgrade_holds_startup_lock_until_new_deployment_is_published(self):
        candidate = self.root/'candidate'; (candidate/'framework').mkdir(parents=True)
        manager = candidate/'framework/manage.py'; manager.write_text('synthetic')
        batch = self.root/'batch.json'
        batch.write_text(json.dumps([{'requestId': self.request, 'fingerprint': self.fingerprint,
            'evidence': str(self.evidence), 'sha256': self.hash, 'summary': 'Synthetic outcome inspected'}]))
        args = SimpleNamespace(root=self.root, payload=candidate, reconciliation_batch=batch, acknowledge_outcome=True)
        configured = []
        def publish(value, *, _startup_locked=False):
            self.assertTrue(_startup_locked)
            with self.assertRaises(BrokerError), InstanceLock(self.state/'startup.lock'):
                pass
            require_reconciled(self.state)
            configured.append(True)
        with patch.object(manage, '__file__', str(manager)), patch.object(manage, 'verify_payload'), \
             patch.object(manage, 'configure', side_effect=publish), patch('universal_tools.cli.probe', return_value=False), \
             patch('universal_tools.cli.stop') as stop, patch('universal_tools.models.load_settings'):
            result = manage.reconcile_upgrade(args, {'state': str(self.state)})
            self.assertEqual(result['state'], 'reconciled-upgrade-ready')
            stop.assert_not_called()
        self.assertEqual(configured, [True])
        self.assertEqual(self.journal.status(self.request), self.original)

    def test_atomic_upgrade_cannot_create_fresh_state_to_bypass_receipts(self):
        args = SimpleNamespace(root=self.root/'different-state', payload=self.root, reconciliation_batch=self.evidence, acknowledge_outcome=True)
        with self.assertRaisesRegex(ValueError, 'retain the existing state'):
            manage.reconcile_upgrade(args, {'state': str(self.state)})


if __name__ == '__main__':
    unittest.main()
