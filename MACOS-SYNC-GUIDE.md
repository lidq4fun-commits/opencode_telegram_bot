# 本次改进总结与 macOS 同步指南

日期：2026-10-27
Windows 桌面版：1.0.22 → 1.0.26（2026-10-26）→ **1.0.32**（2026-10-27）
适用对象：本仓库的 `bot/`（共享）与 `macos/`（Swift Package，需要同步）

> 说明：本次多数修复在共享的 `bot/` 里（Windows 与 macOS 共用同一份 bot 源码），macOS 只需重新打包 bot 就能拿到；**桌面壳**（状态轮询、服务启动、网页端、关闭）需要按本文在 Swift 里对应修改。

---

## 0. 2026-10-27 增量（1.0.27–1.0.29）

> 本节只列 1.0.26 之后的增量；下面的 §0.1–§6 仍是 1.0.23–1.0.26 的完整说明，macOS 移植时两节都要看。

### 制品与发布（1.0.32）

| 制品 | 大小 | SHA256 |
|---|---|---|
| `desktop/artifacts/OpenCodeTelegram-Setup-1.0.32.exe` | 688,103,936 B | `357EE6D94014A67D58E6E68E2F3692B0AAFE8E0B4EC5FBBEE1BAC0C0F4E330F6` |
| `desktop/artifacts/OpenCodeTelegram-1.0.32-Windows-x64.zip` | 682,758,305 B | `F1EB46CCE5B8ADD4C726734D72ED4DD6189D8A48CB5C67B4358B017FCC3ED13F` |
| `desktop/artifacts/OpenCodeTelegram-Setup-1.0.31.exe` | 688,103,936 B | `0ECB63A9D870758DA58AAAEBAB1A666B7B89132BFAD8AB79A5836A8D33F4ED88` |
| `desktop/artifacts/OpenCodeTelegram-1.0.31-Windows-x64.zip` | 682,758,279 B | `AB4DC6B1A2891AC6A9D1D4A2A1A06F9402B1BA2128798AFD31FDA9D28E70C72B` |

发布说明：`desktop/releases/desktop-v1.0.27.md` 至 `v1.0.32.md`。本机已安装 1.0.27；1.0.32 统一所有非媒体文档的工作区文件交付。

### A. 共享 bot：新命令

以下都在共享 `bot/`，macOS 重新打包 bot 即生效。

| 命令 | 关键文件 | 行为 |
|---|---|---|
| `/connect` | `app/services/provider-connection-service.ts`、`bot/commands/connect-command.ts`、`bot/callbacks/connect-callback-handler.ts`、`bot/menus/connect-menu.ts`、`bot/services/provider-connect-runtime.ts` | 读取引擎 `/api/integration`，支持 OAuth 与 API Key；OAuth 每 3 秒轮询；设备码方式标 📱，localhost 回调方式标 🖥️；API Key 消息读取后立即删除；支持搜索、已连接标记、取消、过期 |
| `/create_project` | `bot/commands/create-project-command.ts`、`bot/callbacks/create-project-callback-handler.ts` | 复用 `/open` 浏览器（新增 `createProject` 元数据、选择按钮「在此创建」、浏览阶段「取消创建」）；输入名称后 `mkdir`、写入项目缓存并切换；校验 Windows 保留名、非法字符、结尾点/空格、重名、超长 |
| `/delete_session` | `bot/commands/delete-session-command.ts`、`bot/callbacks/delete-callback-handler.ts`、`bot/menus/delete-menu.ts` | 列出当前项目会话，二次确认后删除；删除当前会话时清理 attach/置顶 |
| `/delete_project` | 同上 + `bot/commands/delete-project-command.ts` | 隐藏项目（`settings.hiddenProjects`）并删除其全部会话；磁盘文件不动；当前项目被删时清理状态；通过 `/open` 重新选择可恢复 |
| `/open` 无限制 | `app/services/file-browser-service.ts` | 未设置 `OPEN_BROWSER_ROOTS` 时 Windows 枚举全部盘符、macOS/Linux 从 `/` 开始；新盘符实时出现；仍可用环境变量限制 |

配套改动：`bot/src/bot/commands/definitions.ts`、`routers/command-router.ts`、`callbacks/callback-router.ts`、`middleware/interaction-guard-decision.ts`、`menus/inline-menu.ts`，13 种语言的 `i18n/*.ts`。

### B. 共享 JS：桌面网页同步

`desktop/EmbeddedWebSync.js` 新增：

- `hiddenProjects`：过滤 `GET /api/project` 返回中被 `/delete_project` 隐藏的项目；隐藏列表变化时刷新页面。
- `deletedSession`：手机删除会话后，桌面若正停在该会话页，自动退回项目列表。
- 项目列表变化（隐藏/恢复）会触发刷新。

macOS 的 `macos/Sources/OpenCodeTelegram/EmbeddedWeb.swift` 直接把 `state.json` 的原始 JSON 传给 `accept(...)`，新字段自动透传，**不需要改 Swift 数据结构**；但必须确认 `macos/Build-App.sh` 复制的是最新 `desktop/EmbeddedWebSync.js`。

### C. Windows 桌面壳：登录检查修复（仅 Windows）

`desktop/src/DesktopApp.cs`：

