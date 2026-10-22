"""Retained Windows lifetime tests using a bare harmless .NET fixture, never MCP."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT/'scripts/fixtures/offline-lifetime/bin/Release/net8.0-windows/win-x64/HarmlessLifetime.exe'


def ready(process):
    lines = queue.Queue()
    threading.Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
    try:
        line = lines.get(timeout=10)
    except queue.Empty:
        raise AssertionError('Harmless fixture startup exceeded ten seconds')
    if not line:
        raise AssertionError('Harmless fixture failed: '+process.stderr.read().decode(errors='replace'))
    return json.loads(line)


@unittest.skipUnless(os.name == 'nt', 'Windows Job/parent-handle fixtures')
class LifetimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build = subprocess.run(['dotnet', 'build', str(ROOT/'scripts/fixtures/offline-lifetime/HarmlessLifetime.csproj'),
                                '-c', 'Release', '--nologo', '-v', 'quiet'], capture_output=True, timeout=60)
        if build.returncode:
            raise AssertionError(build.stdout.decode(errors='replace')+build.stderr.decode(errors='replace'))

    def start(self, mode='echo'):
        process = subprocess.Popen([str(EXE), mode], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
        self.addCleanup(self.close, process)
        return process

    @staticmethod
    def close(process):
        if process.poll() is None:
            process.kill()  # original Popen handle, never a PID lookup
            process.wait(timeout=10)
        for stream in (process.stdin, process.stdout, process.stderr):
            if stream:
                stream.close()

    def test_plain_bytes_eof_and_creation_identity(self):
        process = self.start()
        receipt = ready(process)['identity']
        self.assertEqual(receipt['owner']['pid'], process.pid)
        self.assertLessEqual(int(receipt['parent']['creationFileTime']), int(receipt['owner']['creationFileTime']))
        self.assertEqual(receipt['activeProcessLimit'], 1)
        payload = ('bytes Español 😀\r\n'*100).encode()
        stdout, stderr = process.communicate(payload, timeout=10)
        self.assertEqual(process.returncode, 0, stderr.decode(errors='replace'))
        self.assertEqual(stdout, payload)

    def test_job_rejects_child_creation(self):
        process = self.start('child-refusal')
        ready(process)
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, stderr.decode(errors='replace'))
        self.assertEqual(stdout.strip(), b'child-blocked')

    def test_windows_subsystem_preserves_stdio_without_a_console_helper(self):
        # Regression for Windows creating conhost.exe before a console app can
        # assign itself to its one-process Job. Inspect actual creation identities;
        # older processes with a reused parent PID do not establish descendants.
        image = EXE.read_bytes()
        pe_offset = int.from_bytes(image[0x3c:0x40], 'little')
        self.assertEqual(image[pe_offset:pe_offset+4], b'PE\0\0')
        self.assertEqual(int.from_bytes(image[pe_offset+24+68:pe_offset+24+70], 'little'), 2)
        process = self.start()
        root = ready(process)['identity']['owner']
        self.assertEqual(self.observed_children(root), [])
        stdout, stderr = process.communicate('stdio Español 😀'.encode(), timeout=10)
        self.assertEqual(process.returncode, 0, stderr.decode(errors='replace'))
        self.assertEqual(stdout, 'stdio Español 😀'.encode())

    @staticmethod
    def observed_children(root, duration=.3):
        class Entry(ctypes.Structure):
            _fields_ = [('size',wintypes.DWORD),('usage',wintypes.DWORD),('pid',wintypes.DWORD),
                        ('heap',ctypes.c_size_t),('module',wintypes.DWORD),('threads',wintypes.DWORD),
                        ('parent',wintypes.DWORD),('priority',ctypes.c_long),('flags',wintypes.DWORD),('name',wintypes.WCHAR*260)]
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD,wintypes.DWORD]; kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        kernel.Process32FirstW.argtypes = [wintypes.HANDLE,ctypes.POINTER(Entry)]
        kernel.Process32NextW.argtypes = [wintypes.HANDLE,ctypes.POINTER(Entry)]
        kernel.OpenProcess.argtypes = [wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]; kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.GetProcessTimes.argtypes = [wintypes.HANDLE]+[ctypes.POINTER(wintypes.FILETIME)]*4
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        seen = {}; deadline = time.monotonic()+duration
        while time.monotonic() < deadline:
            snapshot = kernel.CreateToolhelp32Snapshot(2,0)
            if snapshot == ctypes.c_void_p(-1).value: raise ctypes.WinError(ctypes.get_last_error())
            try:
                entry = Entry(); entry.size = ctypes.sizeof(entry)
                present = kernel.Process32FirstW(snapshot,ctypes.byref(entry))
                while present:
                    if entry.parent == root['pid']:
                        handle = kernel.OpenProcess(0x1000,False,entry.pid)
                        if handle:
                            try:
                                times = [wintypes.FILETIME() for _ in range(4)]
                                if kernel.GetProcessTimes(handle,*(ctypes.byref(value) for value in times)):
                                    creation = times[0].dwLowDateTime | (times[0].dwHighDateTime << 32)
                                    if creation >= int(root['creationFileTime']):
                                        seen[(entry.pid,creation)] = {'pid':entry.pid,'creationFileTime':str(creation),'name':entry.name}
                            finally: kernel.CloseHandle(handle)
                    present = kernel.Process32NextW(snapshot,ctypes.byref(entry))
            finally: kernel.CloseHandle(snapshot)
            time.sleep(.03)
        return list(seen.values())

    def test_capacity_refusal_and_crash_releases_slot(self):
        active = [self.start() for _ in range(4)]
        for process in active:
            ready(process)
        denied = self.start()
        stdout, stderr = denied.communicate(timeout=10)
        self.assertEqual(denied.returncode, 1)
        self.assertFalse(stdout)
        self.assertIn(b'capacity is unavailable', stderr)
        active[0].kill(); active[0].wait(timeout=10)
        replacement = self.start()
        ready(replacement)
        self.assertIsNone(replacement.poll())

    def test_parent_death_exits_idle_root_using_retained_handle(self):
        # Wrapper retains the child until this test permits it to exit. The child
        # ignores stdin: EOF alone cannot make this assertion pass.
        code = ('import subprocess,sys,json,os;'
                'p=subprocess.Popen([sys.argv[1],"wait"],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE);'
                'print(p.stdout.readline().decode().strip(),flush=True);'
                'sys.stdin.buffer.read(1);os._exit(0)')
        wrapper = subprocess.Popen([sys.executable, '-B', '-c', code, str(EXE)], stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
        self.addCleanup(self.close, wrapper)
        receipt = ready(wrapper)['identity']['owner']
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.GetProcessTimes.argtypes = [wintypes.HANDLE]+[ctypes.POINTER(wintypes.FILETIME)]*4
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x100000 | 0x1000, False, receipt['pid'])
        self.assertTrue(handle)
        try:
            values = [wintypes.FILETIME() for _ in range(4)]
            self.assertTrue(kernel.GetProcessTimes(handle, *(ctypes.byref(value) for value in values)))
            creation = values[0].dwLowDateTime | (values[0].dwHighDateTime << 32)
            self.assertEqual(str(creation), receipt['creationFileTime'])
            wrapper.stdin.write(b'x'); wrapper.stdin.flush()
            wrapper.wait(timeout=10)
            self.assertEqual(kernel.WaitForSingleObject(handle, 10000), 0)
            exit_code = wintypes.DWORD()
            self.assertTrue(kernel.GetExitCodeProcess(handle, ctypes.byref(exit_code)))
            self.assertEqual(exit_code.value, 125)
        finally:
            kernel.CloseHandle(handle)


if __name__ == '__main__':
    unittest.main(verbosity=2)
