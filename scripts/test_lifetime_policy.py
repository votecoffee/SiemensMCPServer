"""Pure in-process policy/admission/ASGI tests. No servers, workers or apps are launched.

OS process-tree, parent-loss and crash behavior require separate isolated acceptance.
"""
import asyncio
from contextlib import ExitStack
from pathlib import Path
from types import SimpleNamespace
import sys
import tempfile
import threading
import unittest
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
import lifetime
import manage
from service_management import ObservedBackend, SafeShutdown
from universal_tools.framework import ApplicationBackend


class Counter:
    def __init__(self): self.active = 0
    def begin(self): self.active += 1
    def end(self): self.active -= 1
    def snapshot(self): return SimpleNamespace(active_operations=self.active)


def backend():
    result = object.__new__(ObservedBackend)
    result.cache, result.admission, result.draining = Counter(), asyncio.Lock(), False
    return result


class FrontendPolicyTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='siemens-policy-')
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)

    def test_slots_bound_connections_release_and_count_slots_above_lowered_limit(self):
        with ExitStack() as held:
            first = held.enter_context(lifetime.FrontendSlot(self.root, 2))
            second = held.enter_context(lifetime.FrontendSlot(self.root, 2))
            with self.assertRaisesRegex(ValueError, 'limit reached'):
                with lifetime.FrontendSlot(self.root, 2): self.fail('Over limit')
            first.__exit__()
            # The remaining connection occupies slot 1. Lowering the ceiling to
            # one must count it, rather than only scanning slot zero.
            with self.assertRaisesRegex(ValueError, 'limit reached'):
                with lifetime.FrontendSlot(self.root, 1): self.fail('Ignored high slot')
            self.assertFalse(second.stream.closed)
        with lifetime.FrontendSlot(self.root, 1):
            pass

    def test_failed_admission_releases_every_temporary_lock(self):
        with lifetime.FrontendSlot(self.root, 1):
            for _ in range(3):
                with self.assertRaises(ValueError):
                    with lifetime.FrontendSlot(self.root, 1): pass
        with ExitStack() as held:
            for _ in range(16): held.enter_context(lifetime.FrontendSlot(self.root, 16))
            with self.assertRaises(ValueError):
                with lifetime.FrontendSlot(self.root, 16): pass

    def test_invalid_limit_rejected_before_creating_state(self):
        for value in (0, 17, True, 1.5, '4', None):
            with self.subTest(value=value), self.assertRaises(ValueError):
                lifetime.FrontendSlot(self.root/'not-created', value)
        self.assertFalse((self.root/'not-created').exists())

    def test_shared_gui_policy_overrides_config_and_reads_each_admission(self):
        policy = self.root/'policy.json'
        config = {'maxFrontends': 6, 'launchOptions': {'settings_file': str(policy)}}
        self.assertEqual(lifetime.frontend_limit(config), 6)
        policy.write_text('{"maxMcpFrontends":2}')
        self.assertEqual(lifetime.frontend_limit(config), 2)
        policy.write_text('{"maxMcpFrontends":1}')
        self.assertEqual(lifetime.frontend_limit(config), 1)
        with patch.object(lifetime, 'default_policy_path', return_value=policy):
            self.assertEqual(lifetime.frontend_limit({}), 1)

    def test_malformed_duplicate_and_oversized_policies_reject_admission(self):
        policy = self.root/'policy.json'
        config = {'launchOptions': {'settings_file': str(policy)}}
        for raw in ('null', '[]', '{', '{"maxMcpFrontends":true}', '{"maxMcpFrontends":0}',
                    '{"maxMcpFrontends":17}', '{"maxMcpFrontends":1,"MaxMcpFrontends":16}',
                    '{"maxMcpFrontends":1,"maxMcpFrontends":16}', ' '*65537):
            policy.write_text(raw)
            with self.subTest(raw=raw[:80]), self.assertRaises(ValueError):
                lifetime.frontend_limit(config)

    def test_stopped_proxy_does_not_launch_or_restart_owner(self):
        config = {'state': str(self.root)}
        with patch.object(manage, 'load_runtime', return_value=config), \
             patch.object(manage, 'default_root', return_value=self.root), \
             patch.object(lifetime, 'frontend_limit', return_value=2), \
             patch('universal_tools.models.load_settings', return_value=Mock()), \
             patch('universal_tools.cli.probe', return_value=False), \
             patch.object(manage, 'ensure_running') as start, \
             patch('subprocess.Popen', side_effect=AssertionError('No process may start')), \
             patch.object(sys, 'argv', ['manage.py', 'proxy']):
            with self.assertRaisesRegex(ValueError, 'service is stopped'):
                manage.main()
            start.assert_not_called()
        with lifetime.FrontendSlot(self.root, 1): pass


