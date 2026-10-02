# opencode_telegram_bot

OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。
An OpenCode installer and launcher with remote control via Telegram and encrypted communications.

Supported target: 64-bit Windows 10/11. The installer includes Node.js/npm for the Bot; Windows supplies the .NET Framework/WPF desktop runtime. V2 local-engine setup downloads Bun on demand when it is absent.

## Install

1. Run `OpenCodeTelegram-Setup-<version>.exe` and choose an installation folder. The setup EXE is self-contained; it does not need Node.js, npm, or a neighboring dependency folder. Setup only installs the app, creates Start menu and desktop shortcuts, and registers an uninstall entry in Windows Apps & Features. The completion page lets you check **Launch opencode_telegram_bot now** or click **Finish** to exit without launching it.
2. Later, open **opencode_telegram_bot** from its desktop or Start menu shortcut, configure your Telegram Bot and OpenCode API version, then turn on the Bot from the Overview page.
3. Configure Windows sign-in startup later from **Startup & CLI** in the app if desired.

The **Startup & CLI** page runs the desktop application's own `opencode-telegram` management CLI. It covers Bot start/stop/status, DPAPI-protected settings, OpenCode engine scan/install/select/uninstall, Bot and desktop autostart, language, recent logs, and web-service start/stop/status/open. For scripts and AI integrations, run `opencode-telegram help`; use `config set <key> --env <NAME>` for secrets so they are not included in command-line history. The **OpenCode Web** page can show the service embedded or open it in the default browser.

When the Bot service starts, the app stops all local `opencode` processes first, then starts the OpenCode CLI matching the selected API version. For V2, it writes the DPAPI-generated password into the selected CLI's managed service configuration, starts that service through `opencode service start`, discovers the URL returned by the running service, and verifies authenticated `/api/info` before launching the Bot. The Bot and Web page use that discovered URL and the same service-managed password. Install the selected API version from **OpenCode Engine** before starting.

The **OpenCode Engine** page keeps the API version selector up front; connection details, scanning, installation, and other options are under the collapsed **Advanced settings** section. Scanning checks process executables, process/user/system `PATH`, npm and Bun global package locations, common Windows install locations, Windows app-path/uninstall registry entries, and the app's own engine folders. It lists every detected OpenCode 1.x/2.x installation so the exact CLI/API pairing can be selected. The app uses its bundled Node.js; if that runtime is unexpectedly missing it tries a user-scope WinGet install. Missing Bun is downloaded from the official release and SHA-256 verified, with WinGet as fallback. V2 requires a server password: local services get a generated DPAPI-protected password, while existing servers require their password in the connection settings. Uninstall completely stops the app-managed engine process tree and removes that API's entire app-managed installation folder; it deliberately leaves global OpenCode installations alone. Installation shows an in-page progress message and a success or failure dialog with the detected CLI version. If the selected CLI is missing or has the wrong API version, starting the Bot opens this page and explains which install button to use.

The installer embeds the standalone Bot, its production Node dependencies, and the bundled Node/npm runtime; installing does not require a preinstalled Node/npm or internet access for the Bot runtime. It creates `Uninstall.exe` and registers its interactive and quiet uninstall commands under the current user's Windows uninstall registry key, so Windows Apps & Features and tools such as Geek Uninstaller can discover the app. The API v1 button installs `opencode-ai@^1`; the API v2 button installs `@opencode/cli@^2`. They are installed into separate directories so both CLI series can coexist. Installing an OpenCode CLI requires internet access.

V2 local-engine installation also needs Bun. When Bun is missing, the app downloads the current Windows x64 release from the official Bun GitHub release, verifies its SHA-256 digest, and installs it under the current user's app-data folder. WinGet is a fallback if the official download is unavailable. Bun requires Windows 10 version 1809 or later. Local engine installation requires internet access; if internet access is unavailable, connect to an existing OpenCode server instead.

The release ZIP contains the standalone setup EXE and this README. The EXE may be copied or run by itself.

## Package contents

- `OpenCodeTelegram-Setup-<version>.exe` — Windows installer.
- `README.md` — these instructions.
- Bundled inside setup: Bot, production dependencies, and Node/npm runtime.
# 桌面开发依赖（不打包）

内嵌 OpenCode 网页使用 Edge WebView2，不再使用 IE 内核。运行机器需要 Microsoft Edge WebView2 Runtime；缺失时网页页面会提示原因，仍可使用默认浏览器。

编译前从 NuGet 官方源获取 `Microsoft.Web.WebView2` `1.0.4258.31`，运行：

```powershell
.\desktop\Restore-WebView2.ps1 -PackagePath <下载的.nupkg路径>
```

此命令只提取 SDK 到被 Git 忽略的 `desktop/dependencies/webview2`，不会构建安装包。打包脚本会携带 Core、WPF 和 x64 Loader DLL。

“启动设置”与“Bot CLI”是独立页面。Bot CLI 支持回车执行、上下键历史、交互隐藏输入和停止当前命令；页面列出实际支持的命令与配置键。网页页面进入后自动连接远程服务或启动本机服务，不需要再点击启动。

网页页面从 Bot 的 `settings.json` 读取当前项目与会话。打开同一个 OpenCode 原生会话后，Telegram 发给模型的任务、模型回答与电脑输入共享一份会话历史，实时更新。Telegram 的 `/projects` 等 Bot 管理命令不属于模型对话，不复制到网页。

页面提供三种跟随方式，选择后立即生效，并通过 Windows DPAPI 保存，重启后仍保留：

| 方式 | 作用 |
| --- | --- |
| 不跟随 | 独立浏览。进入时打开本次桌面运行期间上次浏览的页面，没有则打开首页；Telegram 切换不影响网页，CLI `web open` 打开独立首页。 |
| 提示跟随（默认） | 进入时打开 Telegram 会话；之后切换只提示，点击“跟随”才跳转。未点击前保留电脑输入。 |
| 强制跟随 | 始终保持 Telegram 当前会话，自动跟随切换，手动浏览其他会话也会被拉回。切换可能丢失电脑尚未发送的输入。 |

选项说明以并排卡片展示，可点击“收起选项”给对话留出更多空间，会话信息和切换提示仍保留。未选择 Telegram 会话时，不会悄悄创建新会话或强制清空现有网页。网页内手动选择其他会话不会修改 Bot 的选择。

本机 V2 的“在默认浏览器打开”和 CLI `web open` 使用 OpenCode 原生配对会话 Cookie，刷新后无需重新登录，并保留原来的项目、会话和页面位置。一次性本机跳转只监听回环地址，成功后立即停止，未完成则最多等待 60 秒；Cookie 保留原生 HttpOnly 等标记，密码和会话 Cookie 不写入应用文件，也不放进最终页面 URL。远程服务和 V1 沿用原有入口，不改变其认证方式。

桌面会话链接与状态读取的回归检查（仅使用隔离的临时数据，不启动模型任务）：

```powershell
.\desktop\tests\Test-TelegramWebContext.ps1 -AssemblyPath <新源码编译的OpenCodeTelegram.exe路径>
.\desktop\tests\Test-BrowserLogin.ps1 -AssemblyPath <新源码编译的OpenCodeTelegram.exe路径>
```