- `CheckTelegramLogin` 从 `bool` 改为 `TelegramLoginCheck`，区分 `Ok / NotLoggedIn / Network / Unavailable / InvalidAccount`，超时 30 s → 8 s。
- 网络不通/未登录/未知错误：**不停止 Bot**，概览页显示明确警告，每 15 秒自动重试，新增「重试验证」按钮。
- 只有 `tg whoami` 成功且账号与向导绑定账号不同，才停止服务并明确显示两端 ID。
- 仅在 `Eot10Enabled` 开启时检查 `tg`；关闭 EOT10 不再被 Telegram 登录状态阻塞。
- 向导步骤 0/1/3 改为使用返回结果，不再依赖方法内部修改 `settings.TelegramUserId`。
- 新增测试：`desktop/tests/Test-TelegramLoginCheck.ps1`（5 项分类检查）。

macOS 当前没有这套 `tg whoami` 启动门禁（Swift 只用 `allowedUserID`）；如果以后加上，按同样分类与重试策略实现，不要用“账号不一致”覆盖网络错误。

### D. macOS 检查表（增量）

- [ ] 重新打包 bot（`Prepare-Runtime.sh` / `Build-App.sh`），确认 bundle 内包含：
  - `bot/dist/bot/commands/connect-command.js`
  - `bot/dist/bot/commands/create-project-command.js`
  - `bot/dist/bot/commands/delete-session-command.js`、`delete-project-command.js`
  - `bot/dist/bot/callbacks/connect-callback-handler.js`、`create-project-callback-handler.js`、`delete-callback-handler.js`
  - `bot/dist/app/services/provider-connection-service.js`
  - `bot/dist/bot/services/provider-connect-runtime.js`
- [ ] 确认 `Build-App.sh` 复制最新 `desktop/EmbeddedWebSync.js`，并包含 `hiddenProjects` / `deletedSession`。
- [ ] 不需要新增 Swift 文件；`/connect`、`/create_project`、`/delete_*`、`/open` 均为共享 bot 功能。
- [ ] 跑 bot 测试（见 §3.1）和 macOS 桌面测试。
- [ ] 手工验收：OpenAI 用 **📱 Codex device code** 在手机完成；`/create_project` 创建并进入；`/delete_project` 后手机和桌面项目列表都消失；`/open` 能浏览其他磁盘。

### E. 增量测试结果

- Bot：**2437 passed / 7 skipped**（205 个测试文件）。
- 桌面登录分类：**5 项全过**。
- 网页同步：**25 项全过**。
- 程序集：`1.0.29.0`。

### F. 1.0.30 增量：DOCX 提示词（共享 bot）

- `bot/src/bot/handlers/document-handler.ts`：附件仍按工作区文件保存到 `.opencode-telegram-attachments/`，但提示词改为明确要求：
  - “Read this file with your tools before answering…”
  - 相对路径用反引号包住，避免文件名含空格/括号时 shell 拼接问题；
  - DOCX 提示可解压 `word/document.xml`；仍不解析、不注入文本、不重复发附件。
- macOS：只需重新打包 bot，无需改 Swift。
- 测试：`bot/tests/bot/handlers/document.test.ts` 已更新（16 项通过）。

### G. 1.0.31 增量：EPUB 工作区文件（共享 bot）

- `bot/src/bot/handlers/document-handler.ts`：`application/epub+zip` 与 `.epub` 改为和 DOCX 一样保存到 `.opencode-telegram-attachments/`，不再作为 file part 发送。
- 提示词统一为 “Read this file with your tools before answering; unzip DOCX/EPUB archives if needed.”。
- EPUB 正文在 XHTML 中，agent 可用 unzip/文件工具读取；图片资源保留在压缩包内。
- macOS：只需重新打包 bot，无需改 Swift。
- 测试：`bot/tests/bot/handlers/document.test.ts` 新增 EPUB 用例（17 项通过）。

### H. 1.0.32 增量：所有文档格式统一工作区文件（共享 bot）

- `bot/src/bot/handlers/document-handler.ts` 的文档路由改为：
  - 文本类 → prompt 文本；
  - 图片/音频/视频 → file part；
  - **其他所有文档/压缩包**（PDF、DOC/DOCX、ODT、RTF、PPT/PPTX、XLS/XLSX、EPUB、ZIP/TAR/GZ/RAR/7Z、未知二进制）→ 存到 `.opencode-telegram-attachments/` 并给路径；
  - 无活动项目时回退 file part。
- 提示词统一为 “Read this file with your tools before answering; unzip/extract DOCX, EPUB, PDF, Office or archive formats if needed.”。
- macOS：只需重新打包 bot，无需改 Swift。
- 测试：`bot/tests/bot/handlers/document.test.ts` 17 项通过（DOCX/EPUB/PDF 存储、图片 file part）。

---

## 0.1 2026-10-26 交付（1.0.23–1.0.26）

| 制品 | 大小 | SHA256 |
|---|---|---|
| `desktop/artifacts/OpenCodeTelegram-Setup-1.0.26.exe` | 688,055,296 B | `F65C262BB4C1A1F4140E6133347064D2479C1FCF23B240DE49CB2614243FE292` |
| `desktop/artifacts/OpenCodeTelegram-1.0.26-Windows-x64.zip` | 682,718,498 B | `85591AD09637B176F08F5EB029C7E491AAC9887BAA35072E299BE0597A0795DA` |

