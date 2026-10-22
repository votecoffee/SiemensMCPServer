"""Retained owner of the shared HTTP host; explicit start only, no client EOF policy."""
from pathlib import Path
import sys
import time

from lifetime import contain_current_process, resource_limits
from resource_management import APP_ID, require_startup_clear
from universal_tools.daemon import InstanceLock
from universal_tools.resources import ResourceLedger


def run_background(command, *, config, ledger=None, sleep=time.sleep):
    maximum, tree_size = resource_limits(config)
    if ledger is None:
        import os
        ledger = ResourceLedger(os.environ.get('SIEMENS_MCP_MANAGEMENT_REGISTRY'), max_active_per_app=maximum)
    require_startup_clear(ledger)
    owner = ledger.owner(APP_ID)
    record = owner.begin_launch('Siemens shared HTTP app host', kind='app-host', cleanup_mode='terminate')
    owner.set_recovery_barrier(record.resource_id,
        'Shared host may retain native contexts, dirty state or interrupted writes. Inspect app journals and exact resources before orphan recovery.')
    group = None
    try:
        # Streamless background profile. Reserve/fence before creation, bind
        # before resume, then retain the Job for the child's entire lifetime.
        import os
        group = ledger.driver.create_suspended(command, max_processes=tree_size,
            env={**os.environ, 'SIEMENS_SHARED_HOST_RESOURCE': record.resource_id})
        record = owner.bind_process(record.resource_id, group.pid, containment='windows_job')
        group.resume()
        while True:
            state = ledger.driver.state(record.process.model_dump())
            if state in {'absent', 'different'}: break
            if state != 'alive':
                raise RuntimeError('Shared-host identity is unverified; original resource barrier was retained')
            sleep(0.1)
        if group.identities():
            raise RuntimeError('Shared host exited with private descendants still present; preserve recovery evidence')
        group.close(); group = None
        current = ledger.get(record.resource_id)
        owner.reconcile(current.resource_id, current.revision,
            'Original shared-host process and its contained tree exited. No engineering result is inferred; original barriers/journals remain authoritative.')
        owner.close()
        return 0
    finally:
        # Failure/owner death closes only our tree. Keep the original claim and
        # native barrier; never guess its outcome or start a replacement.
        if group is not None: group.close()


def main():
    from manage import load_runtime
    if len(sys.argv) != 3 or sys.argv[1] != '--config':
        raise ValueError('Supply one exact installed --config path')
    config_path = Path(sys.argv[2]); config = load_runtime(config_path)
    # Windows venv python.exe is a retained redirector. Watch that launcher's
    # exact handle, not the short-lived start CLI which intentionally exits.
    # A non-redirected Python is itself the Popen root and survives its CLI parent.
    redirected = Path(sys.executable).resolve() != Path(getattr(sys, '_base_executable', sys.executable)).resolve()
    contain_current_process(watch_parent=redirected, watch_stdin=False, max_processes=resource_limits(config)[1])
    with InstanceLock(Path(config['state'])/'supervisor.lock'):
        return run_background([sys.executable, '-B', str(Path(__file__).with_name('manage.py')),
            'serve', '--config', str(config_path)], config=config)


if __name__ == '__main__':
    try: raise SystemExit(main())
    except Exception as error:
        print('Siemens shared-host supervision failed: '+str(error), file=sys.stderr)
        raise SystemExit(1)
