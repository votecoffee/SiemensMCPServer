"""Exercise local package preparation and rejection controls without changing Codex settings."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
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
            assert not (root/'valid/integration-choice.json').exists()
        with zipfile.ZipFile(args.package) as archive:
            files = {info.filename: archive.read(info.filename) for info in archive.infolist() if not info.is_dir()}
        # Old route-choice files have no authority over the standalone installer.
        standalone_cli = root/'standalone-cli'; standalone_cli.mkdir()
        (standalone_cli/'codex.cmd').write_text('@echo off\nif "%1"=="mcp" (\necho []\nexit /b 0\n)\nif "%2"=="marketplace" (\necho registered\nexit /b 0\n)\necho {"installed":true}\nexit /b 0\n', encoding='ascii')
        standalone_home = root/'standalone-home'; standalone_home.mkdir()
        original_config = '# Preserve unrelated Codex settings\n'
        (standalone_home/'config.toml').write_text(original_config, encoding='utf-8')
        standalone_env = {**os.environ, 'PATH': str(standalone_cli)+os.pathsep+os.environ['PATH'], 'CODEX_HOME': str(standalone_home)}
        for old_choice in ['{"schemaVersion":1,"integration":"Broker","selectionOnly":true}', '{malformed']:
            install_root = root/('upgrade-'+str(len(old_choice))); install_root.mkdir()
            choice = install_root/'integration-choice.json'; choice.write_text(old_choice, encoding='utf-8')
            for _ in range(2):
                installed = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(installer),
                    '-Package', str(args.package.resolve()), '-InstallRoot', str(install_root)],
                    env=standalone_env, capture_output=True, text=True, encoding='utf-8', timeout=60)
                assert installed.returncode == 0, installed.stdout+installed.stderr
                installed_report = json.loads(installed.stdout)
                assert installed_report['state']=='installed' and installed_report['integration']=='Standalone'
                assert choice.read_text(encoding='utf-8')==old_choice
                assert (standalone_home/'config.toml').read_text(encoding='utf-8')==original_config
        removed_route = subprocess.run(['powershell.exe', '-NoProfile', '-File', str(installer),
            '-Package', str(args.package.resolve()), '-InstallRoot', str(root/'removed-route'), '-Integration', 'Broker'],
            env=standalone_env, capture_output=True, text=True, encoding='utf-8', timeout=30)
        assert removed_route.returncode != 0 and not (root/'removed-route').exists()
        # Hold the install mutex from another process. Admission must fail before writes.
        locked_root = root/'locked-root'
        ready, release = root/'lock-ready', root/'lock-release'
        holder_code = r'''$ErrorActionPreference='Stop'
$algorithm=[Security.Cryptography.SHA256]::Create()
$key=([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($env:SIEMENS_INSTALL_TEST_ROOT).ToLowerInvariant())))).Replace('-','').ToLowerInvariant()
$mutex=[Threading.Mutex]::new($false,('Local\SiemensMCPServer-Install-'+$key))
try { if (-not $mutex.WaitOne(0)) { throw 'Cannot acquire test lock' }; [IO.File]::WriteAllText($env:SIEMENS_INSTALL_TEST_READY,'ready'); $deadline=[DateTime]::UtcNow.AddSeconds(30); while (-not [IO.File]::Exists($env:SIEMENS_INSTALL_TEST_RELEASE) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 } } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
'''
        holder = subprocess.Popen(['powershell.exe', '-NoProfile', '-Command', holder_code],
            env={**os.environ, 'SIEMENS_INSTALL_TEST_ROOT': str(locked_root), 'SIEMENS_INSTALL_TEST_READY': str(ready), 'SIEMENS_INSTALL_TEST_RELEASE': str(release)},
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            deadline = time.monotonic()+10
            while not ready.exists():
                assert holder.poll() is None and time.monotonic() < deadline, 'Install mutex fixture did not start'
                time.sleep(0.05)
            locked = prepare(args.package.resolve(), locked_root)
            assert locked.returncode != 0 and 'Another installation' in ' '.join(locked.stderr.split())
            assert not locked_root.exists()
        finally:
            release.touch(); holder.communicate(timeout=10)
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
    report['standaloneInstallation'] = 'passed; fresh/repeated installs and stale or malformed legacy choice files always install the plugin'
    report['removedIntegrationOption'] = 'rejected before extraction'
    report['concurrentInstaller'] = 'rejected before extraction or settings changes'
    if args.report:
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