发布说明：`desktop/releases/desktop-v1.0.23.md`（性能 + 引擎面板修复）、`desktop-v1.0.24.md`（模型重试 + 工具图片）、`desktop-v1.0.25.md`（网页选项目锁定）、`desktop-v1.0.26.md`（DOCX 内容交付）。
版本基线：用户已安装的是 **1.0.22**；1.0.23–1.0.26 是其后续（1.0.19–1.0.21 为内部编号，已删除）。

---

## 1. 变更总览

| # | 问题 | 根因 | 改法 | 归属 | macOS 落点 |
|---|---|---|---|---|---|
| 1 | 手机模型菜单没有当前模型（opencode-go） | 菜单只读 OpenCode 自己的 `model.json`，Telegram 的选择不写回 | 当前模型作为第一个收藏项合并 | 共享 bot | 无需改，重新打包 bot |
| 2 | 启动慢、状态卡、网页慢、关闭慢 | 反复 spawn Node/CLI、反复重启引擎、串行等待 | 本地状态、服务复用、并行启动、缓存、按变化同步 | 共享 bot + 桌面壳 | 见 §2.2.3 |
| 3 | 手机报 `Model unavailable`（网页正常） | 引擎拉取 `opencode.ai/console/api/v2/config` 时瞬时 TLS 失败（VPN/代理），provider 目录失效 | 识别后自动重发 prompt（2/5/10 s，最多 3 次）+ 明确提示 | 共享 bot | 无需改，重新打包 bot |
| 4 | OpenCode 生成的图片在手机端不显示 | V2 工具结果里的 `{type:"file", mime:"image/*"}` 被映射层丢弃 | 保留附件 → 聚合器回调 → 以照片发送（EOT10 自动加密） | 共享 bot | 无需改，重新打包 bot |
| 5 | 引擎面板"已应用"按钮颜色错误（既有回归） | 内置引擎优先命中蓝色分支 | 已应用（active）一律绿色 + 禁用 | 仅 Windows | 检查 macOS 是否有同款逻辑 |
| 6 | 拆分源码后产物版本 0.0.0 | 漏传 `AssemblyVersion.cs` | 修复 + 构建期版本校验 | 仅 Windows | 无（Swift 用 Package.swift） |
| 7 | 网页选了一个手机没有的项目后，页面锁在项目列表 | 共享状态没有 session 时脚本反复把页面拉回根目录；AI 回答期间 `POST /session` 被无条件拦截 | 无 session 不再强制跟随；选中项目后直接打开草稿；创建 session 只锁"当前项目" | 共享 JS | 同上：重新打包即生效（`Build-App.sh` 会复制 `desktop/EmbeddedWebSync.js`） |
| 8 | 手机发来的 DOCX 模型读不到 | 引擎不会把 OOXML 文本交给纯文本模型（空目录新会话实测确认） | 原始文件存进项目 `.opencode-telegram-attachments/`，prompt 只给路径，agent 用工具读取 | 共享 bot | 无需改，重新打包 bot |

---

## 2. 逐项详解

### 2.1 模型菜单：当前模型置顶（共享 bot）

**文件**：`bot/src/app/services/model-selection-service.ts`

- 新增 `getSelectedFavoriteModel()`：取 `getCurrentModel()`，无有效 provider/model 返回 `null`。
- 新增 `mergeFavorites(selected, favorites, envDefault)`：顺序为 **当前模型 → 状态文件收藏 → `OPENCODE_MODEL_PROVIDER/ID` 默认值**，`dedupeModels` 去重。
- `getModelSelectionLists()` 用它替换原来的 `[...validatedFavorites, envDefault]`；`model.json` 读不到时同样保留当前模型。
- 文档：`bot/PRODUCT.md` 的 Model picker behavior 增加一条。
- 测试：`bot/tests/app/services/model-selection-service.test.ts` 新增 3 个（当前模型不在状态文件、状态文件不可读、与状态文件重复）。

**macOS**：不需要改 Swift；`macos/` 重新打包 bot 后自动生效。

---

### 2.2 启动与性能

#### 2.2.1 共享 bot 侧

