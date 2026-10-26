# Desktop 1.0.18 / 桌面版 1.0.18

- New desktop configurations explicitly default to encryption disabled. Existing saved choices remain unchanged.
- 新配置明确默认关闭加密，保留已有用户主动保存的开关状态。
- The bottom of Encryption settings explains that EOT10 requires the specially customized encrypted Telegram Android client, cannot be used with standard Telegram, and needs the same password on both ends.
- 加密设置底部说明：EOT10 必须配合专门定制的加密 Telegram 安卓客户端使用，普通 Telegram 不支持，并且两端密码必须一致。
- Adds a clickable GitHub link to the verified Android v11.0.22 release, in Chinese and English.
- 加入已核实的安卓 v11.0.22 GitHub 发布页链接，支持中英文显示。

Android release: https://github.com/lidq4fun-commits/opencode_telegram_bot/releases/tag/desktop-1.0.15-android-11.0.22

Android APK: `OpenCodeTelegram-Android-v11.0.22-arm64-debug.apk`.

## Verification / 验证

- Desktop package build passed; encryption defaults, saved-choice preservation, bottom notice and hyperlink were tested in Chinese and English.
- OpenCode engine-selection regression tests passed.
- Setup: `desktop/artifacts/OpenCodeTelegram-Setup-1.0.18.exe`.
- Setup SHA256: `9FCE94D8C47C5BC2A70BD4536A624D1EBE8628FD0804DE83454FD4F460D4F83C`.
- Release ZIP SHA256: `1121FA4EA25A2FEABED660673E04FBB28259B83D1DB699122D42A863C68B90C9`.
