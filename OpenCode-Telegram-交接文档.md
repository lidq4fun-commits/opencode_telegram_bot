# OpenCode Telegram Bot 交接文档

## 1. 方案概览

Android 手机通过 Telegram Bot API 与 Windows 上的机器人通信，机器人再访问本机 OpenCode Server：

```text
Android Telegram → Telegram Bot API → Windows Telegram Bot → 127.0.0.1:4096 → OpenCode → 本地项目
```

本方案不依赖 Tailscale、公网 IP、DDNS、路由器端口映射或入站公网端口。Windows 只需要能够访问 Telegram API 和模型 Provider 的 HTTPS 服务。

## 2. 原作者项目

- 项目：`@grinev/opencode-telegram-bot`
- GitHub：<https://github.com/grinev/opencode-telegram-bot>
- npm：<https://www.npmjs.com/package/@grinev/opencode-telegram-bot>
- 已安装版本：启动器会自动更新到 npm `latest`（本机最近记录的版本为 `0.25.3`）。
- 安装命令：

  ```powershell
  npm install --global @grinev/opencode-telegram-bot@latest
  ```

这不是 OpenCode 官方组件，而是第三方开源 Telegram 适配器。升级前应先阅读原项目的 CHANGELOG/README，并保留当前配置备份。

## 3. 当前机器和机器人信息

### Windows

- 用户：`ldq`
- Node.js：`v24.15.0`
- Git：`2.55.0.windows.3`
- OpenCode：`1.18.25`
- OpenCode 命令：`C:\Users\ldq\AppData\Roaming\npm\opencode.cmd`

### Telegram

- Bot 用户名：`@isabella_li_bot`
- BotFather Token：已配置，但**不写入本交接文档**
- 允许操作的 Telegram User ID：`8241663031`
- 机器人语言：简体中文（`zh`）

Bot Token 配置文件：

```text
C:\Users\ldq\AppData\Roaming\opencode-telegram-bot\.env
```

不要把该文件上传到 GitHub、网盘或发送给他人。若 Token 泄露，应立即在 `@BotFather` 使用 `/revoke` 撤销并重新配置。

## 4. OpenCode 配置

机器人连接本机 OpenCode：

```text
OPENCODE_API_URL=http://127.0.0.1:4096
```

当前默认模型配置为：

```text
OPENCODE_MODEL_PROVIDER=opencode
OPENCODE_MODEL_ID=big-pickle
```

后续可以通过机器人模型菜单或修改 `.env` 配置 OpenAI、Google Gemini、Anthropic Claude 等 Provider。修改 Provider 后应重启机器人。

OpenCode 自动恢复已启用：

```text
OPENCODE_AUTO_RESTART_ENABLED=true
OPENCODE_MONITOR_INTERVAL_SEC=60
```

机器人启动时会检查 `127.0.0.1:4096`；服务不可用时自动执行本机 `opencode serve --port 4096`。

## 5. 启动、停止和重启

在 PowerShell 中执行：

```powershell
# 查看机器人状态
opencode-telegram status

# 启动机器人后台服务
opencode-telegram start --daemon --mode installed

# 停止机器人后台服务
opencode-telegram stop
```

机器人日志目录：

```text
C:\Users\ldq\AppData\Roaming\opencode-telegram-bot\logs\
```

当前使用的日志文件通常包括 `bot-YYYY-MM-DD.log` 和 `bot-service-*.log`。

## 6. Windows 自动启动

已删除登录自动启动任务，改用便携式 EXE 手动控制。Windows 登录后不会自动启动 Bot 或 OpenCode。

## 7. Telegram 使用流程

首次使用时，在 Telegram 打开 `@isabella_li_bot` 并发送：

```text
/start
/status
/help
```

常用功能包括：