| 文件 | 改动 |
|---|---|
| `bot/src/config.ts` | `opencode.managedByDesktop = process.env.OPENCODE_MANAGED_BY_DESKTOP === "1"` |
| `bot/src/opencode/local-start.ts` | 托管模式下直接拒绝 spawn 本地引擎（`reportOpencodeProblem("desktop-managed")` + 警告），避免与桌面抢引擎 |
| `bot/src/opencode/auto-restart.ts` | 新增 `STARTUP_READY_WAIT_MS = 60000`、`waitForManagedStartup()`：托管模式下每 500 ms 探活，就绪后 `notifyReady("auto_restart_startup_wait")` |
| `bot/src/app/bootstrap/start-bot-app.ts` | ① `startEventLoopMonitor()` / 退出时 `stopLagMonitor()`；② 启动时 `seedModelCatalogFromDisk()`；③ EOT10 开启时后台 `prepareEot10Key()`；④ `reconcileStoredModelSelection()` 与 `scheduledTaskRuntime.initialize()` 用 `Promise.all` 并行；⑤ `getMe` 与移除 webhook 并行；⑥ 退出时 `stopEot10Workers()` |
| `bot/src/app/services/model-catalog-cache.ts`（新） | `model-catalog-cache.json`（appHome）：`{version:1, savedAt, providers:[{id,name,models[]}]}`；`getModelCatalogCachePath/readModelCatalogCache/writeModelCatalogCache` |
| `bot/src/app/services/model-selection-service.ts` | 新增 `seedModelCatalogFromDisk()`（读磁盘缓存 → 填充内存目录 → 30 秒 `MODEL_CATALOG_SEED_TTL_MS` 内直接服务 + 后台刷新）；完整读取成功后 `writeModelCatalogCache()`；`readModelCatalog({ignoreSeed})`；`reconcileStoredModelSelection()` 固定 `ignoreSeed: true`，避免拿旧缓存误判模型下线 |
| `bot/src/app/services/desktop-sync-service.ts` | `STATE_HEARTBEAT_MS = 2000`：内容变化立即写，空闲每 2 秒心跳一次（原来是每 500 ms 无条件写） |
| `bot/src/app/services/event-loop-monitor.ts`（新） | `monitorEventLoopDelay`，每 60 s 一条 `[Perf]` debug，p95 ≥ 150 ms 警告（30 s 冷却）；导出 `readEventLoopLag/summarizeEventLoopLag/startEventLoopMonitor/stopEventLoopMonitor` |
| `bot/EOT10Crypto.mjs` | `prepareEot10Key()` 用异步 `pbkdf2`（libuv 线程池）预派生；新增 worker 池：`MEDIA_WORKER_THRESHOLD_BYTES = 2 MiB`、`encryptEot10MediaBufferAsync`、`decryptEot10MediaBufferAsync`、`stopEot10Workers`；`encryptEot10MediaInput` 改走异步加密 |
| `bot/EOT10CryptoWorker.mjs`（新） | 单 worker：`{id, kind, bytes, metadata}` → 结果/错误；同一时刻一个任务 |
| `bot/src/types/eot10-crypto.d.ts` | 补 `prepareEot10Key/encryptEot10MediaBufferAsync/decryptEot10MediaBufferAsync/stopEot10Workers` |
| `bot/src/bot/index.ts` | 入站附件解密改为 `await decryptEot10MediaBufferAsync(...)` |

测试：`tests/app/services/event-loop-monitor.test.ts`、`tests/eot10-worker.test.ts`、`tests/app/services/model-selection-service.test.ts`（磁盘缓存 3 例）、`tests/opencode/auto-restart.test.ts`（托管等待）、`tests/opencode/local-start.test.ts`（托管拒绝）。

#### 2.2.2 Windows 桌面壳（C#）

文件已从单文件拆为 `desktop/src/*.cs`：

```
desktop/src/Contracts.cs        # 数据契约（含 BotServiceState / OpenCodeServiceState）
desktop/src/ProductPaths.cs
desktop/src/Settings.cs         # DesktopSettings + SettingsStore + 启动项/卸载注册
desktop/src/BotRuntime.cs       # 进程/引擎/服务/安装/状态
desktop/src/Cli.cs
desktop/src/DesktopApp.cs       # WPF 界面与页面
desktop/src/InstallerWindow.cs
desktop/src/Program.cs
```

关键改动：

| 项 | 实现 |
|---|---|
| 本地状态（无 spawn） | `BotRuntime.ReadServiceStatus()`：读 `run/bot-service.json`（`pid/startedAt/logFilePath/mode`），校验 PID 存活与 PID 复用（进程创建时间 > startedAt 视为复用），输出与 CLI 相同的 6 行文本；实测 **477 ms → 0.52 ms** |
| 服务复用 | `TryReadRunningV2Service()`（读 `~/.local/state/opencode/service.json`，PID 存活、loopback、端口一致）+ `TryUseRunningV2Service()`（`GET /api/info` Basic 认证，2 s 超时）。实测 **22 ms** |
| 启动流水线 | `RequireV2Executable` → `ConfigureLocalV2ServiceRegistration`（unset/port/password/username，几次 CLI）→ `Task.Run(StartPreparedLocalV2Service)`（`service start`，阻塞到就绪）**同时** `Execute("start --daemon")` 起 Bot → `VerifyRunningV2Service(settings, url, true)`；失败时 `Execute("stop")` 回滚 |
| 托管标记 | `Build()` 环境新增 `OPENCODE_MANAGED_BY_DESKTOP=1` |
| 概览页立即可用 | `CreateWindow` 里 `ShowPage(0)` + “正在启动…”；`OpenHome` 不再禁用窗口；`startupInProgress` 期间 `RefreshStatus` 跳过、`StartOpenCodeWeb` 跳过、`RunBotAction` 直接返回、服务按钮禁用 |
| 日志 | `LoadLog()` 记录 `长度:mtime` 签名，未变化不重读；只读末尾 64 KB、最多 250 行 |
| 网页端 | `ShowPage` 不再 Dispose 浏览器；`BuildOpenCodeWeb` 缓存 `webPageLayout` 复用；打开前用 `TryUseRunningV2Service` 替代必然的 `StartLocalV2Service`；`PrewarmWebView()` 在窗口创建时预热环境 |
| 网页同步 | `RefreshTelegramWebContext()` 先 stat `desktop-sync/state.json`，签名未变且 < 3 s 直接返回；导航完成后清空签名强制重注入 |
| 关闭 | Bot 停止与引擎停止并行，兜底 `StopOpenCodeProcesses()` 最后执行；进程等待 10 s → 3 s；“等待中的操作”最多 5 s；关闭时 Dispose 浏览器 |
| 引擎面板 | `RenderOpenCodeInstallations`：`active` 优先 → 绿色（33,128,74）+ 禁用；否则内置蓝、普通灰 |
| 构建 | `desktop/Build-Desktop-Package.ps1` 编译 `desktop/src/*.cs` + 生成的 `AssemblyVersion.cs`；新增构建期校验：`[Reflection.AssemblyName]::GetAssemblyName($appExe).Version` 必须等于目标版本 |

