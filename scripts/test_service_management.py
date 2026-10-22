"""Synthetic tray/status/restart-barrier checks. Never attaches TIA or stops real services."""
import asyncio
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
import json
import os
from pathlib import Path
import secrets
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
import manage
from integrity import digest
from service_management import APP_ID, PLUGIN_ID, RESTART_REASON, ObservedBackend, StatusCache
from universal_tools.framework import ApplicationBackend
from universal_tools.journal import WriteJournal
from universal_tools.management import ManagedService, ManagedStatus, ManagementClient, RestartResult
from universal_tools.ownership import OwnershipJournal


class ManagementTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='siemens-management-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        registry_patch = patch.dict(os.environ, {'SIEMENS_MCP_MANAGEMENT_REGISTRY': str(self.root/'registry')})
        registry_patch.start(); self.addCleanup(registry_patch.stop)
        self.payload = self.root/'payload'; self.payload.mkdir()
        (self.payload/'plugin.json').write_text('{"version":"0.19.0"}')
        self.state = self.root/'state'; self.state.mkdir()
        self.config = self.root/'active.json'
        self.config.write_text(json.dumps({'payloadRoot': str(self.payload), 'state': str(self.state),
            'launchOptions': {'settings_file': str(self.root/'safeguards.json')}}))
        (self.root/'installed-components.json').write_text(json.dumps({'pluginId': PLUGIN_ID, 'pluginVersion': '0.19.0'}))
        self.cache = StatusCache(self.config, json.loads(self.config.read_text()))
        self.cache.refresh()
        self.registry = self.root/'registry'
        self.client = ManagementClient(self.registry)

    def agent(self):
        return ManagedService(APP_ID, 'Synthetic Siemens service', self.cache.snapshot,
                              restart=self.cache.restart, registry=self.registry, plugin_id=PLUGIN_ID)

    def restart(self, row, request=None):
        return self.client.restart(row['app_id'], row['instance_id'], row['revision'], request or secrets.token_hex(16))

    def test_versions_are_distinct_unknown_availability_and_callback_does_no_io(self):
        (self.payload/'plugin.json').write_text('{"version":"0.20.0"}')
        self.cache.refresh()
        with patch('pathlib.Path.open', side_effect=AssertionError('Status must use memory')):
            status = self.cache.snapshot()
        self.assertEqual(status.versions['app'].installed, '0.20.0')
        self.assertEqual(status.versions['app'].running, '0.19.0')
        self.assertEqual(status.versions['framework'].running, '0.20.0')
        self.assertEqual(status.versions['sdk'].running, '2.3.0')
        self.assertTrue(all(v.available is None and v.update_pending is False for v in status.versions.values()))
        self.assertFalse(status.restart_allowed)
        self.assertEqual(self.cache.restart(None), RestartResult(outcome='blocked', message=RESTART_REASON))

    def test_unknown_install_and_failed_or_stale_snapshots_stay_blocked(self):
        (self.root/'installed-components.json').unlink()
        self.cache.refresh()
        self.assertEqual(self.cache.snapshot().versions['plugin'].installed, 'unknown')
        self.config.write_text('{truncated')
        self.cache.refresh()
        self.assertEqual(self.cache.snapshot().state, 'degraded')
        self.cache.checked -= 10
        self.assertIn('stale', self.cache.snapshot().message)
        self.assertFalse(self.cache.snapshot().restart_allowed)

    def test_canonical_timestamp_install_receipt_reports_padded_identity(self):
        stamp = {'schema_version': 1, 'timezone': 'America/New_York',
            'display_version': '26.0103.000507', 'package_version': '26.103.507',
            'assembly_version': '26.103.5.7', 'file_version': '26.103.5.7'}
        (self.payload/'app-version.json').write_text(json.dumps(stamp))
        (self.payload/'plugin.json').write_text('{"version":"26.103.507"}')
        (self.root/'installed-components.json').write_text(json.dumps({'pluginId': PLUGIN_ID, 'pluginVersion': '26.103.507'}))
        cache = StatusCache(self.config, json.loads(self.config.read_text())); cache.refresh()
        component = cache.snapshot().versions['plugin']
        self.assertEqual(component.installed, '26.0103.000507')
        self.assertEqual(component.running, '26.0103.000507')
        self.assertFalse(component.update_pending)

    def test_retained_context_and_uncertain_receipt_are_reported_without_changes(self):
        ownership = OwnershipJournal(self.state/'ownership.sqlite3')
        with ownership.connect() as db:
            db.execute("INSERT INTO contexts VALUES ('service','context','owner','manifest','active','fixture',0,0)")
        journal = WriteJournal(self.state/'writes.sqlite3')
        journal.claim('synthetic-management-request', 'a'*64, 'siemens-engineering/hmi_apply_text_edits')
        journal.finish('synthetic-management-request', 'uncertain')
        before = journal.status('synthetic-management-request')
        self.cache.refresh()
        self.assertEqual(self.cache.snapshot().state, 'degraded')
        self.assertIn('1 retained context(s); 1 unreconciled request(s)', self.cache.snapshot().message)
        self.assertEqual(journal.status('synthetic-management-request'), before)
        self.assertEqual(len(ownership.entries()), 1)

    def test_all_queued_frontend_calls_are_counted_and_cancel_cleanup_runs(self):
        async def exercise():
            blocker = asyncio.Event()
            async def pending(*_args, **_kwargs):
                await blocker.wait()
            backend = object.__new__(ObservedBackend)
            backend.cache = self.cache
            backend.admission = asyncio.Lock()
            backend.draining = False
            with patch.object(ApplicationBackend, 'invoke', new=pending):
                tasks = [asyncio.create_task(backend.invoke('call_read_tool', {})) for _ in range(2)]
                await asyncio.sleep(0)
                self.assertEqual(self.cache.snapshot().active_operations, 2)
                self.assertEqual(self.cache.snapshot().state, 'busy')
                for task in tasks:
                    task.cancel()
                await asyncio.gather(*tasks, return_exceptions=True)
            self.assertEqual(self.cache.snapshot().active_operations, 0)
        asyncio.run(exercise())

    def test_real_http_specific_denial_stale_busy_duplicate_and_clean_stop(self):
        with self.agent() as agent:
            row = self.client.snapshot()['services'][0]
            self.assertEqual(row['plugin_id'], PLUGIN_ID)
            snapshot = json.dumps(self.client.snapshot())
            self.assertNotIn(agent.token, snapshot)
            self.assertNotIn('endpoint', snapshot)
            key = secrets.token_hex(16)
            first = self.restart(row, key)
            self.assertEqual(first, {'outcome': 'blocked', 'message': RESTART_REASON})
            self.assertEqual(self.restart(row, key), first)
            self.cache.begin()
            try:
                self.assertEqual(self.restart(row)['outcome'], 'blocked')
                busy = self.client.snapshot()['services'][0]
                self.assertEqual(busy['status']['active_operations'], 1)
                self.assertEqual(self.restart(busy)['outcome'], 'blocked')
                with self.assertRaises(ValueError):
                    agent.request_restart({'instance_id': agent.instance_id, 'expected_revision': busy['revision'], 'request_id': key})
            finally:
                self.cache.end()
        stopped = self.client.snapshot()['services'][0]
        self.assertEqual(stopped['status']['state'], 'stopped')
        self.assertFalse(stopped['status']['restart_allowed'])

    def test_concurrent_duplicates_keep_one_receipt_and_no_restart_effect(self):
        with self.agent() as agent, patch.object(agent, 'restart_callback') as effect:
            row = self.client.snapshot()['services'][0]
            key = secrets.token_hex(16)
            with ThreadPoolExecutor(max_workers=4) as pool:
                replies = list(pool.map(lambda _: self.restart(row, key), range(4)))
            self.assertTrue(all(r == replies[0] and r['outcome'] == 'blocked' for r in replies))
            effect.assert_not_called()
            with closing(sqlite3.connect(self.registry/'restart-journal.sqlite3')) as db:
                self.assertEqual(db.execute('SELECT count(*) FROM restarts WHERE request_id=?', (key,)).fetchone()[0], 1)

    def test_process_loss_after_effect_blocks_other_instances_without_retry(self):
        # A disposable supervisor records one independent fake effect, then dies before replying.
        effect = self.root/'synthetic-effects.txt'
        child_code = '''import os,sys
from pathlib import Path
from universal_tools.management import ManagedService,ManagedStatus
def restart(_):
 with Path(sys.argv[2]).open('a') as f:
  f.write('synthetic-effect\\n'); f.flush(); os.fsync(f.fileno())
 os._exit(9)
with ManagedService('synthetic-siemens-worker','Disposable supervisor',lambda:ManagedStatus(restart_allowed=True),restart=restart,registry=sys.argv[1]):
 print('ready',flush=True)
 sys.stdin.readline()
'''
        child = subprocess.Popen([sys.executable, '-B', '-c', child_code, str(self.registry), str(effect)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        try:
            self.assertEqual(child.stdout.readline().strip(), 'ready')
            row = self.client.snapshot()['services'][0]
            key = secrets.token_hex(16)
            self.assertEqual(self.restart(row, key)['outcome'], 'uncertain')
            self.assertEqual(child.wait(timeout=10), 9)
            with ManagedService('synthetic-siemens-worker', 'Replacement supervisor', lambda: ManagedStatus(restart_allowed=True),
                                restart=lambda _: self.fail('Uncertain restart must never dispatch'), registry=self.registry) as replacement:
                self.assertNotEqual(replacement.instance_id, row['instance_id'])
                current = next(r for r in self.client.snapshot()['services'] if r['instance_id'] == replacement.instance_id)
                self.assertIn('reconciliation', current['status']['restart_reason'])
                self.assertEqual(self.restart(current)['outcome'], 'blocked')
                self.assertEqual(replacement.pending_restarts()[0]['request_id'], key)
            self.assertEqual(effect.read_text(), 'synthetic-effect\n')
        finally:
            if child.poll() is None:
                child.kill(); child.wait(timeout=5)
            child.stdin.close(); child.stdout.close(); child.stderr.close()

    def test_upgrade_reads_verified_previous_config_only_in_explicit_new_runtime_route(self):
        publication = self.root/'application.json'; publication.write_text('{}')
        manager = self.payload/'framework/manage.py'; manager.parent.mkdir(); manager.write_text('fixture')
        config = {'schemaVersion': 1, 'frameworkVersion': '0.17.0', 'mcpPythonVersion': '2.3.0',
                  'payloadRoot': str(self.payload), 'payloadManifestSha256': 'a'*64,
                  'managerSha256': digest(manager), 'publication': str(publication),
                  'publicationSha256': digest(publication), 'python': str(self.root/'old-runtime/python.exe')}
        self.config.write_text(json.dumps(config))
        with patch.object(manage, 'verify_payload', return_value='a'*64), patch.object(manage, 'dependencies'):
            with self.assertRaisesRegex(ValueError, 'Unsupported'):
                manage.load_runtime(self.config)
            self.assertEqual(manage.load_runtime(self.config, upgrade=True), config)
            manager.write_text('tampered')
            with self.assertRaisesRegex(ValueError, 'changed'):
                manage.load_runtime(self.config, upgrade=True)


if __name__ == '__main__':
    unittest.main()