- `/status`：查看 OpenCode Server 和当前会话状态
- `/new`：创建新会话
- `/sessions`：查看会话
- `/projects`：选择项目
- 模型按钮：在 `/start` 显示的底部键盘中查看/选择模型（当前版本使用底部按钮，不依赖 `/models` 斜杠命令）
- `/opencode_start`：手动启动 OpenCode Server
- `/opencode_stop`：停止由机器人启动的 OpenCode Server
- 直接发送文字：向当前 OpenCode 会话发送任务
- 发送图片或文档：作为任务附件交给 OpenCode（具体支持范围随项目版本变化）

如果机器人提示 Server 不可用，先发送 `/opencode_start`，再发送 `/status`。

## 8. 故障排查

### 机器人无响应

```powershell
opencode-telegram status
Get-Content "$env:APPDATA\opencode-telegram-bot\logs\bot-*.log" -Tail 80
```

确认：

1. Windows 网络可以访问 Telegram。
2. Bot Token 没有被撤销。
3. Telegram 账号 User ID 仍为 `8241663031`。
4. 没有同时运行多个相同 Bot 实例（否则 Telegram 会出现 polling 冲突）。

### 电脑断网后机器人不再回复

旧版第三方 Bot 在 Telegram API 请求失败后可能退出且不再自动重试。v8 启动器加入了 20 秒间隔的恢复监控：检查 Telegram API 是否可达及 Bot daemon 是否仍运行。网络恢复后会停止并重新启动 Bot；Bot daemon 意外退出时也会自动重新启动。只重启 Telegram Bot，不重启 OpenCode Server。恢复过程和结果会显示在启动器窗口中。

如果使用的是旧版启动器，应先关闭旧窗口，改用同目录的 `OpenCodeTelegram-v8.exe`。检查期间无需手动关闭或反复打开窗口。

### OpenCode 不可用

```powershell
Invoke-RestMethod http://127.0.0.1:4096/global/health
```

应返回 `healthy: true`。若没有返回，执行：

```powershell
opencode-telegram start --daemon --mode installed
```

### 修改配置后不生效

修改 `.env` 后必须重启机器人：

```powershell
opencode-telegram stop
opencode-telegram start --daemon --mode installed
```

## 9. 安全配置

- OpenCode 仅绑定 `127.0.0.1:4096`，不接受局域网或公网直接访问。
- 没有配置公网端口、DDNS、路由器端口映射或 Tailscale。
- Telegram 只允许 User ID `8241663031` 操作，其他用户应被拒绝。
- Bot Token 只保存在本机 `%APPDATA%\opencode-telegram-bot\.env`。
- Windows 防火墙不需要为 OpenCode 新增公网入站规则。
- Telegram Bot API 的聊天内容不是端到端加密；不要发送密码、私钥、Cookie、Token 或高度敏感文件。
- OpenCode 的工具审批和模型权限应保持默认安全设置；不要为了省事启用“信任所有工具”。

## 10. 备份和迁移

迁移到另一台 Windows 电脑时：

