"""Exercise local package preparation and rejection controls without changing Codex settings."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--installer", type=Path, help="Use the installer from a freshly extracted package")
    args = parser.parse_args()
    installer = args.installer.resolve() if args.installer else Path(__file__).resolve().parents[1] / "build/Install-CodexPlugin.ps1"
    with tempfile.TemporaryDirectory(prefix="tia-codex-installer-") as scratch:
        root = Path(scratch)

        def prepare(package, destination):
            return subprocess.run(["powershell.exe", "-NoProfile", "-File", str(installer), "-Package", str(package),
                                   "-InstallRoot", str(destination), "-PrepareOnly"], capture_output=True, text=True, encoding="utf-8", timeout=60)

        for _ in range(2):
            result = prepare(args.package.resolve(), root / "valid")
            assert result.returncode == 0, result.stdout + result.stderr
            report = json.loads(result.stdout)
            assert report["state"] == "prepared" and report["settingsChanged"] is False
        with zipfile.ZipFile(args.package) as archive:
            files = {info.filename: archive.read(info.filename) for info in archive.infolist() if not info.is_dir()}
        if tuple(int(part) for part in json.loads(files['plugin.json'])['version'].split('.')) >= (0, 14, 0):
            # Broker preparation must not even consult a Codex executable.
            broker_cli = root/'broker-cli'; broker_cli.mkdir()
            (broker_cli/'codex.cmd').write_text('@echo off\necho called>"%CODEX_HOME%\\unexpected-call"\nexit /b 99\n', encoding='ascii')
            broker_home = root/'broker-home'; broker_home.mkdir()
            (broker_home/'config.toml').write_text('# Preserve existing plugin choices\n', encoding='utf-8')
            broker_root = root/'broker-prepared'
            broker_result = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(installer),
                '-Package', str(args.package.resolve()), '-InstallRoot', str(broker_root), '-Integration', 'Broker'],
                env={**os.environ, 'PATH': str(broker_cli)+os.pathsep+os.environ['PATH'], 'CODEX_HOME': str(broker_home)},
                capture_output=True, text=True, encoding='utf-8', timeout=60)
            assert broker_result.returncode == 0, broker_result.stdout+broker_result.stderr
            staged = json.loads(broker_result.stdout)
            assert staged['state'] == 'broker-registration-prepared' and not staged['codexPluginInstalled'] and not staged['brokerSettingsChanged']
            descriptor = json.loads(Path(staged['registration']).read_text(encoding='utf-8'))
            assert descriptor['session_mode'] == 'exclusive' and descriptor['allow_writes'] is False
            assert descriptor['allowed_write_tools'] == [] and descriptor['transport']['args'] == []
            assert descriptor['capability_catalog']['tool'] == 'tia_list_broker_capabilities'
            assert Path(descriptor['transport']['command']).is_file()
            assert not (broker_root/'.agents').exists() and not (broker_home/'unexpected-call').exists()
            assert (broker_home/'config.toml').read_text(encoding='utf-8') == '# Preserve existing plugin choices\n'
            helper = installer.parent/'New-BrokerRegistration.ps1'
            scoped_path = root/'scoped-pilot.json'
            scoped = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(helper),
                '-PluginRoot', descriptor['transport']['cwd'], '-OutputPath', str(scoped_path), '-AllowedWriteTools', 'tia_attach'],
                capture_output=True, text=True, encoding='utf-8', timeout=30)
            assert scoped.returncode == 0, scoped.stdout+scoped.stderr
            pilot = json.loads(scoped_path.read_text(encoding='utf-8'))
            assert pilot['allow_writes'] is True and pilot['allowed_write_tools'] == ['tia_attach']
            assert pilot['transport']['args'] == ['--allow-writes']
            retained = scoped_path.read_bytes()
            repeated = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(helper),
                '-PluginRoot', descriptor['transport']['cwd'], '-OutputPath', str(scoped_path)],
                capture_output=True, text=True, encoding='utf-8', timeout=30)
            assert repeated.returncode != 0 and scoped_path.read_bytes() == retained
        # A renamed plugin must not register beside the enabled old identity.
        mock_cli=root/'mock-cli';mock_cli.mkdir()
        (mock_cli/'codex.cmd').write_text('@echo off\nif "%1"=="mcp" (\necho [{"name":"siemens-tia-openness","enabled":true,"transport":{}}]\nexit /b 0\n)\necho Unexpected Codex registration call\nexit /b 99\n',encoding='ascii')
        mock_home=root/'mock-home';mock_home.mkdir()
        blocked=subprocess.run(['powershell.exe','-NoProfile','-File',str(installer),'-Package',str(args.package.resolve()),'-InstallRoot',str(root/'legacy-guard')],
            env={**os.environ,'PATH':str(mock_cli)+os.pathsep+os.environ['PATH'],'CODEX_HOME':str(mock_home)},capture_output=True,text=True,encoding='utf-8',timeout=60)
        assert blocked.returncode!=0 and 'old siemens-tia-openness server is still enabled' in ' '.join(blocked.stderr.split()),blocked.stdout+blocked.stderr
        assert not (mock_home/'config.toml').exists()
        controls = {}
        for name, changed in [
            ("tampered_payload", {**files, "server/SiemensMCPServer.Mcp.exe": files["server/SiemensMCPServer.Mcp.exe"] + b"tamper"}),
            ("extra_file", {**files, "extra.txt": b"extra"}),
            ("path_traversal", {**files, "../outside.txt": b"escape"}),
            ("case_collision", {**files, "PLUGIN.JSON": files["plugin.json"]}),
            ("siemens_dll", {**files, "server/Siemens.Engineering.dll": b"not redistributable"}),
        ]:
            package = root / (name + ".zip")
            with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as archive:
                for path, data in changed.items():
                    archive.writestr(path, data)
            Path(str(package) + ".sha256").write_text(hashlib.sha256(package.read_bytes()).hexdigest(), encoding="ascii")
            result = prepare(package, root / name)
            assert result.returncode != 0, name
            assert not (root / name).exists() and not (root / "outside.txt").exists(), name
            controls[name] = "rejected before extraction"
    report = {"preparation": "passed", "repeatPreparation": "passed", "settingsChanged": False, "legacyDuplicateServer": "rejected before Codex registration", "negativeControls": controls}
    if 'staged' in locals():
        report['brokerPreparation'] = 'passed; exclusive/read-only default, explicit scoped write opt-in, existing outputs preserved, no Codex calls or settings changes'
    if args.report:
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
