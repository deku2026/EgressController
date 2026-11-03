#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
mac_rid="${1:-osx-arm64}"
case "$mac_rid" in osx-arm64|osx-x64) ;; *) exit 2;; esac
mac_output="$(pwd)/artifacts/mac-$mac_rid"
dotnet publish src/EgressController.MacApp -c Release -r "$mac_rid" -p:PublishAot=true -o "$mac_output/gui"
dotnet publish src/EgressController.MacAgent -c Release -r "$mac_rid" -p:PublishAot=true -o "$mac_output/agent"
mac_bundle="$mac_output/EgressController.app/Contents"
# Rebuild this generated bundle; debug symbols remain beside it, outside code directories.
rm -rf "$mac_output/EgressController.app"
mkdir -p "$mac_bundle/MacOS" "$mac_bundle/Resources"
cp "$mac_output/gui/EgressController.MacApp" "$mac_bundle/MacOS/"
cp "$mac_output/gui/"*.dylib "$mac_bundle/MacOS/"
cat > "$mac_bundle/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>EgressController.MacApp</string>
<key>CFBundleIdentifier</key><string>com.egresscontroller.mac</string>
<key>CFBundleName</key><string>EgressController</string>
<key>CFBundleDisplayName</key><string>EgressController</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>LSMinimumSystemVersion</key><string>12.0</string>
</dict></plist>
PLIST
for mac_library in "$mac_bundle/MacOS/"*.dylib; do codesign --force --sign - "$mac_library"; done
codesign --force --sign - "$mac_output/EgressController.app"
codesign --verify --deep --strict "$mac_output/EgressController.app"
"$mac_bundle/MacOS/EgressController.MacApp" --mock-smoke
"$mac_output/agent/EgressController.MacAgent" --mock-smoke
printf '%s\n' "$mac_output/EgressController.app" "$mac_output/agent/EgressController.MacAgent"
