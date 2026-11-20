#!/usr/bin/env bash
set -euo pipefail
publish=$1
rid=$2
version=$3
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
app="$stage/CubicLauncher.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" artifacts
cp -R "$publish/." "$app/Contents/MacOS/"
cp packaging/Info.plist "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$app/Contents/Info.plist"
icons="$stage/CubicLauncher.iconset"
mkdir -p "$icons"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" packaging/icon.png --out "$icons/icon_${size}x${size}.png" >/dev/null
  sips -z "$((size * 2))" "$((size * 2))" packaging/icon.png --out "$icons/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$icons" -o "$app/Contents/Resources/CubicLauncher.icns"
mkdir "$stage/dmg"
mv "$app" "$stage/dmg/"
ln -s /Applications "$stage/dmg/Applications"
# Ad-hoc signature for Apple Silicon. Release distributions are not Developer-ID notarized.
codesign --force --deep --sign - "$stage/dmg/CubicLauncher.app"
hdiutil create -volname 'CubicLauncher' -srcfolder "$stage/dmg" -ov -format UDZO "artifacts/CubicLauncher-$rid.dmg"
