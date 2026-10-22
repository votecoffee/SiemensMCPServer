"""Measure private discovery hints against real backend metadata; never calls engineering tools."""
import argparse
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace
import time

from mcp import Client, StdioServerParameters
from mcp.client.subscriptions import listen
from universal_tools.transport import list_all_tools


async def exercise(executable):
    params = StdioServerParameters(command=str(executable), args=[])
    async with Client(params, mode='auto', cache=None) as client, Client(params, mode='auto', cache=None) as other:
        assert client.session.protocol_version == '2026-07-28'
        async with listen(client.session, tools_list_changed=True):
            pass  # Real native metadata endpoint must acknowledge the modern watcher.
        count = 0
        async def fetch(**arguments):
            nonlocal count
            count += 1
            return await client.session.list_tools(**arguments)
        hints = await fetch()
        assert hints.ttl_ms == 5000 and hints.cache_scope == 'private'
        uncached = SimpleNamespace(list_tools=fetch, cache_ttl=0, tool_cache=None)
        baseline = count; start = time.perf_counter()
        for _ in range(20):
            assert len(await list_all_tools(uncached)) == 51
        uncached_seconds = time.perf_counter()-start; uncached_roundtrips = count-baseline
        cached = SimpleNamespace(list_tools=fetch, cache_ttl=5, tool_cache=None)
        baseline = count; start = time.perf_counter()
        for _ in range(20):
            assert len(await list_all_tools(cached)) == 51
        cached_seconds = time.perf_counter()-start; cached_roundtrips = count-baseline
        assert uncached_roundtrips == 20 and cached_roundtrips == 1
        (await list_all_tools(cached))[0].name = 'poisoned-copy'
        assert (await list_all_tools(cached))[0].name != 'poisoned-copy'
        baseline = count
        await list_all_tools(cached, fresh=True)
        assert count == baseline+1
        other_count = 0
        async def other_fetch(**arguments):
            nonlocal other_count
            other_count += 1
            return await other.session.list_tools(**arguments)
        isolated = SimpleNamespace(list_tools=other_fetch, cache_ttl=5, tool_cache=None)
        await list_all_tools(isolated)
        assert other_count == 1
        cached.tool_cache = None  # The watcher uses this same invalidation seam.
        baseline = count; await list_all_tools(cached); assert count == baseline+1
        return {'protocol': client.session.protocol_version, 'ttlMs': 5000, 'scope': 'private',
            'listingIterations': 20, 'uncachedRoundtrips': uncached_roundtrips, 'cachedRoundtrips': cached_roundtrips,
            'uncachedSeconds': uncached_seconds, 'cachedSeconds': cached_seconds,
            'freshCheckBypassesCache': True, 'separateConnectionsFetchIndependently': True,
            'detachedCopies': True, 'invalidationRefetches': True, 'nativeWatcherAcknowledged': True, 'nativeProjectContact': False}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path); parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    result = asyncio.run(exercise(args.executable.resolve()))
    if args.report:
        args.report.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result, indent=2))
