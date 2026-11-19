#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
output="${1:-"$root/build/macos"}"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
app="$scratch/SSH Manager.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$scratch/SshManager.iconset"
export MACOSX_DEPLOYMENT_TARGET=14.0
for architecture in arm64 x86_64; do
    xcrun clang -arch "$architecture" -mmacosx-version-min=14.0 -O2 -c "$root/macos/Sources/PTY.c" -o "$scratch/pty-$architecture.o"
    xcrun swiftc -O -swift-version 5 -target "$architecture-apple-macosx14.0" \
        -import-objc-header "$root/macos/Sources/PTY.h" \
        "$root"/macos/Sources/*.swift "$scratch/pty-$architecture.o" -larchive -o "$scratch/SshManager-$architecture"
done
xcrun lipo -create "$scratch"/SshManager-* -output "$app/Contents/MacOS/SshManager"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$root/assets/AppMark.png" --out "$scratch/SshManager.iconset/icon_${size}x${size}.png" >/dev/null
    sips -z "$((size * 2))" "$((size * 2))" "$root/assets/AppMark.png" --out "$scratch/SshManager.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$scratch/SshManager.iconset" -o "$app/Contents/Resources/SshManager.icns"
cp "$root/macos/Resources/Info.plist" "$app/Contents/Info.plist"
cp "$root/LICENSE" "$app/Contents/Resources/LICENSE"
cp -R "$root/macos/Resources/Terminal" "$app/Contents/Resources/"
signer="${SSH_MANAGER_SIGN_IDENTITY:--}"
if [[ "$signer" == "-" ]]; then
    codesign --force --sign - "$app"
else
    codesign --force --options runtime --timestamp --sign "$signer" "$app"
fi
codesign --verify --strict "$app"
mkdir "$scratch/image"
mv "$app" "$scratch/image/"
ln -s /Applications "$scratch/image/Applications"
hdiutil create -quiet -volname "SSH Manager" -srcfolder "$scratch/image" -format UDZO "$scratch/SSH-Manager-macOS.dmg"
if [[ "$signer" != "-" ]]; then
    codesign --force --timestamp --sign "$signer" "$scratch/SSH-Manager-macOS.dmg"
fi
mv -f "$scratch/SSH-Manager-macOS.dmg" "$output/SSH-Manager-macOS.dmg"
printf '%s\n' "$output/SSH-Manager-macOS.dmg"
