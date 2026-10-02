# opencode_telegram_bot

OpenCode Telegram is an independent Windows desktop product and Bot fork. The Windows ZIP distribution contains a standalone setup program and README. The setup EXE embeds the Bot source, compiled Bot, production Node dependencies, Node.js runtime, and npm runtime; installed builds do not install or call `@grinev/opencode-telegram-bot` from npm.

Target machines: 64-bit Windows 10/11. The installer carries Node.js/npm for the Bot; the desktop host uses the .NET Framework/WPF runtime supplied by supported Windows releases. Local OpenCode engine packages are downloaded when selected.

## Features

- **Windows-native WPF interface:** control center, Telegram Bot binding, EOT10 encryption, OpenCode engine selection, and startup options.
- **Independent Bot runtime:** forked TypeScript Bot and Node are installed together; the system does not need a global Telegram Bot npm package.
- **OpenCode choice:** connect to an existing local/remote OpenCode server, or install a local OpenCode engine from the GUI or CLI.
- **EOT10:** encrypts authorized private-chat text, Rich Message text, media attachments, and media groups. Bot token, OpenCode credentials, and EOT10 password are stored together in a file protected by Windows CurrentUser DPAPI.
- **Dual control surface:** GUI and CLI use the same settings and service manager.
- **Windows sign-in startup:** separately enable starting the desktop app and starting the Bot from the GUI.
- **Windows installation integration:** Start menu and desktop shortcuts plus an Apps & Features uninstall entry.
- **Bilingual desktop UI:** switch between Simplified Chinese and English from the window toolbar.
- **Attachment routing:** recognized attachment MIME types go directly to OpenCode; text-like files are submitted as text; an unknown MIME with valid UTF-8 content is detected as text; only unknown binary files are saved byte-for-byte into the current project for local agent tools.

## Build the Windows ZIP package

Build prerequisites: 64-bit Windows with the .NET Framework C# compiler, Node.js 24+ x64 with npm, and installed Bot development dependencies. The build script auto-detects Node.js from `PATH` or common install folders (or accepts `-NodeHome`), invokes that exact Node/npm rather than relying on global command resolution, and creates a unique staging directory without deleting previous build files. It prunes dev-only packages and bundles Node/npm with Bot production dependencies.

```powershell
& '.\desktop\Build-Desktop-Package.ps1'
```

Output:

```text
desktop\artifacts\OpenCodeTelegram-1.0.0-Windows-x64.zip
```

Before binding the same Telegram Bot token in the fork for the first time, stop the legacy v11 launcher/daemon to avoid two polling clients using one token. The new installer does not import the legacy `.env` or DPAPI configuration.

The standalone setup EXE embeds the compressed runtime archive. Installing extracts the independent Bot source/runtime and places `OpenCodeTelegram.exe` plus `opencode-telegram.cmd` in the selected installation folder. For a clean build, run `npm ci` in `bot/` first; `Build-Desktop-Package.ps1` prunes dev dependencies only in a staging copy.

## First run

1. Run the standalone setup EXE and select an installation folder. No Node.js, npm, or adjacent dependency folder is required for the Bot runtime.
2. Setup only installs the app. On the completion page, select **Launch opencode_telegram_bot now** if desired, or click **Finish** to exit; later, open it from the desktop or Start menu shortcut. Uninstall from Windows **Installed apps** or a compatible uninstall manager.
3. In **机器人账号**, bind a BotFather token and the allowed Telegram user ID.
4. In **加密设置**, choose EOT10 and set a password of at least 12 characters. Use the same password for this Bot in the Android client.
5. In **OpenCode 引擎**, select an existing server or choose the separate **Install OpenCode (API v1)** or **Install OpenCode (API v2)** action. V1 installs `opencode-ai@^1`; V2 installs `@opencode/cli@^2`. The two CLI versions are kept in separate folders. The app checks Node.js and Bun prerequisites and installs missing dependencies at user scope when needed. V2 requires a server password. For a local V2 service, the app generates and DPAPI-protects one automatically; for an existing server, enter its password in the engine settings.
   Local engine installation requires internet access. If V2's Bun runtime is missing, the app downloads and SHA-256 verifies the official Windows x64 release and installs it under the current user's app-data folder; WinGet is only a fallback. Bun requires Windows 10 version 1809 or later.