class ShutdownTests(unittest.IsolatedAsyncioTestCase):
    async def test_active_queued_request_blocks_shutdown_until_it_finishes(self):
        owner = backend()
        entered, finish = asyncio.Event(), asyncio.Event()
        async def pending(*_args, **_kwargs):
            entered.set()
            await finish.wait()
        with patch.object(ApplicationBackend, 'invoke', new=pending), \
             patch('service_management.local_state', return_value=(0, 0)):
            work = asyncio.create_task(owner.invoke('acquire_service', {}))
            await entered.wait()
            accepted, _ = await owner.request_shutdown('unused')
            self.assertFalse(accepted)
            self.assertFalse(owner.draining)
            finish.set()
            await work
            self.assertEqual(owner.cache.active, 0)
            self.assertTrue((await owner.request_shutdown('unused'))[0])

    async def test_shutdown_fences_request_arriving_during_journal_check(self):
        owner = backend()
        entered, finish = asyncio.Event(), threading.Event()
        loop = asyncio.get_running_loop()
        def check(_state, *, require_journals):
            self.assertTrue(require_journals)
            loop.call_soon_threadsafe(entered.set)
            if not finish.wait(3): raise AssertionError('Test did not release journal stub')
            return 0, 0
        effect = AsyncMock()
        with patch('service_management.local_state', side_effect=check), \
             patch.object(ApplicationBackend, 'invoke', new=effect):
            shutdown = asyncio.create_task(owner.request_shutdown('unused'))
            await entered.wait()
            incoming = asyncio.create_task(owner.invoke('acquire_service', {}))
            try:
                await asyncio.sleep(0)
                self.assertFalse(incoming.done())
            finally:
                finish.set()
            self.assertTrue((await shutdown)[0])
            self.assertTrue((await incoming).is_error)
            effect.assert_not_called()
            self.assertEqual(owner.cache.active, 0)

    async def test_unknown_context_or_outcome_retains_owner_and_admission(self):
        for value in ((1, 0), (0, 1), (1, 1), OSError('unavailable')):
            owner = backend()
            options = {'side_effect': value} if isinstance(value, Exception) else {'return_value': value}
            with self.subTest(value=value), patch('service_management.local_state', **options):
                self.assertFalse((await owner.request_shutdown('unused'))[0])
                self.assertFalse(owner.draining)

    async def test_missing_journals_cannot_be_treated_as_idle_shutdown(self):
        with tempfile.TemporaryDirectory(prefix='siemens-missing-journals-') as state:
            owner = backend()
            self.assertFalse((await owner.request_shutdown(state))[0])
            self.assertFalse(owner.draining)

    async def test_shutdown_endpoint_auth_busy_and_delegation_in_process(self):
        import httpx
        owner = Mock()
        owner.request_shutdown = AsyncMock(return_value=(False, 'busy'))
        process = SimpleNamespace(should_exit=False)
        settings = SimpleNamespace(host='127.0.0.1', port=43210, token='test-secret')
        async def protected(scope, receive, send):
            from starlette.responses import JSONResponse
            await JSONResponse({'delegated': True})(scope, receive, send)
        app = SafeShutdown(protected, settings, process, owner, 'unused')
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:43210') as client:
            for headers in ({}, {'Authorization': 'Bearer wrong'},
                            {'Authorization': 'Bearer test-secret', 'Origin': 'http://example.invalid'},
                            {'Authorization': 'Bearer test-secret', 'Host': 'example.invalid'}):
                self.assertEqual((await client.post('/shutdown', headers=headers)).status_code, 401)
            owner.request_shutdown.assert_not_called()
            auth = {'Authorization': 'Bearer test-secret'}
            self.assertEqual((await client.get('/shutdown', headers=auth)).status_code, 401)
            self.assertEqual((await client.post('/shutdown', headers=auth)).status_code, 409)
            self.assertFalse(process.should_exit)
            owner.request_shutdown.return_value = True, 'fenced'
            self.assertEqual((await client.post('/shutdown', headers=auth)).status_code, 200)
            self.assertTrue(process.should_exit)
            self.assertEqual((await client.get('/other')).json(), {'delegated': True})


if __name__ == '__main__':
    unittest.main()
