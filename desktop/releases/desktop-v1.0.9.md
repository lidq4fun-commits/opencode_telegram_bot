# Desktop v1.0.9

## Changes

- Separate startup settings and the embedded Bot CLI with interactive input and streamed output.
- Replace the legacy embedded browser with WebView2 and automatically connect the web workspace.
- Continue the Telegram-selected OpenCode session with off, prompt and force follow modes.
- Preserve local V2 browser login across refreshes while retaining the selected workspace.
- Include WebView2 SDK DLLs, tighten runtime payload staging and fix the bundled Node search path.
- Respect the explicitly selected OpenCode executable in the Bot process launcher.

## Requirements and known limitations

- Target: Windows 10/11 x64 with the .NET Framework/WPF desktop runtime.
- The package includes the Bot, production npm dependencies, Node/npm and WebView2 SDK DLLs.
- The WebView2 browser runtime is NOT included; install it separately if it is absent.
- OpenCode and Bun are NOT included; local engine setup still needs internet access when these are absent.
- Git is NOT included. Git-dependent project operations require a local Git installation.
- The planned complete offline dependency payload and compatible-existing-installation reuse policy are not implemented in this release.
- Telegram and model provider access require network connectivity and user configuration.
- No clean-machine installation certification or code signing is claimed.

## Source and artifacts

- Git tag: `desktop-v1.0.9`.
- Installer: `desktop/artifacts/OpenCodeTelegram-Setup-1.0.9.exe`.
- Distribution ZIP: `desktop/artifacts/OpenCodeTelegram-1.0.9-Windows-x64.zip`.
- Artifact hashes are recorded alongside the artifacts in `SHA256-1.0.9.txt`.
- Artifacts and local dependency binaries are intentionally excluded from Git.

## Verification

- Bot build, ESLint and TypeScript checks passed.
- Full Bot tests with `BOT_LOCALE=en`: 2355 passed, 7 skipped.
- After correcting an existing test's absent-project mock from null to undefined: 26 document/process regression tests passed.
- Packaged desktop assembly: 29 session-follow/browser-login regression assertions passed.
- Bundled Bot CLI and native SQLite smoke checks passed.
- Installer SHA256: `E3B7EF748E0872F60E630BA41B1FE33D621C37BC3E2C275608A2AB481ACE50EB`.
- Distribution ZIP SHA256: `549F18FFD24D910C1BFB7E534ADBB377914B1A129B4BC2CB2EE3D15D5EF26402`.