6. Press **启动机器人** from **概览**.
7. Use **启动与 CLI** to run `opencode_telegram_bot`'s own management commands. **OpenCode Web** starts the selected version's web service, displays it in-app, and can open it in the default browser.

The setup executable is self-contained and can be distributed by itself; the release ZIP contains the same installer plus this README.

When the Bot service starts, the app first stops all local `opencode` processes, then starts the CLI matching the selected API version. Install that API version from **OpenCode 引擎** first.

The app starts with fresh configuration; it does not import old npm-Bot `.env` or legacy DPAPI files. The setup registers these per-user environment variables and adds its CLI directory to the user PATH:

- `OPENCODE_TELEGRAM_ROOT` — installation directory.
- `OPENCODE_TELEGRAM_HOME` — `%APPDATA%\OpenCodeTelegram`, the per-user data directory.
- `PATH` — includes `opencode-telegram.cmd`.

Secrets are not stored in these variables. The desktop host decrypts its DPAPI settings and passes credentials only to the Bot process when starting it.

## CLI

Open PowerShell or CMD after installation:

```text
opencode-telegram help
opencode-telegram version
opencode-telegram configure bot
opencode-telegram configure eot10 enable
opencode-telegram configure eot10 disable
opencode-telegram configure opencode
opencode-telegram configure show
opencode-telegram opencode install
opencode-telegram opencode scan
opencode-telegram opencode select v2
opencode-telegram opencode uninstall v1
opencode-telegram config set model.id big-pickle
opencode-telegram config set bot-token --env TELEGRAM_BOT_TOKEN
opencode-telegram config unset eot10.password
opencode-telegram opencode install v2
opencode-telegram start
opencode-telegram status
opencode-telegram stop
opencode-telegram autostart enable
opencode-telegram autostart disable
opencode-telegram autostart status
opencode-telegram app-autostart enable
opencode-telegram language en
opencode-telegram logs --lines 100
opencode-telegram web start
opencode-telegram web status
opencode-telegram web open
opencode-telegram web stop
opencode-telegram environment
```

The GUI command panel runs this same `opencode-telegram` CLI, not OpenCode's CLI. `config set` supports Bot credentials, EOT10 settings, API version/URL, engine executable, and model settings; pass secrets using `--env NAME` to avoid placing them in command history. `configure show` never prints tokens or passwords. The CLI covers Bot lifecycle, both autostart entries, app settings, engine scan/install/select/uninstall, and web-service lifecycle/opening. `environment` prints only installed paths.

## EOT10 protocol

The Bot and Android client implement the EOT10 envelope in `bot/EOT10Crypto.mjs` and `E:\opencode\Telegram-master\TMessagesProj\src\main\java\org\telegram\messenger\Eot10Crypto.java`. Text uses PBKDF2-HMAC-SHA256 and AES-256-GCM; media uses authenticated `EOT10M01` envelopes with original filename, MIME metadata, and file bytes inside the ciphertext. Failed validation never falls back to plaintext.

## Fork provenance

The `bot/` directory vendors upstream `grinev/opencode-telegram-bot` v0.26.1 under its MIT license, then carries the local fork changes. See [FORK.md](FORK.md) for the upstream commit, source layout, and product boundary. The Android client remains maintained in the separate Telegram GPL source repository.

`OpenCodeTelegram-v11.0.17.exe` is retained as a legacy launcher for the previous npm-installed deployment. New installations should use the standalone setup EXE.
