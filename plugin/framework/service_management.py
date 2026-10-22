"""Cached tray reporting for the owning Siemens host; no native status probes/restart."""
import asyncio
from contextlib import asynccontextmanager, suppress
from importlib.metadata import version
import json
import logging
import hmac
import os
from pathlib import Path
import secrets
import sqlite3
import threading
import time

from universal_tools.framework import ApplicationBackend, ApplicationHost
from universal_tools.management import ComponentVersion, ManagedService, ManagedStatus, RestartResult
from universal_tools.models import load_settings

from reconciliation import valid_attestation

APP_ID = 'siemens-mcp-server'
PLUGIN_ID = 'siemens-mcp-server@siemens-mcp-server-local'
RESTART_REASON = ('Tray restart unavailable: no surviving app supervisor can fence all contexts, '
                  'selections, previews, unsaved projects and queued native work.')


def bounded_json(path):
    with Path(path).open('rb') as stream:
        raw = stream.read(1024*1024+1)
    if len(raw) > 1024*1024:
        raise ValueError('Oversized management metadata')
    return json.loads(raw)


def local_state(state, *, require_journals=False):
    """Read existing journals only; never acquire a context or call engineering tools."""
    state = Path(state)
    contexts = unresolved = 0
    for name in ('ownership.sqlite3', 'writes.sqlite3'):
        path = state/name
        if not path.exists():
            if require_journals:
                raise ValueError('Required ownership/outcome journal is unavailable')
            continue
        db = sqlite3.connect(path.resolve().as_uri()+'?mode=ro', uri=True, timeout=0.2)
        try:
            if name == 'ownership.sqlite3':
                contexts = db.execute('SELECT count(*) FROM contexts').fetchone()[0]
            else:
                db.row_factory = sqlite3.Row
                rows = db.execute("SELECT * FROM requests WHERE status!='completed' LIMIT 1001").fetchall()
                if len(rows) > 1000:
                    raise ValueError('Management receipt inspection exceeds its bound')
                unresolved = sum(not valid_attestation(state, dict(row)) for row in rows)
        finally:
            db.close()
    return contexts, unresolved


class StatusCache:
    def __init__(self, config_path, config):
        self.config_path, self.config = Path(config_path), config
        self.lock = threading.Lock()
        self.active = 0
        self.checked = 0.0
        self.running_app = bounded_json(Path(config['payloadRoot'])/'plugin.json')['version']
        self.running_framework = version('universal-codex-tools')
        self.running_sdk = version('mcp')
        self.current = ManagedStatus(state='starting', message='Engineering host starting',
            versions={'app': ComponentVersion(installed=self.running_app, running=self.running_app),
                      'plugin': ComponentVersion(installed='unknown', running=self.running_app),
                      'framework': ComponentVersion(installed=self.running_framework, running=self.running_framework),
                      'sdk': ComponentVersion(installed=self.running_sdk, running=self.running_sdk)},
            restart_reason=RESTART_REASON)

    def begin(self):
        with self.lock:
            self.active += 1

    def end(self):
        with self.lock:
            self.active -= 1

    def refresh(self):
        try:
            active = bounded_json(self.config_path)
            installed_app = bounded_json(Path(active['payloadRoot'])/'plugin.json')['version']
            receipt_path = self.config_path.parent/'installed-components.json'
            receipt = bounded_json(receipt_path) if receipt_path.exists() else {}
            if receipt and receipt.get('pluginId') != PLUGIN_ID:
                raise ValueError('Installed plugin identity changed')
            contexts, unresolved = local_state(self.config['state'])
            from lifetime import owned_resources, frontend_limit
            owned = owned_resources()
            owned_count = len(owned['owned_processes']) if owned.get('inventory_available') else 'unknown'
            current = ManagedStatus(
                state='degraded' if unresolved else 'busy' if contexts else 'running',
                message=(f'Engineering host; {contexts} retained context(s); {unresolved} unreconciled request(s). '
                         f'Owned process tree: {owned_count}; connected-chat ceiling: {frontend_limit(self.config)}. '
                         'Native selection, previews and unsaved state are not inspected.'),
                versions={
                    'app': ComponentVersion(installed=installed_app, running=self.running_app),
                    'plugin': ComponentVersion(installed=receipt.get('pluginVersion', 'unknown'), running=self.running_app),
                    'framework': ComponentVersion(installed=version('universal-codex-tools'), running=self.running_framework),
                    'sdk': ComponentVersion(installed=version('mcp'), running=self.running_sdk),
                },
                restart_allowed=False, restart_reason=RESTART_REASON)
        except Exception:
            with self.lock:
                current = self.current.model_copy(update={'state': 'degraded',
                    'message': 'Local management metadata unavailable; engineering state was not probed.'})
        with self.lock:
            self.current, self.checked = current, time.monotonic()

    def snapshot(self):
        # The HTTP thread reads memory only, including during long native calls.
        with self.lock:
            current = self.current.model_copy(deep=True)
            active, checked = self.active, self.checked
        updates = {'active_operations': active, 'restart_allowed': False, 'restart_reason': RESTART_REASON}
        if checked and time.monotonic()-checked > 8:
            updates.update(state='degraded', message='Management snapshot is stale; engineering state unknown.')
        elif active:
            updates['state'] = 'busy'
        return current.model_copy(update=updates)

    def restart(self, _request):
        # Intentionally no stop/launch/cancel action. Safety cannot be inferred from idle counters.
        return RestartResult(outcome='blocked', message=RESTART_REASON)