1. 安装 Node.js、OpenCode 和 Git。
2. 执行 npm 安装命令。
3. 将 `.env` 安全复制到同一 `%APPDATA%\opencode-telegram-bot\` 目录，或重新运行配置向导：

   ```powershell
   opencode-telegram config
   ```

4. 确保新机器上的 OpenCode 项目路径可访问。
5. 启动机器人并用 Telegram 发送 `/status` 验证。

建议备份 `.env` 前先加密；不要将包含 Bot Token 的备份提交到任何代码仓库。

## 13. v10 加密协议（电脑端）

v10 只保护授权用户与配置 Bot 的**私聊**：入站文本在 Bot 本地解密后才进入命令/OpenCode 路由；发往该私聊的 Bot 文本、按钮显示标签和媒体说明文字在调用 Telegram API 前加密。授权用户 ID 和私聊 chat ID 必须同时匹配才应用 EOT10；其他聊天目标不改写。认证失败或该 Bot 私聊中的未加密文本会被拒绝，不会作为普通提示词继续处理。手机端实现也必须只在该 Bot 私聊中解密消息文本和按钮标签；Saved Messages/收藏、好友和群组聊天保持普通 Telegram 行为。Telegram Bot API 服务器仍可访问密文和通信元数据，因此这是应用层加密，不等同于 Telegram Secret Chat/端到端加密。

### 密钥

- 首次运行时输入并确认密码（至少 12 个字符，控制台不回显）；密码通过 Windows 当前用户 DPAPI 加密保存于 `%APPDATA%\opencode-telegram-bot\eot10-password.dpapi`，以后启动自动解锁。运行时仅存在于进程环境，不写入 `.env`。
- 修改密码：先关闭正在运行的启动器，再运行 `OpenCodeTelegram-v10.exe --change-password`，重新输入两次新密码；完成后重新启动 v10。
- DPAPI 密码仅能由同一 Windows 用户配置解锁；迁移电脑时需安全地重新设置密码并同步手机端。密码变更后旧密文仍需旧密码解密。
- 手机端仅在配置 Bot 的私聊里以用户输入的密码作为解锁凭据；其他 Telegram 对话不得做本协议加解密。建议选择容易安全记住的长口令。忘记密码后无法解密历史密文。

### 密文格式

消息文本：`EOT10:` + Base64URL(UTF-8 JSON)，JSON 字段如下：

```json
{"v":10,"alg":"A256GCM","kdf":"PBKDF2-SHA256","iterations":250000,"salt":"<base64url 16-byte salt>","iv":"<base64url 12-byte nonce>","tag":"<base64url 16-byte tag>","data":"<base64url ciphertext>"}
```

每条消息独立生成 16-byte 随机 salt 和 12-byte nonce；先以 PBKDF2-HMAC-SHA256、250,000 次迭代派生 32-byte AES 密钥，再使用 AES-256-GCM。GCM authentication tag 验证失败必须拒收，禁止降级为明文。手机端解密 `data` 后按 UTF-8 显示；对发送消息同样生成新 salt/nonce 并构造同格式 envelope。命令（例如 `/status`）也必须加密后发送。

当前电脑端适配将富文本结构序列化为 JSON 后加密，手机端解密后可按 JSON 内容展示；本地 EOT10 加密模块 `EOT10Crypto.mjs` 必须与 `OpenCodeTelegram-v10.exe` 放在同目录。启动器会在每次启动时重新应用 npm 包补丁，npm 更新后无需手动 patch。

**当前范围限制：**这版适配保护 Bot 私聊中的文字字段；入站照片、文档、音频等非文本消息会被拒绝。Bot 发送的媒体文件本体尚未纳入 EOT10 加密，手机端配套实现前应只通过文本交互。出站单条明文长度应留意加密 Base64 膨胀后的 Telegram 消息长度限制。

## 14. v1–v9 版本脉络

- v1–v4：启动器早期版本；曾有重复启动/遗留进程问题，早期版本进程需谨慎清理。
- v5/v6：全局单实例互斥锁、服务清理和 UTF-8 阶段提示；npm 更新输出收敛。
- v7：仅检测到版本变化时才执行 npm 更新，避免每次启动无意义重装。
- v8：每 20 秒监测 Telegram API 和 Bot daemon，网络恢复或 daemon 退出后自动恢复 Bot。
- v9：修复空闲问答状态下 `/new` 被阻塞，加入 90 秒 Bot 恢复冷却，并复用健康的 OpenCode Server。
- v10：在上述启动器行为基础上，加入电脑端 AES-256-GCM Telegram 文本收发适配；手机端加密客户端待后续实现。

## 11. 当前验证结果

- Telegram `getMe` 验证成功，Bot 为 `@isabella_li_bot`。
- OpenCode 健康检查成功：`http://127.0.0.1:4096/global/health`。
- 机器人后台服务状态：`running`。
- 自动重启监控：已启用，检查间隔 60 秒。
- 登录自动启动任务：已删除，改用便携式 EXE 手动控制。
- 便携式 EXE 实测：OpenCode health 为 `healthy`，Bot daemon 状态为 `running`。

