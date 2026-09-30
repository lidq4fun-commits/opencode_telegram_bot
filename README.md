# OpenCode Telegram Desktop

OpenCode Telegram is an independent Windows desktop product and Bot fork. The self-extracting setup embeds the Bot source, compiled Bot, production Node dependencies, Node.js runtime, npm runtime, and the WPF desktop application. Installed builds do not install or call `@grinev/opencode-telegram-bot` from npm.

## Features

- **Apple-inspired WPF interface:** control center, Telegram Bot binding, EOT10 encryption, OpenCode engine selection, and startup options.
- **Independent Bot runtime:** forked TypeScript Bot and Node are installed together; the system does not need a global Telegram Bot npm package.
- **OpenCode choice:** connect to an existing local/remote OpenCode server, or install a local OpenCode engine from the GUI or CLI.
- **EOT10:** encrypts authorized private-chat text, Rich Message text, media attachments, and media groups. Bot token, OpenCode credentials, and EOT10 password are stored together in a file protected by Windows CurrentUser DPAPI.
- **Dual control surface:** GUI and CLI use the same settings and service manager.
- **Windows sign-in startup:** enable or disable through the GUI or CLI.
- **Attachment routing:** recognized attachment MIME types go directly to OpenCode; text-like files are submitted as text; an unknown MIME with valid UTF-8 content is detected as text; only unknown binary files are saved byte-for-byte into the current project for local agent tools.

## Build the setup EXE

Build prerequisites: Windows .NET Framework C# compiler, Node.js 24 x64 with npm, and installed Bot development dependencies. The build script creates a clean staging directory, prunes dev-only Node packages, bundles Node/npm and Bot production dependencies, then emits the setup executable.

```powershell
& 'E:\opencode\encrypted_telegram_opencode_bot\desktop\Build-Desktop-Package.ps1'
```

Output:

```text
desktop\artifacts\OpenCodeTelegram-Setup-1.0.0.exe
Size: 77,143,552 bytes
SHA-256: A388A1BD3F634CFFE55A93091D820F438B01F94BF8C994CBED888CA2EB870708
```

Before binding the same Telegram Bot token in the fork for the first time, stop the legacy v11 launcher/daemon to avoid two polling clients using one token. The new installer does not import the legacy `.env` or DPAPI configuration.

The setup EXE embeds a compressed runtime archive. Installing extracts the independent Bot source/runtime and places `OpenCodeTelegram.exe` plus `opencode-telegram.cmd` in the selected installation folder. For a clean build, run `npm ci` in `bot/` first; `Build-Desktop-Package.ps1` prunes dev dependencies only in a staging copy.

## First run

1. Run the setup EXE and select an installation folder.
2. Optionally check **Start Bot at Windows sign-in**.
3. Launch **OpenCode Telegram** from the Start menu shortcut.
4. In **机器人账号**, bind a BotFather token and the allowed Telegram user ID.
5. In **加密设置**, choose EOT10 and set a password of at least 12 characters. Use the same password for this Bot in the Android client.
6. In **OpenCode 引擎**, select an existing server or choose **安装 OpenCode**. Configure the server API URL and model provider/model ID.
7. Press **启动机器人** from **概览**.

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
opencode-telegram start
opencode-telegram status
opencode-telegram stop
opencode-telegram autostart enable
opencode-telegram autostart disable
opencode-telegram autostart status
opencode-telegram environment
```

`configure bot` and `configure eot10` prompt for credentials without echoing secrets. `configure show` never prints tokens or passwords. `environment` prints only installed paths.

## EOT10 protocol

The Bot and Android client implement the EOT10 envelope in `bot/EOT10Crypto.mjs` and `E:\opencode\Telegram-master\TMessagesProj\src\main\java\org\telegram\messenger\Eot10Crypto.java`. Text uses PBKDF2-HMAC-SHA256 and AES-256-GCM; media uses authenticated `EOT10M01` envelopes with original filename, MIME metadata, and file bytes inside the ciphertext. Failed validation never falls back to plaintext.

## Fork provenance

The `bot/` directory vendors upstream `grinev/opencode-telegram-bot` v0.26.1 under its MIT license, then carries the local fork changes. See [FORK.md](FORK.md) for the upstream commit, source layout, and product boundary. The Android client remains maintained in the separate Telegram GPL source repository.

`OpenCodeTelegram-v11.0.17.exe` is retained as a legacy launcher for the previous npm-installed deployment. New installations should use the standalone setup EXE.
