"""Project maintained native guides into a direct-host, data-only package."""
import argparse
import hashlib
import json
from pathlib import Path
import re

WORKFLOWS = ('edit-hmi-text','edit-siemens-plc','inspect-libraries','inspect-project-texts')
DIRECT = ('This direct Siemens host exposes the engineering tools named below. Discover their live schemas and keep the same owning stdio session through completion or reconciliation. Attach only to an explicit TIA PID/project and select the exact target. Guides never authorize writes. Apply a matching reviewed candidate once; compile and save are separate. Use `tia_detach` only after completion or reconciliation. Detach/EOF never saves, closes or kills user TIA. If a native reply is uncertain, retain its artifacts and attachment claim, inspect native state and do not replay or switch sessions to bypass it.')
LINK = re.compile(r'\]\(([^)]+\.md(?:#[^)]*)?)\)')


def build(repo, destination):
    repo=Path(repo).resolve(); destination=Path(destination).resolve()
    destination.mkdir(parents=True,exist_ok=False)
    rows=[]; pending=[]; seen=set()
    def emit(identifier,kind,relative,text):
        raw=text.encode('utf-8'); path=destination/relative; path.parent.mkdir(parents=True,exist_ok=True); path.write_bytes(raw)
        rows.append(dict(id=identifier,kind=kind,path=relative,sha256=hashlib.sha256(raw).hexdigest(),bytes=len(raw)))
    def links(text,source,workflow):
        def replace(match):
            target,separator,fragment=match[1].partition('#')
            if '://' in target: return match[0]
            resolved=(source.parent/target).resolve()
            if not resolved.is_relative_to(repo) or not resolved.is_file(): raise ValueError('Missing/outside Markdown reference: '+target)
            name=resolved.name
            if name in ('README.md','APP-HOST.md'): resolved=repo/'plugin-native/README.md'; name='NATIVE-DIRECT.md'
            pending.append((resolved,name))
            return ']('+('references/' if workflow else '')+name+(separator+fragment if separator else '')+')'
        return LINK.sub(replace,text)
    for name in WORKFLOWS:
        source=repo/'plugin/guidance'/name/'SKILL.md'; text=source.read_text(encoding='utf-8-sig')
        paragraphs=text.split('\n\n')
        matches=[i for i,p in enumerate(paragraphs) if p.startswith('The standalone Codex plugin uses an app-owned framework gateway.')]
        if len(matches)!=1: raise ValueError('Expected one maintained gateway introduction in '+name)
        paragraphs[matches[0]]=DIRECT
        text=links('\n\n'.join(paragraphs),source,True)
        emit(name,'workflow',name+'.md',text)
    while pending:
        source,name=pending.pop(0)
        if name in seen: continue
        seen.add(name)
        text=source.read_text(encoding='utf-8-sig')
        if name=='NATIVE-DIRECT.md': text=text.replace('[LICENSE](../LICENSE)','MIT license is included at the package root.')
        text=text.replace('The MCP server has 51 tools.','The native direct candidate declares 56 tools (52 engineering/lifecycle and four guidance/doctor tools).')
        if name=='CLASSIC-TEXT-LIST-WORKFLOW.md':
            text=re.sub(r'In the standalone gateway,.*?`tia_list_extensions` advertises', 'Inspect the direct live tool schemas. `tia_list_extensions` advertises',text,count=1)
        if name=='CONCURRENCY.md':
            text=re.sub(r'These concurrency lanes apply.*?engineering context\.', 'These concurrency lanes apply to the direct native candidate. Each connection owns its session; retained attachment claims prevent a second host from attaching to the same TIA process.',text,count=1)
        # Remaining references can describe historical profiles; their native
        # acceptance claims stay dated and are never promoted by packaging.
        text=links(text,source,False)
        emit(name,'reference','references/'+name,text)
    if len(rows)>100: raise ValueError('Native guide catalog exceeds reader capacity')
    manifest=dict(format='siemens-native-guidance-v1',files=sorted(rows,key=lambda r:r['id']))
    (destination/'guidance-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    return manifest


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--repo',type=Path,required=True); parser.add_argument('--destination',type=Path,required=True)
    args=parser.parse_args(); print(json.dumps(dict(files=len(build(args.repo,args.destination)['files']),runtimeStarted=False)))
