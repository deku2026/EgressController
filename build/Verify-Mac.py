#!/usr/bin/env python3
"""Mock-only macOS acceptance: build, in-memory tests, pure ABI checks, renders and optional AOT package."""
import argparse
import os
from pathlib import Path
import platform
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = ("EgressController.Policy.Tests", "EgressController.Runtime.Tests", "EgressController.Mac.Tests", "EgressController.Client.Tests")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--without-package", action="store_true", help="Development iteration; CI also runs the AOT package step.")
    parser.add_argument("--version", default="0.1.6", help="Three-component package version.")
    args = parser.parse_args()
    if platform.system() != "Darwin":
        parser.error("Run this acceptance script on macOS. Policy/runtime managed tests may run independently on other hosts.")
    environment = {key: value for key, value in os.environ.items() if not key.startswith("EGRESS_LIVE_")}
    environment.update(EGRESS_MOCK_ONLY="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", TESTINGPLATFORM_TELEMETRY_OPTOUT="1")

    def run(*command):
        print("Running:", " ".join(map(str, command)), flush=True)
        subprocess.run(list(map(str, command)), cwd=ROOT, env=environment, check=True)

    run("dotnet", "restore", "EgressController.slnx")
    run("dotnet", "build", "EgressController.slnx", "-c", args.configuration, "--no-restore", "-p:EgressMockOnly=true", "-p:EgressPrepareRiderPayload=false")
    for name in PROJECTS:
        run("dotnet", "run", "--project", ROOT / "tests" / name, "-c", args.configuration, "--no-build", "--", "--progress", "off")
    run(sys.executable, ROOT / "build" / "Verify-XrayRoutingRpc.py")
    run(sys.executable, ROOT / "build" / "Build-MacNative.py", "--configuration", args.configuration)
    run("dotnet", "run", "--project", "tools/EgressController.MacPreview", "-c", args.configuration, "--no-build", "--", "--screenshots", ROOT / "artifacts" / "mac-ui")
    if not args.without_package:
        # Exercise the normal Rider build path, including native payload preparation.
        run("dotnet", "build", "src/EgressController.Desktop", "-c", args.configuration, "-m:1", "--disable-build-servers")
        run(sys.executable, ROOT / "build" / "Package-Mac.py", "--configuration", args.configuration, "--version", args.version)
    print("Mock-only acceptance passed. No real TUN, protected process, service, startup entry or system DNS was changed.")


if __name__ == "__main__":
    main()
