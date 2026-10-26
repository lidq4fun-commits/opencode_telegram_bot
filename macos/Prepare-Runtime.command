#!/bin/bash
set -uo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
echo 'Preparing macOS Apple Silicon Bot runtime'
echo 'Package source: https://registry.npmjs.org'
echo "Destination: $ROOT/dependencies/payload"
echo 'npm aggregate speed and percentage are unavailable; native progress is enabled.'
mkdir -p "$ROOT/dependencies/payload"
ditto -x -k "$ROOT/bot-source.zip" "$ROOT/dependencies/payload"
bash "$ROOT/Prepare-Runtime.sh" 2>&1 | tee "$ROOT/dependencies/prepare-runtime.log"
result=${PIPESTATUS[0]}
printf '%s\n' "$result" > "$ROOT/dependencies/prepare-runtime.exit"
echo "Preparation exit code: $result"
exit "$result"
