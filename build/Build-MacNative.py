#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Compile the read-only macOS bridge and run only its pure, offline ABI probes."""
import argparse
from pathlib import Path
import platform
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
parser.add_argument("--output", type=Path)
arguments = parser.parse_args()
if platform.system() != "Darwin":
    parser.error("The native bridge requires the macOS SDK")
root = Path(__file__).resolve().parents[1]
output = arguments.output or root / "artifacts" / "macos-native"
subprocess.run(["cmake", "-S", str(root / "native" / "macos"), "-B", str(output),
                "-DCMAKE_BUILD_TYPE=" + arguments.configuration, "-DBUILD_TESTING=ON"], check=True)
subprocess.run(["cmake", "--build", str(output), "--config", arguments.configuration], check=True)
subprocess.run(["ctest", "--test-dir", str(output), "--output-on-failure", "-C", arguments.configuration], check=True)
print("Compiled macOS read-only bridge; pure ABI probes passed. No live platform functions were invoked.")
