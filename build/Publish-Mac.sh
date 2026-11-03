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
mac_icons="$mac_output/App.iconset"
mkdir -p "$mac_icons"
for mac_size in 16 32 128 256 512; do
    sips -z "$mac_size" "$mac_size" packaging/Assets/Square150x150Logo.png --out "$mac_icons/icon_${mac_size}x${mac_size}.png" >/dev/null
    mac_retina=$((mac_size * 2))
    sips -z "$mac_retina" "$mac_retina" packaging/Assets/Square150x150Logo.png --out "$mac_icons/icon_${mac_size}x${mac_size}@2x.png" >/dev/null
done
iconutil -c icns "$mac_icons" -o "$mac_bundle/Resources/App.icns"
cat > "$mac_bundle/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>EgressController.MacApp</string>
<key>CFBundleIdentifier</key><string>com.egresscontroller.mac</string>
<key>CFBundleName</key><string>EgressController</string>
<key>CFBundleDisplayName</key><string>EgressController</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>App.icns</string>
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
