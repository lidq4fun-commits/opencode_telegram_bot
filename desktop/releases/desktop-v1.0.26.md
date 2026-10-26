# Desktop 1.0.26 / 桌面版 1.0.26

## Fixes / 修复

- A DOCX sent from the phone is now readable by the agent. Verified in fresh, empty sessions
  that OpenCode does not hand OOXML text to text-only models (the model answers "no document
  content" while the file is attached), so the Bot follows the workspace-file convention used by
  Codex/Claude Code style harnesses instead of parsing the document itself:
- 手机发来的 DOCX 现在 agent 能读到。用空目录的新会话验证过：OpenCode 不会把 OOXML 文本交给纯文本
  模型（附件在也一样回答"没有文档内容"），因此改用 Codex/Claude Code 这类 harness 的"工作区文件"
  约定，而不是在 Bot 里解析文档：
  - the original document is stored in `.opencode-telegram-attachments/` inside the active
    project and the prompt names that relative path; the agent reads it with its own tools
    (unzip, python, …), so tables, embedded images and layout stay intact;
  - 原始文档存进当前项目的 `.opencode-telegram-attachments/`，prompt 里只给相对路径；agent 用
    自己的工具读取（解压、python 等），表格、内嵌图片和版式都完整保留；
  - no document text is injected into the prompt and no redundant attachment is sent: the file
    lives in the workspace exactly like a file the user created there;
  - 不往 prompt 里塞文档文本，也不重复发送附件：文件就在工作区里，和用户自己放进去的文件一样；
  - without an active project the document still travels as an attachment (no place to store it);
  - 没有活动项目时仍作为附件发送（没有可存放的工作区）。
- Works together with EOT10: the encrypted document is decrypted first, then stored and referenced.
- 与 EOT10 兼容：先解密，再存档并在 prompt 中引用。
- Contains every 1.0.25 fix (web project-selection lock, `Model unavailable` retry, tool images)
  and the 1.0.23/1.0.24 startup and performance work.
- 包含 1.0.25 的全部修复（网页选项目锁定、`Model unavailable` 自动重试、工具图片转发）以及
  1.0.23/1.0.24 的启动与性能改动。

## Verification / 验证

- Bot: build, lint, typecheck and the full suite passed (2389 passed, 7 skipped), including the
  stored-DOCX and no-project handler tests.
- Desktop suites unchanged and still green; the packaged app reports 1.0.26.
- Setup: `desktop/artifacts/OpenCodeTelegram-Setup-1.0.26.exe` (688,055,296 bytes; compiled app reports 1.0.26).
- Setup SHA256: `F65C262BB4C1A1F4140E6133347064D2479C1FCF23B240DE49CB2614243FE292`.
- Release ZIP: `desktop/artifacts/OpenCodeTelegram-1.0.26-Windows-x64.zip` (682,718,498 bytes).
- Release ZIP SHA256: `85591AD09637B176F08F5EB029C7E491AAC9887BAA35072E299BE0597A0795DA`.
