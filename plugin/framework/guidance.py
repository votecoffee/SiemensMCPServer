"""App-owned Markdown snapshots shared by ordinary guide reads and Skills-over-MCP."""
import asyncio
import hashlib
import json
import posixpath
from pathlib import Path
import re
import tempfile
from types import SimpleNamespace
from urllib.parse import unquote, urlsplit

from mcp.types import PaginatedRequestParams
from universal_tools.models import BrokerError
from universal_tools.skill_catalog import SkillCatalog
from universal_tools.skill_manifest import OPENAI_IMPORT_PROFILE, frontmatter
from universal_tools.surface import READ, error_result, json_result

from integrity import atomic_json, ordinary, relative

FORMAT = 'siemens-guidance-bundle-v1'
READERS = {'siemens_list_workflows', 'siemens_read_workflow', 'siemens_read_reference', 'siemens_doctor'}
CONTEXT = SimpleNamespace(protocol_version='2025-11-25')


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def text_bytes(path, limit=1024*1024):
    with ordinary(path).open('rb') as stream:
        raw = stream.read(limit+1)
    if len(raw) > limit:
        raise ValueError('Guidance file exceeds its budget')
    raw.decode('utf-8')
    return raw


def prepare_bundle(source, destination):
    """Project canonical guides/root references to data-only self-contained packages."""
    source, destination = ordinary(source), ordinary(destination)
    if destination == source or destination.is_relative_to(source) or destination.exists():
        raise ValueError('Guidance destination must be new and outside the canonical source')
    references = {p.name: text_bytes(p) for p in sorted(source.glob('*.md'))}
    guides = sorted((source/'guidance').glob('*/SKILL.md'))
    if not 1 <= len(guides) <= 5:
        raise ValueError('Select one to five canonical workflow guides')
    files, sources, names = {}, {}, []
    for name, raw in references.items():
        sources[name] = {'sha256': sha(raw), 'bytes': len(raw)}

    def project(raw, original, *, instruction):
        def link(match):
            target = match.group(1)
            path, separator, anchor = target.partition('#')
            if not path or urlsplit(target).scheme:
                return match.group(0)
            resolved = (original.parent/unquote(path)).resolve()
            if resolved.parent == source.resolve() and resolved.name in references:
                new = ('references/' if instruction else '') + resolved.name
            else:
                # Files outside the published Markdown set remain explicit source links.
                logical = posixpath.normpath('plugin/'+original.parent.relative_to(source).as_posix()+'/'+path)
                new = 'https://github.com/votecoffee/SiemensMCPServer/blob/main/'+logical
            return ']('+new+('#'+anchor if separator else '')+')'
        return re.sub(r'\]\(([^)]+)\)', link, raw.decode('utf-8')).encode('utf-8')

    for guide in guides:
        raw = text_bytes(guide, 256*1024)
        header = frontmatter(raw)
        name = header['name']
        if name != guide.parent.name or name in names:
            raise ValueError('Canonical workflow identity conflicts with its directory')
        names.append(name)
        sources[guide.relative_to(source).as_posix()] = {'sha256': sha(raw), 'bytes': len(raw)}
        files[name+'/SKILL.md'] = project(raw, guide, instruction=True)
        for reference, content in references.items():
            files[name+'/references/'+reference] = project(content, source/reference, instruction=False)
    if sum(map(len, files.values())) > 8*1024*1024:
        raise ValueError('Guidance publication exceeds its byte budget')
    destination.mkdir(parents=True)
    for name, raw in files.items():
        path = relative(destination, name); path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(raw)
    manifest = {'format': FORMAT, 'workflows': names, 'sources': sources,
        'files': [{'path': p, 'bytes': len(raw), 'sha256': sha(raw)} for p, raw in sorted(files.items())]}
    atomic_json(destination/'guidance-manifest.json', manifest)
    SkillCatalog([destination/n for n in names], profile=OPENAI_IMPORT_PROFILE)
    return {'bundle': str(destination), 'manifest_sha256': sha((destination/'guidance-manifest.json').read_bytes()), 'workflows': names}


