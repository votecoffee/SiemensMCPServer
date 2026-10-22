"""Windows byte-echo and process-tree fixtures only. Never starts MCP or native engineering apps."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

from universal_tools.resources import ResourceLedger
from universal_tools.windows_resources import ProcessDriver

FRAMEWORK = Path(__file__).resolve().parents[1]/'plugin/framework'
BOOT = "import sys;sys.path.insert(0,sys.argv[1]);from stdio_supervisor import run_stdio;raise SystemExit(run_stdio([sys.executable,'-c',sys.argv[2]],config={'launchOptions':{'settings_file':sys.argv[3]}},label='Harmless process fixture',native=sys.argv[4]=='native'))"


@unittest.skipUnless(os.name == 'nt', 'Windows Job fixtures')
class ProcessTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='siemens-harmless-process-')
        self.addCleanup(temp.cleanup); self.root = Path(temp.name)
        self.env = {**os.environ, 'SIEMENS_MCP_MANAGEMENT_REGISTRY': str(self.root/'registry')}
        self.ledger = ResourceLedger(self.root/'registry')

    def command(self, code, native=False):
        return [sys.executable, '-B', '-c', BOOT, str(FRAMEWORK), code, str(self.root/'absent-policy.json'), 'native' if native else 'plain']

    def test_plain_bytes_eof_exit_status_and_durable_cleanup(self):
        code = "import sys;sys.stdout.buffer.write(sys.stdin.buffer.read());sys.stdout.buffer.flush();sys.exit(7)"
        payload = ('plain byte echo ñ 😀\n'*1000).encode()
        result = subprocess.run(self.command(code), input=payload, capture_output=True, env=self.env, timeout=20)
        self.assertEqual(result.returncode, 7, result.stderr.decode(errors='replace'))
        self.assertEqual(result.stdout, payload)
        records = self.ledger.records()
        primary = next(record for record in records if record.label == 'Harmless process fixture')
        self.assertEqual(primary.receipt.outcome, 'already_exited')
        self.assertEqual(primary.state, 'terminated')
        self.assertTrue(all(record.state in {'released', 'terminated'} for record in records))

    def test_owner_loss_closes_child_and_grandchild_with_original_identity_handles(self):
        marker = self.root/'pids.json'
        # Only bare Python fixtures are created; both remain blocked/idle.
        code = ("import subprocess,sys,json,os;from pathlib import Path;"
                "child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)']);"
                f"Path({str(marker)!r}).write_text(json.dumps([os.getpid(),child.pid]));"
                "sys.stdin.buffer.read()")
        owner = subprocess.Popen(self.command(code, native=True), stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=self.env, creationflags=subprocess.CREATE_NO_WINDOW)
        handles = []
        try:
            deadline = time.monotonic()+15
            while not marker.exists():
                self.assertIsNone(owner.poll())
                if time.monotonic() > deadline: self.fail('Harmless fixture did not become ready')
                time.sleep(0.05)
            driver = ProcessDriver()
            for pid in json.loads(marker.read_text()):
                handles.append(driver.open(driver.capture(pid)))
            record = self.ledger.records()[0]
            # A venv python.exe can redirect to a second Python process. The
            # ledger must name the actual retained supervisor, not that launcher.
            handles.append(driver.open(record.owner.model_dump()))
            self.assertIsNotNone(record.recovery_barrier)
            owner.kill(); owner.wait(timeout=10)  # exact disposable supervisor handle, never a PID/name search
            for handle in handles: self.assertTrue(handle.wait(5000), 'Owned descendant outlived Job owner')
            retained = self.ledger.get(record.resource_id)
            self.assertEqual(retained, record)
            self.assertFalse(self.ledger.plan(record.resource_id)['eligible'])
        finally:
            if owner.poll() is None: owner.kill(); owner.wait(timeout=10)
            for handle in handles: handle.close()
            owner.stdin.close(); owner.stdout.close(); owner.stderr.close()

    def test_background_profile_closes_a_harmless_tree_and_retains_native_barrier(self):
        sys.path.insert(0, str(FRAMEWORK))
        from service_supervisor import run_background
        result = run_background([sys.executable, '-c', 'import time;time.sleep(.15)'],
            config={'launchOptions': {'settings_file': str(self.root/'absent-policy.json')}}, ledger=self.ledger)
        self.assertEqual(result, 0)
        record = self.ledger.records()[0]
        self.assertEqual(record.owner.pid, os.getpid())
        self.assertEqual(record.state, 'released')
        self.assertEqual(record.containment, 'windows_job')
        self.assertIsNotNone(record.recovery_barrier)

    def test_failed_background_resume_closes_suspended_process_before_any_work(self):
        sys.path.insert(0, str(FRAMEWORK))
        from service_supervisor import run_background
        marker = self.root/'must-not-execute'
        original_create = self.ledger.driver.create_suspended
        def create(*args, **kwargs):
            group = original_create(*args, **kwargs)
            def fail(): raise OSError('Synthetic failure before resume')
            group.resume = fail
            return group
        self.ledger.driver.create_suspended = create
        with self.assertRaisesRegex(OSError, 'before resume'):
            run_background([sys.executable, '-c', f'from pathlib import Path;Path({str(marker)!r}).write_text("unexpected")'],
                config={'launchOptions': {'settings_file': str(self.root/'absent-policy.json')}}, ledger=self.ledger)
        record = self.ledger.records()[0]
        deadline = time.monotonic()+5
        while self.ledger.driver.state(record.process.model_dump()) not in {'absent', 'different'}:
            self.assertLess(time.monotonic(), deadline); time.sleep(.05)
        self.assertFalse(marker.exists())
        self.assertEqual(record.state, 'active'); self.assertIsNotNone(record.recovery_barrier)


if __name__ == '__main__':
    unittest.main()
