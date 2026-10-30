#!/usr/bin/env python3
"""Build an Apple Silicon NativeAOT preview .app, verify it without live initialization, and archive its exact bytes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--version", default="0.1.0")
    args = parser.parse_args()
    if platform.system() != "Darwin" or platform.machine() != "arm64":
        parser.error("Build the Apple Silicon package on an arm64 macOS host.")
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        parser.error("Version must contain three non-negative numeric components.")
    environment = {key: value for key, value in os.environ.items() if not key.startswith("EGRESS_LIVE_")}
    environment["EGRESS_MOCK_ONLY"] = "1"
    output = ROOT / "artifacts" / "package"
    output.mkdir(parents=True, exist_ok=True)

    def run(*command):
        print("Running:", " ".join(map(str, command)), flush=True)
        subprocess.run(list(map(str, command)), cwd=ROOT, env=environment, check=True)

    native = ROOT / "artifacts" / "macos-native" / "libEgressMacNative.dylib"
    if not native.is_file():
        raise RuntimeError("Run build/Build-MacNative.py before packaging; the verified native bridge is missing.")
    with tempfile.TemporaryDirectory(prefix=".mac-package-", dir=output) as temporary:
        staging = Path(temporary)
        published = staging / "published"
        run("dotnet", "publish", "src/EgressController.Desktop", "-c", args.configuration, "-r", "osx-arm64",
            "-p:PublishAot=true", "-p:StripSymbols=true", "-p:Version=" + args.version, "-o", published)
        binary_name = "EgressController.Desktop"
        if not (published / binary_name).is_file():
            raise RuntimeError("The NativeAOT desktop entrypoint is missing.")
        app = staging / "Egress.app"
        executable_directory = app / "Contents" / "MacOS"
        resources = app / "Contents" / "Resources"
        shutil.copytree(published, executable_directory, ignore=shutil.ignore_patterns("*.pdb", "*.dSYM"))
        resources.mkdir(parents=True)
        shutil.copy2(native, executable_directory / native.name)
        notices = resources / "Notices"
        notices.mkdir()
        shutil.copy2(ROOT / "LICENSE", notices / "Egress-LICENSE.txt")
        artwork = ROOT / "src" / "EgressController.Desktop.Presentation" / "Assets"
        for source in list(artwork.glob("LICENSE-*.txt")) + [artwork / "SOURCES.md"]:
            if source.is_file():
                shutil.copy2(source, notices / source.name)
        # Preserve license/notice files from the exact restored package closure.
        assets = json.loads((ROOT / "src/EgressController.Desktop/obj/project.assets.json").read_text())
        packages = []
        for identity, metadata in assets.get("libraries", {}).items():
            if metadata.get("type") != "package":
                continue
            package_path = metadata["path"]
            entry = {"package": identity, "license": "", "source": "https://www.nuget.org/packages/" + identity}
            for folder in assets.get("packageFolders", {}):
                package = Path(folder) / package_path
                if not package.is_dir():
                    continue
                candidates = list(package.glob("*LICENSE*")) + list(package.glob("*license*")) + list(package.glob("*NOTICE*")) + list(package.glob("*COPYING*"))
                for specification in package.glob("*.nuspec"):
                    license_item = ET.parse(specification).find(".//{*}license")
                    if license_item is not None:
                        entry["license"] = license_item.text or ""
                        if license_item.attrib.get("type") == "file":
                            candidate = (package / (license_item.text or "")).resolve()
                            if candidate.is_relative_to(package.resolve()):
                                candidates.append(candidate)
                for source in set(candidates):
                    if source.is_file():
                        shutil.copy2(source, notices / (identity.replace("/", "-") + "-" + source.name))
                break
            packages.append(entry)
        (notices / "packages.json").write_text(json.dumps(packages, ensure_ascii=False, indent=2) + "\n")
        icon = ROOT / "artifacts" / "mac-ui" / "AppIcon.png"
        if not icon.is_file():
            icon = ROOT / "src" / "EgressController.Desktop.Presentation" / "Assets" / "AppIcon.png"
        if icon.is_file():
            iconset = staging / "Egress.iconset"
            iconset.mkdir()
            for points in (16, 32, 128, 256, 512):
                for scale in (1, 2):
                    suffix = "@2x" if scale == 2 else ""
                    run("sips", "-z", points * scale, points * scale, icon, "--out", iconset / f"icon_{points}x{points}{suffix}.png")
            run("iconutil", "-c", "icns", "-o", resources / "Egress.icns", iconset)
        metadata = {
            "CFBundleExecutable": binary_name,
            "CFBundleIdentifier": "app.egresscontroller.mac",
            "CFBundleName": "Egress",
            "CFBundleDisplayName": "Egress",
            "CFBundlePackageType": "APPL",
            "CFBundleShortVersionString": args.version,
            "CFBundleVersion": args.version,
            "LSMinimumSystemVersion": "14.0",
            "NSHighResolutionCapable": True,
            "NSPrincipalClass": "NSApplication",
        }
        if (resources / "Egress.icns").is_file():
            metadata["CFBundleIconFile"] = "Egress.icns"
        with (app / "Contents" / "Info.plist").open("wb") as handle:
            plistlib.dump(metadata, handle)
        (resources / "PREVIEW.txt").write_text(
            "Egress macOS preview. Default startup is isolated mock mode.\n"
            "No privileged helper is installed and no real network takeover is enabled.\n"
            "Source and third-party notices are in the accompanying repository.\n")
        run("codesign", "--force", "--deep", "--sign", "-", app)
        run("codesign", "--verify", "--deep", "--strict", app)
        run(executable_directory / binary_name, "--mock-smoke")
        run("file", executable_directory / binary_name)
        destination = output / "Egress.app"
        if destination.exists():
            # Only the exact generated artifact in this repository's output directory is replaced.
            backup = output / "Egress.previous.app"
            if backup.exists():
                shutil.rmtree(backup)
            destination.rename(backup)
        app.rename(destination)
        archive = output / "EgressController-osx-arm64.zip"
        run("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", destination, archive)
        checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
        (output / "SHA256SUMS.txt").write_text(checksum + "  " + archive.name + "\n")
        print("NativeAOT preview package:", destination)
        print("Archive:", archive)


if __name__ == "__main__":
    main()
