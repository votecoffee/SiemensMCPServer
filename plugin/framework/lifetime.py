"""OS-owned frontend bounds and process-tree containment. No service starts on import."""
import ctypes
import json
import os
from pathlib import Path
import threading
import time
import sys

_job_handle = None
_kernel = None
_tracked = {}
_tracking_lock = threading.Lock()


def default_policy_path():
    if os.name == 'nt':
        from win32com.shell import shell, shellcon
        base = Path(shell.SHGetKnownFolderPath(shell.FOLDERID_LocalAppData, shellcon.KF_FLAG_NO_PACKAGE_REDIRECTION))
    else:
        base = Path(os.environ.get('LOCALAPPDATA', Path.home()/'.local/share'))
    return base/'SiemensMCPServer/safeguards.json'


def unique_properties(pairs):
    result, seen = {}, set()
    for name, value in pairs:
        if name.casefold() in seen:
            raise ValueError('Shared frontend policy has duplicate properties')
        result[name] = value
        seen.add(name.casefold())
    return result


def frontend_limit(config):
    """Shared GUI policy is authoritative; startup config is the missing-field default."""
    configured = config.get('launchOptions', {}).get('settings_file')
    path = Path(configured) if configured else default_policy_path()
    value = config.get('maxFrontends', 4)
    if path.exists():
        from integrity import ordinary
        path = ordinary(path)
        with path.open('rb') as stream:
            raw = stream.read(65537)
        if len(raw) > 65536:
            raise ValueError('Shared frontend policy is oversized')
        policy = json.loads(raw, object_pairs_hook=unique_properties)
        if not isinstance(policy, dict):
            raise ValueError('Shared frontend policy must be an object')
        value = policy.get('maxMcpFrontends', value)
    if type(value) is not int or not 1 <= value <= 16:
        raise ValueError('Set Maximum connected MCP chats to 1..16 in the desktop safeguards editor')
    return value


def lock_stream(stream):
    stream.seek(0, 2)
    if stream.tell() == 0:
        stream.write(b'0'); stream.flush()
    stream.seek(0)
    if os.name == 'nt':
        import msvcrt
        msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
    else:
        import fcntl
        fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)


class FrontendSlot:
    """An exited/crashed frontend releases its slot in the OS; no PID reuse guessing."""
    def __init__(self, state, limit=4):
        if type(limit) is not int or not 1 <= limit <= 16:
            raise ValueError('Maximum connected chats must be between 1 and 16')
        self.root, self.limit, self.stream = Path(state)/'frontends', limit, None

    def __enter__(self):
        self.root.mkdir(parents=True, exist_ok=True)
        admission = (self.root/'admission.lock').open('a+b')
        free = []
        try:
            deadline = time.monotonic()+2
            while True:
                try:
                    lock_stream(admission); break
                except OSError:
                    if time.monotonic() >= deadline:
                        raise ValueError('Another Siemens connection admission is busy; no frontend was started') from None
                    time.sleep(0.025)
            occupied = 0
            for index in range(16):
                stream = (self.root/f'slot-{index}.lock').open('a+b')
                try:
                    lock_stream(stream)
                except OSError:
                    stream.close(); occupied += 1
                else:
                    free.append(stream)
            if occupied >= self.limit or not free:
                raise ValueError('Siemens connected-chat limit reached. Close an unused connection before opening another.')
            self.stream = free.pop(0)
            return self
        finally:
            for stream in free: stream.close()
            admission.close()

    def __exit__(self, *_):
        if self.stream is not None:
            self.stream.close()
            self.stream = None


