#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
PAYLOAD="$ROOT/dependencies/payload"
ARCHIVES="$ROOT/dependencies/archives"
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { echo 'Apple Silicon Mac required.' >&2; exit 1; }
mkdir -p "$PAYLOAD/runtime" "$PAYLOAD/dependencies/opencode/v2" "$PAYLOAD/dependencies/bun" "$PAYLOAD/dependencies/licenses"
check_sha256() {
    local actual
    actual="$(shasum -a 256 "$ARCHIVES/$1" | awk '{print $1}')"
    [[ "$actual" == "$2" ]] || { echo "Checksum mismatch: $1" >&2; exit 1; }
}
check_sha512() {
    local actual
    actual="$(openssl dgst -sha512 -binary "$ARCHIVES/$1" | openssl base64 -A)"
    [[ "$actual" == "$2" ]] || { echo "Integrity mismatch: $1" >&2; exit 1; }
}
check_sha256 node-arm64.tar.gz 372331b969779ab5d15b949884fc6eaf88d5afe87bde8ba881d6400b9100ffc4
check_sha256 bun-arm64.zip 90987a3a16d7db556d886ac3d551e7b6d3edf0a1cf43acaed622e8676be1d12f
check_sha512 opencode-v2.tgz 'NNg1VCCTWSfLlNKpRb4RA6IE7H67ZBLBYmfIWjP3CxR9NPtdROQFCL8lpPf+Tz2Qje4Bb27sQOLWanYyNT/9dQ=='
echo 'Verified publisher checksums. Extracting Apple Silicon runtimes...'
tar -xzf "$ARCHIVES/node-arm64.tar.gz" --strip-components=1 -C "$PAYLOAD/runtime"
STAGE="$(mktemp -d "$ROOT/dependencies/extract.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
ditto -x -k "$ARCHIVES/bun-arm64.zip" "$STAGE"
cp "$STAGE/bun-darwin-aarch64/bun" "$PAYLOAD/dependencies/bun/bun"
for version in v2; do
    mkdir -p "$STAGE/$version"
    tar -xzf "$ARCHIVES/opencode-$version.tgz" -C "$STAGE/$version"
    cp "$STAGE/$version/package/bin/opencode" "$PAYLOAD/dependencies/opencode/$version/opencode"
done
chmod +x "$PAYLOAD/dependencies/bun/bun" "$PAYLOAD/dependencies/opencode/"*/opencode
cp "$PAYLOAD/runtime/LICENSE" "$PAYLOAD/dependencies/licenses/Node-LICENSE.txt"
cp "$ARCHIVES/OpenCode-LICENSE.txt" "$ARCHIVES/Bun-LICENSE.md" "$PAYLOAD/dependencies/licenses/"
cp "$ROOT/OfflineDependencies.json" "$PAYLOAD/dependency-sources.json"
export PATH="$PAYLOAD/runtime/bin:$PATH"
node --version
"$PAYLOAD/dependencies/bun/bun" --version
"$PAYLOAD/dependencies/opencode/v2/opencode" --version
echo 'Source: https://registry.npmjs.org (locked Bot dependencies)'
echo "Destination: $PAYLOAD/bot/node_modules"
echo 'Installing and compiling the Bot for macOS arm64. npm does not expose reliable aggregate download speed/percentage.'
cd "$PAYLOAD/bot"
npm ci --no-audit --no-fund --progress=true
npm run build
npm prune --omit=dev --ignore-scripts --no-audit --no-fund --progress=true
node -e 'const D=require("better-sqlite3");const db=new D(":memory:");if(db.prepare("select 1 as ok").get().ok!==1)process.exit(1);db.close();console.log("Native SQLite: OK")'
node dist/cli.js --help
echo 'macOS Bot runtime prepared and validated.'
