# macOS 1.35

- Prevent Telegram outages from blocking local session attachment or desktop synchronization heartbeats.
- Initialize command menus and send supplemental outage notices in the background.
- Prevent late question deliveries or failures from replacing or clearing newer questions.
- Retain send scheduling, conflict checks, six-second disconnect protection and missing-directory validation.
- Rebuild the shared Bot and refresh bot-source.zip; no Swift source changes.

# macOS 版本记录

## 1.34 — 同步 Windows 1.0.34 的 Bot 与网页桥

日期：2026-10-29；版本号与 Windows 1.0.34 对齐。

- 同步 Windows 1.0.34 共享 Bot 源码：Telegram 长限流冷却、统一发送调度、`/connect`、`/create_project`、`/delete_session`、`/delete_project`、模型不可用自动重试、工具图片转发、DOCX/EPUB/多格式文档统一工作区、EOT10 worker。
- 同步 Windows 1.0.34 的 `desktop/EmbeddedWebSync.js`。
- 保留 macOS 0.1.28 的 Swift 外壳（22 个 Swift 文件），不回退到 Windows 仓库中的旧 `macos/`。
- 打包修复：`Build-App.sh` 增加复制 `EOT10CryptoWorker.mjs`，否则 EOT10 媒体加密会缺文件。
- 验证：Swift 76 个通过/5 跳过/0 失败；Bot 核心 2399 个测试通过；Smoke-Check 和 DMG 校验通过。
- 限制：仅 Apple Silicon、macOS 14+，ad-hoc 签名且未公证；3 个 e2e 代理/启动器测试因 Windows 源码包未带 Mac e2e 脚本而失败，不影响功能。

### 1.34 重打包修订（2026-10-29）

- 修复网页端黑屏：`EmbeddedWebSync.js` 在首次同步时把非空 `hiddenProjects` 误判为变化，触发 `reloadAuthenticated()` 无限重载。现在只有存在上一份状态基线时才比较隐藏项目。
- `compactOutputMode` 默认改为开启（与 Windows 1.34 本机设置一致），思考内容折叠进精简进度卡，降低 Telegram 请求密度；同时避免 EOT10 加密后编辑超过 4096 字符导致的 `MESSAGE_TOO_LONG` 与流中断。
- 重新执行 `swift build`、ad-hoc 签名、`codesign --verify --deep --strict`、`hdiutil verify`；DMG 内 `EmbeddedWebSync.js` 与修复源码一致（SHA256 `f83806c6…`），`bot/dist` 默认值为 `true`。

### 1.34 后续修复（2026-10-29，未重打包）

- 修复 `/create_project` 创建文件夹后当前项目被网页端拉回旧目录：网页桥只在用户刚刚操作过时上报路由变化产生的 `selection`，避免应用自动恢复旧标签页时把手机切回上一个项目；新增对应回归测试。
- `/create_project` 增加写权限检查：在不可写目录点「在此创建」时直接提示没有写入权限，`mkdir` 返回 `EACCES/EPERM/EROFS` 时也给出明确原因并结束本次输入，不再只显示笼统的「创建文件夹失败」。
- 修复切换项目后网页端丢失 `auth_token`、弹出账号密码登录的问题：项目切换、删除会话和隐藏项目时生成的跳转 URL 会保留认证参数。
- 修复后续重编时 `compactOutputMode` 默认值被共享源码的 `false` 覆盖的问题，恢复默认开启精简输出。

## 0.1.27 — 开箱使用分步说明

日期：2026-10-11；本地安装包，未创建发行标签。

- 每步向导明确说明用途、操作路径、信息来源、成功标志和失败处理：手机扫码入口、双重验证密码、官方 BotFather 创建/获取 Token、User ID 自动填写及第三方查询、手机 /start、可选 EOT10 配置和内置 OpenCode 选择。
- 内置 OpenCode 排在扫描结果首位，用蓝色「选择内置环境（推荐）」按钮呈现；明确无需单独下载运行依赖，并说明模型提供商账号需要用户自行连接。
- 保持仅一次的持久完成标记，不因升级或启动失败重新弹出向导；后续打开仍以网页端为首页并自动启动服务。
- 原生测试执行 74 个，5 个环境依赖测试跳过，无失败。

限制：Apple Silicon、macOS 14+，ad-hoc 签名且未公证。真实首用流程及自动启动仍需用户验证。

## 0.1.26 — 首次使用向导与网页首页

日期：2026-10-11；本地安装包，未创建发行标签。

- 首次使用按顺序完成 tg 登录、Bot 绑定验证、加密设置和 OpenCode V2 环境扫描/选择；登录和绑定不可跳过，加密可保持关闭。内置环境使用蓝色选择按钮。
- Bot 绑定仅验证 getMe 和指定用户私聊 getChat，不发送消息；请用户先在手机发送 /start。Token 保存在钥匙串，网络错误不暴露含凭据的 URL。
- 仅在所选环境验证、OpenCode 连接和 Bot 启动成功后记录向导完成，随后直接进入网页端。已有配置保留；升级到本版后首次显示一次向导。
- 后续打开软件默认进入 OpenCode 网页端，自动连接/启动 OpenCode、已配置且未运行的 Bot，并保留 tg 自动监听；自动连接不抢占其他进程端口，也不重复改写正在运行的服务配置。
- 原生测试执行 72 个，5 个环境依赖测试跳过，无失败；新增 9 个向导、绑定隐私与启动策略测试。