## 12. 便携式 EXE 启动器

已关闭并删除登录自动启动任务。现在使用：

```text
OpenCodeTelegram-v9.exe
```

文件位于本交接文档同一目录，也可以复制到任意目录运行。启动器通过 `%APPDATA%` 查找已安装的机器人，不依赖 EXE 所在路径。

- 双击 EXE：直接启动本机 OpenCode Server，再启动 Telegram Bot。
- 保持启动器窗口打开即可使用。
- 关闭启动器窗口或按 `Ctrl+C`：停止 Bot，并结束 `127.0.0.1:4096` 的 OpenCode 进程。
- EXE 直接管理 OpenCode 进程，不依赖 Bot 的自动拉起监控。
- 每次打开 EXE 会先比较本机版本和 npm `latest` 版本；版本相同时提示“已是最新版，无需更新”，不执行 npm 安装，也不重启旧 Bot。只有确认存在新版本时才执行 `npm install --loglevel=error --global @grinev/opencode-telegram-bot@latest`。
- 查询失败或更新失败时，会显示错误并启用当前已安装版本继续启动；普通 deprecated/funding 提示会隐藏，真正的 npm 错误、超时和退出码会保留。更新检查最长等待 120 秒。
- 更新前会先停止已存在的 Bot daemon，避免 `better-sqlite3` 文件被占用导致 npm 出现 `EPERM cleanup` 警告；更新完成后再启动 Bot。
- daemon 启动命令会快速返回，这是正常现象；启动器不会再把这个正常返回误报为“机器人进程已退出”。
- 不再有 Windows 登录自动启动。
- 启动器使用全局单实例互斥锁；重复双击同一新版启动器时只提示“已经在运行”，不会再次更新、启动 Bot 或创建新的 OpenCode 进程。
- v7 修复了每次启动都执行 npm 安装的问题：无新版本时不会再出现 `changed 206 packages`，也不会因无意义更新重启 Bot。
- v8 增加断网恢复看门狗：每 20 秒检查 Telegram API 连通性和 Bot daemon 状态；网络恢复后重新启动 Bot，daemon 意外退出时在网络可用后自动拉起。不会重启 OpenCode Server。
- v9 修复旧问答卡片阻塞新聊天：Bot 恢复了尚未回答的 OpenCode 问题时，只要当前没有运行中的任务，发送 `/new` 即可直接创建新会话；创建成功后自动清除旧问答状态并移除旧卡片，不再需要先手动取消。正在运行的任务及其他类型确认交互不受影响。
- v9 启动器每次启动（包括 npm 更新后）会检查并重新应用这项兼容补丁，并在首次修改时保留 `.codex-original` 原文件备份。
- v9 还增加 90 秒 Bot 恢复冷却时间，避免启动耗时较长时每 20 秒重复创建 Bot 实例，并在 OpenCode Server 已健康时复用现有服务。
- v6 启动器使用 UTF-8 控制台输出，并显示 `[1/5]` 到 `[5/5]` 阶段提示：检查更新、执行更新、选择新旧版本、启动 OpenCode、启动机器人；最后提示“机器人已经启动，可以进行对话”。
- 更新命令使用 `npm install --loglevel=error`：隐藏普通 deprecated/funding 提示，但保留真正错误。
- 启动器使用全局单实例互斥锁，重复打开不会创建新的 Bot/OpenCode 进程。
- 排查发现旧版 `OpenCodeTelegram-v4.exe`（PID 21848）仍在运行并对应一个 OpenCode 进程（PID 35156，端口 4096）；另有一个独立 OpenCode 进程（PID 7088，端口 4592），无法仅凭端口确认是否属于本方案。使用 v5 前应关闭旧版启动器。

启动器源代码为 `OpenCodeTelegramLauncher.cs`。如果 EXE 被误杀，可能来不及清理 OpenCode；此时可手动执行：

```powershell
opencode-telegram stop
Get-NetTCPConnection -LocalPort 4096 -State Listen
```

## Android EOT10 客户端

