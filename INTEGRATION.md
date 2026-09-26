# Desktop and Android EOT10 connection guide

This guide connects the Windows OpenCode Telegram Bot launcher and the customized Telegram Android client. They are maintained as two separate local Git repositories.

## Project locations

| Component | Directory | Entry point |
|---|---|---|
| Windows desktop Bot + launcher | `E:\opencode\encrypted_telegram_opencode_bot` | `OpenCodeTelegram-v11.0.5.exe` |
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
- When EOT10 is enabled, the desktop decrypts before routing and rebuilds Telegram `bot_command` metadata so commands such as `/status` reach their handlers. When disabled, EOT10 envelopes are rejected rather than routed as ordinary OpenCode prompts.
- Desktop configuration is stored at `%APPDATA%\opencode-telegram-bot\eot11-settings.dpapi`, protected with Windows CurrentUser DPAPI. Passwords are not written to `.env`.
- The Android client stores an independent password for each configured Bot using Android Keystore and no-backup app storage. Configure the corresponding private chat with the same password on desktop and phone.
- Android runs PBKDF2 encryption off the UI thread, prewarms two outbound keys for the Bot's current session salt when entering a configured chat, and predecrypts cached history asynchronously in a bounded worker pool. Successfully decrypted text is saved in a separate local cache encrypted with Android Keystore, allowing later opens/app restarts to skip EOT10 decryption; cached text is removed when its Bot configuration is deleted.
- The EOT10 wire format is `EOT10:` plus Base64URL JSON. It uses PBKDF2-HMAC-SHA256 (250,000 iterations) and AES-256-GCM. A fresh salt is generated when the Bot process starts and reused for its outgoing messages; each envelope still has a fresh GCM nonce. Incoming messages with older or client-generated salts remain supported through a bounded derived-key cache.
- Rich Message replies preserve the Telegram rich block structure; visible text nodes use EOT10, while keyboard/button labels and the block schema remain readable. Encrypted text nodes use the Bot process's session salt and independent GCM nonces, avoiding a PBKDF2 derivation for every message or Rich Message node.
- `/open` and other Rich Message results remain structured Telegram rich messages instead of being flattened into an encrypted JSON text bubble; the Android client decrypts Rich Message text leaves before layout.
- When encryption is enabled, only the authorized user's private conversation with the Bot is encrypted/decrypted. Other chats remain unchanged. Current support is text and keyboard labels; media payloads are not EOT10 protected.

## Bot developer checklist

Keep these rules when changing the Bot or updating its npm package; the launcher reapplies its compatibility patch to the installed package:

1. **Inbound order:** EOT10 middleware must run before command routing. In the authorized private Bot chat, reject non-EOT text, decrypt the envelope, then reconstruct the `bot_command` entity for decrypted `/status`-style commands before grammY sees the update. GrammY does not dispatch commands from text alone.
2. **Scope:** only encrypt/decrypt when the feature flag is on, the sender is the configured user, and the chat is that user's private conversation with this Bot. When off, pass ordinary messages unchanged but reject an EOT10 envelope rather than feeding cipher text to OpenCode.
3. **Normal sends:** encrypt the `text` (and supported `caption`) fields to the target private chat. Do not recursively encrypt `reply_markup`; button labels and callback structure must remain unchanged.
4. **Rich Message sends:** keep `sendRichMessage`/`sendRichMessageDraft` and the `rich_message` block structure. Encrypt only Rich Text leaves, preserving block types and layout fields. `encryptEot10RichMessage` is the reference implementation: it skips button/button-row objects, uses the Bot-process session salt plus a distinct GCM nonce for each encrypted text leaf, and avoids flattening Rich Message output into an unrenderable EOT10 JSON text bubble.
5. **Android rendering:** do not put ciphertext into Bot button labels. The client decrypts Rich Message TL text nodes on a copy before native layout; legacy flattened EOT10 JSON has a plain-text display fallback.
6. **Pairing:** configure the peer's actual public Bot username and the matching Bot-specific passphrase on both endpoints.
7. **Updates/restarts:** use one launcher version at a time. v9 and v11 compete for the same Bot daemon; concurrent launchers can toggle the daemon environment and interrupt polling. Re-run the launcher after package updates so it reapplies and validates the API and middleware hooks.
8. **Media:** files are not covered by EOT10; block unsupported protected-chat media instead of implying it is encrypted.

For the current phone-side implementation details, see [`../Telegram-master/EOT10-ANDROID.md`](../Telegram-master/EOT10-ANDROID.md).

## Versioning and builds

Both folders are independent Git repositories. The desktop release source is `OpenCodeTelegramLauncher.cs`; `EOT10Crypto.mjs` is embedded into `OpenCodeTelegram-v11.0.5.exe` during compilation, so the runtime EXE can be copied and launched from any directory without sidecar files. Keep the `.mjs` beside the source when rebuilding. The launcher can retry npm installation if a timed-out update leaves the Bot entry missing.

`OpenCodeTelegram-v11.0.5.exe --verify-portable` checks the embedded module without starting services. Use `OpenCodeTelegram-v11.0.5.exe --skip-update` to restart the existing Bot with current saved encryption settings while skipping npm update checks. The EXE location is independent; the machine still needs the configured Windows-user npm/OpenCode installation and `%APPDATA%` Bot configuration.

Android debug build command:

```powershell
$env:JAVA_HOME='D:\Program Files\Java\jdk-17.0.2'
$env:ANDROID_HOME='C:\Users\ldq\AppData\Local\Android\Sdk'
$env:ANDROID_SDK_ROOT=$env:ANDROID_HOME
```

The app project intentionally ignores local signing keystores, Google/Firebase service JSON, `.env` files, local SDK configuration, and Gradle build outputs. Do not add these files to Git. Build APKs and per-user DPAPI settings are runtime/release artifacts, not source credentials.

## Repository connection policy

Keep the two local repositories linked through these documents and the shared EOT10 wire contract; do not merge them into one Git tree. Do not add a remote until an explicit hosting destination is selected. For a coordinated release, update the desktop and Android handoff notes with both commit IDs and matching protocol/version information.
