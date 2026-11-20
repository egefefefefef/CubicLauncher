#!/usr/bin/env bash
set -Eeuo pipefail

publish=$1
rid=$2
version=$3
case "$rid" in
  osx-arm64) architecture=arm64 ;;
  osx-x64) architecture=x86_64 ;;
  *) echo "Unsupported macOS runtime: $rid" >&2; exit 2 ;;
esac

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
phase='prepare app bundle'
trap 'printf "::error title=macOS packaging failed::%s: %s (line %s, exit %s)\n" "$rid" "$phase" "$LINENO" "$?" >&2' ERR

app="$stage/payload/CubicLauncher.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" artifacts
cp -R "$publish/." "$app/Contents/MacOS/"
cp packaging/Info.plist "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$app/Contents/Info.plist"

phase='verify app executable'
test -x "$app/Contents/MacOS/CubicLauncher"
lipo -verify_arch "$architecture" "$app/Contents/MacOS/CubicLauncher"

phase='build app icon'
icons="$stage/CubicLauncher.iconset"
mkdir -p "$icons"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" packaging/icon.png --out "$icons/icon_${size}x${size}.png" >/dev/null
  sips -z "$((size * 2))" "$((size * 2))" packaging/icon.png --out "$icons/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$icons" -o "$app/Contents/Resources/CubicLauncher.icns"
ln -s /Applications "$stage/payload/Applications"

phase='sign and verify app bundle'
# Ad-hoc signature for Apple Silicon. Release distributions are not Developer-ID notarized.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

# Disk-image tooling on hosted runners can fail intermittently. Retry only the DMG
# operations, discarding partial images; never upload an image that didn't verify.
phase='create and verify DMG'
image="$stage/CubicLauncher-$rid.dmg"
log="$stage/hdiutil.log"
for attempt in 1 2 3; do
  rm -f "$image"
  if { hdiutil create -volname CubicLauncher -fs HFS+ -srcfolder "$stage/payload" -format UDZO "$image" &&
       hdiutil verify "$image"; } >"$log" 2>&1; then
    cat "$log"
    mv "$image" "artifacts/CubicLauncher-$rid.dmg"
    exit 0
  fi
  cat "$log" >&2
  if [[ $attempt -lt 3 ]]; then
    printf '::warning title=macOS DMG retry::%s: attempt %s failed; retrying.\n' "$rid" "$attempt" >&2
    sleep "$((attempt * 5))"
  fi
done

# Keep the disk-image error visible in the Checks UI, even if full logs are unavailable.
detail=$(tail -n 4 "$log")
detail=${detail//%/%25}
detail=${detail//$'\r'/%0D}
detail=${detail//$'\n'/%0A}
printf '::error title=macOS DMG failed::%s: could not create and verify the image in 3 attempts. %s\n' "$rid" "$detail" >&2
exit 1