class ObservedBackend(ApplicationBackend):
    def __init__(self, host, cache):
        super().__init__(host)
        self.cache = cache
        self.admission = asyncio.Lock()
        self.draining = False

    async def invoke(self, name, arguments, *, relay=None):
        from universal_tools.surface import error_result
        async with self.admission:
            if self.draining:
                return error_result('Siemens service is stopping; no new engineering request was admitted.')
            self.cache.begin()  # Includes requests queued behind another chat's service gate.
        try:
            return await super().invoke(name, arguments, relay=relay)
        finally:
            self.cache.end()

    async def request_shutdown(self, state):
        async with self.admission:
            if self.cache.snapshot().active_operations:
                return False, 'Active requests must finish before service shutdown.'
            try:
                contexts, unresolved = await asyncio.to_thread(local_state, state, require_journals=True)
            except Exception:
                return False, 'Cannot verify ownership/outcomes; service remains running.'
            if contexts or unresolved:
                return False, 'Retained contexts or unreconciled outcomes block service shutdown.'
            self.draining = True
            return True, 'Service admission fenced; graceful shutdown scheduled.'


class SafeShutdown:
    """Server-side ownership check and admission fence, without a lease-admission race."""
    def __init__(self, protected, settings, process, backend, state):
        self.protected, self.settings, self.process, self.backend, self.state = protected, settings, process, backend, state

    async def __call__(self, scope, receive, send):
        if scope['type'] != 'http' or scope['path'] != '/shutdown':
            return await self.protected(scope, receive, send)
        from starlette.responses import JSONResponse
        headers = dict(scope.get('headers', []))
        authenticated = (scope['method'] == 'POST' and b'origin' not in headers
            and headers.get(b'host') == f'{self.settings.host}:{self.settings.port}'.encode()
            and hmac.compare_digest(headers.get(b'authorization', b''), ('Bearer '+self.settings.token).encode()))
        if not authenticated:
            return await JSONResponse({'error': 'unauthorized'}, status_code=401)(scope, receive, send)
        accepted, message = await self.backend.request_shutdown(self.state)
        if accepted:
            self.process.should_exit = True
        await JSONResponse({'stopping': accepted, 'message': message}, status_code=200 if accepted else 409)(scope, receive, send)


def serve_managed(config_path, config):
    """Embed the supported ApplicationHost with its normal authenticated MCP surface."""
    import uvicorn
    from starlette.applications import Starlette
    from universal_tools.daemon import ProtectedApp
    from guidance import Guidance, GuidanceAdmin, register_readers

    state = Path(config['state'])
    settings = load_settings(state)
    host = ApplicationHost(state, Path(config['publication']), max_request_body_bytes=settings.max_request_body_bytes)
    cache = StatusCache(config_path, config)
    host.backend = ObservedBackend(host, cache)
    guidance = Guidance(Path(config['payloadRoot']), state/'guidance')
    server = host.create_server(port=settings.port, skills=guidance.catalog)
    register_readers(server, guidance=guidance)
    base = server.streamable_http_app(**server.http_options)

    async def refresh():
        while True:
            await asyncio.to_thread(cache.refresh)
            await asyncio.sleep(2)

    @asynccontextmanager
    async def lifespan(_):
        async with host:
            await asyncio.to_thread(cache.refresh)
            agent = ManagedService(APP_ID, 'SiemensMCPServer', cache.snapshot, restart=cache.restart,
                plugin_id=PLUGIN_ID, registry=os.environ.get('SIEMENS_MCP_MANAGEMENT_REGISTRY'))
            reporting = False
            try:
                agent.start()
                reporting = True
            except Exception:
                logging.getLogger(__name__).warning('Siemens tray reporting unavailable; engineering service remains running.')
            task = asyncio.create_task(refresh())
            try:
                async with server.session_manager.run():
                    yield
            finally:
                task.cancel()
                with suppress(asyncio.CancelledError):
                    await task
                if reporting:
                    await asyncio.to_thread(agent.close)

    app = Starlette(routes=base.routes, lifespan=lifespan)
    options = uvicorn.Config(app, host=settings.host, port=settings.port, log_level='warning',
                             access_log=False, timeout_graceful_shutdown=15)
    process = uvicorn.Server(options)
    protected = ProtectedApp(GuidanceAdmin(app, guidance, settings.port), settings, process, secrets.token_hex(16))
    options.app = SafeShutdown(protected, settings, process, host.backend, state)
    process.run()
