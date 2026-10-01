#!/usr/bin/env bash
# Builds "Gcode Recovery.app" for macOS (arm64 by default) and packs it into a compressed,
# drag-to-Applications DMG plus a zip. Requires the .NET 10 SDK and Xcode command line tools.
#
#   scripts/package-macos.sh [rid]        rid: osx-arm64 (default) | osx-x64
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
APP_NAME="Gcode Recovery"
OUT="$ROOT/artifacts"
PUBLISH="$OUT/publish/$RID"
APP="$OUT/$APP_NAME.app"
ARCH="${RID#osx-}"

rm -rf "$PUBLISH" "$APP" "$OUT/dmg-staging"
mkdir -p "$OUT"

echo "==> Publishing self-contained $RID build"
dotnet publish "$ROOT/src/GcodeRecovery.App/GcodeRecovery.App.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:UseAppHost=true -p:DebugType=None -o "$PUBLISH"

echo "==> Creating app bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"

ICONSET="$OUT/AppIcon.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
swift "$ROOT/scripts/render-icon.swift" "$ROOT/assets/icon.svg" "$OUT/icon-1024.png" 1024
for s in 16 32 128 256 512; do
  sips -z $s $s "$OUT/icon-1024.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) "$OUT/icon-1024.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET" "$OUT/icon-1024.png"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>io.github.ddann.gcoderecovery</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>GcodeRecovery</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>Gcode Recovery talks to your 3D printer on the local network (status, camera, control and file upload).</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>G-code</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>CFBundleTypeExtensions</key><array><string>gcode</string><string>3mf</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST

echo "==> Ad-hoc code signing"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

echo "==> Building DMG (drag to Applications)"
STAGE="$OUT/dmg-staging"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
DMG="$OUT/GcodeRecovery-$VERSION-macos-$ARCH.dmg"
rm -f "$DMG"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$DMG" >/dev/null
rm -rf "$STAGE"

ZIP="$OUT/GcodeRecovery-$VERSION-macos-$ARCH.zip"
rm -f "$ZIP"
(cd "$OUT" && ditto -c -k --keepParent "$APP_NAME.app" "$ZIP")

echo "==> Done"
ls -lh "$DMG" "$ZIP"
