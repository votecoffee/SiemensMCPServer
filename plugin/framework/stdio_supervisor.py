"""A retained stdio supervisor; never use the background launcher's streamless profile.

Each invocation owns exactly one child for that child's whole lifetime. Children
inherit an already established Windows Job. Input is gated until ledger binding;
no MCP packet is parsed, changed, retried or synthesized here.
"""
import os
from pathlib import Path
import subprocess
import sys
import threading
import uuid

from lifetime import contain_current_process, owned_resources, resource_limits, stdio_drain_timeout
from universal_tools.resources import ResourceLedger

APP_ID = 'siemens-mcp-server'


def run_stdio(command, *, config, label, native=False, ledger=None, process_factory=None,
              read=None, containment=None, eof_grace=None):
    maximum, tree_size = resource_limits(config)
    if eof_grace is None:
        eof_grace = stdio_drain_timeout(config)
    (containment or contain_current_process)(watch_parent=True, watch_stdin=False, max_processes=tree_size)
    ledger = ledger or ResourceLedger(os.environ.get('SIEMENS_MCP_MANAGEMENT_REGISTRY'), max_active_per_app=maximum)
    owner = ledger.owner(APP_ID)  # This process waits for its child; it is never a registration helper.
    reservation = owner.begin_launch(label, cleanup_mode='terminate')
    if native:
        owner.set_recovery_barrier(reservation.resource_id,
            'Native adapter may have attached/dirty state or an interrupted write. Preserve app journals; inspect before orphan recovery.')
    child = (process_factory or subprocess.Popen)(command, stdin=subprocess.PIPE, close_fds=True)
    try:
        owner.bind_process(reservation.resource_id, child.pid)
    except BaseException:
        # No request was forwarded. Returning from the launcher exits its Job and
        # contains the child even if journaling failed. Keep the original intent.
        raise

    indirect = {}
    def capture_launchers():
        if native:
            return  # .NET records its own feature workers; avoid overlapping claims.
        for member in owned_resources().get('owned_processes', []):
            pid = member['pid']
            if pid in {os.getpid(), child.pid} or pid in indirect:
                continue
            try:
                identity = ledger.driver.capture(pid)
                if identity['created'] != str(member['creation_filetime']):
                    continue  # process identity changed since the Job snapshot
                intent = owner.begin_launch('Contained Siemens frontend launcher descendant', cleanup_mode='report')
                try:
                    record = owner.bind_process(intent.resource_id, pid)
                except ProcessLookupError:
                    if ledger.driver.state(identity) not in {'absent', 'different'}:
                        raise
                    current = ledger.get(intent.resource_id)
                    owner.reconcile(current.resource_id, current.revision,
                        'Exact previously verified Job member exited before identity binding; no engineering operation was performed by this frontend.')
                    continue
                indirect[pid] = record.resource_id
            except ProcessLookupError:
                continue  # OS Job still contains all descendants between snapshots

    eof = threading.Event()
    def forward():
        try:
            while True:
                chunk = (read or (lambda: os.read(sys.stdin.fileno(), 65536)))()
                if not chunk:
                    break
                child.stdin.write(chunk)
                child.stdin.flush()
        except (BrokenPipeError, OSError):
            pass
        finally:
            try: child.stdin.close()
            except (BrokenPipeError, OSError): pass
            eof.set()

    threading.Thread(target=forward, name='siemens-stdio-forward', daemon=True).start()
    while child.poll() is None:
        capture_launchers()
        if eof.wait(0.1):
            # Normal SDK EOF gets a bounded drain. On timeout, process exit closes
            # this supervisor's Job; uncertain native effects remain fenced.
            child.wait(timeout=eof_grace)
            break
    result = child.wait()
    for resource_id in indirect.values():
        record = ledger.get(resource_id)
        owner.reconcile(resource_id, record.revision,
            'Contained stdio launcher descendant exited after the original child handle signalled completion; no engineering outcome inferred.')
    if native:
        record = ledger.get(reservation.resource_id)
        owner.reconcile(record.resource_id, record.revision,
            'Original retained child process handle signalled exit. Native outcome is not inferred; original recovery barrier and engineering journals remain authoritative.')
    else:
        record = ledger.get(reservation.resource_id)
        owner.terminate_private(record.resource_id, record.revision, uuid.uuid4().hex)
    owner.close()
    return result
