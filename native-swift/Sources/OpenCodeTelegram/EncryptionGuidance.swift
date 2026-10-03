import Foundation

enum EncryptionGuidance {
    static let release = URL(string: "https://github.com/lidq4fun-commits/opencode_telegram_bot/releases/tag/desktop-1.0.15-android-11.0.22")!
    static let androidSource = URL(string: "https://github.com/lidq4fun-commits/opencode_telegram_bot/releases/download/desktop-1.0.15-android-11.0.22/OpenCodeTelegram-Android-v11.0.22-source.zip")!
    static let chinese = "默认关闭。只有使用支持 EOT10 的特殊加密版 Telegram 客户端才能开启；普通官方 Telegram 不支持该加密协议，不能正常解密加密消息。若使用普通 Telegram，请保持关闭。"
    static let english = "Off by default. Enable only when using a special Telegram client supporting EOT10. The standard official Telegram client does not support this protocol and cannot decrypt these messages. Leave this off when using standard Telegram."
}