#### 2.2.3 macOS 对应改法（逐文件）

> macOS 桌面壳是 Swift Package：`macos/Sources/OpenCodeTelegram/`。

**A. `Runtime.swift`**

- `environment(_:)` 增加：
  ```swift
  env["OPENCODE_MANAGED_BY_DESKTOP"] = "1"
  ```
  （这是 Bot 不再自行 spawn 引擎的前提。）

**B. `AppModel.swift` — `connect()` 拆分与并行**

现状：`connect()` 顺序执行 `service unset disabled` → `service set password` → `service set env` → `service start` → 校验 `/api/info`，然后 `botAction("start")` 才启动 Bot。

目标结构（对应 C# 的 Prepare/Start/Verify）：

```swift
// 1) 先判断能否复用（对应 TryUseRunningV2Service）
//    读 ~/.local/state/opencode/service.json: { url, pid, version, password }
//    - kill(pid, 0) == 0 视为存活
//    - url 端口与 settings.serverURL 一致、host 为回环
//    - GET {url}/api/info，Basic opencode:<serverPassword>，2s 超时，200 且版本以 "2." 开头
//    命中 → settings.serverURL = 实际地址，跳过重启
func runningService() async -> URL? { ... }

// 2) 未命中才注册（快）+ 后台启动（慢）
func prepareService() async throws -> URL {   // unset/port/password/username 注册
}
func startService(_ executable: URL) async throws -> URL { // service start + 解析 URL
}

// 3) connect() 新流程
let serviceURL: URL
if let running = await runningService() { serviceURL = running }
else {
    let executable = Runtime.engine(settings)
    _ = try await prepareService()
    let serviceTask = Task { try await startService(executable) }   // 后台跑
    _ = try await Runtime.bot(["start", "--daemon", "--mode", "installed"], settings: settings) // 立刻起 Bot
    serviceURL = try await serviceTask.value
    do { try await verifyService(serviceURL) }                       // /api/info
    catch { _ = try? await Runtime.bot(["stop"], settings: settings); throw error }  // 回滚
}
settings.serverURL = serviceURL.absoluteString
try save()
```

要点：
- Bot 进程自身会在 60 s 内以 500 ms 轮询等待引擎（共享 bot 已实现），所以先起 Bot 是安全的。
- 校验失败要回滚停 Bot，保持“认证失败不留下半启动状态”的旧保证。
- `botAction("start")` 里现在重复调用 `connect()`，改成复用上面的流程即可。

**C. 新增 `ServiceState.swift`（本地状态，替代 `Runtime.bot(["status"])`）**

- 路径：`SettingsStore.home/run/bot-service.json`（字段 `pid/startedAt/logFilePath/mode`）。
- 存活：`kill(pid, 0) == 0`。
- 输出与 CLI 相同的文本：
  ```
  Service status: running
  PID: <pid>
  Started at: <startedAt>
  Uptime: <秒> sec
  Log file: <logFilePath>
  App home: <home>
  ```
- macOS 上进程创建时间可用 `proc_pidinfo(PROC_PIDTBSDINFO)` 或 `ps -p <pid> -o lstart=` 做 PID 复用校验（Windows 用 `Process.StartTime`）。
- 概览页改为调用本地读取（毫秒级），日志仍是“末尾 64 KB”（`refreshLogs()` 已经这样做了，保持）。

**D. `EmbeddedWeb.swift` — 同步按变化 + 浏览器只建一次**

- 现在 `Timer` 每 0.5 s 都 `Data(contentsOf:)` + `JSONSerialization` + `evaluateJavaScript`。改成：
  ```swift
  // Coordinator 里保存
  var lastSignature: String?
  var lastCheckedAt = Date.distantPast

  func refresh() {
      let file = SettingsStore.home.appendingPathComponent("desktop-sync/state.json")
      let attrs = try? FileManager.default.attributesOfItem(atPath: file.path)
      let size = (attrs?[.size] as? NSNumber)?.intValue ?? -1
      let mtime = (attrs?[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
      let signature = "\(size):\(mtime)"
      if signature == lastSignature, Date().timeIntervalSince(lastCheckedAt) < 3 { return }
      lastSignature = signature; lastCheckedAt = Date()
      // …原来的解析与注入…
  }
  ```
