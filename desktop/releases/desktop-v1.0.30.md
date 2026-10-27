# OpenCode Telegram Desktop 1.0.30

Windows x64 桌面版，2026-10-27 构建。仅调整 DOCX/二进制附件的提示词，让 agent 明确读取工作区文件。

## 安装包

| 文件 | 大小 | SHA256 |
|---|---:|---|
| `OpenCodeTelegram-Setup-1.0.30.exe` | 688,103,936 B | `4053047A19AE62445F4E5FFC97CB96C08093E59BC3F6A6A93ECFBF7653B84864` |
| `OpenCodeTelegram-1.0.30-Windows-x64.zip` | 682,758,206 B | `4FEAE70D99349D551598A1CE684BD8EE0C3624B6C302C3EFD6AAF437DF42DB10` |

ZIP 内含 Setup 与 `README.md`。程序集版本：`1.0.30.0`。

## 改动

手机发来的 DOCX/二进制附件仍按 1.0.26 的约定保存到当前项目：

```text
.opencode-telegram-attachments/<时间戳>-<随机>-<原文件名>
```

但发给 agent 的提示词不再只是“文件存在”，而是明确要求读取：

```text
The user sent a file (<name>). Its original bytes are stored in the active project at:

`<relative path>`

Read this file with your tools before answering; for DOCX unzip word/document.xml if needed.
Do not ask the user to paste its contents.
```

- 路径用反引号包住，减少文件名含空格/括号时的 shell 拼接问题。
- 仍然不在 Bot 内解析文档、不注入文本、不重复发附件；agent 用自己的文件工具读取。
- 没有活动项目时仍回退为附件发送。
- 测试：`bot/tests/bot/handlers/document.test.ts` 更新，16 项通过。

## 验证

- Bot：**2437 passed / 7 skipped**；lint / typecheck / build 全过。
- 桌面登录分类 5 项、网页同步 25 项通过（沿用 1.0.28/1.0.29）。
- 程序集：`1.0.30.0`。
