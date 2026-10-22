"""Fixed app checkpoint identity, independent of framework/SDK/protocol versions."""
from datetime import datetime
import json
from pathlib import Path
import re


def canonical(value):
    if not isinstance(value, str) or len(value) > 64 or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', value):
        raise ValueError('Use a three-part numeric application version')
    parts = [int(part) for part in value.split('.')]
    if any(part > 2147483647 for part in parts):
        raise ValueError('Application version component exceeds loader comparison bounds')
    return '.'.join(map(str, parts))


def equivalent(left, right):
    return canonical(left) == canonical(right)


def timestamp(value):
    parts = canonical(value).split('.')
    padded = f'{int(parts[0]):02d}.{int(parts[1]):04d}.{int(parts[2]):06d}'
    if not re.fullmatch(r'[0-9]{2}\.[0-9]{4}\.[0-9]{6}', padded):
        raise ValueError('Timestamp version must represent YY.MMDD.HHmmss')
    # Prefixing the century avoids strptime's two-digit year pivot.
    parsed = datetime.strptime('20'+padded.replace('.', ''), '%Y%m%d%H%M%S')
    return padded, parsed


def assembly_version(value):
    try:
        padded, _ = timestamp(value)
        yy, mmdd, clock = padded.split('.')
        return f'{int(yy)}.{int(mmdd)}.{int(clock[:4])}.{int(clock[4:])}'
    except ValueError:
        parts = canonical(value).split('.')
        if any(int(part) > 65534 for part in parts):
            raise ValueError('Non-timestamp version cannot fit a bounded .NET assembly identity') from None
        return '.'.join(parts)+'.0'


def display_identity(value):
    try: return timestamp(value)[0]
    except ValueError: return value  # historical app versions and explicit "unknown"


def release(root=None):
    root = Path(root) if root is not None else Path(__file__).resolve().parents[1]
    record = json.loads((root/'app-version.json').read_bytes())
    padded, _ = timestamp(record['display_version'])
    if (record['schema_version'] != 1 or record['timezone'] != 'America/New_York'
            or record['display_version'] != padded or record['package_version'] != canonical(padded)
            or record['assembly_version'] != assembly_version(padded)
            or record['file_version'] != record['assembly_version']):
        raise ValueError('Application checkpoint identities disagree; stamp once before building')
    return record


def display_for_payload(root):
    root = Path(root)
    if (root/'app-version.json').exists():
        record = release(root)
        plugin = json.loads((root/'plugin.json').read_bytes())
        if not equivalent(record['package_version'], plugin['version']):
            raise ValueError('Plugin version differs from its app checkpoint')
        return record['display_version']
    return json.loads((root/'plugin.json').read_bytes())['version']  # verified historical deployment


def stamp_surface(server, display):
    """Pre-start identity adapter for the pinned framework 0.20 / SDK 2.3 factory.

    The framework factory has no version argument. Change only its SDK low-level
    identity before any runner/cache exists; never mutate the shared dependency.
    """
    padded, _ = timestamp(display)
    if padded != display:
        raise ValueError('Use the fixed padded app display on the MCP surface')
    low = server._lowlevel_server
    if '_server_info_stamp_source' in vars(low):
        raise ValueError('MCP identity was already observed; a new surface is required')
    low.version = display
    return server
