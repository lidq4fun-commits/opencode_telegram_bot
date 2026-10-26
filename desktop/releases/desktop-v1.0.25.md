# Desktop 1.0.25 / 桌面版 1.0.25

## Fixes / 修复

- Selecting a project in the embedded web page no longer leaves the page stuck on the project
  list. This affected folders the phone had never opened: the shared state had no session, so
  the sync script kept pulling the page back to the server root, and creating a session was
  blocked while an answer was running.
- 修复网页端选择项目后卡在项目列表的问题（尤其是手机上没打开过的文件夹）：此时共享状态没有会话，
  同步脚本会把页面反复拉回根目录，且 AI 回答期间无法新建会话。
- A selected project now opens its draft directly (`/new-session?directory=…`), even before the
  Bot has adopted the folder; polling never pulls the page back while there is no shared session.
- 选中的项目会直接进入草稿（`/new-session?directory=…`），即使 Bot 还没采纳该目录；没有共享会话时
  轮询不会再把页面拉回列表。
- Session creation while the shared session answers is now blocked only for the **busy project**;
  another project can be prepared at the same time. Phone activity still blocks it.
- AI 回答期间只拦截**当前项目**的新建会话；另一个项目可以同时准备，手机正在操作时仍然拦截。
- Contains every 1.0.24 fix (automatic retry for `Model unavailable`, tool-generated images) and
  the 1.0.23 startup and performance work.
- 包含 1.0.24 的全部修复（`Model unavailable` 自动重试、工具图片转发）与 1.0.23 的启动/性能改动。

## Verification / 验证

- `desktop/tests/Test-EmbeddedWebSync.mjs`: 22 assertions passed, including four new ones for the
  project-selection flow, the busy-project guard and draft navigation.
- Desktop suites unchanged (service status/reuse 14, browser login 12, encryption 19, wizard 46,
  web context 22, engine selection 23) and still pass on the packaged app.
- Bot unchanged since 1.0.24 (2387 passed, 7 skipped).
- Setup: `desktop/artifacts/OpenCodeTelegram-Setup-1.0.25.exe` (688,055,296 bytes; compiled app reports 1.0.25).
- Setup SHA256: `9BA75E1444CBB0D297023314FE1596BBCC39A26B6F13AB76EC174A87F0B9CFEF`.
- Release ZIP: `desktop/artifacts/OpenCodeTelegram-1.0.25-Windows-x64.zip` (682,718,338 bytes).
- Release ZIP SHA256: `83A1F469BD1D487B61DC6AD426C23A3441E528A501AD027B3BF363EB0FBEA533`.
