"""Start the installed app-owned frontend. Never installs dependencies during a chat."""
import json
import os
from pathlib import Path
import sys

from integrity import digest, ordinary, verify_payload


def main():
    root = Path(__file__).resolve().parents[1]
    fingerprint = verify_payload(root)
    config = Path(os.environ.get('SIEMENS_MCP_APPHOST', str(
        Path(os.environ['LOCALAPPDATA'])/'SiemensMCPServer/AppHost/active.json')))
    runtime = json.loads(ordinary(config).read_bytes())
    if runtime['payloadManifestSha256'] != fingerprint:
        raise ValueError('Installed framework runtime does not match this plugin; rerun its installer')
    python = ordinary(runtime['python'])
    manager = ordinary(Path(runtime['payloadRoot'])/'framework/manage.py')
    if not python.is_file() or digest(manager) != runtime['managerSha256']:
        raise ValueError('Framework runtime is missing or changed; rerun its installer')
    command = [str(python), '-B', str(manager), 'proxy', '--config', str(config)]
    if os.name == 'nt':
        import subprocess
        from lifetime import contain_current_process
        contain_current_process(watch_parent=True)
        # Windows exec terminates the tracked launcher while its child keeps the pipes.
        # Wait instead, preserving the MCP client's process lifetime and exit status.
        return subprocess.call(command)
    os.execv(str(python), command)


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except Exception as error:
        print('Siemens framework startup failed: '+str(error), file=sys.stderr)
        raise SystemExit(1)
