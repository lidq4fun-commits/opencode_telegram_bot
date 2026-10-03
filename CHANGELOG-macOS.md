# macOS 版本记录

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
