"""Verify a plugin ZIP, then smoke-test its freshly extracted stdio executable."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tempfile
import zipfile


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
    assert portable["name"] == "siemens-tia-openness"
    modern = json.loads(files["mcp.json"])["mcpServers"]["siemens-tia-openness"]
    compat = json.loads(files[".mcp.json"])["mcpServers"]["siemens-tia-openness"]
    assert modern["type"] == "stdio"
    assert {k: v for k, v in modern.items() if k != "type"} == compat
    assert modern["command"] == "${PLUGIN_ROOT}/server/SiemensTiaOpenness.Mcp.exe"
    assert modern["args"] == ["--allow-writes"]
    assert "skills/edit-hmi-text/SKILL.md" in files
    return len(manifest)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--native-read-only", action="store_true")
    args = parser.parse_args()
    package = args.package.resolve()
    package_hash = hashlib.sha256(package.read_bytes()).hexdigest()
    sidecar = Path(str(package) + ".sha256")
    assert sidecar.exists(), "Package SHA-256 sidecar is missing"
    assert sidecar.read_text(encoding="utf-8-sig").split()[0] == package_hash
    with zipfile.ZipFile(package) as archive:
        names = [info.filename for info in archive.infolist() if not info.is_dir()]
        assert len(names) == len(set(names)), "Duplicate ZIP members"
        files = {name: archive.read(name) for name in names}
    file_count = validate(files)
    controls = {}
    for name, mutated in (
        ("tampered_payload", {**files, "server/SiemensTiaOpenness.Mcp.exe": files["server/SiemensTiaOpenness.Mcp.exe"] + b"tamper"}),
        ("extra_file", {**files, "extra.txt": b"extra"}),
        ("path_traversal", {**files, "../outside.txt": b"escape"}),
    ):
        try:
            validate(mutated)
        except (ValueError, AssertionError):
            controls[name] = "rejected"
        else:
            raise AssertionError("Negative control was accepted: " + name)
    with tempfile.TemporaryDirectory(prefix="tia-plugin-extract-") as scratch:
        root = Path(scratch).resolve()
        for name, data in files.items():
            target = (root / name).resolve()
            assert target.is_relative_to(root)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        command = [sys.executable, "-X", "utf8", str(Path(__file__).with_name("test_mcp_protocol.py")),
                   str(root / "server/SiemensTiaOpenness.Mcp.exe")]
        if args.native_read_only:
            command.append("--native-read-only")
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=60)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        protocol = json.loads(result.stdout)
    report = {"package": package.name, "sha256": package_hash, "manifestFileCount": file_count,
              "manifest": "passed", "noSiemensRedistribution": "passed", "negativeControls": controls,
              "freshExtractionProtocol": protocol, "nativeImportCompileSave": "pending"}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
