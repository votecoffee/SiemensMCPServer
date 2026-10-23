"""Only the harmless net48 lifetime fixture runs. Never MCP/TIA/feature workers."""
import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import subprocess
import sys
import unittest
from test_offline_lifetime import ready, LifetimeTests

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT/'scripts/fixtures/native-lifetime/bin/Release/net48/HarmlessNativeLifetime.exe'


@unittest.skipUnless(os.name == 'nt', 'Windows net48 containment fixture')
class NativeLifetimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(['dotnet','build',str(ROOT/'scripts/fixtures/native-lifetime/HarmlessNativeLifetime.csproj'),'-c','Release','--nologo','-v','quiet'],capture_output=True,timeout=60)
        if build.returncode: raise AssertionError(build.stdout.decode(errors='replace')+build.stderr.decode(errors='replace'))

    close = staticmethod(LifetimeTests.close)

    def start(self, mode='echo'):
        process = subprocess.Popen([str(EXE),mode],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
        self.addCleanup(self.close, process)
        return process

    def test_unicode_eof_and_windows_subsystem_without_console_child(self):
        image = EXE.read_bytes(); pe = int.from_bytes(image[0x3c:0x40],'little')
        self.assertEqual(int.from_bytes(image[pe+24+68:pe+24+70],'little'),2)
        process = self.start(); receipt = ready(process)['identity']
        self.assertEqual(receipt['root']['pid'],process.pid)
        self.assertEqual(LifetimeTests.observed_children(receipt['root']),[])
        payload = 'Español 😀\r\n'.encode()
        out, err = process.communicate(payload,timeout=5)
        self.assertEqual(process.returncode,0,err.decode(errors='replace')); self.assertEqual(out,payload)

    def test_capacity_and_abandoned_slot_recovery(self):
        active = [self.start('parent-wait') for _ in range(2)]
        for process in active: ready(process)
        denied = self.start(); out,err = denied.communicate(timeout=5)
        self.assertEqual(denied.returncode,1); self.assertFalse(out); self.assertIn(b'capacity unavailable',err)
        active[0].kill(); active[0].wait(timeout=5)
        replacement = self.start(); ready(replacement)

    def test_eof_hung_drain_exits_124(self):
        process=self.start('force-eof'); ready(process)
        out,err=process.communicate(timeout=5)
        self.assertEqual(process.returncode,124,err.decode(errors='replace'))

    def test_root_crash_kills_only_its_inherited_child(self):
        peer=self.start('parent-wait'); ready(peer)
        process=self.start('tree'); ready(process); child=ready(process)
        self.assertEqual(child['ready'],'child-ready')
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]; kernel.OpenProcess.restype=wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD]; kernel.CloseHandle.argtypes=[wintypes.HANDLE]
        handle=kernel.OpenProcess(0x100000,False,child['child']); self.assertTrue(handle)
        try:
            process.kill(); process.wait(timeout=5)
            self.assertEqual(kernel.WaitForSingleObject(handle,5000),0)
            self.assertIsNone(peer.poll())
        finally: kernel.CloseHandle(handle)

    def test_parent_handle_normal_and_forced_exit_without_eof(self):
        for mode,expected in [('parent-wait',125),('force-parent',127)]:
            with self.subTest(mode=mode):
                # Fixture ignores stdin: parent death cannot be mistaken for EOF.
                code=('import subprocess,sys,os;'
                      'p=subprocess.Popen([sys.argv[1],sys.argv[2]],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE);'
                      'print(p.stdout.readline().decode().strip(),flush=True);'
                      'sys.stdin.buffer.read(1);os._exit(0)')
                parent=subprocess.Popen([sys.executable,'-B','-c',code,str(EXE),mode],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
                self.addCleanup(self.close,parent); root=ready(parent)['identity']['root']
                kernel=ctypes.WinDLL('kernel32',use_last_error=True)
                kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]; kernel.OpenProcess.restype=wintypes.HANDLE
                kernel.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD]; kernel.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]; kernel.CloseHandle.argtypes=[wintypes.HANDLE]
                handle=kernel.OpenProcess(0x100000|0x1000,False,root['pid']); self.assertTrue(handle)
                try:
                    parent.stdin.write(b'x'); parent.stdin.flush(); parent.wait(timeout=5)
                    self.assertEqual(kernel.WaitForSingleObject(handle,5000),0)
                    result=wintypes.DWORD(); self.assertTrue(kernel.GetExitCodeProcess(handle,ctypes.byref(result)))
                    self.assertEqual(result.value,expected)
                finally: kernel.CloseHandle(handle)


if __name__=='__main__': unittest.main(defaultTest='NativeLifetimeTests',verbosity=2)