def contain_current_process(*, watch_parent=False):
    """Keep one kill-on-exit Job handle for this process lifetime on Windows.

    Intentional OS lifetime: the handle is never inherited or closed prematurely.
    Kernel teardown kills descendants after EOF/parent death/crash. A frontend
    never creates the separately started shared service, so it cannot kill it.
    """
    global _job_handle, _kernel
    if os.name != 'nt' or _job_handle is not None:
        return
    from ctypes import wintypes as w
    class Basic(ctypes.Structure):
        _fields_ = [('process_time', ctypes.c_longlong), ('job_time', ctypes.c_longlong),
            ('flags', w.DWORD), ('minimum', ctypes.c_size_t), ('maximum', ctypes.c_size_t),
            ('active_limit', w.DWORD), ('affinity', ctypes.c_size_t), ('priority', w.DWORD), ('scheduling', w.DWORD)]
    class Counters(ctypes.Structure):
        _fields_ = [(n, ctypes.c_ulonglong) for n in ('read_ops', 'write_ops', 'other_ops', 'read_bytes', 'write_bytes', 'other_bytes')]
    class Extended(ctypes.Structure):
        _fields_ = [('basic', Basic), ('io', Counters), ('process_limit', ctypes.c_size_t),
            ('job_limit', ctypes.c_size_t), ('peak_process', ctypes.c_size_t), ('peak_job', ctypes.c_size_t)]
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, w.LPCWSTR]; kernel.CreateJobObjectW.restype = w.HANDLE
    kernel.SetInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD]; kernel.SetInformationJobObject.restype = w.BOOL
    kernel.AssignProcessToJobObject.argtypes = [w.HANDLE, w.HANDLE]; kernel.AssignProcessToJobObject.restype = w.BOOL
    kernel.GetCurrentProcess.restype = w.HANDLE
    kernel.CloseHandle.argtypes = [w.HANDLE]; kernel.CloseHandle.restype = w.BOOL
    job = kernel.CreateJobObjectW(None, None)
    if not job:
        raise OSError('Cannot create the Siemens process lifetime boundary')
    limits = Extended(); limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if not kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
        kernel.CloseHandle(job)
        raise OSError('Cannot configure the Siemens process lifetime boundary')
    if not kernel.AssignProcessToJobObject(job, kernel.GetCurrentProcess()):
        kernel.CloseHandle(job)
        raise OSError('Cannot contain the Siemens process tree; no child was started')
    _job_handle, _kernel = job, kernel
    # The OS owns this non-inheritable handle until process exit. Do not close it:
    # the process itself is a job member, and closing would terminate it early.
    if watch_parent:
        kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]; kernel.OpenProcess.restype = w.HANDLE
        kernel.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]; kernel.WaitForSingleObject.restype = w.DWORD
        parent = kernel.OpenProcess(0x00100000, False, os.getppid())  # SYNCHRONIZE
        if not parent:
            raise OSError('Cannot monitor the tracked Siemens launcher parent')
        def parent_exit():
            try:
                if kernel.WaitForSingleObject(parent, 0xFFFFFFFF) == 0:
                    os._exit(125)
            finally:
                kernel.CloseHandle(parent)
        threading.Thread(target=parent_exit, name='siemens-parent-lifetime', daemon=True).start()
        # Do not consume stdin a second time. Peek only detects a closed writer;
        # the SDK gets a short grace period for normal EOF/session cleanup.
        import msvcrt
        stdin = msvcrt.get_osfhandle(sys.stdin.fileno())
        kernel.PeekNamedPipe.argtypes = [w.HANDLE, ctypes.c_void_p, w.DWORD, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
        kernel.PeekNamedPipe.restype = w.BOOL
        def eof_exit():
            while True:
                if not kernel.PeekNamedPipe(stdin, None, 0, None, None, None):
                    if ctypes.get_last_error() in {109, 232}:
                        time.sleep(0.5)
                        os._exit(0)
                    return  # Console/non-pipe input is left to the SDK.
                time.sleep(0.25)
        threading.Thread(target=eof_exit, name='siemens-stdio-lifetime', daemon=True).start()


def owned_resources():
    """Read only this job's members, retaining creation handles; never enumerate/kill other apps."""
    if os.name != 'nt' or _job_handle is None:
        return {'contained': False, 'owned_processes': [], 'attached_native_apps': 'user-owned or unknown; no close authority'}
    from ctypes import wintypes as w
    class Members(ctypes.Structure):
        _fields_ = [('assigned', w.DWORD), ('listed', w.DWORD), ('pids', ctypes.c_size_t*256)]
    kernel = _kernel
    kernel.QueryInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD, ctypes.c_void_p]
    kernel.QueryInformationJobObject.restype = w.BOOL
    kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]; kernel.OpenProcess.restype = w.HANDLE
    kernel.IsProcessInJob.argtypes = [w.HANDLE, w.HANDLE, ctypes.POINTER(w.BOOL)]; kernel.IsProcessInJob.restype = w.BOOL
    kernel.GetProcessTimes.argtypes = [w.HANDLE, ctypes.POINTER(w.FILETIME), ctypes.POINTER(w.FILETIME), ctypes.POINTER(w.FILETIME), ctypes.POINTER(w.FILETIME)]
    kernel.GetProcessTimes.restype = w.BOOL
    with _tracking_lock:
        members = Members()
        if not kernel.QueryInformationJobObject(_job_handle, 3, ctypes.byref(members), ctypes.sizeof(members), None):
            return {'contained': True, 'inventory_available': False, 'owned_processes': [], 'inventory_bound': 256}
        ids = {int(pid) for pid in members.pids[:min(members.listed, 256)]}
        for pid in list(_tracked):
            if pid not in ids:
                kernel.CloseHandle(_tracked.pop(pid)[0])
        rows = []
        for pid in sorted(ids):
            handle = kernel.OpenProcess(0x00101000, False, pid)
            if not handle: continue
            belongs = w.BOOL(); created, exited, user, system = w.FILETIME(), w.FILETIME(), w.FILETIME(), w.FILETIME()
            if not kernel.IsProcessInJob(handle, _job_handle, ctypes.byref(belongs)) or not belongs.value or not kernel.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(user), ctypes.byref(system)):
                kernel.CloseHandle(handle); continue
            identity = (created.dwHighDateTime << 32) | created.dwLowDateTime
            old = _tracked.get(pid)
            if old and old[1] == identity:
                kernel.CloseHandle(handle)
            else:
                if old: kernel.CloseHandle(old[0])
                _tracked[pid] = (handle, identity)
            rows.append({'pid': pid, 'creation_filetime': identity, 'ownership': 'exclusive-job-member', 'role': 'service-owner' if pid == os.getpid() else 'owned-child'})
        return {'contained': True, 'inventory_available': True, 'owned_processes': rows,
            'inventory_bound': 256, 'truncated': members.assigned > 256, 'unverified_member_count': len(ids)-len(rows),
            'attached_native_apps': 'user-owned or unknown; no close authority',
            'teardown': 'Only contained descendants; external/attached applications are excluded.'}