限制：Apple Silicon、macOS 14+，ad-hoc 签名且未公证。真实首次配置和自动启动需用户安装后验证。

## 0.1.25 — 内置 tg 登录与消息监听

日期：2026-10-11；本地安装包，未创建发行标签。

- 新增 Telegram 客户端页面，支持桌面扫码登录、双重验证安全输入、账号状态检查、退出账号和开始/停止监听。
- 桌面启动后，已登录的独立 tg 账号自动监听新消息；未登录不强制登录。退出软件会停止相关 tg 进程。
- 二维码和验证密码不写日志；消息最多保留 200 条在内存中，退出即清空。不读取历史、不自动下载附件、不标记已读、不发送消息、不解密 EOT10、不转交 AI。
- 通过模拟 tg 子进程测试验证登录、监听和清理流程；真实 Telegram 扫码登录及消息收取须用户安装后验证。
- 原生测试执行 63 个，5 个环境依赖测试跳过，无失败；新增 12 个 tg 协议及子进程测试。

限制：Apple Silicon、macOS 14+，ad-hoc 签名且未公证。本版按用户确认仅实现登录与监听，不包含发送功能。

## 0.1.24 — 内置 tg 与 PATH 安装

日期：2026-10-11；本地安装包，未创建发行标签。

- 离线内置官方 gotd/cli v0.11.0 macOS arm64，校验官方 SHA256，保留 LICENSE、README 和来源记录；不自动登录或执行 tg。
- 安装后使用独立管理区块将 tg 目录写入 zsh 的 `.zprofile` / `.zshrc`，保留用户配置及已有 OpenCode 引擎 PATH；应用子进程 PATH 同步包含 tg。
- 按用户要求移除 TELEGRAM_FILE 自动结果附件发送、工具附件收集、相关测试、skill 及自动安装逻辑。原有 EOT10 和用户文件接收流程保留。
- Bot 构建、Lint、类型检查通过；2403 个测试通过、7 个跳过。原生测试执行 51 个，5 个跳过，无失败。

限制：Apple Silicon、macOS 14+，ad-hoc 签名且未公证；tg 仅随包安装，不自动登录或验证真实发送。

## 0.1.23 — EOT10 附件发送修复

日期：2026-10-11；本地安装包，未创建发行标签。

- 修复 EOT10 加密发送文档时误删 `document` 字段，导致 Telegram 返回 `400: there is no document in the request` 的问题；保留加密，不改变安全边界。
- 新增实际 Bot 请求回归测试，覆盖文档和图片转换为加密附件，验证附件字段、文件名以及无明文泄漏。
- Bot 构建、Lint、类型检查通过；2414 个测试通过，7 个跳过。原生测试执行 50 个，5 个跳过，无失败。

限制：Apple Silicon、macOS 14+，ad-hoc 签名且未公证。安装后仍需验证真实 Telegram 附件收取。

## 0.1.22 — Telegram result file delivery

Date: 2026-10-11. Local installer build; no release tag created.

- Automatically upload local artifact links and completed tool file attachments through the connected Bot, preserving V2 file results previously dropped by the adapter.
- Support explicit `TELEGRAM_FILE` delivery and bundle the global `telegram-bot-files` skill; install it on Bot startup without overwriting user customizations.
- No delivery instructions are appended to model prompts. Enforce session-directory boundaries, deduplicate files within a reply, and retain EOT10 delivery through the existing Bot API pipeline.
- Bot build, lint and typecheck pass; 2412 tests pass with 7 skipped. Native tests: 50 executed, 5 skipped, no failures.

Limitations: Apple Silicon, macOS 14+, ad-hoc signed and not notarized. Live Telegram delivery still requires verification after installing this build.

## 0.1.21 — EOT10 富消息解密修复

日期：2026-10-03；Git 标签：`macos-0.1.21-eot10-rich-fix`。

- 修复 Telegram 合并相邻富文本片段后，多个独立密文被拼接导致解密失败的问题：先合并相邻明文，再加密，保留格式边界和按钮。
- 增加列表片段、嵌套格式文本及按钮行为的回归测试。
- Bot 构建、Lint、类型检查通过；2401 个测试通过，7 个跳过。
- 用户安装后确认实际 Bot 的长文本、列表、表格与代码完整显示，无解密失败。

发行限制：Apple Silicon，macOS 14+，ad-hoc 签名且未公证；此次实测仅验证上述 EOT10 场景。

## 0.1.20 — 第一个可用版本 / First usable macOS release

日期：2026-10-03；Git 标签：`macos-0.1.20-first-usable`。

该版本经用户确认满意，作为后续开发、回归测试及发布的基线。此里程碑仅针对 macOS；保留此前 Windows/Android 发布。

- 完成 OpenCode V2-only 重构和 Bot 测试修复。
- 应用与可更新依赖分离，支持离线安装到同一安装目录。
- 引擎应用状态、来源标签和红色卸载按钮；动态 zsh PATH。
- 默认折叠的 Node/Bun 更新区域，完整性校验、占用检查及回滚。
- 未登录提供商的网页 `/connect` 引导。
- 加密默认关闭；特殊 Android Telegram 客户端要求、下载、源码及限制说明。
- 机器人绑定和加密详细说明放在操作区下方，保留原布局。

发行限制：Apple Silicon，macOS 14+，ad-hoc 签名且未公证；真实 Telegram/OAuth 请求由用户进一步验证。
