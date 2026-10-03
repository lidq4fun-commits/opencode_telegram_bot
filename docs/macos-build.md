# macOS build and release baseline

## Requirements

- Apple Silicon Mac running macOS 14 or newer.
- Xcode command-line tools / Swift 5.9 or newer, Python 3, and npm.
- Network access for initial dependency preparation. The resulting installer is offline-capable.

## Prepare the pinned runtime

From the repository root:

```sh
python3 native-swift/Fetch-Dependencies.py
```

This downloads the archives in `native-swift/OfflineDependencies.json` and verifies their pinned SHA-256/SHA-512 checksums. Supply the matching OpenCode and Bun license texts as:

```text
native-swift/dependencies/archives/OpenCode-LICENSE.txt
native-swift/dependencies/archives/Bun-LICENSE.md
```

These license texts are included in this repository's `docs/dependency-licenses/` directory. Copy them to the archive directory before preparing the runtime:

```sh
cp docs/dependency-licenses/OpenCode-LICENSE.txt native-swift/dependencies/archives/
cp docs/dependency-licenses/Bun-LICENSE.md native-swift/dependencies/archives/
bash native-swift/Prepare-Runtime.sh
bash native-swift/Prepare-Git.sh
```

The scripts build/prune the Bot production dependencies, validate native SQLite, build portable Git, and include matching Git source/license material. Do not commit downloads, `node_modules`, build outputs, logs or credentials.

## Test

```sh
cd native-swift
swift test
cd dependencies/payload/bot
npm ci --ignore-scripts --no-audit --no-fund
npm run build
npm run typecheck
npm run lint
npm test
```

Return to `native-swift` to build. If development dependencies were installed for testing, prune them before packaging:

```sh
npm prune --omit=dev --ignore-scripts --no-audit --no-fund
```

`--ignore-scripts` in the test-only installation assumes the production native SQLite dependency was already prepared. A fresh runtime preparation must build/install native dependencies with scripts enabled.

## Build and check

Run these commands from `native-swift`:

```sh
bash Build-App.sh
bash Smoke-Check.sh artifacts/0.1.20-binding-guides-20261003/OpenCodeTelegram.app
MACOS_TEST_APP="$PWD/artifacts/0.1.20-binding-guides-20261003/OpenCodeTelegram.app" \
  swift test --filter NativeTests.testActualAppBundleCopyRetainsSignature
swift Check-Window.swift "$PWD/artifacts/0.1.20-binding-guides-20261003/OpenCodeTelegram.app"
```

The build defaults to `native-swift/VERSION`; override with `VERSION=<new-version>`. Outputs are version-separated and the script refuses to overwrite an existing DMG. This baseline uses ad-hoc signing, not Developer ID signing or Apple notarization. Do not claim notarization or live Telegram/OAuth acceptance without completing those checks.

The optional `MACOS_TEST_DEPENDENCIES=<external-dependency-root>` native test validates official dependency downloads and SQLite compatibility without installing a new runtime.

## Version management

`macos-0.1.20-first-usable` is an annotated, immutable baseline tag. Future changes should use new commits and new version tags; do not force-push or move this milestone. DMGs and source archives are release attachments, not Git blobs. Preserve existing Windows/Android release tags and assets.
