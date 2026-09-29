# OpenCode Telegram Windows 启动器

本目录保存 Windows 上通过 Telegram 远程使用本机 OpenCode 的启动器和交接资料。

## 推荐启动器

双击：`OpenCodeTelegram-v11.0.17.exe`

v11.0.17 可执行文件已构建并内嵌 EOT10 加密模块；可从任意目录运行，不依赖旁置脚本。`EOT10Crypto.mjs` 保留为协议源文件和重新编译输入。可运行 `OpenCodeTelegram-v11.0.17.exe --verify-portable` 单独验证内嵌模块，不启动服务。

启动器会按顺序：

1. 检查是否已有启动器实例；重复打开时直接退出，不重复创建进程。
2. 读取本机版本并查询 npm 最新版本。
3. 版本相同：提示“已是最新版，无需更新”，不执行 npm 安装，也不重启旧 Bot。
4. 版本不同：停止旧 Bot 后执行 `npm install --loglevel=error --global @grinev/opencode-telegram-bot@latest`。
5. 查询失败或更新失败：保留当前版本并继续启动。
6. 启动本机 OpenCode Server（`127.0.0.1:4096`）。
7. 启动 Telegram Bot，并提示“机器人已经启动，可以进行对话”。
8. 每 20 秒检查 Bot daemon 状态；意外退出时按至少 90 秒的间隔尝试重新启动，不使用可能与 Bot 代理设置不同的独立 Telegram API 探针判定断线。
9. 运行 npm 更新后重新应用兼容修复：空闲时遇到旧问答卡片，可直接用 `/new` 开新对话并自动清理旧卡片。
10. Bot 重连尝试间隔至少 90 秒，避免恢复窗口尚未完成时重复拉起多个轮询实例；启动时会复用已健康的 OpenCode Server。
11. 启动后显示 10 秒加密设置入口；默认关闭 EOT10，超时自动按普通模式启动。此前保存的设置会自动加载；可在窗口启动时按 `E` 开启/关闭加密、添加/修改密码或删除密码。
12. 每次启动 Bot 前先停止旧 daemon，确保它继承本次启动所加载的加密开关和密码；`--skip-update` 可在修复/重启时跳过 npm 检查。
13. EOT10 开启时先解密再恢复 Telegram 命令 entity，使 `/status` 等命令正常分派；关闭时拒绝 EOT10 密文，避免密文被当作普通提示词处理。
14. 发往手机的 Rich Message 保留 Telegram 富文本块结构，只加密可见文本字段；回复键盘和 Rich Message 按钮标签保持可见。
15. Bot 对可识别 MIME 的附件继续直接提交 OpenCode file part；文本/代码文件作为文本内容提交。Telegram 给出 `application/octet-stream` 时，常见后缀会映射到对应 MIME；未知后缀若内容为有效 UTF-8 且无二进制控制字符（如 `.plain` 配置），也会作为文本处理。仍无法识别的二进制文件才原样保存到当前项目 `.opencode-telegram-attachments` 并由 OpenCode agent 本地工具读取。EOT10 开启时，文档、图片、视频、音频、贴纸等上传 API 及媒体组中的每个附件都会分别加密为 `.eotm`，字幕也会加密；入站只接收认证通过的 EOT10 文件，解密后再转交。所有失败都不会退回明文发送。

`Restart-OpenCodeTelegramEncrypted.ps1` 会使用当前 Windows 用户已保存的 DPAPI 密码开启 EOT10、关闭旧启动器并启动 v11.0.17；没有已保存密码时需先在启动器设置菜单设置。上传的未知二进制文件保留在项目中的 `.opencode-telegram-attachments`，该目录被 Git 忽略，应按需自行清理。已知类型直接发送仍受 OpenCode 和当前模型的实际输入能力约束。

窗口保持打开即可使用；关闭窗口会停止 Bot 和本次启动的 OpenCode Server。没有配置 Windows 登录自动启动。

控制台使用 UTF-8，并显示 `[1/5]` 至 `[5/5]` 阶段提示。只有确认存在新版本才更新；npm 使用 `--loglevel=error`，会隐藏普通 deprecated/funding 提示，但保留真正的更新错误、超时和退出码。

## 项目来源

- GitHub：<https://github.com/grinev/opencode-telegram-bot>
- npm：<https://www.npmjs.com/package/@grinev/opencode-telegram-bot>
- 包名：`@grinev/opencode-telegram-bot`
- 启动器会跟随 npm `latest` 更新（本机最近记录的版本为 `0.25.3`）。

## 机器人配置

- Bot：`@isabella_li_bot`
- 允许的 Telegram User ID：`8241663031`
- 配置文件：`%APPDATA%\opencode-telegram-bot\.env`
- OpenCode API：`http://127.0.0.1:4096`
- 当前默认模型：`opencode/big-pickle`

Bot Token 不写入本目录文档，也不要提交到 Git、网盘或聊天记录。

## 重复进程防护

旧版本曾存在重复打开 EXE 后累积 Bot/OpenCode 进程的风险。v5/v6 使用全局 Windows 单实例互斥锁，重复双击不会再次启动服务。更新前会停止已有 Bot daemon；启动器退出时会清理其 Bot 和 `4096` 端口对应的 OpenCode 进程。

