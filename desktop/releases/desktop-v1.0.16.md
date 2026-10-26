# Desktop v1.0.16

- Port the macOS 0.1.21 EOT10 rich-text boundary fix from commit `dd97968dcd610aca791057a672c5daa22544818e`: merge adjacent plaintext string leaves before encryption so Telegram cannot concatenate independent authenticated envelopes. Preserve styled boundaries and buttons.
- Split long plain-text sends into independently decryptable envelopes within Telegram's 4096-character limit, accounting for UTF-8 and Base64 expansion without splitting Unicode surrogate pairs.
- Remove plaintext formatting options from encrypted text sends.

Artifacts:

- `OpenCodeTelegram-Setup-1.0.16.exe`
- `OpenCodeTelegram-1.0.16-Windows-x64.zip`

Source validation: Bot build, lint, typecheck and six EOT10 regression tests passed. The full test suite had failures, including English expectations running against Chinese localization. Phone end-to-end verification and installation testing have not been performed for this version.
