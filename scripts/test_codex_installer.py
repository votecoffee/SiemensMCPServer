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
    args = parser.parse_args()
    installer = Path(__file__).resolve().parents[1] / "build/Install-CodexPlugin.ps1"
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
        # A renamed plugin must not register beside the enabled old identity.
        mock_cli=root/'mock-cli';mock_cli.mkdir()
        (mock_cli/'codex.cmd').write_text('@echo off\nif "%1"=="mcp" (\necho [{"name":"siemens-tia-openness","enabled":true,"transport":{}}]\nexit /b 0\n)\necho Unexpected Codex registration call\nexit /b 99\n',encoding='ascii')
        mock_home=root/'mock-home';mock_home.mkdir()
        blocked=subprocess.run(['powershell.exe','-NoProfile','-File',str(installer),'-Package',str(args.package.resolve()),'-InstallRoot',str(root/'legacy-guard')],
            env={**os.environ,'PATH':str(mock_cli)+os.pathsep+os.environ['PATH'],'CODEX_HOME':str(mock_home)},capture_output=True,text=True,encoding='utf-8',timeout=60)
        assert blocked.returncode!=0 and 'old siemens-tia-openness server is still enabled' in blocked.stderr,blocked.stdout+blocked.stderr
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
    if args.report:
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
