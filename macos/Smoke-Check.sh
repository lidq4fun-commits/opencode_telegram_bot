#!/bin/bash
set -euo pipefail
APP="${1:?Pass the OpenCodeTelegram.app path}"
[[ "$APP" = /* ]] || APP="$(cd "$(dirname "$APP")" && pwd)/$(basename "$APP")"
RES="$APP/Contents/Resources"
codesign --verify --deep --strict "$APP"
plutil -lint "$APP/Contents/Info.plist"
file "$APP/Contents/MacOS/OpenCodeTelegram"
[[ "$("$RES/runtime/bin/node" -p process.arch)" == arm64 ]]
export PATH="$RES/runtime/bin:$RES/dependencies/bun:$RES/dependencies/git/bin:/usr/bin:/bin"
export GIT_EXEC_PATH="$RES/dependencies/git/libexec/git-core"
export GIT_TEMPLATE_DIR="$RES/dependencies/git/share/git-core/templates"
"$RES/runtime/bin/node" --version
"$RES/dependencies/bun/bun" --version
"$RES/dependencies/opencode/v1/opencode" --version
"$RES/dependencies/opencode/v2/opencode" --version
"$RES/dependencies/git/bin/git" --version
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
export OPENCODE_TELEGRAM_HOME="$STAGE/bot-home"
export OPENCODE_TELEGRAM_RUNTIME_MODE=installed
cd "$RES/bot"
node -e 'const D=require("better-sqlite3");const db=new D(":memory:");if(db.prepare("select 1 as ok").get().ok!==1)process.exit(1);db.close();console.log("Packaged native SQLite: OK")'
node dist/cli.js --help
git init -q "$STAGE/git-repo"
git -C "$STAGE/git-repo" -c user.name=PackagingTest -c user.email=packaging@example.invalid commit --allow-empty -q -m 'Isolated packaging smoke test'
git -C "$STAGE/git-repo" log -1 --format=%s
[[ -f "$RES/EmbeddedWebSync.js" ]]
for license in Node-LICENSE.txt Bun-LICENSE.md OpenCode-LICENSE.txt Git-COPYING.txt; do [[ -f "$RES/dependencies/licenses/$license" ]]; done
[[ -f "$RES/dependencies/git-source/git-2.56.0.tar.xz" ]]
echo 'Packaged runtime smoke checks passed. No real Bot was started or model request sent.'
