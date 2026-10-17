"""Exercise real worker upgrades behind one persistent MCP host. Never attach to TIA."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading
import time


class Client:
    def __init__(self, executable, root):
        self.process = subprocess.Popen([str(executable), '--artifact-root='+str(root/'artifacts'), '--extensions-root='+str(root/'external')],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', bufsize=1)
        self.messages = queue.Queue(); self.errors = []; self.counter = 0; self.records = []
        def read():
            try:
                for line in self.process.stdout: self.messages.put(json.loads(line))
            except Exception as exc: self.messages.put(exc)
        threading.Thread(target=read,daemon=True).start()
        threading.Thread(target=lambda:self.errors.extend(self.process.stderr),daemon=True).start()
        self.send('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'extension-reload-test','version':'1.0.0'}})
        self.send('notifications/initialized',{},True)
    def send(self, method, params, notification=False):
        self.counter+=1
        packet={'jsonrpc':'2.0','method':method,'params':params}
        if not notification:packet['id']=self.counter
        self.process.stdin.write(json.dumps(packet,ensure_ascii=False)+'\n');self.process.stdin.flush()
        if notification:return
        deadline=time.monotonic()+60
        while True:
            value=self.messages.get(timeout=max(0.1,deadline-time.monotonic()))
            if isinstance(value,Exception):raise value
            if value.get('id')==self.counter:
                self.records.append({'request':packet,'response':value})
                assert 'error' not in value,value
                return value['result']
    def call(self,name,**args):
        value=self.send('tools/call',{'name':name,'arguments':args})
        assert not value.get('isError'),value
        return value['structuredContent']['result']
    def rejected(self,name,**args):
        value=self.send('tools/call',{'name':name,'arguments':args});assert value.get('isError'),value
        return value['content'][0]['text']
    def close(self):
        self.process.stdin.close()
        try:self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:self.process.kill();self.process.wait();raise AssertionError('Host shutdown timed out')
        assert self.process.returncode==0,''.join(self.errors)


def sha(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def manifest(directory):
    path=directory/'extension.json';data=json.loads(path.read_text(encoding='utf-8-sig'))
    data['files']=[{'path':f.relative_to(directory).as_posix(),'bytes':f.stat().st_size,'sha256':sha(f)} for f in sorted(directory.rglob('*')) if f.is_file() and f.name!='extension.json']
    path.write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8')
def alive(pid):
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.restype=ctypes.c_void_p;kernel.OpenProcess.argtypes=[ctypes.c_uint32,ctypes.c_int,ctypes.c_uint32]
    kernel.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_uint32];kernel.CloseHandle.argtypes=[ctypes.c_void_p]
    handle=kernel.OpenProcess(0x100000,False,pid)
    if not handle:return False
    try:return kernel.WaitForSingleObject(handle,0)==258
    finally:kernel.CloseHandle(handle)


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('executable',type=Path);parser.add_argument('--report',type=Path)
    args=parser.parse_args();executable=args.executable.resolve();repo=Path(__file__).resolve().parent.parent
    host_hash=sha(executable);workers=set();client=None
    with tempfile.TemporaryDirectory(prefix='tia-extension-reload-') as temporary:
        root=Path(temporary)
        def install(bundle, reject=False):
            completed=subprocess.run(['powershell.exe','-NoProfile','-File',str(repo/'build/Install-Extension.ps1'),'-BundleDirectory',str(bundle),'-ExtensionRoot',str(root/'external')],capture_output=True,text=True,encoding='utf-8')
            if reject:assert completed.returncode!=0,completed.stdout;return
            assert completed.returncode==0,completed.stdout+completed.stderr
            return json.loads(completed.stdout)
        try:
            client=Client(executable,root);host_pid=client.process.pid
            def catalog():
                result=client.call('tia_list_extensions');assert result['hostProcessId']==host_pid
                for module in result['modules']:workers.add(module['workerProcessId'])
                return {m['module']:m for m in result['modules']}
            tools_before=client.send('tools/list',{})['tools'];initial=catalog()
            parts=[int(p) for p in initial['comfort']['version'].split('.')];parts[-1]+=1;next_version='.'.join(map(str,parts))
            completed=subprocess.run(['powershell.exe','-NoProfile','-File',str(repo/'build/Build-Extensions.ps1'),'-Destination',str(root/'built'),'-WorkerVersion',next_version,'-Modules','comfort'],capture_output=True,text=True,encoding='utf-8',timeout=120)
            assert completed.returncode==0,completed.stdout+completed.stderr
            registry=json.loads((root/'built/active.json').read_text(encoding='utf-8-sig'));bundle=root/'built'/registry['comfort']
            (bundle/'recipes.json').write_text(json.dumps([{'name':'reload_grid_preset','description':'Added while the host stays connected','xPath':'//GridColor','newValue':'9, 8, 7'}]),encoding='utf-8');manifest(bundle)
            installed=install(bundle);good_registry=(root/'external/active.json').read_bytes()
            assert catalog()['comfort']['generation']==initial['comfort']['generation'],'Update must wait for explicit reload'
            reloaded=client.call('tia_reload_extensions',module='comfort');assert reloaded['hostProcessId']==host_pid
            updated=catalog()['comfort'];assert updated['version']==next_version and updated['generation']!=initial['comfort']['generation']
            assert updated['workerProcessId']!=initial['comfort']['workerProcessId']
            assert any(o['name']=='reload_grid_preset' for o in updated['operations'])
            assert tools_before==client.send('tools/list',{})['tools']
            probe={'xml':'<Document><Hmi.Screen.Screen><AttributeList><Name>Café — テスト</Name></AttributeList></Hmi.Screen.Screen></Document>','editable':True}
            assert client.call('tia_read_extension',module='comfort',operation='inspect_xml',argumentsJson=json.dumps(probe,ensure_ascii=False))['data']['name']=='Café — テスト'
            # Invoke the new transform through the identical worker protocol, without any native project.
            peer=subprocess.Popen([str(Path(installed['bundleDirectory'])/'SiemensMCPServer.ExtensionWorker.exe'),'--module=comfort'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8')
            request={'id':1,'operation':'reload_grid_preset','input':{'snapshot':probe['xml'].replace('</AttributeList>','<GridColor>0, 0, 0</GridColor></AttributeList>'),'arguments':{'expectedValue':'0, 0, 0'}}}
            stdout,stderr=peer.communicate(json.dumps(request)+'\n',timeout=20);assert peer.returncode==0,stderr
            assert '<GridColor>9, 8, 7</GridColor>' in json.loads(stdout)['result']['candidateXml']
            invalid=root/'invalid';shutil.copytree(bundle,invalid)
            (invalid/'recipes.json').write_text('[{"name":"edit_xml","xPath":"//Name","newValue":"bad"}]',encoding='utf-8');manifest(invalid)
            bad=install(invalid)
            failed=client.rejected('tia_reload_extensions',module='comfort')
            assert catalog()['comfort']['generation']==updated['generation']
            assert client.call('tia_read_extension',module='comfort',operation='inspect_xml',argumentsJson=json.dumps(probe))['data']['name']==probe['xml'].split('<Name>')[1].split('</Name>')[0]
            (Path(bad['bundleDirectory'])/'SiemensMCPServer.ExtensionWorker.exe').write_bytes(b'bad hash')
            client.rejected('tia_reload_extensions',module='comfort');assert catalog()['comfort']['generation']==updated['generation']
            for control in ['tampered','unlisted','traversal','siemens_dll']:
                changed=root/control;shutil.copytree(bundle,changed)
                if control=='tampered':(changed/'SiemensMCPServer.ExtensionWorker.exe').write_bytes(b'tampered')
                elif control=='unlisted':(changed/'extra.txt').write_text('unlisted')
                elif control=='traversal':
                    value=json.loads((changed/'extension.json').read_text());value['files'][0]['path']='../outside';(changed/'extension.json').write_text(json.dumps(value))
                else:(changed/'Siemens.Engineering.dll').write_bytes(b'forbidden');manifest(changed)
                before=(root/'external/active.json').read_bytes();install(changed,True);assert (root/'external/active.json').read_bytes()==before
            temporary_registry=root/'external/active-test.tmp';temporary_registry.write_bytes(good_registry);os.replace(temporary_registry,root/'external/active.json')
            client.call('tia_reload_extensions',module='comfort');restored=catalog()['comfort']
            os.kill(restored['workerProcessId'],15)
            client.rejected('tia_read_extension',module='comfort',operation='inspect_xml',argumentsJson=json.dumps(probe))
            client.call('tia_reload_extensions',module='comfort');recovered=catalog()['comfort']
            assert recovered['generation']!=restored['generation']
            # A valid-handshake peer that hangs proves the deadline kills only the pure worker.
            hung=root/'hung';hung.mkdir()
            hello={'id':1,'result':{'protocol':1,'module':'comfort','version':'0.7.0','operations':[{'name':'inspect_xml','description':'timeout fixture','route':'local','preview':False,'inputSchema':{'type':'object'}}]}}
            literal=json.dumps(hello).replace('"','""')
            (hung/'Program.cs').write_text('using System; using System.Threading; class Program { static void Main() { Console.ReadLine(); Console.WriteLine(@"'+literal+'"); Console.ReadLine(); Thread.Sleep(60000); } }',encoding='utf-8')
            (hung/'fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net48</TargetFramework><AssemblyName>SiemensMCPServer.ExtensionWorker</AssemblyName><Version>0.7.0</Version></PropertyGroup></Project>')
            completed=subprocess.run(['dotnet','build',str(hung/'fixture.csproj'),'-c','Release','-o',str(hung/'payload'),'-v','quiet'],capture_output=True,text=True,encoding='utf-8',timeout=60)
            assert completed.returncode==0,completed.stdout+completed.stderr
            (hung/'payload/extension.json').write_text(json.dumps({'format':'tia-extension-v1','protocol':1,'module':'comfort','version':'0.7.0','entryPoint':'SiemensMCPServer.ExtensionWorker.exe'}))
            manifest(hung/'payload');install(hung/'payload');client.call('tia_reload_extensions',module='comfort');timed=catalog()['comfort']
            started=time.monotonic();timeout_error=client.rejected('tia_read_extension',module='comfort',operation='inspect_xml',argumentsJson='{}');elapsed=time.monotonic()-started
            assert 'deadline' in timeout_error and 25<elapsed<45,timeout_error
            assert not alive(timed['workerProcessId'])
            temporary_registry.write_bytes(good_registry);os.replace(temporary_registry,root/'external/active.json')
            client.call('tia_reload_extensions',module='comfort');catalog()
            assert sha(executable)==host_hash and client.process.pid==host_pid
            records=client.records
            client.close();client=None
            for _ in range(40):
                if not any(alive(pid) for pid in workers):break
                time.sleep(0.05)
            assert not any(alive(pid) for pid in workers),'Owned workers leaked after host shutdown'
            report={'hostSha256':host_hash,'hostProcessId':host_pid,'hostRestarted':False,'codexRestarted':False,'initialWorker':initial['comfort'],'updatedWorker':updated,
                'newOperation':'reload_grid_preset','newOperationTransform':'passed on supplied XML','toolSchemaUnchanged':True,'failedReloadPreservedGeneration':True,
                'tamperedBundleRejected':True,'installerNegativeControls':['tampered','unlisted','traversal','siemens_dll'],'workerCrashRecovery':'passed','workerShutdown':'passed',
                'workerTimeout':'passed; hung pure worker killed, host recovered','workerTimeoutSeconds':round(elapsed,2),
                'scope':'Persistent STDIO transport; installed Codex chat activation is a separate initial host upgrade',
                'nativeProjectAttached':False,'nativeProjectChanged':False,'nativeAttachedReload':'pending separate trial','calls':records}
            if args.report:args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(report,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
            print(json.dumps({k:v for k,v in report.items() if k not in ['calls','initialWorker','updatedWorker']},indent=2))
        finally:
            if client:client.close()

if __name__=='__main__':main()
