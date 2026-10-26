# Desktop 1.0.24 / 桌面版 1.0.24

## Fixes / 修复

- A prompt that failed with `Model unavailable` (the engine could not refresh the `opencode-go`
  provider config over the network, e.g. a VPN/proxy or TLS hiccup) is now sent again
  automatically: up to three attempts with 2 s / 5 s / 10 s backoff. A notice explains the retry,
  and the user only sees an error when all attempts fail, with a hint to check the network or VPN.
- 因网络、VPN/代理或 TLS 抖动导致引擎拉取 `opencode-go` provider 配置失败、报 `Model unavailable`
  的请求，现在会自动重发：最多 3 次，间隔 2 s / 5 s / 10 s；重试时会提示，全部失败才报错并提示
  检查网络或 VPN。
- Images a tool returns (screenshots, generated pictures) are forwarded to Telegram as photos
  instead of being dropped together with the text-only tool output. With EOT10 enabled they are
  encrypted like any other attachment.
- 工具返回的图片（截图、生成的图片）现在会作为照片发到 Telegram，不再随工具输出被丢弃；开启
  EOT10 时会像其他附件一样加密发送。
- Contains every 1.0.23 startup and performance change and the engine-panel fix.
- 包含 1.0.23 的全部启动/性能改动与引擎面板修复。

## Verification / 验证

- Bot: build, lint, typecheck and the full suite passed (2387 passed, 7 skipped), including new
  tests for the retry policy, tool-image mapping and the tool-image callback.
- Desktop: unchanged since 1.0.23; package build passed and its checks are included.
- Setup: `desktop/artifacts/OpenCodeTelegram-Setup-1.0.24.exe` (688,052,224 bytes; compiled app reports 1.0.24).
- Setup SHA256: `1911EAD2B3C0E9C00A0A088B27F1CFB6B05357C3F466A02541ED05E7C2366E55`.
- Release ZIP: `desktop/artifacts/OpenCodeTelegram-1.0.24-Windows-x64.zip` (682,717,196 bytes).
- Release ZIP SHA256: `C082BC12BA844E9C04F3F64D90111B057D54481DD6236F4DBB2A5E5BF90D58CC`.