手机端 Telegram 定制客户端的专项说明见源码目录中的 `EOT10-ANDROID.md`。项目源码位于 `E:\opencode\Telegram-master`，发布用 APK 位于 `E:\opencode\OpenCodeTelegram-v10-Android-arm64-debug.apk`。

- Android 客户端支持配置多个 Bot，并为每个 Bot 单独设置至少 12 个字符的密码；每个 Bot 私聊按对应密码加解密。用户名是路由选择，不是密钥。
- 密码以 Android Keystore AES-GCM 加密，密文文件保存在 app `noBackupFilesDir`；保存不向网络发送密码。新版本修复了 Keystore 加密 IV 错误：由 Keystore 生成 IV 并与密文一起保存。
- EOT10 消息使用 `EOT10:` + Base64URL JSON；KDF 为 PBKDF2-HMAC-SHA256（250,000 次），加密为 AES-256-GCM。手机端和电脑端必须对同一个 Bot 使用相同密码。
- 当前只支持文本和 Bot 键盘按钮标签；媒体尚未加密，目标 Bot 私聊中的媒体发送会被阻止。旧消息不会因修改密码而重加密，仍需旧密码。
- 加密设置页现为跟随 Telegram 语言和主题切换的卡片式界面，包含 Bot 卡片和确认删除流程；文案提供英文、简体中文、繁体中文。
- 最新一次构建成功并通过 `apksigner verify`；APK 大小 67,121,980 bytes，Telegram Saved Messages 消息 ID `16496`（2026-09-26）。

构建命令、源码文件列表、当前功能边界和 Keystore IV 说明详见 `EOT10-ANDROID.md`。这个 APK 是个人 debug 构建，不是官方 Telegram 发布版。

## 电脑端 v11 启动器

当前开发的 v11 启动器源文件为 `OpenCodeTelegramLauncher.cs`，构建产物为 `OpenCodeTelegram-v11.exe`，EOT10 模块 `EOT10Crypto.mjs` 需与 EXE 放在同一目录。v11 保留 v9 默认服务生命周期：npm 版本检查/更新、启动或复用 `127.0.0.1:4096` 上的 OpenCode、启动 Bot、网络恢复监控；退出窗口停止本次 Bot/OpenCode 服务。

- EOT10 **默认关闭**。启动时显示 10 秒倒计时；按 `E` 打开设置菜单，超时则直接继续启动。若已保存 v11 配置，超时自动加载保存值；首次无配置时是普通明文 Bot 模式。
- 菜单支持：用已保存密码开启；添加/修改密码并开启；关闭但保留密码；删除密码并关闭；不更改继续启动。
- 配置文件 `%APPDATA%\opencode-telegram-bot\eot11-settings.dpapi` 由 Windows 当前用户 DPAPI 加密。旧 v10 密码可迁移为“已保存但关闭”，升级不会擅自开启加密。
- 若 npm 安装目录仍有 v10 无条件加密补丁，v11 在关闭模式下恢复 `.eot10-original` 备份；开启后应用检查 `OPENCODE_TELEGRAM_EOT10_ENABLED` 的开关补丁。
- `--change-password` 兼容参数现在打开 v11 加密设置菜单。
- 已用 .NET Framework `csc.exe` 编译成功：`OpenCodeTelegram-v11.exe`，143,872 bytes，SHA-256 `C88537870AE66D02A60A3005E2A4432C83B33C474B364363555F7673B1638CBB`（2026-09-26）。与 `EOT10Crypto.mjs`、图标文件放在同一目录运行。

编译（Windows .NET Framework C# 编译器）：

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /optimize+ /out:'E:\opencode\encrypted_telegram_opencode_bot\OpenCodeTelegram-v11.exe' /win32icon:'E:\opencode\encrypted_telegram_opencode_bot\OpenCodeTelegramLauncher.ico' /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll 'E:\opencode\encrypted_telegram_opencode_bot\OpenCodeTelegramLauncher.cs'
```