- 在 `webView(_:didFinish:)` 里把 `lastSignature = nil`，确保刷新后重新注入。
- Bot 侧已改为“内容变化才写 + 2 秒心跳”，所以空闲时约 2 秒一次，而不是 2 次/秒。
- WKWebView 只创建一次：`makeNSView` 每次页面切换都会重建，建议把 `WKWebView` 缓存在 `AppModel`（或让网页页保持挂载、非当前页隐藏），避免每次进入网页页重新起 web content 进程。
- 预热：macOS 没有 WebView2 那种“环境对象”，预热的意义在于复用同一个 `WKWebView`；如需更激进，应用启动时创建隐藏 WKWebView 加载 `about:blank`。

**E. `AppModel.cleanup()` — 并行关闭**

```swift
func cleanup() async throws {
    console.stop()
    async let botStop: Void = Runtime.bot(["stop"], settings: settings).map { _ in () }
    async let webStop: Void = stopWeb()
    _ = try? await botStop
    _ = try? await webStop
}
```
- 进程等待建议 ≤ 3 s；macOS 上终止后 `kill(pid, 0)` 轮询即可。
- `stopWeb()` 里的 `service stop` 与 `ps`/`kill` 逻辑保持，但不要和 Bot 停止串行。

**F. `Main.swift`**

- 启动期间禁用“启动 Bot/停止 Bot/刷新状态”：`AppModel` 增加 `starting` 状态（`busy` 已覆盖大部分按钮，确认启动流程也纳入 `busy`）。
- 若概览页有状态文本，改为 `ServiceState` 的本地读取结果，不再点击才刷新。

**G. `macos/` 的 V1 分支**

`bot/` 已是 V2-only（`opencodeServerVersion` 固定 `v2`），macOS 仍保留 V1（`connect()` 里的 v1 分支、`v1Process`、`v1-web-process.json`）。建议后续单独清理；本次不强制。

**H. 重新打包 bot**

macOS 的 bot 来自 `macos/dependencies/...` 与 `bot/dist`。同步后执行 `macos/Prepare-Runtime.sh`（或 `Build-App.sh` 的打包步骤）以重新构建并嵌入新的 `bot/dist`；否则 §2.1/§2.3/§2.4 的修复不会生效。

---

### 2.3 `Model unavailable` 自动重试（共享 bot）

**背景**：引擎日志里的真实原因

```
WARN  failed to load OpenCode provider config
      cause=Transport error (GET https://opencode.ai/console/api/v2/config)
            (TypeError: unknown certificate verification error)
ERROR SessionRunnerModel.ModelUnavailableError: Model unavailable: opencode-go/deepseek-v4.1-flash
```

即：引擎拉取 provider 配置失败（VPN/代理/TLS 抖动）→ 目录瞬时失效 → 模型被判不可用。恢复通常只需几秒。

**新增**：`bot/src/app/services/prompt-model-retry.ts`

- `MODEL_UNAVAILABLE_RETRY_DELAYS_MS = [2000, 5000, 10000]`（最多 3 次）
- `SNAPSHOT_TTL_MS = 5 min`、`MAX_REMEMBERED_SESSIONS = 8`
- API：
  - `isModelUnavailableMessage(message)`：`/^\s*model unavailable\b/i`
  - `rememberPromptForModelRetry(request)` / `forgetPromptForModelRetry(sessionID)` / `getRetryablePrompt(sessionID)`
  - `retryPromptAfterModelUnavailable(sessionID, { send, onRetryAccepted, onRetryAttempt, wait })`：按延迟重发，成功返回 `true`，耗尽返回 `false`

**接入点**：

| 文件 | 改动 |
|---|---|
| `bot/src/bot/handlers/prompt.ts` | `processUserPrompt` 与 `admitPromptToInbox` 在 `preparePromptRequest` 之后 `rememberPromptForModelRetry(promptOptions)` |
| `bot/src/bot/events/handlers/session-lifecycle-handler.ts` | ① `session.idle` 时 `forgetPromptForModelRetry`；② 错误分支用 `consumePromptResponseMode` 取出响应模式；③ `isModelUnavailableMessage` 命中时调用重试，第一次尝试前发提示 `bot.model_unavailable_retrying`，成功后 `startPromptRun(...)` 重新标记 busy 并 return（不显示红色错误）；④ 全部失败时用 `bot.model_unavailable_failed`（含“检查网络/VPN”提示） |
| `bot/src/i18n/*.ts` | 新增 `bot.model_unavailable_retrying`、`bot.model_unavailable_failed`。**注意：`I18nDictionary` 是全量类型，12 个 locale 必须同时补齐，否则 `tsc` 直接报 TS2739**（本次非中英 locale 用英文兜底） |

测试：`bot/tests/app/services/prompt-model-retry.test.ts`（识别、成功重试、耗尽、无快照、TTL、清理、覆盖旧快照）。

---

### 2.4 工具生成的图片转发（共享 bot）

**根因**：V2 工具完成的 `content` 里带 `{ type: "file", uri, mime, name }`，映射层只把文本拼进 `output`，文件被丢弃。

**改动**：