def freeze_bundle(source, expected_sha256, cache):
    source, cache = ordinary(source), ordinary(cache)
    raw = text_bytes(source/'guidance-manifest.json')
    if sha(raw) != expected_sha256:
        raise ValueError('Guidance manifest differs from its reviewed hash')
    manifest = json.loads(raw)
    if manifest.get('format') != FORMAT or not 1 <= len(manifest['workflows']) <= 5:
        raise ValueError('Unsupported guidance bundle')
    data, total = {}, 0
    if not 1 <= len(manifest['files']) <= 500:
        raise ValueError('Guidance member count exceeds its bound')
    for item in manifest['files']:
        name = item['path']
        if not name.endswith('.md') or name.casefold() in {n.casefold() for n in data}:
            raise ValueError('Guidance contains duplicate or non-Markdown members')
        value = text_bytes(relative(source, name))
        if sha(value) != item['sha256'] or len(value) != item['bytes']:
            raise ValueError('Guidance member differs from its reviewed bytes')
        total += len(value)
        if total > 8*1024*1024:
            raise ValueError('Guidance exceeds its byte budget')
        data[name] = value
    actual = set()
    for p in source.rglob('*'):
        ordinary(p)
        if p.is_file():
            actual.add(p.relative_to(source).as_posix())
    if actual != set(data) | {'guidance-manifest.json'}:
        raise ValueError('Guidance contains unreviewed members')
    cache.mkdir(parents=True, exist_ok=True)
    snapshot = relative(cache, expected_sha256)
    if not snapshot.exists():
        with tempfile.TemporaryDirectory(prefix='guidance-stage-', dir=cache) as temporary:
            stage = Path(temporary)/'bundle'; stage.mkdir()
            for name, value in data.items():
                path = relative(stage, name); path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(value)
            (stage/'guidance-manifest.json').write_bytes(raw)
            packages = [relative(stage, n) for n in manifest['workflows']]
            checked = SkillCatalog(packages, profile=OPENAI_IMPORT_PROFILE)
            if len(checked.resource_metadata()) != len(data):
                raise ValueError('Guidance has files outside its skill manifest')
            if not stage.resolve().is_relative_to(cache.resolve()) or not snapshot.resolve().is_relative_to(cache.resolve()):
                raise ValueError('Guidance snapshot leaves its private cache')
            stage.rename(snapshot)
    # Recheck a reused private snapshot before handing it to the SDK.
    for name, value in {**data, 'guidance-manifest.json': raw}.items():
        if text_bytes(relative(snapshot, name)) != value:
            raise ValueError('Retained guidance snapshot changed')
    packages = [relative(snapshot, n) for n in manifest['workflows']]
    checked = SkillCatalog(packages, profile=OPENAI_IMPORT_PROFILE)
    if len(checked.resource_metadata()) != len(data):
        raise ValueError('Retained guidance snapshot has extra members')
    return packages


