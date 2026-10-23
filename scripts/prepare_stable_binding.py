"""Read explicit inputs and retain a coordinator proposal; never writes host settings."""
import argparse
import hashlib
import json
import os
from pathlib import Path

from codex_config_bytes import parse


def prepare(config, receipt, output):
    config, receipt, output = Path(config).resolve(), Path(receipt).resolve(), Path(output).resolve()
    raw = config.read_bytes()
    settings = parse(raw)
    installed = json.loads(receipt.read_bytes())
    if installed['state'] != 'selected' or installed['profile'] != 'direct-offline' or installed['settingsChanged']:
        raise ValueError('Expected a selected closed-offline installation receipt')
    command = Path(installed['command']).resolve(strict=True)
    root = command.parent
    if command.name != 'SiemensMCPServer.Launcher.exe' or not os.path.samefile(Path(installed['cwd']) / command.name, command) or installed['args']:
        raise ValueError('Expected the fixed stable entry point without arguments')
    selection = root / 'launcher/current.json'
    selected_bytes = selection.read_bytes()
    if hashlib.sha256(selected_bytes).hexdigest() != installed['selectionSha256']:
        raise ValueError('Installed selection changed since the receipt')
    pointer = json.loads(selected_bytes)
    snapshot_bytes = (root / 'launcher/snapshots' / (pointer['snapshotSha256'] + '.json')).read_bytes()
    if hashlib.sha256(snapshot_bytes).hexdigest() != installed['snapshotSha256']:
        raise ValueError('Installed snapshot differs')
    snapshot = json.loads(snapshot_bytes)
    runtime = (root / snapshot['runtime']).resolve()
    if not runtime.is_relative_to(root) or runtime != Path(installed['runtime']).resolve():
        raise ValueError('Runtime path differs')
    for item in snapshot['files']:
        path = (runtime / item['path']).resolve()
        if not path.is_relative_to(runtime) or hashlib.sha256(path.read_bytes()).hexdigest() != item['sha256']:
            raise ValueError('Runtime payload differs')
    stable_assets = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
        for path in root.glob('SiemensMCPServer.Launcher.*') if path.is_file()}
    output.mkdir(parents=True, exist_ok=False)
    backup = output / 'config-before.toml'
    backup.write_bytes(raw)
    result = {
        'schemaVersion': 1, 'application': 'SiemensMCPServer', 'configPath': str(config),
        'expectedConfigSha256': hashlib.sha256(raw).hexdigest(), 'exactBackup': str(backup),
        'expectedRoute': settings.get('mcp_servers', {}).get('siemens-mcp-server'),
        'proposedRoute': {'command': str(command), 'args': [], 'cwd': str(root), 'env': installed['env']},
        'profile': 'direct-offline', 'version': installed['version'],
        'packageSha256': installed['packageSha256'], 'selectionSha256': installed['selectionSha256'],
        'snapshotSha256': installed['snapshotSha256'], 'runtime': installed['runtime'],
        'stableAssetHashes': stable_assets,
        'installationReceiptSha256': hashlib.sha256(receipt.read_bytes()).hexdigest(),
        'standalonePluginId': 'siemens-mcp-server@siemens-mcp-server-local',
        'pluginSource': installed['pluginSource'], 'marketplaceRoot': installed['marketplaceRoot'],
        'pluginPublication': 'Register the staged app-owned marketplace/plugin through the serialized publisher; preserve the enabled standalone skills and disable any duplicate unresolved plugin route.',
        'managementStatus': 'Not reporting', 'restartAvailable': False, 'settingsChanged': False,
        'publicationRule': 'Re-read current bytes/owned route; refuse a stale preimage. Preserve unrelated values/comments/newlines/BOM, publish validated binary TOML once, and retain exact backup. Never restore a stale global file.',
        'nextCheck': 'After serialized publication, connect through the exact command/cwd/env; check current catalog/version/skills in a fresh or restarted Codex chat. Open-chat activation is unverified.'
    }
    (output / 'proposal.json').write_bytes(json.dumps(result, ensure_ascii=False, indent=2).encode('utf-8'))
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--receipt', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    result = prepare(args.config, args.receipt, args.output)
    print(json.dumps({key: result[key] for key in ('application', 'version', 'expectedConfigSha256', 'settingsChanged')}))