| 文件 | 改动 |
|---|---|
| `bot/src/opencode/v2/mappers.ts` | `toV1ToolState(tool, toolName, base)` 新增 `base = { sessionID, messageID }`；完成状态把 `content` 里的 `file` 项映射为 `attachments: FilePart[]`（`url = item.uri`，`filename = item.name`） |
| `bot/src/app/managers/summary-aggregation-manager.ts` | 新增 `ToolImageInfo`、`ToolImageCallback`、`onToolImageCallback`、`setOnToolImage`；工具完成时过滤 `mime` 以 `image/` 开头的附件并逐个回调 |
| `bot/src/bot/events/telegram-event-delivery.ts` | 新增 `sendPhoto(destination, buffer, filename, options?)`（`InputFile`），EOT10 中间件会自动转成加密文档 |
| `bot/src/bot/events/handlers/tool-activity-handler.ts` | 注册 `setOnToolImage`；`readToolImage(uri)` 支持 `data:`、`file://`、绝对路径；带 caption（工具标题，截断 1024）；失败只记日志 |

测试：`tests/opencode/v2/mappers.test.ts`（附件保留）、`tests/app/managers/summary-aggregation-manager.test.ts`（`onToolImage` 回调）。

> EOT10 开启时图片会以加密附件（`.eotm`，metadata `kind: "photo"`）到达，显示效果取决于定制 Android 客户端。

---

### 2.5 网页选项目后锁在项目列表（共享 JS）

**现象**：在电脑网页里选一个手机没有的文件夹项目 → 手机显示了该项目，但网页停在项目列表，点不进去。

**根因**（`desktop/EmbeddedWebSync.js`）：

1. 选中项目后共享状态里是 `currentProject = 新项目`、`currentSession = null`，`WebAddress` 返回服务器根目录；脚本在 `accept()` 里把"目标与当前路径不同"一律当作"跟随共享状态"，于是把页面从草稿页拉回 `/`。
2. `routeChanged()` 在 `busy/mobileActive` 时也会强制 `follow()`，即使共享状态根本没有 session。
3. `POST /session` 在 `busy` 时被无条件拦截，导致在另一个项目里连"新建会话"都做不了。

**改法**：

| 位置 | 改动 |
|---|---|
| `accept()` | 记录每条 `selection` 命令的 `directory`（`pending` 里存 `kind/directory`）；ack 成功后，如果共享状态没有 session 且页面停在 `/`，直接 `location.replace('<origin>/new-session?directory=<目录>')`，即使 Bot 还没采纳该项目也能进入草稿 |
| `accept()` 跟随守卫 | `mayFollow = 有共享 session || 项目发生了变化`；没有 session 时不再把页面拉回列表 |
| `routeChanged()` | 只在"存在共享 session"时才执行 busy/mobile 的强制跟随 |
| `window.fetch` 的 `POST /session` | mobileActive 时仍拦截；`busy` 时**只拦截当前项目**（读取请求体 `location.directory`），允许在另一个项目里创建会话 |

**测试**（`desktop/tests/Test-EmbeddedWebSync.mjs`，共 22 项）：

- 选中的项目没有 session → 打开 `/new-session?directory=…`，后续轮询（包括 busy）不把它拉回列表；
- Bot 尚未采纳该项目时，ack 后依然打开草稿；
- AI 回答期间在**另一个**项目创建 session 允许；在**当前**项目仍被拦截（原有测试保留）。

**macOS**：`macos/Build-App.sh` 会直接复制 `desktop/EmbeddedWebSync.js`，重新打包即生效，无需改 Swift。

---

### 2.6 手机发来的 DOCX 模型读不到（共享 bot）

**实测结论**：在**空目录**里新建会话、附件只存在于内存中时，同一个模型对 DOCX 的回答是"没有看到文档内容"——`agent`、`variant` 有无都一样。之前看到"能读"，是因为那份 docx 恰好也在磁盘上，模型用工具自己找到并解压了。**所以引擎并不会把 OOXML 文本交给纯文本模型。**

**改法**（工作区文件约定，Codex/Claude Code 同类做法）：

1. 收到 DOCX 时把**原始文件**写入当前项目的 `.opencode-telegram-attachments/<时间戳>-<随机>-<文件名>`；
2. prompt 里只追加 `Telegram attachment stored verbatim in the active project: <相对路径>`，**不注入文档文本、不重复发附件**；
3. agent 用自己的工具读取该文件（解压 `word/document.xml`、python-docx 等），表格、内嵌图片、版式完整保留；
4. 没有活动项目时回退为纯附件发送；
5. 与 EOT10 兼容：加密文档先解密，再存档/引用。

测试：

- `bot/tests/bot/handlers/document.test.ts`（DOCX 存档 + prompt 路径；无项目时回退附件）

**macOS**：无需改 Swift；重新打包 bot 后生效。

---

## 3. 验证与测试

### 3.1 命令

```powershell
cd bot
npm run build
npm run lint
npm run typecheck
npm test -- --maxWorkers=4
```

> Windows 上如果 shell 里带了 `OPENCODE_TELEGRAM_HOME`（指向已安装应用的 home，其 `.env` 里是 V1），测试会被 config 的 V2 校验打断。跑测试前清掉：
> `Remove-Item Env:OPENCODE_TELEGRAM_HOME, Env:OPENCODE_TELEGRAM_ROOT, Env:OPENCODE_SESSION_ID, Env:OPENCODE, Env:OPENCODE_TERMINAL -ErrorAction SilentlyContinue`