class Guidance:
    def __init__(self, source, cache):
        self.cache = ordinary(cache); self.cache.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='guidance-source-', dir=self.cache) as temporary:
            prepared = prepare_bundle(source, Path(temporary)/'bundle')
            self.bundle_sha256 = prepared['manifest_sha256']
            snapshot = relative(self.cache, self.bundle_sha256)
            stage = Path(prepared['bundle'])
            if not snapshot.exists():
                if not stage.resolve().is_relative_to(self.cache.resolve()) or not snapshot.resolve().is_relative_to(self.cache.resolve()):
                    raise ValueError('Initial guidance leaves its private cache')
                stage.rename(snapshot)
                self.packages = [snapshot/n for n in prepared['workflows']]
            else:
                self.packages = freeze_bundle(stage, self.bundle_sha256, self.cache)
        self.catalog = SkillCatalog(self.packages, profile=OPENAI_IMPORT_PROFILE, page_size=2, directory_read=True)
        self.lock = asyncio.Lock()

    async def entries(self):
        entries, cursor = [], None
        while True:
            page = await self.catalog.list_skills(CONTEXT, PaginatedRequestParams(cursor=cursor))
            entries.extend(page.skills)
            cursor = page.next_cursor
            if not cursor:
                return entries

    def identity(self):
        return {'app_id': 'siemens-mcp-server', 'route': 'standalone-app-host',
                'catalog_revision': self.catalog.revision, 'bundle_sha256': self.bundle_sha256,
                'generation': self.catalog.generation, 'engineering_context_required': False}

    async def list_workflows(self):
        async with self.lock:
            workflows = []
            for entry in await self.entries():
                resource = next(r for r in entry['resources'] if r['uri'] == entry['uri'])
                workflows.append({'workflow_id': entry['frontmatter']['name'], 'description': entry['frontmatter']['description'],
                    'uri': entry['uri'], 'sha256': resource['digest'].removeprefix('sha256:'), 'bytes': resource['size']})
            return {**self.identity(), 'workflows': workflows}

    async def doctor(self):
        return {**(await self.list_workflows()), 'framework_version': '0.18.0', 'python_sdk_version': '2.3.0',
            'profiles': {'standalone_stdio': 'engineering plus ordinary guide tools; no Skills extension advertised',
                'authenticated_app_host_http': 'engineering plus guide tools, Skills extension and directory reads'},
            'features': {
                'current_guides_and_references': {'status': 'enabled-and-tested', 'evidence': 'connected-client revision/hash and resource parity tests'},
                'skills_publication_and_resource_notifications': {'status': 'enabled-and-tested', 'evidence': 'app-host HTTP profile; explicit CAS publication and reconnect/refetch'},
                'native_operation_stages': {'status': 'enabled-and-tested', 'evidence': 'request lane stages/structured status; notifications need caller progress support'},
                'queued_cancellation': {'status': 'enabled-and-tested', 'evidence': 'queued work removed before dispatch; running native calls are never aborted'},
                'native_tool_watch_and_ttl_cache': {'status': 'blocked', 'reason': 'Selected C# backend retains the assessed legacy protocol and emits no TTL hints; explicit refresh remains required.'},
                'native_tasks': {'status': 'blocked', 'reason': 'No durable app-owned TIA job identity, lost-start reconciliation or surviving native-job adapter.'},
                'approval_continuations': {'status': 'blocked', 'reason': 'No target/preview-bound native approval adapter; ordinary authorization and reviewed hashes remain required.'},
                'engineering_artifact_resources': {'status': 'blocked', 'reason': 'No owner-bound retained export registry for resource authorization; existing scoped engineering readers remain usable.'},
                'redis_oauth_apps': {'status': 'outside-app-workflow', 'reason': 'Local single-owner engineering profile; no replicas or additional remote/UI grants.'}},
            'acceptance': {'source_protocol_tests': 'covered', 'codex_native_skill_activation': 'not-established',
                           'native_project_runtime_acceptance': 'not-established', 'guide_read_grants_write_approval': False}}

    async def read(self, workflow_id, expected_revision, expected_sha256, reference_id=None):
        async with self.lock:
            if expected_revision != self.catalog.revision:
                raise BrokerError('Stale guidance revision; rediscover and review the current workflow')
            entry = next((e for e in await self.entries() if e['frontmatter']['name'] == workflow_id), None)
            if entry is None:
                raise BrokerError('Unknown workflow identity')
            uri = entry['uri'] if reference_id is None else entry['uri'].removesuffix('SKILL.md')+'references/'+reference_id
            item = next((r for r in entry['resources'] if r['uri'] == uri), None)
            if item is None or item['digest'] != 'sha256:'+expected_sha256:
                raise BrokerError('Unknown or stale guidance resource hash')
            raw = await self.catalog.read_bytes(uri)
            if len(raw) != item['size'] or sha(raw) != expected_sha256:
                raise BrokerError('Guidance resource integrity check failed')
            refs = [{'reference_id': r['uri'].rsplit('/', 1)[-1], 'uri': r['uri'],
                'sha256': r['digest'].removeprefix('sha256:'), 'bytes': r['size']} for r in entry['resources'] if r['uri'] != entry['uri']]
            return {**self.identity(), 'workflow_id': workflow_id, 'uri': uri, 'sha256': expected_sha256,
                'bytes': len(raw), 'content': raw.decode('utf-8'), **({'references': refs} if reference_id is None else {})}

    async def publish(self, bundle, expected_sha256, expected_revision):
        async with self.lock:
            if expected_revision != self.catalog.revision:
                raise BrokerError('Guidance publication revision changed')
            packages = await asyncio.to_thread(freeze_bundle, bundle, expected_sha256, self.cache)
            await self.catalog.publish(packages, expected_revision=expected_revision)
            self.packages, self.bundle_sha256 = packages, expected_sha256
            return {**self.identity(), 'published': True, 'persistent': False,
                'restart_behavior': 'Host restart restores the installed canonical guidance; republish a reviewed override explicitly.'}


