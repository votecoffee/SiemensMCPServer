"""Real OS-lock shutdown checks in the pinned framework test environment; no native app."""
from importlib.metadata import version
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
import manage
from integrity import FRAMEWORK_VERSION
from universal_tools.daemon import InstanceLock
from universal_tools.models import BrokerError

assert version('universal-codex-tools') == FRAMEWORK_VERSION

HOLDER = """import sys
from pathlib import Path
from universal_tools.daemon import InstanceLock
with InstanceLock(Path(sys.argv[1])/'broker.lock'):
 print('locked',flush=True)
 sys.stdin.readline()
"""


class ShutdownTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='siemens-shutdown-')
        self.addCleanup(temporary.cleanup)
        self.state = Path(temporary.name)
        self.holder = subprocess.Popen([sys.executable, '-B', '-c', HOLDER, str(self.state)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(self.close)
        self.assertEqual(self.holder.stdout.readline().strip(), 'locked')

    def close(self):
        if not self.holder.stdin.closed:
            self.holder.stdin.close()
        try:
            self.holder.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.holder.kill(); self.holder.wait(timeout=5)
        self.holder.stdout.close(); self.holder.stderr.close()

    def test_restart_waits_for_previous_process_lock(self):
        def release():
            self.holder.stdin.write('\n'); self.holder.stdin.flush()
        timer = threading.Timer(0.3, release)
        timer.start()
        self.addCleanup(timer.join)
        started = time.monotonic()
        manage.wait_for_instance_exit(self.state, timeout=5)
        self.assertGreaterEqual(time.monotonic()-started, 0.25)
        self.assertEqual(self.holder.wait(timeout=5), 0)
        with InstanceLock(self.state/'broker.lock'):
            pass

    def test_busy_owner_is_retained_on_timeout(self):
        with self.assertRaisesRegex(ValueError, 'still owns its state'):
            manage.wait_for_instance_exit(self.state, timeout=0.2)
        self.assertIsNone(self.holder.poll())
        with self.assertRaises(BrokerError), InstanceLock(self.state/'broker.lock'):
            pass


if __name__ == '__main__':
    unittest.main()