### 3.2 结果

- Bot：**2387 passed / 7 skipped**（198 个测试文件）
- 桌面（用打包产物 exe 复跑）：服务状态与复用 14、浏览器登录 12、加密设置 19、首次向导 46、Telegram Web 上下文 22、引擎选择 23 —— **136 项全过，0 失败**
- 实测：状态轮询 **477 ms → 0.52 ms**；服务复用校验 **22 ms**

### 3.3 新增/更新的测试文件

- `bot/tests/app/services/prompt-model-retry.test.ts`（新）
- `bot/tests/app/services/event-loop-monitor.test.ts`（新）
- `bot/tests/eot10-worker.test.ts`（新）
- `bot/tests/app/services/model-selection-service.test.ts`（+3 目录缓存 / +3 当前模型）
- `bot/tests/opencode/auto-restart.test.ts`（+2 托管等待）
- `bot/tests/opencode/local-start.test.ts`（+1 托管拒绝）
- `bot/tests/opencode/v2/mappers.test.ts`（+1 工具附件）
- `bot/tests/app/managers/summary-aggregation-manager.test.ts`（+1 工具图片）
- `desktop/tests/Test-ServiceStatus.ps1`（新，14 项）
- `desktop/tests/Test-TelegramWebContext.ps1`（V1 路由断言改为 V2-only）
- `desktop/tests/Test-FirstRunWizard.ps1`（启动期改为"立即显示概览页"）

---

## 4. macOS 移植检查表

- [ ] `Runtime.swift`：`OPENCODE_MANAGED_BY_DESKTOP=1`
- [ ] `AppModel.connect()`：复用检测（service.json + `/api/info`）→ 注册 → 后台 `service start` → 立即起 Bot → 校验/回滚
- [ ] 新增 `ServiceState.swift`：本地读 `run/bot-service.json`（pid 存活 + uptime + log 路径），替换点击式的 `bot status`
- [ ] 概览页状态不再 spawn `Runtime.bot(["status"])`
- [ ] `EmbeddedWeb.swift`：0.5 s 刷新改为"文件签名未变且 <3 s 直接返回"，`didFinish` 清签名
- [ ] WKWebView 只创建一次（缓存或保持挂载）
- [ ] `AppModel.cleanup()`：Bot 停止与 `stopWeb()` 并行，等待 ≤ 3 s
- [ ] 启动期间禁用启动/停止/刷新按钮
- [ ] 重新打包 bot（`Prepare-Runtime.sh` / `Build-App.sh`），确认 bundle 内含：
  - `prompt-model-retry.js`
  - `summary-aggregation-manager.js` 里的 `onToolImage`
  - `model-catalog-cache.js` 与 `event-loop-monitor.js`
  - `bot/EOT10CryptoWorker.mjs`
- [ ] 按 §3.1 跑一遍 bot 测试；macOS 桌面测试（`macos/Tests/...` + `Smoke-Check.sh`）按原流程跑
- [ ] 确认 `Build-App.sh` 复制的是最新 `desktop/EmbeddedWebSync.js`（§2.5 的网页修复）
- [ ] 手工验收见 §5

---

## 5. 手工验收

1. **启动**：安装后首次启动 → 概览页立即出现（不再禁用窗口），状态几秒内变 running；二次启动应显示"复用正在运行的 OpenCode V2 服务"且明显更快。
2. **模型菜单**：手机打开模型菜单 → 第一条是当前模型（带 ✅）；点"提供商"仍可见 OpenCode Go。
3. **瞬时模型故障**：断开/切换 VPN 后立刻发一条消息；若引擎报 `Model unavailable`，应看到"正在自动重试…"，随后正常回答；连不上时最后提示检查网络/VPN。
4. **图片**：让 OpenCode 生成/截图一张图 → 手机上应收到照片（EOT10 开启时为加密附件）。
5. **网页端**：多次在"概览/网页端"之间切换 → 不应反复重建浏览器；网页同步在空闲时约 2 秒一次。
6. **网页选项目**：在网页项目列表里选一个手机上没打开过的文件夹 → 页面应直接进入该项目的草稿（`/new-session?directory=…`），而不是停在列表；AI 回答期间也能在另一个项目里新建会话。
7. **DOCX**：从手机发一个 `.docx` 并附一句说明 → 项目目录下出现 `.opencode-telegram-attachments/` 存档，助手用自己的工具读取并复述内容。
8. **关闭**：点关闭 → 1–2 秒内退出，无残留 `GameViewer/opencode/node` 进程。

---

## 6. 已知问题与后续

- **引擎侧 TLS**：`opencode.ai/console/api/v2/config` 拉取失败是引擎行为，我们只能重试；若频繁出现，检查 VPN/代理是否做 TLS 拦截，或把该域名加入直连。
- **EOT10 图片渲染**：加密图片以 `.eotm` 文档到达，标准 Telegram 无法预览，需定制客户端支持。
- **macOS V1**：`bot` 已 V2-only，macOS 仍保留 V1 分支，建议单独清理。
- **Windows 版本号历史**：1.0.22 是安装基线，1.0.23/1.0.24 为后续；不要再按 `desktop/releases/` 目录最大编号推导下一个版本号，以 `desktop/artifacts/` 与已安装程序的版本为准。
