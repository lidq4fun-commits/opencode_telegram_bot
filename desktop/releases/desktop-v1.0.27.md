# OpenCode Telegram Desktop 1.0.27

Windows x64 桌面版，2026-10-27 构建。

## 安装包

| 文件 | 大小 | SHA256 |
|---|---:|---|
| `OpenCodeTelegram-Setup-1.0.27.exe` | 688,090,624 B | `A543F19081D69E1EA75243E001E610EF798E0AD03825CB661713EEB2E2D44A0D` |
| `OpenCodeTelegram-1.0.27-Windows-x64.zip` | 682,751,302 B | `D7868333F9D0BEE3A7C9D41DAAE7173D6CC604567D694BD67CC89F4A1634BC5C` |

ZIP 内含 Setup 与 `README.md`。程序集版本：`1.0.27.0`。

## 新增

### `/connect`：在手机上连接模型提供商

- 从引擎读取集成列表；当前引擎识别到 **231 个可连接提供商**（230 个 API Key、7 个 OAuth）。
- 支持搜索提供商名称/ID，也匹配其关联提供商名（例如搜索 `go` 可找到用于 OpenCode Go 的 OpenCode Console）。
- OAuth：
  - 设备码流程（OpenCode Console、GitHub Copilot、xAI、OpenAI 设备码）可在手机上完成；
  - 浏览器回调流程显示登录链接；本机回调类会提示在电脑打开或粘贴回调链接/验证码；
  - 每 3 秒轮询状态，成功后自动编辑消息，失败/过期/取消同样提示。
- API Key：
  - 选择 Key 方式后发送 Key；Bot 读完 **立即删除该消息**，再写入引擎；
  - 保存失败可重试；成功提示 `/reload` 刷新模型。
- 已连接状态显示 ✅ 并排在列表前面；OpenCode Console 详情会显示 `Used by: OpenCode Go, Personal / OpenCode`。
- 暂不支持需要额外多字段表单的 Cloudflare AI Gateway。

### `/create_project`：创建文件夹并进入项目

- 与 `/open` 相同的无限制目录浏览。
- 浏览阶段有「❌ 取消创建」；选中父文件夹后输入名称。
- 校验非法字符、Windows 保留名、结尾点/空格、重名和超长名称。
- 创建文件夹后注册为项目并直接切换；失败时提示用 `/open` 重新选择。

### `/delete_session`、`/delete_project`

- `/delete_session`：删除当前项目的会话（二次确认）。
- `/delete_project`：从手机和电脑的项目列表隐藏项目，并删除其全部会话；**磁盘文件不删除**（二次确认）。
- 删除结果写入 `settings.json` 的 `hiddenProjects`，通过 `desktop-sync/state.json` 同步到桌面网页；桌面端会过滤已隐藏项目并自动刷新。
- 通过 `/open` 重新选择被隐藏的文件夹会恢复显示。

### `/open` 无限制

- 未配置 `OPEN_BROWSER_ROOTS` 时，Windows 列出全部盘符，macOS/Linux 从 `/` 开始；新插入的磁盘也会出现。
- 仍可通过 `OPEN_BROWSER_ROOTS` 显式限制。

## 验证

- Bot：`lint` / `typecheck` / `build` 通过；**2436 passed / 7 skipped**。
- 桌面 Web 同步：25 项通过。
- 程序集版本校验：`1.0.27.0`。
- Setup 内已包含新的 Bot 文件（`connect-command.js`、`create-project-command.js`、`delete-*`、`provider-connection-service.js`）和新的 `EmbeddedWebSync.js`。

## 安装

1. 运行 `OpenCodeTelegram-Setup-1.0.27.exe`。
2. 安装完成后重启一次桌面应用，让 Bot 加载新的 `bot/dist`。
3. 手机 Telegram 发送 `/connect`、`/create_project`、`/delete_session`、`/delete_project` 使用新功能。
