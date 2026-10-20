"""Build an offline dependency bundle from an explicitly supplied framework wheel."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'plugin/framework'))
from integrity import FRAMEWORK_COMMIT, FRAMEWORK_VERSION, atomic_json, digest, verify_wheels, wheel_metadata


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('wheel', type=Path)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    info = wheel_metadata(args.wheel)
    assert info == {'name': 'universal-codex-tools', 'version': FRAMEWORK_VERSION}
    destination = args.destination.resolve()
    wheels = destination/'wheels'
    wheels.mkdir(parents=True, exist_ok=False)
    shutil.copy2(args.wheel, wheels/args.wheel.name)
    subprocess.run([sys.executable, '-m', 'pip', 'download', '--only-binary=:all:', '--dest', str(wheels),
        '--find-links', str(wheels), '-r', str(destination/'requirements-lock.txt')], check=True)
    entries = [{'path': 'wheels/'+path.name, 'bytes': path.stat().st_size, 'sha256': digest(path),
                **wheel_metadata(path)} for path in sorted(wheels.glob('*.whl'))]
    atomic_json(destination/'wheels.json', {'schemaVersion': 1, 'frameworkVersion': FRAMEWORK_VERSION,
        'sourceCommit': FRAMEWORK_COMMIT, 'files': entries})
    verify_wheels(destination)
    print(json.dumps({'frameworkVersion': FRAMEWORK_VERSION, 'wheels': len(entries), 'RECORD': 'verified'}))


if __name__ == '__main__':
    main()
