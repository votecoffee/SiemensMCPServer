"""Connected guide/SkillCatalog parity and publication checks; no native app or context."""
import asyncio
import hashlib
import json
from pathlib import Path
import sys

import pytest
from mcp import Client
from mcp.shared.exceptions import MCPError
from mcp.client.subscriptions import listen
from mcp.types import PaginatedRequestParams, Request, Result

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
from guidance import Guidance, prepare_bundle, register_readers
from universal_tools.extensions import ExtensionServer
from universal_tools.models import BrokerError
from universal_tools.skill_catalog import SkillGet, SkillList, SkillParams, DirectoryParams
from universal_tools.skill_client import SkillClient

NAMES = ['edit-hmi-text', 'edit-siemens-plc', 'inspect-libraries', 'inspect-project-texts']


class DirectoryResult(Result):
    resources: list[dict]


def source(root):
    root.mkdir()
    (root/'REFERENCE.md').write_text('# Reference\nExact retained evidence.\n', encoding='utf-8')
    for name in NAMES:
        add_guide(root, name)
    return root


def add_guide(root, name):
    path = root/'guidance'/name/'SKILL.md'; path.parent.mkdir(parents=True)
    path.write_text(f'---\nname: {name}\ndescription: Synthetic {name} workflow\n---\n'
                    'No native operations. Read [reference](../../REFERENCE.md).\n', encoding='utf-8')


def data(result):
    assert not result.is_error, result
    return result.structured_content


async def listing(client):
    return data(await client.call_tool('siemens_list_workflows', {}))


async def read(client, catalog, entry):
    return data(await client.call_tool('siemens_read_workflow', {'workflow_id': entry['workflow_id'],
        'expected_revision': catalog['catalog_revision'], 'expected_sha256': entry['sha256']}))


async def protocol_list(client):
    entries, cursor = [], None
    while True:
        page = await client.session.send_request(Request(method='skills/list', params=PaginatedRequestParams(cursor=cursor)), SkillList)
        entries.extend(page.skills); cursor = page.next_cursor
        if not cursor:
            return entries


@pytest.mark.asyncio
async def test_connected_tool_skill_reference_and_directory_bytes_match(tmp_path):
    root = source(tmp_path/'canonical')
    guides = Guidance(root, tmp_path/'cache')
    server = ExtensionServer('Siemens guide fixture', extensions=[guides.catalog], resource_providers=[guides.catalog])
    register_readers(server, guidance=guides)
    async with Client(server, mode='2026-07-28', extensions=[SkillClient()], cache=None) as client:
        tools = await client.list_tools()
        assert len(tools.tools) == 4 and all(t.annotations.read_only_hint for t in tools.tools)
        catalog = await listing(client)
        entries = await protocol_list(client)
        assert len(entries) == len(catalog['workflows']) == 4
        doctor = data(await client.call_tool('siemens_doctor', {}))
        assert doctor['features']['native_tasks']['status'] == 'blocked'
        assert not doctor['acceptance']['guide_read_grants_write_approval']
        for workflow in catalog['workflows']:
            guide = await read(client, catalog, workflow)
            entry = next(e for e in entries if e['uri'] == guide['uri'])
            fetched = await client.session.send_request(Request(method='skills/get', params=SkillParams(uri=entry['uri'])), SkillGet)
            assert fetched.skill == entry
            assert 'references/REFERENCE.md' in guide['content']
            for item in entry['resources']:
                content = (await client.read_resource(item['uri'])).contents
                assert len(content) == 1 and str(content[0].uri) == item['uri']
                raw = content[0].text.encode('utf-8')
                assert len(raw) == item['size'] and 'sha256:'+hashlib.sha256(raw).hexdigest() == item['digest']
                if item['uri'] == guide['uri']:
                    assert raw.decode() == guide['content']
                else:
                    ref = next(r for r in guide['references'] if r['uri'] == item['uri'])
                    actual = data(await client.call_tool('siemens_read_reference', {'workflow_id': workflow['workflow_id'],
                        'reference_id': ref['reference_id'], 'expected_revision': catalog['catalog_revision'], 'expected_sha256': ref['sha256']}))
                    assert actual['content'].encode() == raw
            directory = await client.session.send_request(Request(method='resources/directory/read',
                params=DirectoryParams(uri=guide['uri'].removesuffix('/SKILL.md')+'/references')), DirectoryResult)
            assert directory.model_dump(by_alias=True)['resources'][0]['uri'].endswith('/references/REFERENCE.md')
    assert not (tmp_path/'ownership.sqlite3').exists()


