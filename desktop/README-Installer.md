# opencode_telegram_bot

OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。
An OpenCode installer and launcher with remote control via Telegram and encrypted communications.

向导和终端 `tg whoami` 共用当前 Windows 用户的 tg 默认配置与会话。登录后自动填入并锁定个人用户 ID，Bot 绑定和向导完成时再次检查账号一致；仅需手动提供 BotFather Token。应用启动的 OpenCode/Bot 子进程 PATH 也包含应用 `bin`，可直接找到打包的 tg。之后手动切换 tg 账号会改变 `whoami` 的结果，不会自动修改已保存的 Bot 访问授权。

首次打开会显示四步向导：Telegram CLI 登录（交互窗口扫码，继续时检查登录）→ Bot Token 与个人用户 ID → 可选 EOT10 加密 → 自动扫描并选择 OpenCode V2。内置引擎的选择按钮为蓝色。每步保存后可继续，未完成的向导下次打开会恢复；完成后自动启动 Bot/OpenCode 并进入内嵌网页。之后每次打开软件都会启动服务，以 OpenCode 网页端为首页；启动失败显示控制中心和错误提示。Windows 登录自启动仍由启动设置独立控制。旧安装升级后也会显示一次向导，保留已有设置和 tg 登录。

The Windows installer also bundles the official [gotd/cli v0.11.0 Windows x64 release](https://github.com/gotd/cli/releases/download/v0.11.0/tg_0.11.0_windows_amd64.tar.gz), verified against its release SHA256. Setup installs `tg.exe` into the app's `bin` directory and adds that directory to user PATH (open a new terminal to use `tg`). Its license is included in `dependencies/tg`. Telegram CLI login is separate: no credentials or sessions are bundled, initialized, or removed by setup.

Supported target: Windows 10 version 1809 or later / Windows 11, x64, with .NET Framework 4.6.2 or later and WPF. The installer includes the Bot, Node/npm, an OpenCode V2 baseline engine, Bun baseline, Portable Git and a fixed WebView2 runtime. V1 and its SDK are excluded. Core runtime setup does not require downloads.

## Install

1. Run `OpenCodeTelegram-Setup-<version>.exe` and choose an installation folder. The setup EXE is self-contained; it does not need Node.js, npm, or a neighboring dependency folder. Setup only installs the app, creates Start menu and desktop shortcuts, and registers an uninstall entry in Windows Apps & Features. The completion page lets you check **Launch opencode_telegram_bot now** or click **Finish** to exit without launching it.
2. Later, open **opencode_telegram_bot** from its desktop or Start menu shortcut, configure your Telegram Bot and OpenCode API version, then turn on the Bot from the Overview page.
3. Configure Windows sign-in startup later from **Startup & CLI** in the app if desired.

The **Startup & CLI** page runs the desktop application's own `opencode-telegram` management CLI. It covers Bot start/stop/status, DPAPI-protected settings, OpenCode engine scan/install/select/uninstall, Bot and desktop autostart, language, recent logs, and web-service start/stop/status/open. For scripts and AI integrations, run `opencode-telegram help`; use `config set <key> --env <NAME>` for secrets so they are not included in command-line history. The **OpenCode Web** page can show the service embedded or open it in the default browser.

When the Bot service starts, the app stops all local `opencode` processes first, then starts the OpenCode CLI matching the selected API version. For V2, it writes the DPAPI-generated password into the selected CLI's managed service configuration, starts that service through `opencode service start`, discovers the URL returned by the running service, and verifies authenticated `/api/info` before launching the Bot. The Bot and Web page use that discovered URL and the same service-managed password. Install the selected API version from **OpenCode Engine** before starting.

The **OpenCode Engine** page automatically scans V2 installations and offers **Scan** and **Update OpenCode** buttons. Bundled engines have a blue **Bundled** badge; **Apply** is gray, **Applied** is green, and **Uninstall** is red. Scanning checks running processes, PATH, npm/Bun globals, Windows installation records and managed engine folders. Selecting an engine restarts the local service through that executable and updates the stable `bin/opencode.cmd` entry point, so terminals using that entry point follow future switches. Open a new terminal after initially adding the app's bin directory to user PATH. A machine-level PATH entry or shell alias can override user PATH and must be removed/reordered separately. Bundled and app-managed engines can be uninstalled individually; external installations open Windows installed-app settings and must be removed with their original installer/package manager. Controls and feedback support Chinese and English.

The installer embeds the complete core runtime payload. It creates `Uninstall.exe` and registers its interactive and quiet uninstall commands under the current user's Windows uninstall registry key. Installation selects a compatible V2 engine without downloading when the offline payload is present. Updating checks the currently applied executable against the official stable V2 npm release, downloads a pinned version into a separate managed folder with visible npm progress, verifies its version, and applies it. Failed downloads leave the applied version unchanged; application failures restore the previous selection. Upgrading removes the obsolete bundled V1 engine and V1 SDK from the app installation, without deleting external V1 installations or user configuration.

Existing Bun >=1.3 and Git >=2.30 are preferred when their version probes succeed; otherwise the app-local copies are used. Existing Node >=24 is reused only when it is x64, matches the bundled native ABI and can actually load the packaged SQLite module. The installed evergreen WebView2 runtime is preferred; when none is installed the fixed app-local runtime is used. No system Node/Git/Bun installation is replaced, and no machine PATH or registry configuration is changed for these tools.

Telegram messaging, cloud model APIs, provider authorization, updates and task-specific downloads still require internet access. Local model weights, optional OpenCode plugins, external language servers, project packages and tools requested by model tasks are not included. This is an offline core-dependency installation package, not a guarantee that every AI workflow can run offline. The .NET/WPF runtime remains an operating-system prerequisite.

The release ZIP contains the standalone setup EXE and this README. The EXE may be copied or run by itself.

## Encryption / 加密

Encryption is disabled by default. EOT10 requires the specially customized encrypted Telegram Android client; the standard Telegram app cannot encrypt or decrypt EOT10 messages. Configure the same password in the Bot and customized client before enabling it. Existing saved encryption choices are preserved.

加密默认关闭。启用 EOT10 必须使用专门定制的加密 Telegram 安卓客户端，普通 Telegram 无法加密或解密 EOT10 消息。开启前，请在 Bot 和定制客户端中设置相同密码。已有用户保存的加密开关不会被强制改变。

[Customized Android client / 定制安卓端 GitHub 下载（v11.0.22）](https://github.com/lidq4fun-commits/opencode_telegram_bot/releases/tag/desktop-1.0.15-android-11.0.22) — select `OpenCodeTelegram-Android-v11.0.22-arm64-debug.apk`.

## Package contents

- `OpenCodeTelegram-Setup-<version>.exe` — Windows installer.
- `README.md` — these instructions.
- Bundled inside setup: Bot, production dependencies, Node/npm, OpenCode V2, Bun, Git, WebView2 SDK and fixed runtime, dependency licenses, source metadata and SHA256 file inventory.
## Desktop build dependencies

内嵌网页优先使用已安装的 Edge WebView2 Runtime；未安装时使用包内固定运行时，不再要求另行下载。

编译前从 NuGet 官方源获取 `Microsoft.Web.WebView2` `1.0.4258.31`，运行：

```powershell
.\desktop\Restore-WebView2.ps1 -PackagePath <下载的.nupkg路径>
```

此命令只提取 SDK 到被 Git 忽略的 `desktop/dependencies/webview2`，不会构建安装包。打包脚本会携带 Core、WPF 和 x64 Loader DLL。

`desktop/OfflineDependencies.json` records pinned official archive URLs and verification information. Download those archives with visible progress into `desktop/dependencies/archives`, save the official OpenCode and Bun license files under `desktop/dependencies/offline/licenses`, then run `desktop/Restore-OfflineDependencies.ps1`. Git/Bun/npm archives are checked against publisher digests; the extracted WebView2 executable must have a valid Microsoft signature. The build refuses an incomplete core payload. Binary dependencies and download artifacts remain Git-ignored; Git tracks the restore/build scripts, versions and tests.

“启动设置”与“Bot CLI”是独立页面。Bot CLI 支持回车执行、上下键历史、交互隐藏输入和停止当前命令；页面列出实际支持的命令与配置键。网页页面进入后自动连接远程服务或启动本机服务，不需要再点击启动。

内嵌网页与手机默认双向强制同步项目、会话、模型和思考程度，不再提供跟随方式选项或设置卡片。任一端选择项目、会话或模型后，另一端采用同一状态；电脑发起任务前先确认 Bot 已跟随该原生会话，随后共享任务和回答的实时历史。Telegram 的 `/projects` 等管理命令不属于模型对话，不复制到网页。

空闲时可打开左上角项目列表、浏览和选择项目，不会被定时轮询拉回旧聊天。手机有新操作或 AI 正在回答时才强制回到共享界面，并显示跟随或阻止切换的原因；回答结束后自动恢复项目与会话切换。模型同步保留已持久化的电脑草稿；强制切换到不同会话时应先保存尚未发送的输入。

同步元数据与操作回执位于应用数据目录的 `desktop-sync`，仅通过本机文件通信，不新增端口，也不在该通道传递提示词、Token 或密码。Bot 是设置文件的唯一写入者；旧操作、旧 Bot 进程和手机操作期间的冲突操作会被拒绝。Bot 未启动或状态过期时保留浏览页面，并提示重新连接，电脑不会在未确认同步时发起模型任务。外部默认浏览器仍只是同一服务的独立浏览入口，不在本次双向同步范围内。

本机 V2 的“在默认浏览器打开”和 CLI `web open` 使用 OpenCode 原生配对会话 Cookie，刷新后无需重新登录，并保留原来的项目、会话和页面位置。一次性本机跳转只监听回环地址，成功后立即停止，未完成则最多等待 60 秒；Cookie 保留原生 HttpOnly 等标记，密码和会话 Cookie 不写入应用文件，也不放进最终页面 URL。远程服务和 V1 沿用原有入口，不改变其认证方式。

桌面会话链接与状态读取的回归检查（仅使用隔离的临时数据，不启动模型任务）：

```powershell
.\desktop\tests\Test-TelegramWebContext.ps1 -AssemblyPath <新源码编译的OpenCodeTelegram.exe路径>
.\desktop\tests\Test-BrowserLogin.ps1 -AssemblyPath <新源码编译的OpenCodeTelegram.exe路径>
.\desktop\tests\Test-OfflineDependencies.ps1 -AssemblyPath <完整暂存包中的OpenCodeTelegram.exe路径>
node --test .\desktop\tests\Test-EmbeddedWebSync.mjs
```
## 第一个可用版本 / First usable release

**1.0.15** 是用户确认的第一个可用桌面版本，Git 基线标签为 `desktop-v1.0.15`。发布内容、验证范围与安装包 SHA256 见 [版本说明](releases/desktop-v1.0.15.md)。后续开发以此版本为可用基线；安装包保存在 `desktop/artifacts/`，不纳入 Git。
