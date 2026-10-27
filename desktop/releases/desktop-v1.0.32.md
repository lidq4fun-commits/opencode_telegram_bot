# OpenCode Telegram Desktop 1.0.32

Windows x64 桌面版，2026-10-27 构建。所有非媒体文档/压缩包统一按工作区文件交付。

## 安装包

| 文件 | 大小 | SHA256 |
|---|---:|---|
| `OpenCodeTelegram-Setup-1.0.32.exe` | 688,103,936 B | `357EE6D94014A67D58E6E68E2F3692B0AAFE8E0B4EC5FBBEE1BAC0C0F4E330F6` |
| `OpenCodeTelegram-1.0.32-Windows-x64.zip` | 682,758,305 B | `F1EB46CCE5B8ADD4C726734D72ED4DD6189D8A48CB5C67B4358B017FCC3ED13F` |

ZIP 内含 Setup 与 `README.md`。程序集版本：`1.0.32.0`。

## 文档格式策略

| 类型 | 处理 |
|---|---|
| 文本（txt/md/csv/json/xml/html/代码等） | 直接注入 prompt 文本 |
| 图片 / 音频 / 视频 | 作为 file part 发送（多模态） |
| **其他所有文档/压缩包** | 保存到当前项目 `.opencode-telegram-attachments/`，prompt 给出路径并要求 agent 读取 |
| 无活动项目 | 回退为 file part；未知二进制提示先选项目 |

覆盖示例：PDF、DOC、DOCX、ODT、RTF、PPT、PPTX、XLS、XLSX、EPUB、ZIP、TAR、GZ、RAR、7Z，以及未知二进制。

提示词：

```text
The user sent a file (<name>). Its original bytes are stored in the active project at:

`<relative path>`

Read this file with your tools before answering; unzip/extract DOCX, EPUB, PDF, Office or
archive formats if needed. Do not ask the user to paste its contents.
```

- 文件名中的空格/括号由反引号包住，减少 shell 拼接问题。
- 不在 Bot 内解析文档、不注入文本、不重复发附件；agent 用自己的工具读取。
- 这样纯文本模型也能通过 unzip、pdftotext、python 等工具读取内容。

## 验证

- Bot：**2438 passed / 7 skipped**；lint / typecheck / build 全过。
- `document.test.ts`：17 项通过，覆盖 DOCX、EPUB、PDF 工作区存储与图片 file part。
- 程序集：`1.0.32.0`。
