"""Real CMD/PowerShell entry points in a disposable checkout; never open a GUI."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]


@unittest.skipUnless(os.name=='nt','Windows root command fixtures')
class RootCommandsTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='Siemens CMD & spaces ');self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.repo=self.root/'repo with spaces & Unicode ü';(self.repo/'build').mkdir(parents=True)
        for relative in ('Run.cmd','InstallAndRun.cmd','build/Invoke-DesktopCommand.ps1','build/Start-Desktop.ps1','build/Install-Desktop.ps1','build/Desktop-Common.ps1'):
            shutil.copyfile(ROOT/relative,self.repo/relative)

    def run_command(self,name,*arguments):
        # Quote every known fixture argument, including CMD metacharacters.
        parts=[str(self.repo/name),*arguments]
        self.assertFalse(any('"' in part or '\n' in part or '\r' in part for part in parts))
        command='cmd.exe /d /s /c "'+' '.join('"'+part+'"' for part in parts)+'"'
        return subprocess.run(command,cwd=self.root,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=20,encoding='utf-8')

    def fixture_install(self):
        root=self.root/'Installed GUI & façade 😀';app=root/'apps/fixture';app.mkdir(parents=True)
        executable=app/'SiemensMCPServer.exe';executable.write_bytes(b'not executable; CheckOnly must never launch')
        receipt=dict(format='siemens-desktop-install-v1',directory='apps/fixture',engineeringVersion='V15.1',version='26.1023.072948',executableSha256=hashlib.sha256(executable.read_bytes()).hexdigest())
        (root/'active.json').write_bytes(json.dumps(receipt).encode())
        return root,executable

    def test_help_from_relocated_checkout_has_no_installation_side_effect(self):
        for name in ('Run.cmd','InstallAndRun.cmd'):
            result=self.run_command(name,'-Help','-NoPause');self.assertEqual(result.returncode,0,result.stdout)
            self.assertIn('GUI only; no Codex configuration changes',result.stdout)
        self.assertFalse((self.repo/'apps').exists())

    def test_run_check_only_preserves_spaced_unicode_and_ampersand_paths(self):
        root,exe=self.fixture_install()
        result=self.run_command('Run.cmd','-InstallRoot',str(root),'-CheckOnly','-NoPause')
        self.assertEqual(result.returncode,0,result.stdout);data=json.loads(result.stdout)
        self.assertEqual(data['state'],'ready');self.assertEqual(data['executable'],str(exe))

    def test_settings_path_is_forwarded_without_creating_policy(self):
        root,_=self.fixture_install();policy=self.root/'Policy & values ü 😀'/'safeguards.json'
        result=self.run_command('Run.cmd','-InstallRoot',str(root),'-CheckOnly','-Safeguards','-SettingsFile',str(policy),'-NoPause')
        self.assertEqual(result.returncode,0,result.stdout);data=json.loads(result.stdout)
        self.assertIn('"--settings-file='+str(policy)+'"',data['arguments']);self.assertFalse(policy.exists())

    def test_real_installer_failure_forwards_api_path_and_creates_no_installation(self):
        root=self.root/'No installation & here';api=self.root/'Missing API & path'
        result=self.run_command('InstallAndRun.cmd','-InstallOnly','-NoPause','-NoShortcut','-InstallRoot',str(root),'-TiaPublicApiDir',str(api))
        self.assertNotEqual(result.returncode,0);self.assertIn(str(api),result.stdout);self.assertFalse(root.exists())

    def test_install_only_and_named_arguments_reach_harmless_installer_fixture(self):
        stub='''param([string]$TiaPortalVersion,[string]$TiaPortalInstallVersion,[string]$TiaPublicApiDir,[string]$InstallRoot,[string]$ProgramsDirectory,[switch]$NoShortcut,[switch]$Run)
$PSBoundParameters | ConvertTo-Json
'''
        (self.repo/'build/Install-Desktop.ps1').write_bytes(stub.encode())
        for only in (False,True):
            extra=['-InstallOnly'] if only else []
            result=self.run_command('InstallAndRun.cmd','-NoPause',*extra,'-TiaPortalVersion','V17','-TiaPortalInstallVersion','V19','-NoShortcut','-InstallRoot',str(self.root/'A & B'),'-TiaPublicApiDir',str(self.root/'API path'),'-ProgramsDirectory',str(self.root/'Menu path'))
            self.assertEqual(result.returncode,0,result.stdout);data=json.loads(result.stdout)
            self.assertEqual(data['Run']['IsPresent'] if isinstance(data['Run'],dict) else data['Run'],not only)
            self.assertEqual(data['TiaPortalVersion'],'V17');self.assertEqual(data['TiaPortalInstallVersion'],'V19')
            self.assertEqual(data['InstallRoot'],str(self.root/'A & B'))

    def test_explicit_child_exit_and_missing_installation_propagate_without_pause(self):
        (self.repo/'build/Install-Desktop.ps1').write_bytes(b'exit 37\r\n')
        result=self.run_command('InstallAndRun.cmd','-InstallOnly','-NoPause');self.assertEqual(result.returncode,37,result.stdout)
        result=self.run_command('Run.cmd','-InstallRoot',str(self.root/'missing'),'-CheckOnly','-NoPause')
        self.assertNotEqual(result.returncode,0);self.assertIn('not installed here',result.stdout)


if __name__=='__main__':unittest.main(verbosity=2)
