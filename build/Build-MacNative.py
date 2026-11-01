#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Compile the macOS platform/privilege bridge and run only its pure, offline ABI probes."""
import argparse
from pathlib import Path
import platform
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
parser.add_argument("--output", type=Path)
parser.add_argument("--architecture", choices=("arm64", "x86_64"), default=platform.machine())
parser.add_argument("--without-tests", action="store_true", help="Build the library only for an ordinary IDE/MSBuild build.")
arguments = parser.parse_args()
if platform.system() != "Darwin":
    parser.error("The native bridge requires the macOS SDK")
root = Path(__file__).resolve().parents[1]
output = arguments.output or root / "artifacts" / "macos-native"
# Finder-launched IDEs need not inherit the shell's Homebrew PATH.
cmake = shutil.which("cmake") or next((str(path) for path in
    (Path("/opt/homebrew/bin/cmake"), Path("/usr/local/bin/cmake")) if path.is_file()), None)
if cmake is None:
    parser.error("CMake is missing. Install CMake and Xcode Command Line Tools, then rebuild.")
source = root / "native" / "macos"
fresh = []
cache = output / "CMakeCache.txt"
if cache.is_file():
    entries = dict(line.split("=", 1) for line in cache.read_text().splitlines()
                   if line.startswith(("CMAKE_HOME_DIRECTORY:INTERNAL=", "CMAKE_CACHEFILE_DIR:INTERNAL=")))
    if (entries.get("CMAKE_HOME_DIRECTORY:INTERNAL") != str(source.resolve())
            or entries.get("CMAKE_CACHEFILE_DIR:INTERNAL") != str(output.resolve())):
        # A worktree move invalidates CMake's absolute-path cache. Regenerate only build metadata.
        fresh = ["--fresh"]
        print("Regenerating native build metadata after checkout relocation.", flush=True)
subprocess.run([cmake, *fresh, "-G", "Unix Makefiles", "-S", str(source), "-B", str(output),
                "-DCMAKE_BUILD_TYPE=" + arguments.configuration, "-DCMAKE_OSX_ARCHITECTURES=" + arguments.architecture,
                "-DBUILD_TESTING=" + ("OFF" if arguments.without_tests else "ON")], check=True)
command = [cmake, "--build", str(output), "--config", arguments.configuration]
if arguments.without_tests:
    command += ["--target", "EgressMacNative"]
subprocess.run(command, check=True)
if arguments.without_tests:
    print("Compiled macOS platform/privilege bridge. No native functions were invoked.")
else:
    subprocess.run([str(Path(cmake).with_name("ctest")), "--test-dir", str(output), "--output-on-failure", "-C", arguments.configuration], check=True)
    print("Compiled macOS platform/privilege bridge; pure ABI probes passed. No live platform functions were invoked.")
