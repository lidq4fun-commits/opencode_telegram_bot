# OpenCode Telegram Desktop 1.0.29

Windows x64 桌面版，2026-10-27 构建。包含 1.0.28 的 Telegram 登录检查修复，并改进 `/connect` 的登录方式选择。

## 安装包

| 文件 | 大小 | SHA256 |
|---|---:|---|
| `OpenCodeTelegram-Setup-1.0.29.exe` | 688,103,936 B | `1F994E36F47595FFF00495258D5D215201C309C124BDAC9EE0BBDC337E5FB0FD` |
| `OpenCodeTelegram-1.0.29-Windows-x64.zip` | 682,758,211 B | `5CE152FD11E9D75D52F2079B893AB4E4C31DB87F95CE10A674531A2131C0000E` |

ZIP 内含 Setup 与 `README.md`。程序集版本：`1.0.29.0`。

## `/connect` 登录方式改进

- 登录方式按 **手机可用** 优先排序：
  - **📱** 设备码流程，可在手机完成；
  - **🖥️** 浏览器回调流程，回调地址是电脑的 `localhost`，手机无法完成。
- 提供商的登录方式页面会显示说明：
  - 有手机方式：`📱 手机可直接完成 · 🖥️ 需要电脑浏览器完成（localhost 回调）`
  - 只有浏览器方式：提示该提供商需要在电脑浏览器完成。
- OpenAI 现在默认把 **📱 Codex device code** 排在第一位；`Sign in with ChatGPT` 与 `Codex browser` 属于 🖥️ 方式。
- 手机浏览器如果出现 `127.0.0.1:端口 拒绝连接`，这是正常的 localhost 回调限制：
  - 改用带 📱 的方式；或
  - 在电脑上打开同一登录链接完成；或
  - 复制登录后地址栏的完整回调链接（包含 `code=`），回到 Telegram 点「📋 粘贴验证码 / 链接」。

## 包含 1.0.28 的修复

- Telegram 登录检查区分 **网络不通**、**未登录**、**未知错误** 和 **真正的账号不一致**。
- 网络/未登录时 Bot 继续运行，显示明确警告，每 15 秒自动重试；控制中心有「重试验证」按钮。
- 只有真正登录了不同账号才停止服务。
- 关闭 EOT10 时不再检查 `tg` 登录。

## 验证

- 程序集：`1.0.29.0`。
- Telegram 登录分类测试：5 项全过。
- OpenAI 方法排序实测：`chatgpt-headless (📱)` → `key` → `chatgpt-token-sharing (🖥️)` → `chatgpt-browser (🖥️)`。
- Bot：**2437 passed / 7 skipped**；lint / typecheck / build 全过。
