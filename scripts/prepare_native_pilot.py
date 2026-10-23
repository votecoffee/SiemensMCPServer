"""Verify an extracted candidate and write isolated pilot inputs. Never launch/register."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath


def digest(path):
    result=hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024*1024),b''): result.update(block)
    return result.hexdigest()


def ordinary(path):
    for item in (path,*path.parents):
        if item.is_symlink() or item.is_junction(): raise ValueError('Linked package/output path is unsupported')


def prepare(package,output):
    package=Path(package).absolute(); output=Path(output).absolute(); ordinary(package); ordinary(output)
    rows=json.loads((package/'manifest.json').read_bytes()); expected=set()
    for row in rows:
        relative=row['path']; parts=PurePosixPath(relative)
        if not relative or '\\' in relative or ':' in relative or parts.is_absolute() or any(p in ('','.','..') for p in relative.split('/')) or relative in expected:
            raise ValueError('Unsafe/duplicate payload path')
        expected.add(relative); path=package/relative; ordinary(path)
        if path.stat().st_size!=row['bytes'] or digest(path)!=row['sha256']: raise ValueError('Payload hash/length mismatch: '+relative)
    actual={p.relative_to(package).as_posix() for p in package.rglob('*') if p.is_file()}
    if actual!=expected|{'manifest.json'}: raise ValueError('Payload has missing/unlisted files')
    info=json.loads((package/'build-info.json').read_bytes())
    if info['profile']!='native-direct-candidate' or info['mcpStarted'] is not False: raise ValueError('Not an unstarted native candidate')
    if output==package or package in output.parents: raise ValueError('Pilot state must remain outside immutable package')
    output.mkdir(parents=True,exist_ok=False)
    settings=output/'safeguards.json'
    policy=dict(SchemaVersion=1,McpWritesEnabled=False,AllowApply=False,AllowCompile=False,AllowSave=False,AllowArchive=False,AllowReopen=False,
                MaxMcpFrontends=1,MaxOwnedResources=8,MaxProcessTreeSize=8,MaxOwnedJobMemoryMiB=1024,StdioDrainTimeoutSeconds=15,AnalysisParallelism=1)
    settings.write_text(json.dumps(policy,indent=2)+'\n',encoding='utf-8')
    executable=package/'server/SiemensMCPServer.Mcp.exe'
    connection=dict(command=str(executable),args=['--tia-version='+info['tiaPortalApi'],'--settings-file='+str(settings),'--artifact-root='+str(output/'sessions'),
                                                '--extensions-root='+str(package/'server/extensions'),'--analysis-parallelism=1'],cwd=str(package),
                    env=dict(SIEMENS_MCP_MANAGEMENT_REGISTRY=str(output/'registry'),COR_ENABLE_PROFILING='0',CORECLR_ENABLE_PROFILING='0',COR_PROFILER='',COR_PROFILER_PATH=''))
    result=dict(profile=info['profile'],version=info['displayVersion'],executableSha256=digest(executable),payloadManifestSha256=digest(package/'manifest.json'),
                sourceCommit=info['sourceCommit'],connection=connection,started=False,registered=False,writesEnabled=False,
                acceptance='Metadata/workers first, then separately coordinated fresh disposable TIA V15.1 project; no customer/controller operation.')
    (output/'connection.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--package',type=Path,required=True); parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args(); print(json.dumps(prepare(args.package,args.output),indent=2))
