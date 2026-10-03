# macOS 0.1.20 release validation

Baseline: user-approved first usable macOS release, 2026-10-03.
Tag: `macos-0.1.20-first-usable`.

## Clean source snapshot

Tests were rerun from a clean export of the staged Git source, including normalized line endings and freshly installed Bot dependencies; no working-directory `node_modules` or build cache was required.

| Check | Result |
| --- | --- |
| Bot `npm ci` | Passed |
| Bot TypeScript build | Passed |
| Bot lint | Passed |
| Bot test typecheck | Passed |
| Bot complete test suite | 196 files passed; 2398 tests passed; 7 Windows-conditioned tests skipped; zero failures |
| Native `swift test` | 50 tests; 5 optional environment/live tests skipped; zero failures |
| Publisher-pinned build archive checksums | Git, Node, Bun and OpenCode V2 passed |

## Binary baseline

The release reuses the exact 0.1.20 installer already delivered to and approved by the user; no runtime/UI change was introduced for Git publication.

- Filename: `OpenCodeTelegram-0.1.20-binding-guides-20261003-macOS-arm64.dmg`
- SHA-256: `486ba78c36b04d84d01a8ea346f5fc2b4cf399970675b191a7a710083626d03f`
- Platform: Apple Silicon, macOS 14+.
- Signature: local ad-hoc; not Apple-notarized.
- Runtime smoke checks: passed for Node, Bun, OpenCode V2, Git, Bot CLI and native SQLite.
- LaunchServices disposable app launch: passed.
- Actual installer app-copy/signature regression: passed during the external-dependency migration.
- Optional live Node/Bun official archive validation and Node SQLite compatibility test: passed during dependency-updater validation; no packaged executable was replaced by this test.

## Publication hygiene and limitations

Git source excludes runtime binaries, historical DMGs, caches, real `.env` files, databases, logs and user settings. A staged-source scan found no GitHub PAT, model API key or Telegram Bot Token matching secret formats. The localhost-only TLS test fixture is intentionally public and is not a user credential.

The installation package does not contain user-specific settings or credentials. No real Telegram/OAuth/provider request was exercised with the user's account. This milestone records user acceptance of the macOS build, not blanket feature parity or security certification.
