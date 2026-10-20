"""Deployment checks shared by the app installer and launcher; standard library only."""
import base64
import csv
from email.parser import BytesParser
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import zipfile

FRAMEWORK_VERSION = '0.16.0'
MCP_VERSION = '2.3.0'
FRAMEWORK_COMMIT = '579b065ecc37f98af72cb148ba47a65587bd167b'


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def ordinary(path):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        if item.is_symlink() or item.is_junction():
            raise ValueError('Linked deployment paths are not supported')
    return path


def relative(root, name):
    parts = PurePosixPath(name)
    if not name or '\\' in name or ':' in name or parts.is_absolute() or any(
            part in {'', '.', '..'} for part in name.split('/')):
        raise ValueError('Unsafe deployment path')
    return ordinary(Path(root).joinpath(*parts.parts))


def verify_payload(root):
    root = ordinary(root)
    manifest = ordinary(root/'manifest.json')
    if manifest.stat().st_size > 1024*1024:
        raise ValueError('Oversized deployment manifest')
    entries = json.loads(manifest.read_bytes())
    if not isinstance(entries, list) or not 1 <= len(entries) <= 1000:
        raise ValueError('Invalid deployment inventory')
    seen = set()
    total = 0
    for entry in entries:
        name = entry['path']
        if name.casefold() in seen or name == 'manifest.json':
            raise ValueError('Duplicate deployment member')
        seen.add(name.casefold())
        path = relative(root, name)
        size = path.stat().st_size
        total += size
        if not path.is_file() or size > 64*1024*1024 or total > 512*1024*1024:
            raise ValueError('Deployment exceeds its bounds')
        if size != entry['bytes'] or digest(path) != entry['sha256']:
            raise ValueError('Deployment bytes differ from the inventory')
    actual = set()
    for parent, directories, files in os.walk(root, followlinks=False):
        for name in [*directories, *files]:
            ordinary(Path(parent)/name)
        actual.update((Path(parent)/name).relative_to(root).as_posix().casefold() for name in files)
    if actual != seen | {'manifest.json'}:
        raise ValueError('Deployment contains unexpected files')
    return digest(manifest)


def wheel_metadata(path):
    with zipfile.ZipFile(path) as archive:
        names = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
        if len(names) != len(set(names)) or len(names) > 10000:
            raise ValueError('Invalid wheel member set')
        for name in names:
            # Validate syntax without resolving the archive against a real directory.
            if '\\' in name or ':' in name or name.startswith('/') or any(
                    part in {'', '.', '..'} for part in name.split('/')):
                raise ValueError('Unsafe wheel member')
        metadata = [name for name in names if name.endswith('.dist-info/METADATA')]
        records = [name for name in names if name.endswith('.dist-info/RECORD')]
        if len(metadata) != 1 or len(records) != 1:
            raise ValueError('Missing wheel metadata or RECORD')
        seen = set()
        total = 0
        for name, checksum, length in csv.reader(io.StringIO(archive.read(records[0]).decode('utf-8'))):
            if name in seen or name not in names:
                raise ValueError('Invalid wheel RECORD member')
            seen.add(name)
            body = archive.read(name)
            total += len(body)
            if total > 512*1024*1024:
                raise ValueError('Oversized wheel contents')
            if name == records[0] and not checksum and not length:
                continue
            algorithm, expected = checksum.split('=', 1)
            actual = base64.urlsafe_b64encode(hashlib.sha256(body).digest()).rstrip(b'=').decode()
            if algorithm != 'sha256' or actual != expected or int(length) != len(body):
                raise ValueError('Wheel RECORD mismatch')
        if seen != set(names):
            raise ValueError('Wheel RECORD membership mismatch')
        result = BytesParser().parsebytes(archive.read(metadata[0]))
        return {'name': result['Name'], 'version': result['Version']}


def verify_wheels(root):
    root = Path(root)
    index = json.loads((root/'wheels.json').read_bytes())
    if index['frameworkVersion'] != FRAMEWORK_VERSION or index['sourceCommit'] != FRAMEWORK_COMMIT:
        raise ValueError('Unaccepted framework bundle')
    expected = {}
    for line in (root/'requirements-lock.txt').read_text(encoding='utf-8-sig').splitlines():
        if not line or line.startswith('#'):
            continue
        package, version = line.split(';', 1)[0].strip().split('==')
        expected[re.sub('[-_.]+', '-', package).lower()] = version
    observed = {}
    for entry in index['files']:
        path = relative(root, entry['path'])
        if not entry['path'].startswith('wheels/') or path.suffix != '.whl':
            raise ValueError('Invalid wheel bundle path')
        if path.stat().st_size != entry['bytes'] or digest(path) != entry['sha256']:
            raise ValueError('Wheel bundle bytes changed')
        info = wheel_metadata(path)
        key = re.sub('[-_.]+', '-', info['name']).lower()
        if key in observed:
            raise ValueError('Duplicate wheel distribution')
        observed[key] = info['version']
    if observed != expected:
        raise ValueError('Wheel distributions differ from exact dependency pins')
    if {p.name for p in (root/'wheels').iterdir()} != {Path(e['path']).name for e in index['files']}:
        raise ValueError('Unexpected wheel bundle member')
    return digest(root/'wheels.json')


def atomic_json(path, value):
    import uuid
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name+'.'+uuid.uuid4().hex+'.tmp')
    try:
        with temporary.open('x', encoding='utf-8', newline='\n') as stream:
            json.dump(value, stream, indent=2)
            stream.write('\n')
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
