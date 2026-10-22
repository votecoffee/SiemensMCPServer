"""Verify a plugin ZIP, then smoke-test its freshly extracted stdio executable."""
import argparse
import hashlib
import json
import re
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tempfile
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
from app_version import equivalent


def validate_skill_content(files):
    skills = {name: data for name, data in files.items() if name.startswith('skills/') and name.endswith('/SKILL.md')}
    if not skills:
        raise ValueError('No packaged skill entrypoints')
    for name, data in skills.items():
        text = data.decode('utf-8-sig').replace('\r\n', '\n')
        if not text.startswith('---\n'):
            raise ValueError('Skill frontmatter missing: '+name)
        sections = text.split('\n---\n', 1)
        if len(sections) != 2 or not sections[1].strip():
            raise ValueError('Skill body is empty or truncated: '+name)
        for field in ('name', 'description'):
            if not re.search(r'^'+field+r':\s*[^\s]+', sections[0], flags=re.MULTILINE):
                raise ValueError('Skill required field missing: '+name+'/'+field)
    return len(skills)


def validate(files):
    if len({name.casefold() for name in files}) != len(files):
        raise ValueError("Case-colliding ZIP members")
    for name in files:
        path = PurePosixPath(name)
        if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
            raise ValueError("Unsafe ZIP member: " + name)
        if path.name.lower().startswith("siemens.engineering") and path.suffix.lower() == ".dll":
            raise ValueError("Siemens DLL found in package")
    manifest = json.loads(files["manifest.json"].decode("utf-8-sig"))
    expected = {item["path"] for item in manifest}
    if len(expected) != len(manifest) or expected != set(files) - {"manifest.json"}:
        raise ValueError("Package member set differs from manifest")
    for item in manifest:
        data = files[item["path"]]
        if len(data) != item["bytes"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise ValueError("Manifest hash or length mismatch: " + item["path"])
    portable = json.loads(files["plugin.json"])
    assert portable["extensions"]["com.openai"]["requires_local_executor"] is True
    assert portable["name"] == "siemens-mcp-server"
    assert portable["repository"] == "https://github.com/votecoffee/SiemensMCPServer"
    compatibility = json.loads(files[".codex-plugin/plugin.json"])
    assert compatibility["name"] == portable["name"] and equivalent(compatibility["version"], portable["version"])
    modern = json.loads(files["mcp.json"])["mcpServers"]["siemens-mcp-server"]
    compat = json.loads(files[".mcp.json"])["mcpServers"]["siemens-mcp-server"]
    assert modern["type"] == "stdio"
    assert {k: v for k, v in modern.items() if k != "type"} == compat
    build = json.loads(files['build-info.json'])
    if build.get('profile') == 'direct-offline':
        assert modern['command'] == '${PLUGIN_ROOT}/server/SiemensMCPServer.Offline.exe'
        assert modern['args'] == [] and modern['cwd'] == '${PLUGIN_ROOT}'
        assert build['tiaPortalApi'] == 'none'
        assert all('server/SiemensMCPServer.Offline.'+suffix in files for suffix in ('exe', 'dll', 'deps.json', 'runtimeconfig.json'))
        assert not any(name.startswith('framework/') or name.endswith(('.py', '.whl')) or '/SiemensMCPServer.Mcp.' in name or '/SiemensMCPServer.ExtensionWorker.' in name for name in files)
        assert validate_skill_content(files) == 4
        for name, data in files.items():
            if name.startswith('skills/') and name.endswith('/SKILL.md'):
                assert b'siemens_offline_catalog' in data and b'siemens_offline_describe' in data and b'siemens_offline_analyze' in data
        assert not any('fixture' in name.lower() for name in files)
        return len(manifest)
    if tuple(map(int, portable["version"].split("."))) >= (0, 17, 0):
        assert modern["command"] == "py" and modern["args"] == ["-3.13", "-B", "framework/launch.py"]
        assert modern["env"]["PYTHONDONTWRITEBYTECODE"] == "1"
    else:
        assert modern["command"] == "cmd.exe"
        assert modern["args"] == ["/d", "/c", "server\\SiemensMCPServer.Mcp.exe", "--allow-writes"]
    assert modern["cwd"] == "."
    assert "skills/edit-hmi-text/SKILL.md" in files
    validate_skill_content(files)
    version = tuple(int(part) for part in portable["version"].split(".")[:3])
    if version >= (0, 20, 0):
        assert 'CURRENT-GUIDANCE.md' in files and 'framework/guidance.py' in files
        for skill in ('edit-hmi-text', 'edit-siemens-plc', 'inspect-libraries', 'inspect-project-texts'):
            assert 'guidance/'+skill+'/SKILL.md' in files
            assert b'siemens_list_workflows' in files['skills/'+skill+'/SKILL.md']
    if version >= (0, 8, 0):
        assert "skills/inspect-libraries/SKILL.md" in files
        assert "LIBRARY-INSPECTION.md" in files
    if version >= (0, 9, 0):
        assert "skills/inspect-project-texts/SKILL.md" in files
        assert "PROJECT-TEXTS.md" in files
    if version >= (0, 9, 1):
        assert "COMFORT-LAYOUT.md" in files
    if version >= (0, 10, 0):
        assert "COMFORT-SCREEN-TEXTS.md" in files
    if version >= (0, 10, 1):
        assert "COMFORT-SCREEN-ITEMS.md" in files
    if version >= (0, 10, 2):
        assert "COMFORT-REFERENCES.md" in files
    if version >= (0, 10, 3):
        assert "COMFORT-BEHAVIORS.md" in files
    if version >= (0, 11, 0):
        assert "HMI-REFERENCE-ANALYSIS.md" in files
        assert "scripts/Analyze-HmiReferences.ps1" in files
    if version >= (0, 12, 0):
        assert "scripts/Edit-ProjectTexts.ps1" in files
    if version >= (0, 13, 0):
        assert "CONCURRENCY.md" in files
    if (0, 16, 0) <= version < (0, 17, 0):
        removed = {"BROKER-INTEGRATION.md", "APP-FRAMEWORK.md", "scripts/New-BrokerRegistration.ps1",
                   "scripts/New-FrameworkPublication.ps1", "scripts/test_app_framework.py"}
        assert not removed.intersection(files)
        assert not any(name.startswith("framework/") or name.startswith("universal_tools/") or name.endswith(".whl") for name in files)
        installer = files["scripts/Install-CodexPlugin.ps1"].decode("utf-8-sig")
        assert "$Integration" not in installer and "integration-choice.json" not in installer
    if version >= (0, 17, 0):
        assert "framework/manage.py" in files and "framework/wheels.json" in files
        pins = files["framework/requirements-lock.txt"].decode("utf-8").splitlines()
        framework_version, framework_commit = (("0.20.0", "5b2cac50ca24d21a15b732e0424bb02fd794e1fb") if version >= (0, 22, 0)
            else ("0.18.0", "7d8a0ccdc06598313061147ee8774837118a8861") if version >= (0, 19, 0)
            else ("0.17.0", "87157f88de4a5320d2712224216b1e6f18a0ec8d") if version >= (0, 17, 1)
            else ("0.16.0", "579b065ecc37f98af72cb148ba47a65587bd167b"))
        assert "universal-codex-tools=="+framework_version in pins and "mcp==2.3.0" in pins
        wheel_index = json.loads(files["framework/wheels.json"])
        assert wheel_index["frameworkVersion"] == framework_version and wheel_index["sourceCommit"] == framework_commit
        if version >= (0, 19, 0):
            reviewed_sha = ('a1b3777f8e8c1900e136a514d89ddb485a6bf4d69a525e3d24e1d04cd040efb3' if version >= (0, 22, 0)
                else '0c1084b424f079957ebd3bec763e609564aaef64cd1fedee78b61a45bfbc24ac')
            assert wheel_index['frameworkWheelSha256'] == reviewed_sha
            assert next(e['sha256'] for e in wheel_index['files'] if e['name'] == 'universal-codex-tools') == reviewed_sha
            assert 'framework/service_management.py' in files
        for entry in wheel_index["files"]:
            assert hashlib.sha256(files["framework/"+entry["path"]]).hexdigest() == entry["sha256"]
        assert "scripts/New-BrokerRegistration.ps1" not in files and "BROKER-INTEGRATION.md" not in files
        if version >= (0, 22, 0):
            assert all('framework/'+name in files for name in ('frontend_stdio.py', 'native_stdio.py',
                'stdio_supervisor.py', 'service_supervisor.py', 'resource_management.py', 'lifetime.py'))
        installer = files["scripts/Install-CodexPlugin.ps1"].decode("utf-8-sig")
        assert "$Integration" not in installer and "integration-choice.json" not in installer
    assert not any('fixture' in name.lower() or '/fixtures/' in name.lower() for name in files), 'Test fixtures must not be distributed'
    return len(manifest)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--native-read-only", action="store_true")
    parser.add_argument("--static-only", action="store_true", help="Verify bytes/negative controls without starting any MCP process")
    args = parser.parse_args()
    package = args.package.resolve()
    package_hash = hashlib.sha256(package.read_bytes()).hexdigest()
    sidecar = Path(str(package) + ".sha256")
    assert sidecar.exists(), "Package SHA-256 sidecar is missing"
    assert sidecar.read_text(encoding="utf-8-sig").split()[0] == package_hash
    with zipfile.ZipFile(package) as archive:
        # On Windows ZipInfo.filename normalizes raw backslashes. Inspect the original central-directory name.
        names = [info.orig_filename for info in archive.infolist() if not info.is_dir()]
        assert len(names) == len(set(names)), "Duplicate ZIP members"
        files = {info.orig_filename: archive.read(info) for info in archive.infolist() if not info.is_dir()}
    file_count = validate(files)
    offline = json.loads(files['build-info.json']).get('profile') == 'direct-offline'
    if offline and not args.static_only:
        parser.error('Direct offline runtime acceptance needs its explicit disposable canary; use --static-only for source/package review')
    executable_name = 'server/SiemensMCPServer.Offline.exe' if offline else 'server/SiemensMCPServer.Mcp.exe'
    controls = {}
    for name, mutated in (
        ("tampered_payload", {**files, executable_name: files[executable_name] + b"tamper"}),
        ("extra_file", {**files, "extra.txt": b"extra"}),
        ("path_traversal", {**files, "../outside.txt": b"escape"}),
    ):
        try:
            validate(mutated)
        except (ValueError, AssertionError):
            controls[name] = "rejected"
        else:
            raise AssertionError("Negative control was accepted: " + name)
    protocol = {'state': 'not-run', 'reason': 'static-only'}
    if not args.static_only:
        with tempfile.TemporaryDirectory(prefix="tia-plugin-extract-") as scratch:
            root = Path(scratch).resolve()
            for name, data in files.items():
                target = (root / name).resolve()
                assert target.is_relative_to(root)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            command = [sys.executable, "-X", "utf8", str(Path(__file__).with_name("test_mcp_protocol.py")),
                       str(root / "server/SiemensMCPServer.Mcp.exe")]
            if args.native_read_only:
                command.append("--native-read-only")
            result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=60)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)
            protocol = json.loads(result.stdout)
            package_version = json.loads(files['plugin.json'])['version']
            if tuple(int(part) for part in package_version.split('.')[:3]) >= (0, 12, 0):
                assert equivalent(protocol['hostVersion'], package_version), 'Advertised host version differs from the package version'
    report = {"package": package.name, "sha256": package_hash, "manifestFileCount": file_count,
              "manifest": "passed", "noSiemensRedistribution": "passed", "negativeControls": controls,
              "freshExtractionProtocol": protocol, "nativeImportCompileSave": "pending"}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
