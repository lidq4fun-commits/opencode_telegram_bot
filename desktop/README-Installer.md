# opencode_telegram_bot

OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。
An OpenCode installer and launcher with remote control via Telegram and encrypted communications.

## Install

1. Extract the complete ZIP to a folder. Keep the `Dependencies` folder next to the setup file.
2. Run `OpenCodeTelegram-Setup-<version>.exe` and choose an installation folder. Setup only installs the app, creates Start menu and desktop shortcuts, and registers an uninstall entry in Windows Apps & Features. The completion page lets you check **Launch opencode_telegram_bot now** or click **Finish** to exit without launching it.
3. Later, open **opencode_telegram_bot** from its desktop or Start menu shortcut, configure your Telegram Bot and OpenCode API version, then turn on the Bot from the Overview page.
4. Configure Windows sign-in startup later from **Startup & CLI** in the app if desired.

When the Bot service starts, the app stops all local `opencode` processes first, then starts the OpenCode CLI matching the selected API version. Install the selected API version from **OpenCode Engine** before starting.

The **OpenCode Engine** page keeps the API version selector up front; connection details, scanning, installation, and other options are under the collapsed **Advanced settings** section. Scanning checks process executables, process/user/system `PATH`, npm and Bun global package locations, common Windows install locations, Windows app-path/uninstall registry entries, and the app's own engine folders. It lists every detected OpenCode 1.x/2.x installation so the exact CLI/API pairing can be selected. V2 installation clears any previously saved server password and leaves it blank by default. The app uses its bundled Node.js; if that runtime is missing it tries a user-scope WinGet install. V2 installation also checks for Bun and installs it with WinGet at user scope if needed. A server password is optional and should only be entered when authentication is enabled on that server. Uninstall completely stops the app-managed engine process tree and removes that API's entire app-managed installation folder; it deliberately leaves global OpenCode installations alone. Installation shows an in-page progress message and a success or failure dialog with the detected CLI version. If the selected CLI is missing or has the wrong API version, starting the Bot opens this page and explains which install button to use.

The installer creates `Uninstall.exe` and registers its interactive and quiet uninstall commands under the current user's Windows uninstall registry key, so Windows Apps & Features and tools such as Geek Uninstaller can discover the app. The `Dependencies\BotRuntime.zip` file contains the standalone Bot, its production Node dependencies, and the bundled Node/npm runtime. The setup program extracts this local payload; installation does not require npm or an internet connection for the Bot runtime. The API v1 button installs `opencode-ai@^1`; the API v2 button installs `@opencode/cli@^2`. They are installed into separate directories so both CLI series can coexist. Installing an OpenCode CLI requires internet access.

Do not run setup from inside the ZIP preview. Extract all files first so the setup program can find `Dependencies\BotRuntime.zip`.

## Package contents

- `OpenCodeTelegram-Setup-<version>.exe` — Windows installer.
- `README.md` — these instructions.
- `Dependencies\BotRuntime.zip` — bundled Bot and runtime dependencies.
