# OpenCode Telegram · 远程控制与加密 / Remote Control & Encryption

## 中文
通过 Telegram，在安卓手机上远程控制 Windows 电脑上的 OpenCode：发送任务、实时查看 AI 回答、切换项目、会话和模型。电脑须保持运行和联网，Bot 轮询无需开放公网入站端口。

**桌面版 1.0.15 — 第一个可用版本：** 集成机器人配置、OpenCode 引擎管理、Bot CLI、共享网页工作区、手机与电脑的项目/会话/模型同步，中英文切换、三点网页菜单及安装/卸载/退出清理进度提示。

**安卓加密版 v11.0.22：** 非官方 Telegram arm64 调试客户端，支持多个 Bot 的独立密码、加密文本/富消息/文件、本地解密与历史缓存。

### 配置与加密
1. 桌面配置 Bot Token、允许的 Telegram 用户 ID 和 OpenCode，启动 Bot。
2. 安卓 Settings → Bot Encryption，添加 Bot 用户名和至少 12 字符的密码。
3. 桌面“加密设置”启用 EOT10，填写相同密码；手机通过该 Bot 私聊远程发送任务。

EOT10 使用 AES-256-GCM 认证加密和 PBKDF2-SHA256 密钥派生。安卓密码由 Android Keystore 保护，桌面配置由 Windows DPAPI 保护。文件加密封装为 .eotm。

**限制：** 仅配置 Bot 的指定私聊适用；这是应用层加密，不是 Telegram Secret Chat。通信元数据、按钮标签并非加密隐藏；端点须保护，安卓本地历史缓存包含解密明文。修改密码不会重加密旧消息。安卓发送文件限 19 MiB。APK 使用调试签名，非官方 Telegram。安卓基于 Telegram（GPL-2.0-or-later），公开再分发须遵守修改后对应源码与许可证提供要求。

## English
Remotely control OpenCode on a Windows PC from Android through Telegram: submit tasks, follow AI responses, and switch projects, sessions and models. The PC must remain online and running. Bot polling requires no public inbound port.

**Desktop 1.0.15 — first usable release:** Bot configuration, OpenCode engine management, embedded CLI/web workspace, phone/desktop project/session/model synchronization, Chinese/English UI, compact three-dot web menu and install/uninstall/exit cleanup progress.

**Encrypted Android v11.0.22:** Unofficial Telegram-based arm64 debug build with independent passphrases for multiple Bots, encrypted text/rich messages/files, local decryption and history caching.

### Setup & encryption
1. Configure the desktop Bot Token, allowed Telegram user ID and OpenCode; start the Bot.
2. Android Settings → Bot Encryption: add the Bot username and a passphrase of at least 12 characters.
3. Enable EOT10 in desktop Encryption Settings with the same passphrase; send tasks in that Bot's private chat.

EOT10 uses AES-256-GCM authenticated encryption and PBKDF2-SHA256 key derivation. Android Keystore protects passphrases; Windows DPAPI protects desktop settings. Files use encrypted .eotm envelopes.

**Limits:** Only the configured Bot private chat is covered. This is application-layer encryption, not Telegram Secret Chat. Metadata and button labels remain visible; protect endpoints, and note Android's local history cache contains decrypted plaintext. Password changes do not re-encrypt old messages. Android outbound files are limited to 19 MiB. The APK is debug-signed and unofficial. Telegram-derived Android code is GPL-2.0-or-later; public redistribution must satisfy corresponding modified-source and license obligations.

## Downloads / 下载
Only current desktop binaries and the Android APK are included. No personal credentials, settings or old binaries are uploaded.

- Windows x64: OpenCodeTelegram-Setup-1.0.15.exe
- Windows package ZIP: OpenCodeTelegram-1.0.15-Windows-x64.zip
- Android arm64: OpenCodeTelegram-Android-v11.0.22-arm64-debug.apk

## SHA256
```text
022597E251A940BA45EF37F12CCCB0226E6E14FA2627CD834DCB9E5C69D0C9B1  OpenCodeTelegram-Setup-1.0.15.exe
FE6F3F7E35520130E157EE6EDB50A37898D58DE8931B36414342A9C70B9CFC23  OpenCodeTelegram-1.0.15-Windows-x64.zip
4DCA0E5742018F0AE6F2886EF47557EE42C23247C87A2B8DF3696C77E7177F29  OpenCodeTelegram-Android-v11.0.22-arm64-debug.apk
```
