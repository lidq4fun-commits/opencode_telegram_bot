#!/bin/bash
set -euo pipefail

# Run on the Apple Silicon build host, not on Windows.
ROOT="$(cd "$(dirname "$0")" && pwd)"
VERSION="${VERSION:-$(cat "$ROOT/VERSION")}"
OUT="$ROOT/artifacts"
APP="$OUT/$VERSION/OpenCodeTelegram.app"
DEPS="$OUT/$VERSION/OpenCodeTelegram-dependencies"
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo "An Apple Silicon Mac is required." >&2
    exit 1
fi
cd "$ROOT"
swift build -c release
BIN="$(swift build -c release --show-bin-path)"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/OpenCodeTelegram" "$APP/Contents/MacOS/OpenCodeTelegram"
cp "$ROOT/../desktop/EmbeddedWebSync.js" "$APP/Contents/Resources/EmbeddedWebSync.js"
PAYLOAD="$ROOT/dependencies/payload"
for input in runtime/bin/node bot/dist/cli.js bot/node_modules/better-sqlite3/prebuilds/darwin-arm64.node dependencies/bun/bun dependencies/git/bin/git dependencies/opencode/v2/opencode; do
    [[ -f "$PAYLOAD/$input" ]] || { echo "Missing runtime input: $input. Run Prepare-Runtime.command first." >&2; exit 1; }
done
ditto "$PAYLOAD/runtime" "$DEPS/runtime"
mkdir -p "$DEPS/bot"
for input in dist node_modules assets; do ditto "$PAYLOAD/bot/$input" "$DEPS/bot/$input"; done
for input in package.json package-lock.json .env.example LICENSE EOT10Crypto.mjs; do cp "$PAYLOAD/bot/$input" "$DEPS/bot/$input"; done
for input in bun git git-source licenses; do ditto "$PAYLOAD/dependencies/$input" "$DEPS/dependencies/$input"; done
ditto "$PAYLOAD/dependencies/opencode/v2" "$DEPS/dependencies/opencode/v2"
if [[ -d "$APP/Contents/Resources/dependencies/opencode/v1" ]]; then
    rm -r "$APP/Contents/Resources/dependencies/opencode/v1"
fi
cp "$PAYLOAD/dependency-sources.json" "$DEPS/dependency-sources.json"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>OpenCodeTelegram</string>
<key>CFBundleIdentifier</key><string>org.opencode.telegram.desktop</string>
<key>CFBundleName</key><string>opencode_telegram_bot</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$VERSION</string>
<key>CFBundleVersion</key><string>1</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSAppTransportSecurity</key><dict><key>NSAllowsArbitraryLoadsInWebContent</key><true/><key>NSAllowsLocalNetworking</key><true/></dict>
</dict></plist>
EOF
plutil -lint "$APP/Contents/Info.plist"
# Local ad-hoc signing is not Developer ID signing or notarization.
codesign --force --deep --preserve-metadata=entitlements --sign - --identifier org.opencode.telegram.desktop "$APP"
codesign --verify --deep --strict "$APP"
DMG_STAGE="$(mktemp -d "$OUT/dmg-stage.XXXXXX")"
trap 'rm -rf "$DMG_STAGE"' EXIT
ditto "$APP" "$DMG_STAGE/OpenCodeTelegram.app"
ditto "$DEPS" "$DMG_STAGE/OpenCodeTelegram-dependencies"
ln -s /Applications "$DMG_STAGE/Applications"
cat > "$DMG_STAGE/README.txt" <<EOF
双击 OpenCodeTelegram.app，选择语言与安装目录，点击安装。
Open OpenCodeTelegram.app, choose a language and destination, then click Install.
Apple Silicon only. This build is ad-hoc signed, not notarized.
仅支持 Apple Silicon。本构建为本地临时签名，未经 Apple 公证。
EOF
DMG="$OUT/OpenCodeTelegram-$VERSION-macOS-arm64.dmg"
[[ ! -e "$DMG" ]] || { echo "Output already exists: $DMG" >&2; exit 1; }
hdiutil create -volname OpenCodeTelegram -srcfolder "$DMG_STAGE" -format UDZO "$DMG"
shasum -a 256 "$DMG"
echo "Application and DMG built: $DMG"
echo 'This migration build is not yet feature-parity accepted or notarized.'
