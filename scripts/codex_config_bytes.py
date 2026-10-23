"""Explicit-path UTF-8 TOML publication. No CLI, live-path discovery or text I/O."""
import os
import math
from pathlib import Path
import tempfile
import tomllib


def parse(raw):
    # CRLF is legal TOML; isolated CR (including CRCRLF) is never repaired here.
    if b'\r' in raw.replace(b'\r\n',b''): raise ValueError('TOML contains isolated CR/CRCRLF; preserve it for inspection')
    return tomllib.loads(raw.decode('utf-8-sig'))


def append_tables(original, addition, *, newline=None):
    """Preserve parsed existing values; emit explicit CRLF on Windows once."""
    before=parse(original)
    added=addition.encode('utf-8'); parse(added)
    first=next((line.lstrip() for line in addition.splitlines() if line.strip() and not line.lstrip().startswith('#')),'')
    if not first.startswith('['): raise ValueError('Append must start a TOML table, never inherit the previous table scope')
    ending=(b'\r\n' if os.name=='nt' else b'\n') if newline is None else newline
    if ending not in (b'\n',b'\r\n'): raise ValueError('Choose LF or CRLF')
    bom=b'\xef\xbb\xbf' if original.startswith(b'\xef\xbb\xbf') else b''
    text=original[len(bom):].replace(b'\r\n',b'\n')
    suffix=added.replace(b'\r\n',b'\n')
    candidate=bom+(text+b'\n'+suffix).replace(b'\n',ending)
    after=parse(candidate)
    # Recursively reject modifications to any existing setting, including tables.
    def unchanged(old,new):
        if isinstance(old,dict):
            if not isinstance(new,dict) or not old.keys()<=new.keys(): raise ValueError('An existing TOML table changed')
            for key,value in old.items(): unchanged(value,new[key])
        elif isinstance(old,list):
            if not isinstance(new,list) or len(old)!=len(new): raise ValueError('An existing TOML array changed')
            for left,right in zip(old,new): unchanged(left,right)
        elif isinstance(old,float) and isinstance(new,float) and math.isnan(old) and math.isnan(new): pass
        elif type(old) is not type(new) or old!=new: raise ValueError('An existing TOML value changed')
    unchanged(before,after)
    return candidate


def replace_if_unchanged(path, expected, candidate):
    """Binary temporary + fsync + replace, with a repeated preimage check.

    Callers still need exclusive publication coordination. This is not an OS
    compare-and-swap or a lock shared with other Codex writers.
    """
    path=Path(path); parse(expected); parse(candidate)
    if path.read_bytes()!=expected: raise ValueError('TOML preimage changed; no retry')
    descriptor,temporary=tempfile.mkstemp(prefix='.siemens-config-',suffix='.tmp',dir=path.parent)
    try:
        with os.fdopen(descriptor,'wb') as stream:
            stream.write(candidate); stream.flush(); os.fsync(stream.fileno())
        if path.read_bytes()!=expected: raise ValueError('TOML changed during preparation; no retry')
        os.replace(temporary,path)
    finally:
        if os.path.exists(temporary): os.unlink(temporary)
