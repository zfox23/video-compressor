#!/bin/bash
# Build the trim/compress GUI app used by the "Compress video..." Quick Action.
cd "$(dirname "$0")" || exit 1
APP=CompressVideo.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
swiftc -O -swift-version 5 -parse-as-library -enable-bare-slash-regex \
    CompressVideoGUI.swift -o "$APP/Contents/MacOS/CompressVideoGUI" || exit 1
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>CompressVideoGUI</string>
    <key>CFBundleIdentifier</key><string>local.zach.compress-video-gui</string>
    <key>CFBundleName</key><string>Compress Video</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
codesign -s - -f "$APP"
rm -f CompressVideoGUI
