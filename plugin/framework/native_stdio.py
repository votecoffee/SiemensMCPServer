"""App-owned, lifetime-retained native MCP stdio supervisor. No work on import."""
from pathlib import Path
import sys

from integrity import ordinary, verify_payload
from stdio_supervisor import run_stdio


def main():
    payload = Path(__file__).resolve().parents[1]
    verify_payload(payload)
    if len(sys.argv) < 2:
        raise ValueError('Supply the exact packaged native backend executable')
    executable = ordinary(Path(sys.argv[1]).absolute())
    if executable != ordinary(payload/'server/SiemensMCPServer.Mcp.exe'):
        raise ValueError('Native supervision is limited to this verified packaged backend')
    settings = [arg.split('=', 1)[1] for arg in sys.argv[2:] if arg.startswith('--settings-file=')]
    if len(settings) > 1:
        raise ValueError('Duplicate settings selection')
    config = {'launchOptions': {'settings_file': settings[0] if settings else None}}
    return run_stdio([str(executable), *sys.argv[2:]], config=config,
        label='Siemens native MCP adapter and contained feature workers', native=True)


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except Exception as error:
        print('Siemens native supervision failed: '+str(error), file=sys.stderr)
        raise SystemExit(1)
