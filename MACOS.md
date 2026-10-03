# OpenCodeTelegram for macOS — 第一个可用版本

**用户验收基线：macOS 0.1.20，2026-10-03。**

Git 标签：`macos-0.1.20-first-usable`。本标签对应用户确认满意的第一个可用 macOS 版本，不改变现有 Windows/Android 版本编号和发布。

## 安装

在 [GitHub Releases](https://github.com/lidq4fun-commits/opencode_telegram_bot/releases/tag/macos-0.1.20-first-usable) 下载
`OpenCodeTelegram-0.1.20-binding-guides-20261003-macOS-arm64.dmg`。

要求：Apple Silicon（arm64），macOS 14 或更高。**本地 ad-hoc 签名，未经 Apple 公证。**

打开 DMG，双击 `OpenCodeTelegram.app`，在安装器中选择目录。不要只拖出 `.app`；安装器会同时安装离线依赖：

```text
安装目录/
├── OpenCodeTelegram.app
└── OpenCodeTelegram-dependencies/
    ├── runtime/             # Node.js
    ├── bot/                 # Bot、原生 SQLite 模块、资源
    └── dependencies/        # OpenCode V2、Bun、Git、许可证和 Git 对应源码
```

可更新的运行依赖不放入 `.app`，也不再复制到 Library。用户配置、数据库和登录凭据保持独立；旧版 Library 引擎不会自动删除，新版不再默认使用旧路径。

## 功能与使用

- **V2-only**：随附 OpenCode `2.0.22`，不支持 V1。
- 「机器人绑定」包含获取 Bot Token 和个人 User ID 的详细步骤，操作区在上，说明在下。
- OpenCode 网页端位于侧栏概览下方；未登录模型提供商时，Bot 引导到电脑网页端运行 `/connect`。
- 应用引擎后同步软件内 CLI 和 zsh PATH。已打开的终端需重新打开或执行 `source ~/.zshrc`；shell alias/function 可能覆盖 PATH，应自行检查 `type -a opencode`。
- 高级设置中的「其他依赖更新」默认折叠，支持 Node/Bun 检查更新、校验、进程占用检查和失败回滚。Node 保持当前主版本并验证 SQLite 兼容性。
- 加密默认关闭；开启 EOT10 必须配合兼容的特殊加密版 Telegram，官方客户端不支持。页面提供 [Android v11.0.22 发布页](https://github.com/lidq4fun-commits/opencode_telegram_bot/releases/tag/desktop-1.0.15-android-11.0.22)、对应源码和限制说明。

## 源码构建

Swift/macOS 源码：`native-swift/Sources`；Bot 源码及测试：`native-swift/dependencies/payload/bot`。
详细构建和验证步骤见 [docs/macos-build.md](docs/macos-build.md)。Git 自动生成的本标签源码包包含这两个组件的源码，不含本地凭据、缓存及预编译运行环境。

Bot 原有许可证位于 `native-swift/dependencies/payload/bot/LICENSE`；第三方依赖保留各自许可证，Git 对应源码随安装包提供。Windows/Android 对应源码仍见原发布附件。

## 验证范围

- Bot 完整测试：196 文件通过，2398 项通过，7 项 Windows 条件跳过；TypeScript 构建、lint、typecheck 通过。
- 最新原生测试：50 项，5 项环境依赖测试跳过，零失败。
- 离线运行依赖、原生 SQLite、安装复制签名及 LaunchServices 启动检查通过。
- Node/Bun 官方更新包下载、校验和 Node SQLite 兼容性专项测试通过，未替换交付包内的固定依赖版本。
- 没有使用用户凭据进行真实 Telegram Bot/模型请求/OAuth 验证；「第一个可用版本」是用户验收里程碑，不代表所有平台功能已完整验收。
