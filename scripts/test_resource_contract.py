"""Validate emitted .NET records with the reviewed framework; optional non-MCP unit lock fixture."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import time

from universal_tools.models import BrokerError
from universal_tools.resources import ResourceLedger, ResourceRecord


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('contract_directory', type=Path)
    parser.add_argument('--check-lock', action='store_true')
    args = parser.parse_args()
    root = args.contract_directory
    before = ResourceRecord.model_validate_json((root/'private-before.json').read_bytes())
    after = ResourceRecord.model_validate_json((root/'private-after.json').read_bytes())
    assert after.receipt.expected_revision == before.revision
    assert after.owner == before.owner and after.resource_id == before.resource_id
    canonical = ResourceRecord.model_validate_json((root/'canonical.json').read_bytes())
    assert canonical.revision == (root/'canonical.sha256').read_text()
    if args.check_lock:
        repo = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory(prefix='siemens-cross-runtime-lock-') as temporary:
            registry = Path(temporary)
            child = subprocess.Popen(['dotnet', 'test', str(repo/'Tests/SiemensMCPServer.Tests.csproj'),
                '--no-build', '-c', 'Release', '--filter', 'FullyQualifiedName~CrossRuntimeLedgerLockMatchesFrameworkByteRange', '--nologo'],
                env={**os.environ, 'SIEMENS_RESOURCE_LOCK_REGISTRY': temporary}, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                deadline = time.monotonic()+15
                while not (registry/'ready').exists():
                    assert child.poll() is None, child.communicate()
                    assert time.monotonic() < deadline, 'C# unit lock fixture did not become ready'
                    time.sleep(0.05)
                ledger = ResourceLedger(registry)
                try:
                    ledger.owner('siemens-mcp-server').begin_launch('Must refuse while native byte lock is held')
                except BrokerError:
                    pass
                else:
                    raise AssertionError('Framework bypassed the native byte-range lock')
                assert len(list((registry/'resources').glob('*.json'))) == 1
            finally:
                (registry/'release').write_text('release')
                output, error = child.communicate(timeout=20)
            assert child.returncode == 0, output+error
    print('Actual framework record/revision parity passed; cross-runtime lock fixture: '+str(args.check_lock))


if __name__ == '__main__':
    main()