def register_readers(server, guidance=None, remote=None):
    async def invoke(name, arguments):
        if remote is not None:
            return await remote.invoke(name, arguments)
        try:
            if name == 'siemens_list_workflows':
                value = await guidance.list_workflows()
            elif name == 'siemens_doctor':
                value = await guidance.doctor()
            else:
                value = await guidance.read(**arguments)
            return json_result(value)
        except (BrokerError, ValueError, KeyError):
            return error_result('Unknown or stale guidance pin; rediscover the workflow catalog and review current guidance.')

    @server.tool(annotations=READ, structured_output=False)
    async def siemens_list_workflows():
        """List current Siemens workflow guides with exact revisions/hashes; no engineering context or attachment."""
        return await invoke('siemens_list_workflows', {})

    @server.tool(annotations=READ, structured_output=False)
    async def siemens_read_workflow(workflow_id: str, expected_revision: str, expected_sha256: str):
        """Read a pinned current workflow and its reference manifest. Stale revisions/hashes reject; no native work."""
        return await invoke('siemens_read_workflow', dict(workflow_id=workflow_id, expected_revision=expected_revision, expected_sha256=expected_sha256))

    @server.tool(annotations=READ, structured_output=False)
    async def siemens_read_reference(workflow_id: str, reference_id: str, expected_revision: str, expected_sha256: str):
        """Read an exact supporting Markdown reference from the same pinned workflow snapshot; no native work."""
        return await invoke('siemens_read_reference', dict(workflow_id=workflow_id, reference_id=reference_id, expected_revision=expected_revision, expected_sha256=expected_sha256))

    @server.tool(annotations=READ, structured_output=False)
    async def siemens_doctor():
        """Report selected guidance/engineering profile capabilities, evidence boundaries and concrete blockers without native probes."""
        return await invoke('siemens_doctor', {})


class GuidanceAdmin:
    """Operator-only authenticated local publication, outside the model-facing tool surface."""
    def __init__(self, app, guidance, port):
        self.app, self.guidance, self.host = app, guidance, f'127.0.0.1:{port}'.encode()

    async def __call__(self, scope, receive, send):
        if scope['type'] != 'http' or scope['path'] != '/siemens/guidance/publish':
            return await self.app(scope, receive, send)
        from starlette.responses import JSONResponse
        if scope['method'] != 'POST' or dict(scope.get('headers', [])).get(b'host') != self.host:
            return await JSONResponse({'error': 'Invalid guidance publication request'}, status_code=400)(scope, receive, send)
        raw = bytearray()
        try:
            while True:
                part = await receive()
                if part['type'] != 'http.request':
                    raise ValueError()
                raw.extend(part.get('body', b''))
                if len(raw) > 16384:
                    raise ValueError()
                if not part.get('more_body'):
                    break
            arguments = json.loads(raw)
            if set(arguments) != {'bundle', 'expected_sha256', 'expected_revision'}:
                raise ValueError()
            result = await self.guidance.publish(**arguments)
            response = JSONResponse(result)
        except (ValueError, KeyError, TypeError, OSError, BrokerError):
            response = JSONResponse({'error': 'Guidance publication rejected; verify the bundle and current revision. Prior catalog retained.'}, status_code=409)
        await response(scope, receive, send)
