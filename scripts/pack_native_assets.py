"""Build a bounded, fully indexed native asset ZIP. Never executes or deletes assets."""
import argparse
import hashlib
import json
import re
import stat
import zipfile
from pathlib import Path

INDEX = 'archive-manifest.json'
LIMIT = 128 * 1024 * 1024
MEMBER_LIMIT = 64 * 1024 * 1024

def read_member(path):
    if not stat.S_ISREG(path.lstat().st_mode):raise ValueError('Asset is not a regular file')
    with path.open('rb') as stream:data=stream.read(MEMBER_LIMIT+1)
    if len(data)>MEMBER_LIMIT:raise ValueError('Asset member exceeds bound')
    return data

def relative(name):
    parts=name.split('/')
    if not name or len(name)>1024 or len(parts)>32 or '\\' in name or ':' in name or any(
        part in ('','.','..') or part.endswith(('.', ' ')) or re.search(r'[<>"|?*\x00-\x1f]',part)
        or re.match(r'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)',part,re.I) for part in parts):
        raise ValueError('Unsafe asset path')
    if parts[-1].lower().startswith('siemens.engineering') or name.lower().endswith('.whl') or parts[-1].lower()=='launch.py':
        raise ValueError('Vendor/framework runtime in native assets')

def pack(source, destination, purpose):
    source,destination=Path(source).absolute(),Path(destination).absolute()
    if purpose not in ('extensions','native-guidance') or destination.is_relative_to(source) or destination.exists():
        raise ValueError('Use a new archive and a known purpose')
    for path in [source,*source.parents,*destination.parents]:
        if path.exists() and (path.is_symlink() or path.is_junction() or getattr(path.lstat(),'st_file_attributes',0)&getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',1024)):
            raise ValueError('Linked asset root')
    records=[];seen=set();total=0
    for path in sorted(source.rglob('*')):
        if path.is_symlink() or path.is_junction() or getattr(path.lstat(),'st_file_attributes',0)&getattr(stat,'FILE_ATTRIBUTE_REPARSE_POINT',1024):raise ValueError('Linked asset member')
        if path.is_dir():continue
        name=path.relative_to(source).as_posix();relative(name)
        if name.lower()==INDEX or name.casefold() in seen:raise ValueError('Duplicate/reserved asset member')
        seen.add(name.casefold());size=path.stat().st_size;total+=size
        if size>MEMBER_LIMIT or total>LIMIT or len(records)>=512:raise ValueError('Asset inventory exceeds bound')
        data=read_member(path)
        if len(data)!=size:raise ValueError('Asset changed during inventory')
        records.append(dict(path=name,bytes=size,sha256=hashlib.sha256(data).hexdigest()))
    if not records:raise ValueError('Empty asset inventory')
    index=json.dumps(dict(format='siemens-native-assets-v1',purpose=purpose,files=records),separators=(',',':')).encode()
    if len(index)>1024*1024:raise ValueError('Asset index exceeds bound')
    destination.parent.mkdir(parents=True,exist_ok=True)
    with zipfile.ZipFile(destination,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
        archive.writestr(INDEX,index)
        for record in records:
            path=source/record['path'];data=read_member(path)
            if len(data)!=record['bytes'] or hashlib.sha256(data).hexdigest()!=record['sha256']:raise ValueError('Asset changed during packaging')
            archive.writestr(record['path'],data)
    if destination.stat().st_size>MEMBER_LIMIT:raise ValueError('Archive exceeds compressed bound; staging preserved')
    with zipfile.ZipFile(destination) as archive:
        if archive.testzip() is not None:raise ValueError('Archive CRC verification failed')
        if len(archive.infolist())!=len(records)+1 or archive.read(INDEX)!=index:raise ValueError('Archive index changed')
        for record in records:
            data=archive.read(record['path'])
            if len(data)!=record['bytes'] or hashlib.sha256(data).hexdigest()!=record['sha256']:raise ValueError('Archive body differs')
    return dict(path=str(destination),members=len(records),decodedBytes=total,bytes=destination.stat().st_size,sha256=hashlib.sha256(destination.read_bytes()).hexdigest())

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',required=True,type=Path);parser.add_argument('--destination',required=True,type=Path)
    parser.add_argument('--purpose',required=True,choices=('extensions','native-guidance'))
    args=parser.parse_args();print(json.dumps(pack(args.source,args.destination,args.purpose)))