如果旧版本已经留下进程，可先关闭旧 EXE，再检查：

```powershell
Get-Process -Name OpenCodeTelegram*,opencode,bun,node -ErrorAction SilentlyContinue
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue
```

不要随意结束其他项目使用的 `node`/`bun` 进程；只处理确认属于本方案的旧启动器及 `127.0.0.1:4096` 服务。

## 断网恢复

Telegram Bot 库在 Telegram API 连接失败后可能退出且不再重试。v11.0.17 启动器每 20 秒检查 Bot daemon 状态，退出后至少间隔 90 秒尝试重新启动；Bot 使用自身的网络/代理配置重新连接。不再以启动器的独立 `getMe` 请求是否成功判断网络可用性，避免 Bot 正常工作时误报“Telegram 暂时无法连接”或误重启。此监控只管理 Telegram Bot，不会重启 OpenCode Server。关闭启动器窗口仍会停止 Bot 和 OpenCode。

## 新会话清理旧问答

如果当前 OpenCode 会话遗留了未回答的问题，Bot 会暂时拦截普通消息。v9 启动器会在启动 Bot 前应用兼容补丁：仅当没有运行中任务且交互类型是 OpenCode 问答时，放行 `/new`；成功创建新会话后自动清除旧问答状态并删除对应问答卡片。正在运行的任务不会被此逻辑取消。补丁在每次启动时检查，因此 npm 更新后也会重新应用。

v9 同时给 Bot 的恢复启动增加 90 秒冷却时间，避免旧实例仍在完成启动时被每 20 秒的监控重复拉起；若本机 OpenCode Server 已健康运行，则直接复用，不再启动同端口的第二个服务。

## v10 加密通信

v10 在电脑端 Bot 收到授权用户私聊文本后、进入命令和 OpenCode 路由前解密；发往授权用户 Bot 私聊的消息文本、按钮标签和媒体说明文字在调用 Telegram API 前加密。首次启动输入两次自选密码（至少 12 个字符；输入不回显），密码由 Windows 当前用户的 DPAPI 加密保存在 `%APPDATA%\opencode-telegram-bot\eot10-password.dpapi`，后续启动自动解锁。密码只在运行期间放入进程环境，不写入 `.env`。可运行 `OpenCodeTelegram-v10.exe --change-password` 修改密码。只接受以 `EOT10:` 开头、认证标签验证成功的密文，明文或篡改密文不会转发给 OpenCode。Bot API 的服务端仍会收到密文；Telegram Bot API 本身不是端到端加密。

- KDF：PBKDF2-HMAC-SHA256，250,000 次迭代，派生 32-byte AES 密钥；每条消息独立随机 16-byte salt。
- 算法：AES-256-GCM，12-byte 随机 nonce，16-byte authentication tag。
- 消息格式：`EOT10:` + Base64URL(JSON `{v:10,alg:"A256GCM",kdf:"PBKDF2-SHA256",iterations:250000,salt,iv,tag,data}`)，二进制字段均为 Base64URL。
- 手机端仅在与配置 Bot 的私聊中输入相同密码解锁，并从每条 Bot 对话密文的 salt 派生 AES 密钥。收藏、好友、群组等其他 Telegram 对话不得加密或解密。建议密码使用至少 12 个字符的长口令；改密码后旧密文仍需旧密码解密。
- v10 适配文件 `EOT10Crypto.mjs` 必须与启动器 EXE 放在同一目录；启动器会复制到 npm Bot 目录，并备份首次修改的 Bot 入口。
- v10 当前保护 Bot 私聊中的文本、按钮标签和媒体说明文字；媒体文件本体尚不加密。手机端需仅在 Bot 私聊解密文本与按钮标签，其他对话不触碰；同时处理密文分段/长度限制后才能作为完整替代客户端使用。

## v11 启动时加密设置

v11 延续 v9 的默认自动启动、npm 更新检查、OpenCode Server 管理和 Telegram/Bot 恢复监控。**加密默认关闭**；首次启动时如果 10 秒内没有按 `E`，直接以普通 Telegram Bot 模式启动，不会等待密码输入。若已有 v11 配置，超时则自动加载该配置。

启动时按 `E` 可选择：

1. 使用已保存密码开启 EOT10。
2. 添加/修改密码并开启 EOT10（至少 12 个字符，两次确认）。
3. 关闭 EOT10，但保留已存密码。
4. 删除已保存密码并关闭 EOT10。
0. 不更改并继续启动。

配置保存在 `%APPDATA%\opencode-telegram-bot\eot11-settings.dpapi`，文件内容由当前 Windows 用户 DPAPI 加密；密码不会写入 `.env`。升级时若发现 v10 密码，会保留并迁移到 v11 配置，但加密保持关闭，需主动开启。运行 `OpenCodeTelegram-v11.0.10.exe --change-password` 可随时打开同一配置菜单。手机端与电脑端对应 Bot 私聊需使用相同密码。

如果当前安装目录内的 Bot 入口残留 v10 强制加密补丁，v11 在关闭加密时会尝试使用保存的原始备份恢复普通行为；启用加密时会应用可开关的 v11 EOT10 运行时补丁。

## 详细交接

参阅：[OpenCode-Telegram-交接文档.md](OpenCode-Telegram-交接文档.md)
