# OpenCode Telegram Desktop 1.0.31

Windows x64 桌面版，2026-10-27 构建。EPUB 也改为工作区文件交付，纯文本模型可通过工具读取。

## 安装包

| 文件 | 大小 | SHA256 |
|---|---:|---|
| `OpenCodeTelegram-Setup-1.0.31.exe` | 688,103,936 B | `0ECB63A9D870758DA58AAAEBAB1A666B7B89132BFAD8AB79A5836A8D33F4ED88` |
| `OpenCodeTelegram-1.0.31-Windows-x64.zip` | 682,758,279 B | `AB4DC6B1A2891AC6A9D1D4A2A1A06F9402B1BA2128798AFD31FDA9D28E70C72B` |

ZIP 内含 Setup 与 `README.md`。程序集版本：`1.0.31.0`。

## 改动

EPUB（`application/epub+zip`）不再作为 file part 发送，而是和 DOCX 一样保存到当前项目：

```text
.opencode-telegram-attachments/<时间戳>-<随机>-<原文件名>
```

提示词明确要求 agent 读取：

```text
The user sent a file (<name>). Its original bytes are stored in the active project at:

`<relative path>`

Read this file with your tools before answering; unzip DOCX/EPUB archives if needed.
Do not ask the user to paste its contents.
```

- `.epub` 扩展名也会识别为 EPUB，即使 Telegram 报 `application/octet-stream`。
- EPUB 的正文在 XHTML 文件里，agent 可用 unzip/文件工具读取；图片等资源保留在压缩包内。
- DOCX、EPUB、未知二进制走工作区文件；PDF/Office/媒体仍保持原有 file part 行为。
- 测试：`bot/tests/bot/handlers/document.test.ts` **17 项通过**。

## 验证

- Bot：**2438 passed / 7 skipped**；lint / typecheck / build 全过。
- 程序集：`1.0.31.0`。
