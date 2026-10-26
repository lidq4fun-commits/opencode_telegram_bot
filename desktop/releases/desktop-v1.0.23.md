# Desktop 1.0.23 / 桌面版 1.0.23

Version note: the previously installed release is 1.0.22; this build continues from it. Internal
builds numbered 1.0.19–1.0.21 were superseded and are not published.

版本说明：此前安装的版本是 1.0.22，本版本在其之后；1.0.19–1.0.21 为内部构建，已作废，不再发布。

## Model picker / 模型选择

- The selected model is always the first favorite, so a pick made in Telegram appears in the model menu (a Telegram pick is not written to OpenCode's own state).
- 选中的模型始终是第一个收藏项，在 Telegram 里选过的模型会出现在模型菜单里（Telegram 的选择不会写进 OpenCode 自己的状态文件）。

## Startup and performance / 启动与性能

- Dashboard status is read from the daemon state file and the process table instead of starting a Node process every two seconds (measured 477 ms → 0.52 ms), and the log panel reads only the changed 64 KB tail.
- 概览页状态改为读守护进程状态文件与进程表，不再每 2 秒启动 Node 进程（实测 477 ms → 0.52 ms）；日志只读变化后的末尾 64 KB。
- A running, authenticated OpenCode V2 service is reused at launch (about 22 ms) instead of being killed and restarted, so the model catalog keeps its warm state.
- 启动时复用已在运行且通过鉴权的 OpenCode V2 服务（约 22 ms），不再杀掉重启，模型目录保持预热状态。
- Cold start overlaps: the engine boots in the background while the Bot performs its own Telegram startup; `OPENCODE_MANAGED_BY_DESKTOP=1` keeps a managed Bot from spawning a competing engine.
- 冷启动并行：引擎在后台启动的同时 Bot 完成自己的 Telegram 启动；`OPENCODE_MANAGED_BY_DESKTOP=1` 确保托管模式下 Bot 不会另起引擎。
- `getMe` runs beside webhook removal and the stored-model check runs beside Bot assembly.
- `getMe` 与移除 webhook 并行；存储模型校验与 Bot 组装并行。
- The last complete provider catalog is cached next to the settings and served for 30 seconds after a restart while a real refresh runs in the background.
- 上次完整的 provider 目录缓存到设置旁边，重启后先服务 30 秒并在后台真正刷新。
- The window is usable immediately: the dashboard appears at once, startup progress shows in the status line, and the service buttons stay disabled until startup finishes.
- 窗口立即可用：直接显示概览页，启动进度写在状态行，启动完成前服务按钮保持禁用。
- The WebView2 environment is prewarmed while the services start; the web page and browser are created once per run and sync state is injected only when it changes.
- WebView2 环境在服务启动期间预热；网页和浏览器每次运行只创建一次，同步状态仅在变化时注入。
- Bot-side desktop sync state is written on change with a 2-second heartbeat; shutdown stops the Bot and the engine in parallel with shorter waits.
- Bot 侧桌面同步状态改为变化时写入 + 2 秒心跳；关闭时并行停止 Bot 与引擎并缩短等待。
- Event loop delay is monitored (`[Perf]` debug line every minute, warning above 150 ms p95); the EOT10 session key is derived on the libuv pool and attachments above 2 MiB are encrypted/decrypted in a worker thread.
- 新增事件循环延迟监控（每分钟一条 `[Perf]` 调试日志，p95 超过 150 ms 告警）；EOT10 会话密钥在 libuv 线程池派生，超过 2 MiB 的附件由 worker 线程加解密。
- The desktop source is split into `desktop/src/*.cs` (Contracts, ProductPaths, Settings, BotRuntime, Cli, DesktopApp, InstallerWindow, Program); the package script compiles that folder.
- 桌面源码拆分为 `desktop/src/*.cs`（Contracts、ProductPaths、Settings、BotRuntime、Cli、DesktopApp、InstallerWindow、Program），打包脚本按目录编译。

## Fixes / 修复

- The applied engine always shows the green, disabled "Applied" button, including when the applied engine is the bundled one; blue stays the bundled hint and gray the plain "Apply" action.
- 已应用的引擎始终显示绿色且不可点击的“已应用”，包括应用内置引擎的情况；蓝色仅表示内置引擎提示，灰色表示可点击的“应用”。
- Desktop tests describe the V2-only web route again (the removed V1 route assertions were replaced), so the desktop suite has no failing checks.
- 桌面测试重新描述 V2-only 的网页路由（替换了已删除的 V1 断言），桌面测试不再有失败项。

## Verification / 验证

- Bot: build, lint, typecheck and the full suite passed (2378 passed, 7 skipped).
- Desktop: package build passed; service status and reuse (14 assertions), browser login (12), encryption settings (19), first-run wizard (46), Telegram web context (22) and OpenCode engine selection (23) passed, with no failures.
- Measured: dashboard status 0.52 ms per call (was 477 ms); service reuse check 22 ms.
- Setup: `desktop/artifacts/OpenCodeTelegram-Setup-1.0.23.exe` (688,047,616 bytes; compiled app reports 1.0.23).
- Setup SHA256: `E6436AE7F388C143352B87030E66ECD50FA2F175C43153EDC793680187AE9ACB`.
- Release ZIP: `desktop/artifacts/OpenCodeTelegram-1.0.23-Windows-x64.zip` (682,709,768 bytes).
- Release ZIP SHA256: `243C3D68CA948FDB9FFBB07D2A0101D928DBB59450128FED2AC16D8AF184C02D`.
