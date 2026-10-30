import AppKit
import CoreImage.CIFilterBuiltins
import SwiftUI

struct TelegramClientView: View {
    @ObservedObject var client: TelegramClient
    let english: Bool
    var loginOnly = false
    @State private var password = ""
    @State private var confirmLogout = false

    private func text(_ zh: String, _ en: String) -> String { english ? en : zh }
    private var status: String {
        switch client.state {
        case .idle, .signedOut: return text("未登录", "Not logged in")
        case .checking: return text("正在检查登录状态…", "Checking session…")
        case .loggingIn: return text("等待扫码登录…", "Waiting for QR login…")
        case .passwordRequired: return text("需要 Telegram 双重验证密码", "Telegram two-step verification required")
        case .connecting: return text("正在连接消息监听…", "Connecting message listener…")
        case .watching: return text("正在监听新消息", "Listening for new messages")
        case .stopped: return text("监听已停止", "Listener stopped")
        case .failed: return text("操作失败", "Operation failed")
        }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(text("个人 Telegram 客户端 · tg v0.11.0", "Personal Telegram client · tg v0.11.0")).fontWeight(.semibold)
            Text(text("与 Bot 登录独立。软件打开后，已登录账号默认自动监听；未登录时不会强制弹出登录。", "Separate from the Bot. Opening the app automatically listens for a logged-in account; login is never forced."))
            HStack {
                Label(status, systemImage: client.state == .watching ? "dot.radiowaves.left.and.right" : "person.crop.circle")
                if let identity = client.identity { Text(identity.label).fontWeight(.semibold).textSelection(.enabled) }
            }
            HStack {
                if client.identity == nil {
                    Button(text("扫码登录", "Log in with QR")) { client.login() }
                        .buttonStyle(.borderedProminent).disabled(client.working)
                }
                Button(text("检查登录状态", "Check session")) { client.check() }.disabled(client.working)
                if client.identity != nil {
                    if !loginOnly {
                    Button(text("开始监听", "Start listening")) { client.startListening() }
                        .disabled(client.working || client.operation == .watch)
                    }
                    Button(text("退出账号", "Log out"), role: .destructive) { confirmLogout = true }
                        .disabled(client.working)
                }
                if (!loginOnly && client.operation == .watch) || client.operation == .login {
                    Button(text("停止 / 取消", "Stop / Cancel")) { password = ""; client.stopListening() }
                }
            }
            if let url = client.qrURL, let image = Self.qrImage(url) {
                VStack(alignment: .leading, spacing: 10) {
                    Image(nsImage: image).interpolation(.none).resizable().frame(width: 240, height: 240)
                    Text(text("在手机 Telegram：设置 → 设备 → 连接桌面设备，扫描此二维码。二维码过期会自动更新。", "In Telegram on your phone: Settings → Devices → Link Desktop Device. Scan this QR code; it refreshes when expired."))
                    Text(text("二维码是登录凭据，请勿截图或分享。", "The QR code is a login credential. Do not share it.")).font(.caption).foregroundStyle(.secondary)
                }
            }
            if client.state == .passwordRequired {
                SecureField(text("Telegram 双重验证密码（不是 Bot 加密密码）", "Telegram two-step verification password (not the Bot passphrase)"), text: $password)
                    .onSubmit { submitPassword() }
                Button(text("提交密码", "Submit password")) { submitPassword() }.disabled(password.isEmpty)
                Text(text("密码只传给本机 tg 进程，不保存、不写日志。", "The password goes only to the local tg process; it is not saved or logged.")).font(.caption)
            }
            if client.state == .failed {
                Text(failureMessage).foregroundStyle(.red)
            }
            if !loginOnly {
            Divider()
            HStack {
                Text(text("本次监听收到的消息", "Messages received during this session")).fontWeight(.semibold)
                Spacer()
                Button(text("清空显示", "Clear display")) { client.clearMessages() }
            }
            Text(text("仅显示新收到的消息，不读取历史，不自动下载附件，不标记已读，不回复或转交 AI。最多保留 200 条在内存中，退出软件即清空。", "New incoming messages only. No history fetch, attachment download, read receipts, replies or AI forwarding. Up to 200 messages stay in memory and are cleared on exit."))
                .font(.caption).foregroundStyle(.secondary)
            if client.messages.isEmpty {
                Text(text("暂无新消息", "No new messages")).foregroundStyle(.secondary).padding(.vertical, 30)
            }
            LazyVStack(alignment: .leading, spacing: 12) {
                ForEach(client.messages.reversed()) { event in
                    VStack(alignment: .leading, spacing: 6) {
                        HStack {
                            Text(event.peer.label).fontWeight(.medium)
                            Spacer()
                            Text(Date(timeIntervalSince1970: TimeInterval(event.message.date)), style: .time).foregroundStyle(.secondary)
                        }
                        if let sender = event.message.from { Text(sender.label).font(.caption).foregroundStyle(.secondary) }
                        if let message = event.message.text, !message.isEmpty {
                            Text(String(message.prefix(16_384))).textSelection(.enabled)
                        }
                        if let media = event.message.media, !media.isEmpty {
                            Text(text("附件：", "Attachment: ") + media).foregroundStyle(.secondary)
                        }
                    }.padding(16).frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color.white, in: RoundedRectangle(cornerRadius: 12))
                }
            }
            }
            Text(text("tg 会话由 macOS 钥匙串保存；配置与对端缓存位于软件数据目录的 tg 子目录。此页面不解密 EOT10 内容。", "tg sessions use macOS Keychain; config and peer cache use the app data directory's tg subfolder. This page does not decrypt EOT10 content."))
                .font(.caption).foregroundStyle(.secondary)
        }
        .onChange(of: client.state) { if client.state != .passwordRequired { password = "" } }
        .onDisappear { password = "" }
        .confirmationDialog(text("退出此 Telegram 账号？", "Log out of this Telegram account?"), isPresented: $confirmLogout) {
            Button(text("退出账号", "Log out"), role: .destructive) { password = ""; client.logout() }
            Button(text("取消", "Cancel"), role: .cancel) {}
        }
    }
    private func submitPassword() { client.submitPassword(password); password = "" }
    private var failureMessage: String {
        if client.failure == "timeout" { return text("操作超时。请检查网络后重试。", "Operation timed out. Check your connection and retry.") }
        if client.failure == "launch" { return text("无法启动内置 tg，请重新安装完整软件包并检查数据目录权限。", "Could not start bundled tg. Reinstall the complete package and check data-directory permissions.") }
        if client.failure == "output" { return text("无法解析 tg 输出，监听已停止。", "Could not parse tg output; listening stopped.") }
        return text("tg 操作未完成，请检查网络和登录状态后重试。出于隐私考虑，不显示原始诊断输出。", "tg did not complete the operation. Check connectivity and session status, then retry. Raw diagnostics are hidden for privacy.")
    }
    static func qrImage(_ value: String) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(value.utf8); filter.correctionLevel = "M"
        guard let output = filter.outputImage,
              let image = CIContext().createCGImage(output.transformed(by: CGAffineTransform(scaleX: 8, y: 8)), from: output.extent.applying(CGAffineTransform(scaleX: 8, y: 8))) else { return nil }
        return NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
    }
}
