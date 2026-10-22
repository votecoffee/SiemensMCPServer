"""Pure ledger/stdio stubs; optional harmless byte-echo/owner-loss fixtures, never MCP."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
from resource_management import reconcile_reviewed, summary, verify_shared_host
from stdio_supervisor import run_stdio
from service_supervisor import run_background
from universal_tools.resources import ResourceLedger


class Driver:
    def __init__(self):
        self.processes = {}
        self.add(os.getpid(), '100')
    def add(self, pid=100001, created='200'):
        self.processes[pid] = {'host_id': 'f'*64, 'pid': pid, 'created': created, 'executable': r'c:\fixture\process.exe'}
    def capture(self, pid): return self.processes[pid]
    def state(self, identity):
        current = self.processes.get(identity['pid'])
        return 'absent' if current is None else 'alive' if current == identity else 'different'


class ResourceTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='siemens-resource-stubs-')
        self.addCleanup(temp.cleanup); self.root = Path(temp.name)
        self.driver = Driver(); self.ledger = ResourceLedger(self.root/'registry', driver=self.driver)
        policy = patch('lifetime.default_policy_path', return_value=self.root/'policy.json')
        policy.start(); self.addCleanup(policy.stop)

    def child(self, native=False, timeout=False):
        driver = self.driver
        done = threading.Event()
        self.writes = []
        writes = self.writes
        class Input:
            def write(self, value): writes.append(value)
            def flush(self): pass
            def close(self): done.set()
        class Child:
            pid = 100001
            stdin = Input()
            def poll(self): return None if not done.is_set() or timeout else 7
            def wait(self, timeout=None):
                if not done.wait(2): raise AssertionError('EOF not forwarded')
                if timeout is not None and timeout_fixture: raise subprocess.TimeoutExpired('fixture', timeout)
                driver.processes.pop(self.pid, None)
                return 7
        timeout_fixture = timeout
        def create(_command, **kwargs):
            records = self.ledger.records()
            self.assertEqual(len(records), 1)
            self.assertEqual(records[0].state, 'launching')
            self.assertEqual(bool(records[0].recovery_barrier), native)
            self.assertEqual(kwargs['stdin'], subprocess.PIPE)
            self.driver.add()
            return Child()
        return create

    def test_stdio_input_gated_until_bound_and_bytes_and_exit_code_preserved(self):
        chunks = iter([b'plain bytes\n', 'ñ 😀\n'.encode(), b''])
        contain = Mock()
        result = run_stdio(['fixture'], config={}, label='Harmless fixture', ledger=self.ledger,
            process_factory=self.child(), read=lambda: next(chunks), containment=contain)
        self.assertEqual(result, 7)
        self.assertEqual(b''.join(self.writes), b'plain bytes\n'+'ñ 😀\n'.encode())
        contain.assert_called_once_with(watch_parent=True, watch_stdin=False, max_processes=64)
        record = self.ledger.records()[0]
        self.assertEqual(record.receipt.outcome, 'already_exited')
        self.assertEqual(record.owner.pid, os.getpid())

    def test_native_exit_retains_recovery_barrier_without_effect_inference(self):
        run_stdio(['fixture'], config={}, label='Native fixture', native=True, ledger=self.ledger,
            process_factory=self.child(native=True), read=lambda: b'', containment=Mock())
        record = self.ledger.records()[0]
        self.assertEqual(record.state, 'released')
        self.assertIsNotNone(record.recovery_barrier)
        self.assertIn('outcome is not inferred', record.reconciliation.evidence)

    def test_eof_timeout_keeps_original_native_claim_and_does_not_retry(self):
        with self.assertRaises(subprocess.TimeoutExpired):
            run_stdio(['fixture'], config={}, label='Native fixture', native=True, ledger=self.ledger,
                process_factory=self.child(native=True, timeout=True), read=lambda: b'', containment=Mock(), eof_grace=1)
        record = self.ledger.records()[0]
        self.assertEqual(record.state, 'active'); self.assertIsNotNone(record.recovery_barrier)
        self.assertIsNone(record.receipt)

    def test_live_private_process_cannot_be_retired_by_reviewed_evidence(self):
        self.driver.add(); owner = self.ledger.owner('siemens-mcp-server')
        record = owner.remember_process(100001, 'Fixture', ownership='private', cleanup_mode='terminate')
        self.driver.add(os.getpid(), '300')
        evidence = self.root/'evidence.json'
        evidence.write_text(json.dumps({'resource_id': record.resource_id, 'expected_revision': record.revision, 'summary': 'Independent fixture inspection'}))
        digest = hashlib.sha256(evidence.read_bytes()).hexdigest()
        before = self.ledger.get(record.resource_id)
        with self.assertRaisesRegex(ValueError, 'private process'):
            reconcile_reviewed(record.resource_id, record.revision, evidence, digest, True, ledger=self.ledger)
        self.assertEqual(self.ledger.get(record.resource_id), before)

    def test_reviewed_retirement_preserves_original_receipt_barrier_and_write_journal(self):
        self.driver.add(); owner = self.ledger.owner('siemens-mcp-server')
        record = owner.remember_process(100001, 'Fixture', ownership='private', cleanup_mode='terminate')
        record = owner.set_recovery_barrier(record.resource_id, 'Independent native outcome inspection required')
        self.driver.processes.pop(100001); self.driver.add(os.getpid(), '300')
        journal = self.root/'original-write-journal'; journal.write_bytes(b'preserve original uncertain engineering claim')
        evidence = self.root/'evidence.json'
        evidence.write_text(json.dumps({'resource_id': record.resource_id, 'expected_revision': record.revision, 'summary': 'Independent fixture inspection confirms original process exit'}))
        digest = hashlib.sha256(evidence.read_bytes()).hexdigest()
        result = reconcile_reviewed(record.resource_id, record.revision, evidence, digest, True, ledger=self.ledger)
        after = self.ledger.get(record.resource_id)
        self.assertEqual(after.recovery_barrier, record.recovery_barrier)
        self.assertEqual(after.receipt, record.receipt)
        self.assertEqual(journal.read_bytes(), b'preserve original uncertain engineering claim')
        self.assertFalse(result['process_terminated']); self.assertFalse(result['engineering_journals_changed'])
        self.assertEqual(summary(self.ledger)['unretired_count'], 0)

    def test_background_host_is_fenced_and_durably_bound_before_resume(self):
        ledger, driver = self.ledger, self.driver
        class Group:
            pid = 100001
            def resume(inner):
                record = ledger.records()[0]
                self.assertEqual(record.kind, 'app-host')
                self.assertEqual(record.containment, 'windows_job')
                self.assertEqual(record.state, 'active')
                self.assertIsNotNone(record.recovery_barrier)
                driver.processes.pop(inner.pid)
            def identities(inner): return []
            def close(inner): pass
        def create(*_args, **_kwargs):
            record = ledger.records()[0]
            self.assertEqual(record.state, 'launching'); self.assertIsNotNone(record.recovery_barrier)
            driver.add(); return Group()
        driver.create_suspended = create
        self.assertEqual(run_background(['fixture'], config={}, ledger=ledger), 0)
        self.assertEqual(ledger.records()[0].state, 'released')
        self.assertIsNotNone(ledger.records()[0].recovery_barrier)

    def test_failed_background_creation_keeps_barrier_and_blocks_another_start(self):
        self.driver.create_suspended = Mock(side_effect=OSError('Synthetic creation failure'))
        with self.assertRaises(OSError): run_background(['fixture'], config={}, ledger=self.ledger)
        record = self.ledger.records()[0]
        self.assertEqual(record.state, 'launching'); self.assertIsNotNone(record.recovery_barrier)
        with self.assertRaisesRegex(ValueError, 'Retained'):
            run_background(['fixture'], config={}, ledger=self.ledger)
        self.assertEqual(self.driver.create_suspended.call_count, 1)

    def test_shared_host_requires_matching_live_supervisor_and_root_identity(self):
        self.driver.add(); owner = self.ledger.owner('siemens-mcp-server')
        record = owner.begin_launch('Fixture host', kind='app-host', cleanup_mode='terminate')
        owner.set_recovery_barrier(record.resource_id, 'Native state inspection required')
        record = owner.bind_process(record.resource_id, 100001, containment='windows_job')
        self.driver.add(100002, '250'); self.driver.add(100003, '250')
        with patch.dict(os.environ, {'SIEMENS_SHARED_HOST_RESOURCE': record.resource_id}), \
             patch('resource_management.os.getpid', return_value=100002), \
             patch('resource_management.os.getppid', return_value=100001):
            self.assertEqual(verify_shared_host(self.ledger).resource_id, record.resource_id)
        with patch.dict(os.environ, {'SIEMENS_SHARED_HOST_RESOURCE': record.resource_id}), \
             patch('resource_management.os.getpid', return_value=100002), \
             patch('resource_management.os.getppid', return_value=100003):
            with self.assertRaisesRegex(ValueError, 'creation identity'):
                verify_shared_host(self.ledger)


if __name__ == '__main__':
    unittest.main()
