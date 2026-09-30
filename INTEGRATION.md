# OpenCode Telegram Desktop / Android integration

The desktop Bot fork and the customized Telegram Android client remain separate projects with a shared EOT10 protocol. The desktop installer contains the forked Bot and Node runtime; it no longer patches a machine-wide npm Bot installation.

## Components

| Component | Source | Current version |
|---|---|---|
| Windows WPF app, CLI, and Bot fork | this repository | Desktop 1.0.0; Bot fork 1.0.0 |
| Android Telegram client | `E:\opencode\Telegram-master` | EOT10 `android-eot10-v11.0.20`; APK 12.10.6 / code 71019 |

## Runtime connection

```text
Telegram Android client
  └─ EOT10 encrypted private message/attachment → Telegram Bot API
       └─ forked Windows Bot decrypts locally when EOT10 is enabled
            └─ OpenCode API endpoint (existing or optional local engine)
```

No inbound public port is required for normal use. The Bot token and OpenCode credentials are encrypted in the desktop app's CurrentUser DPAPI settings and passed to the Bot child process at runtime.

## First-run pairing

1. Install `desktop\artifacts\OpenCodeTelegram-Setup-1.0.0.exe`.
2. In **机器人账号**, bind the BotFather token and allowed Telegram user ID.
3. In **加密设置**, enable EOT10 and set a passphrase of at least 12 characters.
4. In Android **Settings → Bot Encryption**, add this Bot's public username with the same passphrase.
5. Select an existing OpenCode server or install the optional engine from **OpenCode 引擎**.
6. Start the Bot from the desktop **概览** page.

Use the same EOT10 mode on phone and desktop. EOT10 protects only the authorized user's private chat with the configured Bot; it is application-layer encryption, not Telegram Secret Chat. Unrelated chats are untouched.

## CLI parity

The installer adds `opencode-telegram.cmd` to the per-user PATH. It controls the same DPAPI configuration and Windows startup setting as the GUI:

```powershell
opencode-telegram help
opencode-telegram configure bot
opencode-telegram configure eot10 enable
opencode-telegram configure opencode
opencode-telegram start
opencode-telegram status
opencode-telegram stop
opencode-telegram autostart enable|disable|status
```

## EOT10 media behavior

Android wraps file-backed attachments as authenticated `.eotm` documents before upload. The desktop Bot validates and decrypts the envelope locally, then forwards the recovered file to OpenCode. Outbound Bot media is encrypted before Telegram upload. Invalid ciphertext or unavailable source bytes fail closed; there is no plaintext downgrade.

## Version and source management

- Desktop installer source/tagging and package build steps: [`FORK.md`](FORK.md), [`README.md`](README.md), `desktop/Build-Desktop-Package.ps1`.
- Android source/build/release metadata: [`E:\opencode\Telegram-master\EOT10-ANDROID.md`](E:\opencode\Telegram-master\EOT10-ANDROID.md).
- Upstream source provenance and MIT license: `bot/LICENSE` and [`FORK.md`](FORK.md).
- Do not commit Bot tokens, local DPAPI files, Android signing credentials, SDK service JSON, or uploaded user attachments.
