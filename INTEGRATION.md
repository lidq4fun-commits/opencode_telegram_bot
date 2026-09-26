# Desktop and Android EOT10 connection guide

This guide connects the Windows OpenCode Telegram Bot launcher and the customized Telegram Android client. They are maintained as two separate local Git repositories.

## Project locations

| Component | Directory | Entry point |
|---|---|---|
| Windows desktop Bot + launcher | `E:\opencode\encrypted_telegram_opencode_bot` | `OpenCodeTelegram-v11.exe` |
| Android Telegram client | `E:\opencode\Telegram-master` | `TMessagesProj_App` (`afatDebug`) |

The Android-side handoff is [`../Telegram-master/EOT10-ANDROID.md`](../Telegram-master/EOT10-ANDROID.md). The general setup and service details are in [`OpenCode-Telegram-交接文档.md`](OpenCode-Telegram-交接文档.md).

## Runtime connection

```text
Telegram Android client
  └─ EOT10 encrypted private message ─> Telegram Bot API
       └─ Windows Bot v11 decrypts locally when enabled
            └─ OpenCode Server at 127.0.0.1:4096
```

The desktop launcher starts or reuses OpenCode on `127.0.0.1:4096`, starts the Bot daemon, watches Telegram connectivity, and stops services it owns when the launcher exits. The Bot token remains in `%APPDATA%\opencode-telegram-bot\.env`; it is deliberately excluded from Git.

## EOT10 configuration

- The v11 desktop launcher defaults to **encryption off**. During the first 10 seconds press `E` to open the encryption menu; otherwise startup proceeds normally. Existing v11 settings are loaded automatically after the prompt times out.
- The menu can enable with a saved password, add/change a password and enable, disable while keeping the password, or delete the saved password and disable.
- Desktop configuration is stored at `%APPDATA%\opencode-telegram-bot\eot11-settings.dpapi`, protected with Windows CurrentUser DPAPI. Passwords are not written to `.env`.
- The Android client stores an independent password for each configured Bot using Android Keystore and no-backup app storage. Configure the corresponding private chat with the same password on desktop and phone.
- The EOT10 wire format is `EOT10:` plus Base64URL JSON. It uses PBKDF2-HMAC-SHA256 (250,000 iterations) and AES-256-GCM. Each encrypted message has its own salt and nonce.
- When encryption is enabled, only the authorized user's private conversation with the Bot is encrypted/decrypted. Other chats remain unchanged. Current support is text and keyboard labels; media payloads are not EOT10 protected.

## Versioning and builds

Both folders are independent Git repositories. The desktop release source is `OpenCodeTelegramLauncher.cs`; `EOT10Crypto.mjs` is embedded into `OpenCodeTelegram-v11.exe` during compilation, so the runtime EXE can be copied and launched from any directory without sidecar files. Keep the `.mjs` beside the source when rebuilding. The launcher can retry npm installation if a timed-out update leaves the Bot entry missing.

`OpenCodeTelegram-v11.exe --verify-portable` checks the embedded module without starting services and is used to verify copied builds. The EXE location is independent; the machine still needs the configured Windows-user npm/OpenCode installation and `%APPDATA%` Bot configuration.

Android debug build command:

```powershell
$env:JAVA_HOME='D:\Program Files\Java\jdk-17.0.2'
$env:ANDROID_HOME='C:\Users\ldq\AppData\Local\Android\Sdk'
$env:ANDROID_SDK_ROOT=$env:ANDROID_HOME
```

The app project intentionally ignores local signing keystores, Google/Firebase service JSON, `.env` files, local SDK configuration, and Gradle build outputs. Do not add these files to Git. Build APKs and per-user DPAPI settings are runtime/release artifacts, not source credentials.

## Repository connection policy

Keep the two local repositories linked through these documents and the shared EOT10 wire contract; do not merge them into one Git tree. Do not add a remote until an explicit hosting destination is selected. For a coordinated release, update the desktop and Android handoff notes with both commit IDs and matching protocol/version information.