@pytest.mark.asyncio
async def test_connected_update_pins_stale_cursors_and_failed_publication(tmp_path):
    root = source(tmp_path/'canonical'); guides = Guidance(root, tmp_path/'cache')
    server = ExtensionServer('Siemens live guide fixture', extensions=[guides.catalog], resource_providers=[guides.catalog])
    register_readers(server, guidance=guides)
    async with Client(server, mode='2026-07-28', extensions=[SkillClient()], cache=None) as client:
        old = await listing(client); item = old['workflows'][0]; before = await read(client, old, item)
        page = await client.session.send_request(Request(method='skills/list', params=PaginatedRequestParams()), SkillList)
        guide_file = root/'guidance'/item['workflow_id']/'SKILL.md'
        guide_file.write_text(guide_file.read_text()+'\nUpdated reviewed offline procedure.\n')
        (root/'REFERENCE.md').write_text('# Reference\nUpdated reference bytes.\n')
        assert await read(client, old, item) == before  # Disk edits do not mutate a live snapshot.
        prepared = prepare_bundle(root, tmp_path/'reviewed')
        async with listen(client.session, resources_list_changed=True) as notifications:
            result = await guides.publish(prepared['bundle'], prepared['manifest_sha256'], old['catalog_revision'])
            await asyncio.wait_for(anext(notifications), timeout=3)
        assert result['published'] and not result['persistent']
        new = await listing(client); changed = next(e for e in new['workflows'] if e['workflow_id'] == item['workflow_id'])
        after = await read(client, new, changed)
        assert 'Updated reviewed offline procedure' in after['content']
        assert (await client.read_resource(item['uri'])).contents[0].text == after['content']
        assert (await client.call_tool('siemens_read_workflow', {'workflow_id': item['workflow_id'],
            'expected_revision': old['catalog_revision'], 'expected_sha256': item['sha256']})).is_error
        assert (await client.call_tool('siemens_read_workflow', {'workflow_id': item['workflow_id'],
            'expected_revision': new['catalog_revision'], 'expected_sha256': item['sha256']})).is_error
        with pytest.raises(MCPError):
            await client.session.send_request(Request(method='skills/list', params=PaginatedRequestParams(cursor=page.next_cursor)), SkillList)
        with pytest.raises(BrokerError):
            await guides.publish(prepared['bundle'], prepared['manifest_sha256'], old['catalog_revision'])
        (Path(prepared['bundle'])/item['workflow_id']/'references/REFERENCE.md').write_text('tampered')
        with pytest.raises(ValueError, match='reviewed bytes'):
            await guides.publish(prepared['bundle'], prepared['manifest_sha256'], new['catalog_revision'])
        assert await listing(client) == new and await read(client, new, changed) == after
        malicious = tmp_path/'reviewed'/'contributed.py'; malicious.write_text('raise AssertionError("must never execute")')
        # A new valid bundle with an extra executable is rejected, never imported.
        fresh = prepare_bundle(root, tmp_path/'extra'); (Path(fresh['bundle'])/'contributed.py').write_text(malicious.read_text())
        with pytest.raises(ValueError, match='unreviewed'):
            await guides.publish(fresh['bundle'], fresh['manifest_sha256'], new['catalog_revision'])
        assert (await listing(client))['catalog_revision'] == new['catalog_revision']
    async with Client(server, mode='2026-07-28', extensions=[SkillClient()], cache=None) as reconnected:
        assert (await listing(reconnected))['catalog_revision'] == new['catalog_revision']
        assert await protocol_list(reconnected) == await guides.entries()


@pytest.mark.asyncio
async def test_add_remove_catalog_is_visible_without_reconnecting(tmp_path):
    root = source(tmp_path/'canonical'); guides = Guidance(root, tmp_path/'cache')
    server = ExtensionServer('Siemens guide changes', extensions=[guides.catalog], resource_providers=[guides.catalog])
    register_readers(server, guidance=guides)
    async with Client(server, mode='2026-07-28', extensions=[SkillClient()], cache=None) as client:
        before = await listing(client)
        add_guide(root, 'synthetic-review')
        prepared = prepare_bundle(root, tmp_path/'added')
        await guides.publish(prepared['bundle'], prepared['manifest_sha256'], before['catalog_revision'])
        assert len(await protocol_list(client)) == 5
        (root/'guidance/synthetic-review/SKILL.md').unlink(); (root/'guidance/synthetic-review').rmdir()
        prepared = prepare_bundle(root, tmp_path/'removed')
        await guides.publish(prepared['bundle'], prepared['manifest_sha256'], (await listing(client))['catalog_revision'])
        assert len(await protocol_list(client)) == 4
        with pytest.raises(MCPError):
            await client.read_resource('skill://synthetic-review/SKILL.md')


@pytest.mark.asyncio
async def test_publication_admin_requires_transport_auth_and_retains_prior_on_rejection(tmp_path):
    import httpx
    from types import SimpleNamespace
    from starlette.responses import JSONResponse
    from guidance import GuidanceAdmin
    from universal_tools.cli import initialize
    from universal_tools.daemon import ProtectedApp
    root = source(tmp_path/'canonical'); guides = Guidance(root, tmp_path/'cache')
    before = await guides.list_workflows()
    (root/'REFERENCE.md').write_text('Reviewed update.\n')
    prepared = prepare_bundle(root, tmp_path/'reviewed')
    settings = initialize(tmp_path/'http-state', 18765)
    async def other(scope, receive, send):
        await JSONResponse({'unexpected': True})(scope, receive, send)
    app = ProtectedApp(GuidanceAdmin(other, guides, settings.port), settings, SimpleNamespace(should_exit=False), 'fixture')
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url=settings.url) as client:
        body = {'bundle': prepared['bundle'], 'expected_sha256': prepared['manifest_sha256'], 'expected_revision': before['catalog_revision']}
        assert (await client.post('/siemens/guidance/publish', json=body)).status_code == 401
        auth = {'Authorization': 'Bearer '+settings.token}
        assert (await client.post('/siemens/guidance/publish', headers={**auth, 'Origin': 'https://example.invalid'}, json=body)).status_code == 401
        assert await guides.list_workflows() == before
        assert (await client.post('/siemens/guidance/publish', headers=auth, json={**body, 'expected_sha256': '0'*64})).status_code == 409
        assert await guides.list_workflows() == before
        assert (await client.post('/siemens/guidance/publish', headers=auth, json=body)).status_code == 200
        assert (await client.post('/siemens/guidance/publish', headers=auth, json=body)).status_code == 409
