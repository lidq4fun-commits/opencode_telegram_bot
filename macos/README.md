# Native macOS client / 原生 macOS 客户端

**Migration test build, not yet accepted as equivalent to Windows 1.0.15. / 迁移测试版，尚未通过与 Windows 1.0.15 的完整功能对等验收。**

Swift/AppKit window hosting a SwiftUI interface, with native macOS window controls and a WKWebView integration. Deployment target: Apple Silicon, macOS 14 or later; only macOS 26.6.2 has been used for build checks so far. Verified compilation on Xcode 27 / Swift 6.4.

Implemented foundation:

- Eight-page layout and immediate Chinese/English selection.
- Keychain-backed configuration, without a plaintext fallback.
- Bot CLI runtime environment and process execution.
- Local V2 service connection, credential configuration and authenticated API check.
- WKWebView metadata bridge reusing `desktop/EmbeddedWebSync.js`.
- Exit cleanup progress and error reporting.
- Managed engine installation/removal and local engine scanning.
- Application/Bot login startup using per-user LaunchAgents without stored plist credentials.
- Native management CLI, quoted arguments, history, hidden input and command stop.
- Local V1 web process management and bilingual installation/uninstallation progress.
- Verified arm64 Node/Bun/OpenCode V1/V2 payload and macOS-native Bot SQLite build.

仍需验证：端到端手机同步、加密消息和附件、网页提交与认证保持、登录启动、安装/更新/卸载和界面对等。新配置的默认 API 版本与 Windows 基线一致，为 V1。当前测试版不应宣称完全对等。

Still to verify: end-to-end phone synchronization, encrypted messages/files, web submission/authentication persistence, login startup, install/update/uninstall and UI parity. Fresh settings default to V1, matching the Windows baseline. Do not claim complete parity for this migration test build.

## Build on Mac

```sh
cd macos
swift build
swift test
# Download and verify the archives listed in OfflineDependencies.json first.
bash Prepare-Runtime.sh
bash Prepare-Git.sh
bash Build-App.sh
bash Smoke-Check.sh artifacts/OpenCodeTelegram.app
```

Build preparation requires a source snapshot at `dependencies/payload/bot`, official runtime archives and license files. Run package downloads in a visible terminal with native progress. The app expects the production payload under `Contents/Resources`: `runtime/bin/node`, `bot/dist/cli.js`, production Bot dependencies and `dependencies/opencode/v1|v2/opencode`. Windows Node/SQLite/Bun/OpenCode binaries cannot be reused on macOS. Git is built for arm64 using macOS-provided libraries; its source and build script are included alongside its GPL license.

The DMG contains an app that opens a language/destination selection installer when launched from the mounted image. The default destination is `~/Applications`, without administrator privileges. Uninstallation is available in Language & Appearance and preserves Keychain settings and logs. Installation copies through a staging bundle and rolls back if replacement fails.

The current build uses local ad-hoc signing only, not Developer ID signing or notarization. macOS may block downloaded builds under Gatekeeper; do not disable system security globally. A trusted distribution build requires the owner's signing/notarization setup.

No SSH credentials belong in this repository. The app stores its settings in macOS Keychain, and runtime metadata under `~/Library/Application Support/OpenCodeTelegram`.
