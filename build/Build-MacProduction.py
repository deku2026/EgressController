#!/usr/bin/env python3
"""Prepare an incremental, complete Rider product. Only mock-smoke entrypoints are executed."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
PRODUCTS = ("EgressController.Desktop", "EgressController.Desktop.Presentation", "EgressController.Mac",
            "EgressController.Policy", "EgressController.Runtime", "EgressController.Transport")
PAYLOAD = ("Contents/MacOS/EgressController.Desktop", "Contents/MacOS/helper/EgressController.MacHelper",
           "Contents/MacOS/libEgressMacNative.dylib", "Contents/MacOS/helper/libEgressMacNative.dylib",
           "Contents/MacOS/core/Xray-macos-arm64-v8a.zip")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source_identity(configuration):
    files = [ROOT / name for name in ("global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props")]
    for folder in [*(ROOT / "src" / name for name in PRODUCTS), ROOT / "tools/EgressController.MacHelper", ROOT / "native/macos", ROOT / "build"]:
        files.extend(path for path in folder.rglob("*") if path.is_file() and not any(part in ("bin", "obj", "__pycache__") for part in path.relative_to(folder).parts))
    files.append(ROOT / "artifacts/core/Xray-macos-arm64-v8a.zip")
    value = hashlib.sha256(configuration.encode())
    for path in sorted(set(files)):
        value.update(str(path.relative_to(ROOT)).encode()); value.update(bytes.fromhex(digest(path)))
    return value.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=("Debug", "Release"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / "src/EgressController.Desktop/bin"):
        parser.error("Rider payloads must stay in the Desktop build output.")
    if not (ROOT / "artifacts/core/Xray-macos-arm64-v8a.zip").is_file():
        parser.error("Missing bundled Xray archive: prepare artifacts/core/Xray-macos-arm64-v8a.zip before a production build.")
    identity = source_identity(args.configuration)
    stamp = output / "source-fingerprint.json"
    app = output / "Egress.app"
    try:
        previous = json.loads(stamp.read_text())
        if previous["source"] == identity and all(digest(app / name) == previous["payload"][name] for name in PAYLOAD):
            verified = subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], capture_output=True)
            if verified.returncode == 0:
                print("Rider NativeAOT product is current:", app, flush=True)
                return
    except (OSError, ValueError, KeyError):
        pass
    subprocess.run([sys.executable, str(ROOT / "build/Package-Mac.py"), "--configuration", args.configuration,
                    "--output-root", str(output), "--no-archive"], cwd=ROOT, check=True)
    record = {"source": identity, "payload": {name: digest(app / name) for name in PAYLOAD}}
    temporary = stamp.with_suffix(".tmp")
    temporary.write_text(json.dumps(record, indent=2) + "\n")
    os.replace(temporary, stamp)


if __name__ == "__main__":
    main()
