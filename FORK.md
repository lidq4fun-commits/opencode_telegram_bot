# OpenCode Telegram Desktop Fork

This repository contains an independent desktop product based on upstream
`grinev/opencode-telegram-bot` v0.26.1 (upstream commit
`8f934fecb8ba08ce56fb137142eb580c3facb1a1`, MIT license). The TypeScript bot
source is vendored under `bot/`; the desktop product builds and ships that
source, its production dependencies, the EOT10 protocol module, and a Node.js
runtime. Installed builds do not run `npm install -g @grinev/opencode-telegram-bot`
and do not patch an upstream installation.

The fork adds EOT10 text, rich-message, and media protection; hybrid direct/local
attachment handling; and a Windows-native desktop application with
DPAPI-protected settings, CLI controls, selectable OpenCode v1/v2 APIs, Chinese
and English UI, and optional Windows sign-in startup.

## Development layout

- `bot/` — vendored TypeScript bot, tests, CLI, and runtime sources.
- `desktop/` — WPF desktop and self-extracting installer source/build script.
- `OpenCodeTelegramLauncher.cs` — legacy launcher retained during migration.
- `EOT10Crypto.mjs` — EOT10 wire-protocol implementation.

## Build/runtime model

The installer carries the compiled Bot, source tree, production dependencies,
Node runtime, npm runtime for optional OpenCode installation, and desktop app.
OpenCode itself remains selectable: use an existing local/remote server or
install the optional local engine from the desktop UI/CLI. GUI settings are
protected with Windows CurrentUser DPAPI. The Bot token and EOT10 password are
not committed to this repository.

CLI entry point after installation:

```text
opencode-telegram help
opencode-telegram configure bot
opencode-telegram configure eot10 enable
opencode-telegram configure opencode
opencode-telegram opencode install
opencode-telegram start
opencode-telegram status
opencode-telegram stop
opencode-telegram autostart enable|disable|status
```

Installer environment variables:

- `OPENCODE_TELEGRAM_ROOT` — installed product directory.
- `OPENCODE_TELEGRAM_HOME` — `%APPDATA%\OpenCodeTelegram` user configuration.
- User `PATH` — contains the fork's `bin` directory for CLI use.

Use `desktop/Build-Desktop-Package.ps1` to build the self-extracting setup EXE.
