"""Retain the installed-runtime supervisor for one Codex frontend."""
from pathlib import Path
import sys

from manage import load_runtime
from stdio_supervisor import run_stdio


def main():
    if len(sys.argv) != 3 or sys.argv[1] != '--config':
        raise ValueError('Supply one exact installed --config path')
    config_path = Path(sys.argv[2])
    runtime = load_runtime(config_path)
    command = [sys.executable, '-B', str(Path(__file__).with_name('manage.py')), 'proxy', '--config', str(config_path)]
    return run_stdio(command, config=runtime, label='Siemens Codex stdio frontend')


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except Exception as error:
        print('Siemens frontend supervision failed: '+str(error), file=sys.stderr)
        raise SystemExit(1)
