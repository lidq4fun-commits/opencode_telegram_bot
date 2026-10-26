# Native macOS migration

## Target

- Preserve the user-accepted Windows `desktop-v1.0.15` baseline unchanged.
- Add a native Swift/AppKit application for Apple Silicon.
- Match all eight Windows pages, their actions and Chinese/English behavior.
- Use the standard macOS title bar and red/yellow/green window controls.
- Package an application bundle and a DMG; do not claim feature parity until the checks below pass.

## Build host

Verified over SSH: macOS 26.6.2, arm64, Xcode 27.0 and Swift 6.4. Node, npm, Bun and OpenCode were not found on the initial shell PATH. SSH credentials must not be stored in this repository.

## Implemented migration foundation

The native application now compiles on the Mac. The implementation includes the eight-page layout, Keychain-backed settings, bilingual installer, staged installation and rollback, managed engine operations, LaunchAgent configuration, interactive CLI, V1/V2 service management, WKWebView synchronization and exit/uninstall cleanup progress. These are implementation milestones, not a declaration of full Windows feature parity.

The arm64 payload includes Node 24.15.0, Bun 1.4.2, OpenCode V1 1.18.34, OpenCode V2 2.0.22, Git 2.56.0 and Bot dependencies with working native SQLite. Git links only to macOS-provided libraries and includes matching source/build instructions. Git and the Swift application target macOS 14 or later; only the supplied macOS 26.6.2 build host has been checked so far.

Native automated checks cover CLI parsing, managed-path validation, encryption environment, origin isolation, session/project routes and installation copying. Packaging smoke checks validate the app signature, arm64 executable, runtime version probes, SQLite loading, a relocated Git repository and license files without starting a real Bot or submitting model prompts.

The generated DMG is a **migration test build** with ad-hoc signing only. Developer ID signing, notarization and the remaining manual acceptance checks are not complete. Do not publish it as the first usable Mac baseline until the user confirms end-to-end behavior.

## Acceptance checklist

- [ ] Overview: service start/stop/status, live logs and actionable errors.
- [ ] Bot account: token and allowed user configuration.
- [ ] Encryption: EOT10 enable/disable and minimum-length password validation.
- [ ] OpenCode engines: API version, connection/authentication, model selection, compatible local tool discovery, install/select/uninstall.
- [ ] Startup: application and Bot startup using macOS mechanisms.
- [ ] Bot CLI: supported management commands, history, hidden secret input and stop.
- [ ] Embedded web: WKWebView, compact three-dot menu, full remaining viewport, authentication persistence and JavaScript synchronization bridge.
- [ ] Language & Appearance: immediate Chinese/English selection and persistence.
- [ ] Secret storage: macOS Keychain, with failures surfaced instead of plaintext fallback.
- [ ] Runtime: bundled arm64 Node, compatible native SQLite, Bun and OpenCode V1/V2; retain upstream licenses and dependency verification.
- [ ] Lifecycle: visible cleanup progress, stop owned processes, and close after cleanup.
- [ ] Installation: language selection before configuration, install progress and correct application/data paths.
- [ ] Uninstallation: visible cleanup progress and preservation of user settings unless explicitly requested otherwise.
- [ ] Build `.app` and `.dmg` on the Mac, with no personal credentials included.
- [ ] Validate initial launch, relaunch, web submission, phone synchronization and cleanup on the Mac.
- [ ] Record signing/notarization status accurately; unsigned artifacts must not be described as notarized.

## Implementation order

1. Swift application shell and matching layout/localization.
2. Keychain settings and platform-specific runtime/service management.
3. Bot integration, CLI, login items and WKWebView synchronization.
4. Arm64 runtime payload, installation/uninstallation flows and packaging.
5. Native build, automated checks and real-device acceptance.
