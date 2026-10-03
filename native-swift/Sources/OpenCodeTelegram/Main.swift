import AppKit
import SwiftUI

struct RootView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Image(systemName: "paperplane.fill").font(.system(size: 23)).foregroundStyle(Color.blue)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("OpenCode").font(.system(size: model.fontSize + 2, weight: .bold))
                        Text("Telegram Bot").font(.system(size: model.fontSize - 2)).foregroundStyle(.secondary)
                    }
                }.padding(.bottom, 20)
                ForEach(AppModel.sidebarPageOrder, id: \.self) { page in
                    Button { model.page = page } label: {
                        Label(model.pages[page], systemImage: ["square.grid.2x2", "person.crop.circle", "lock.shield", "cpu", "power", "terminal", "globe", "character.bubble"][page])
                            .frame(maxWidth: .infinity, alignment: .leading).padding(12)
                            .background(model.page == page ? Color.blue.opacity(0.12) : Color.clear, in: RoundedRectangle(cornerRadius: 12))
                            .foregroundStyle(model.page == page ? Color.blue : Color.primary)
                    }.buttonStyle(.plain)
                }
                Spacer()
                Text(model.text("OpenCode 安装与启动器", "OpenCode installer and launcher")).font(.system(size: model.fontSize)).foregroundStyle(.secondary)
            }.padding(16).frame(width: 220).background(Color(red: 0.94, green: 0.95, blue: 0.98))
            if model.page == 6 { webPage }
            else { ScrollView { pageContent.padding(32).frame(maxWidth: .infinity, alignment: .leading) } }
        }
        .frame(minWidth: 980, minHeight: 680)
        .font(.system(size: model.fontSize))
        .environment(\.colorScheme, .light)
        .background(Color(red: 0.965, green: 0.97, blue: 0.985))
        .sheet(isPresented: Binding(get: { model.engineProgress != nil }, set: { _ in })) {
            if let progress = model.engineProgress {
                OperationProgressDialog(title: model.engineProgressTitle,
                    message: progress.phase.title(english: model.settings.locale == "en"),
                    fraction: progress.fraction, detail: progress.detail)
            }
        }
        .alert(model.text("错误", "Error"), isPresented: Binding(get: { !model.error.isEmpty }, set: { if !$0 { model.error = "" } })) {
            Button(model.text("确定", "OK")) { model.error = "" }
        } message: { Text(model.error) }
        .overlay {
            if !model.notice.isEmpty {
                ZStack {
                    Color.black.opacity(0.2)
                    VStack(spacing: 20) {
                        Image(systemName: "checkmark.circle.fill").font(.system(size: model.fontSize * 2)).foregroundStyle(Color.green)
                        Text(model.notice).font(.system(size: model.fontSize, weight: .medium))
                            .multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
                        Text(model.text("5 秒后自动关闭", "Closes automatically after 5 seconds"))
                            .font(.system(size: model.fontSize)).foregroundStyle(.secondary)
                    }.padding(32).frame(width: 440)
                        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20))
                        .shadow(color: .black.opacity(0.18), radius: 20, y: 8)
                }.transition(.opacity)
            }
        }.animation(.easeInOut(duration: 0.2), value: model.notice)
    }
    @ViewBuilder var pageContent: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(model.pages[model.page]).font(.system(size: model.fontSize + 14, weight: .semibold))
            switch model.page {
            case 0:
                Text(model.text("OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。", "OpenCode installer and launcher with Telegram remote control and encrypted communications."))
                serviceCard
                HStack {
                    Label(model.text("实时日志", "Live logs"), systemImage: "terminal").fontWeight(.semibold)
                    Spacer()
                    Text(model.text("仅显示本次打开后的日志", "Logs since opening this app")).foregroundStyle(.secondary)
                }
                LiveOutput(text: model.logs, fontSize: model.fontSize).frame(minHeight: 280)
                    .clipShape(RoundedRectangle(cornerRadius: 16))
            case 1:
                SecureField("Bot Token", text: $model.settings.botToken)
                TextField(model.text("允许的 Telegram User ID", "Allowed Telegram User ID"), text: $model.settings.allowedUserID)
                Text(model.text("从 @BotFather 获取 Token；仅允许指定用户控制。", "Get a Token from @BotFather; only the allowed user can control this Bot."))
                saveButton
                Divider().padding(.top, 24)
                VStack(alignment: .leading, spacing: 14) {
                    Text(model.text("机器人绑定说明", "Bot binding guide")).fontWeight(.semibold)
                    Text(model.text("1. 获取 Bot Token\n在 Telegram 搜索并打开官方、带认证标记的 @BotFather（或点击下面链接），点击 Start，发送 /newbot。按提示输入机器人显示名称，再输入唯一用户名（必须以 bot 结尾，例如 my_opencode_bot）。创建成功后，复制 BotFather 消息中 HTTP API 的 Token，粘贴到上方 Bot Token。已有机器人可发送 /mybots，选择机器人 → API Token 获取；不要填写 Telegram 登录验证码。", "1. Get the Bot Token\nOpen the official verified @BotFather in Telegram (or use the link below), tap Start and send /newbot. Enter a display name, then a unique username ending in bot, for example my_opencode_bot. Copy the HTTP API token from BotFather's confirmation into Bot Token above. For an existing bot, send /mybots, select the bot and choose API Token. Do not enter a Telegram login code."))
                    Link(model.text("打开官方 @BotFather", "Open official @BotFather"), destination: URL(string: "https://t.me/BotFather")!)
                    Text(model.text("2. 获取你自己的 Telegram User ID\n使用准备控制电脑的那个 Telegram 账号，搜索并打开 @userinfobot（第三方 ID 查询机器人，或点击下面链接），点击 Start 或发送 /start。复制回复中 Id 字段的纯数字，填入上方允许的 Telegram User ID。这里需要你的个人用户 ID，不是手机号、@用户名、机器人 ID 或群组 ID。查询 ID 不需要提供 Bot Token、密码或登录验证码。", "2. Get your personal Telegram User ID\nUsing the Telegram account that will control this computer, open @userinfobot (a third-party ID lookup bot, or use the link below) and tap Start or send /start. Copy the numeric Id from its reply into Allowed Telegram User ID above. This must be your personal user ID, not a phone number, @username, bot ID or group ID. ID lookup does not require your Bot Token, password or login code."))
                    Link(model.text("打开 @userinfobot 查询个人 ID（第三方）", "Look up your personal ID with @userinfobot (third-party)"), destination: URL(string: "https://t.me/userinfobot")!)
                    Text(model.text("3. 保存并验证绑定\n填写以上两项后点击保存设置，在概览页启动 Bot。在 Telegram 打开你刚创建的机器人，点击 Start 或发送 /start，再发送 /status 检查连接。仅允许上方指定的用户控制；若没有回复，请检查 Token 是否完整、User ID 是否来自当前账号，以及电脑网络和 Bot 是否运行。", "3. Save and verify\nFill in both fields, click Save settings and start the Bot from Overview. Open your newly created bot in Telegram, tap Start or send /start, then send /status to check the connection. Only the specified user can control it. If there is no reply, check the complete token, your current account's User ID, the computer's network and whether the Bot is running."))
                    Text(model.text("安全提醒：Bot Token 相当于机器人的控制凭据，不要发送给其他机器人或他人，也不要公开截图。若泄露，请在 @BotFather 中选择对应机器人并撤销/重新生成 Token，再更新这里的设置。凭据保存在 macOS 钥匙串。", "Security: the Bot Token is a control credential. Never send it to other bots or people, or publish screenshots of it. If exposed, revoke/regenerate it for that bot in @BotFather and update these settings. Credentials are stored in macOS Keychain."))
                        .font(.caption).foregroundStyle(.secondary)
                }.fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            case 2:
                Toggle(model.text("启用 EOT10 加密", "Enable EOT10 encryption"), isOn: $model.settings.encryptionEnabled)
                SecureField(model.text("加密密码（至少 12 个字符）", "Encryption passphrase (12+ characters)"), text: $model.settings.encryptionPassword)
                    .disabled(!model.settings.encryptionEnabled)
                Text(model.text("与安卓 Bot Encryption 中的密码保持一致。凭据存储在 macOS 钥匙串。", "Use the same passphrase in Android Bot Encryption. Credentials are stored in macOS Keychain."))
                saveButton
                Divider().padding(.top, 24)
                VStack(alignment: .leading, spacing: 14) {
                Text(model.text("加密使用说明", "Encryption guide")).fontWeight(.semibold)
                Text(model.text(EncryptionGuidance.chinese, EncryptionGuidance.english))
                    .fixedSize(horizontal: false, vertical: true)
                Text(model.text("安卓客户端示例：OpenCodeTelegram Android v11.0.22（arm64，非官方 Telegram，调试签名）。请先阅读发布说明，再决定是否安装。", "Example Android client: OpenCodeTelegram Android v11.0.22 (arm64, unofficial Telegram, debug-signed). Read the release notes before deciding whether to install."))
                    .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Link(model.text("安卓加密版项目与下载（含 APK、配置说明）", "Encrypted Android project and downloads (APK and setup)"), destination: EncryptionGuidance.release)
                Link(model.text("安卓加密版对应源码（ZIP）", "Corresponding encrypted Android source (ZIP)"), destination: EncryptionGuidance.androidSource)
                Text(model.text("配置步骤：在上述特殊客户端中打开 Settings → Bot Encryption，添加 Bot 用户名和至少 12 个字符的密码；再在这里启用 EOT10，并填写完全相同的密码，保存后通过该 Bot 私聊使用。桌面密码存储在 macOS 钥匙串。", "Setup: in the special client, open Settings → Bot Encryption, add the Bot username and a passphrase of at least 12 characters. Then enable EOT10 here, enter exactly the same passphrase and save. Use that Bot's private chat. The desktop passphrase is stored in macOS Keychain."))
                    .fixedSize(horizontal: false, vertical: true)
                Text(model.text("注意：这是应用层加密，不是 Telegram Secret Chat；仅适用于配置的 Bot 私聊。通信元数据和按钮标签不会隐藏，安卓本地历史缓存可能包含解密明文，修改密码不会重新加密旧消息。", "Note: this is application-layer encryption, not Telegram Secret Chat, and covers only the configured Bot private chat. Metadata and button labels remain visible. Android local history may contain decrypted plaintext; changing the passphrase does not re-encrypt old messages."))
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }.fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            case 3:
                Text(model.text("仅支持 OpenCode V2", "OpenCode V2 only"))
                VStack(alignment: .leading, spacing: 18) {
                    Label(model.text("引擎管理", "Engine management"), systemImage: "cpu").fontWeight(.semibold)
                    Text(Runtime.installedEngine(model.settings) == nil ? model.text("尚未安装 · 点击更新可恢复安装", "Not installed · Update to restore installation") : model.text("已安装", "Installed"))
                        .foregroundStyle(.secondary)
                    Text(Runtime.engine(model.settings).path).textSelection(.enabled).foregroundStyle(.secondary)
                    HStack(spacing: 12) {
                        Button { model.updateEngine() } label: { Label(model.text("更新 OpenCode", "Update OpenCode"), systemImage: "arrow.down.circle") }
                            .buttonStyle(.borderedProminent)
                        Button { model.scanEngines() } label: { Label(model.text("扫描所有 OpenCode", "Scan all OpenCode"), systemImage: "magnifyingglass") }
                            .buttonStyle(.bordered)
                    }.controlSize(.large).disabled(model.busy)
                    Text(model.text("扫描不按安装来源过滤，不自动应用；请在结果中自行选择。扫描覆盖登录环境、常见安装目录、Node 多版本目录、Spotlight 与自定义目录。", "Scanning does not filter by installation source or apply results automatically. Choose from results. Search includes the login environment, common installation directories, Node version managers, Spotlight, and custom directories."))
                        .foregroundStyle(.secondary)
                    if !model.engines.isEmpty {
                        DisclosureGroup(model.text("扫描到的安装", "Detected installations") + " (\(model.engines.count))") {
                            VStack(alignment: .leading, spacing: 14) {
                                ForEach(model.engines) { engine in
                                    let active = engine.isActive(settings: model.settings)
                                    HStack(spacing: 12) {
                                        VStack(alignment: .leading, spacing: 4) {
                                            HStack(spacing: 8) {
                                                Text("OpenCode \(engine.version) · \(engine.apiVersion.uppercased())").fontWeight(.medium)
                                                if engine.isBundled() {
                                                    engineTag(model.text("随附依赖", "Included dependency"), color: .blue)
                                                }
                                            }
                                            Text(engine.path).foregroundStyle(.secondary).textSelection(.enabled)
                                        }
                                        Spacer()
                                        if active {
                                            engineTag(model.text("正在应用", "In use"), color: .green)
                                        } else {
                                            Button { model.applyExistingEngine(engine) } label: {
                                                engineTag(model.text("应用", "Apply"), color: .gray)
                                            }.buttonStyle(.plain).disabled(model.busy)
                                        }
                                        Button(model.text("卸载", "Uninstall"), role: .destructive) { confirmEngineUninstall(engine) }
                                            .buttonStyle(.borderedProminent).tint(.red)
                                            .disabled(model.busy)
                                    }
                                }
                            }.padding(.top, 12)
                        }
                    }
                }.padding(22).frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color.white, in: RoundedRectangle(cornerRadius: 18))
                DisclosureGroup(model.text("高级设置", "Advanced settings")) {
                    VStack(spacing: 14) {
                        TextField("Server API URL", text: $model.settings.serverURL)
                        TextField(model.text("Server 用户名", "Server username"), text: $model.settings.serverUsername)
                        SecureField(model.text("Server 密码", "Server password"), text: $model.settings.serverPassword)
                        TextField(model.text("OpenCode 可执行文件", "OpenCode executable"), text: $model.settings.executable)
                        Button(model.text("添加自定义扫描目录…", "Add a custom scan directory…")) {
                            let panel = NSOpenPanel(); panel.canChooseFiles = false; panel.canChooseDirectories = true
                            if panel.runModal() == .OK, let folder = panel.url { model.addScanFolder(folder) }
                        }
                        ForEach(model.scanFolders, id: \.self) { Text($0).foregroundStyle(.secondary).textSelection(.enabled) }
                        HStack {
                            ForEach([model.settings.apiVersion], id: \.self) { version in
                                Button(model.text("安装 ", "Install ") + version.uppercased()) { model.installEngine(version) }
                                Button(model.text("卸载 ", "Uninstall ") + version.uppercased(), role: .destructive) {
                                    let alert = NSAlert(); alert.messageText = model.text("卸载应用管理的 ", "Remove app-managed ") + version.uppercased() + "?"
                                    alert.addButton(withTitle: model.text("卸载", "Remove")); alert.addButton(withTitle: model.text("取消", "Cancel"))
                                    if alert.runModal() == .alertFirstButtonReturn { model.uninstallEngine(version) }
                                }.buttonStyle(.borderedProminent).tint(.red)
                            }
                        }.disabled(model.busy)
                        TextField("Model provider", text: $model.settings.modelProvider)
                        TextField("Model ID", text: $model.settings.modelID)
                        DisclosureGroup(model.text("其他依赖更新", "Other dependency updates")) {
                            VStack(alignment: .leading, spacing: 14) {
                                Text(model.text("依赖位于安装目录的 OpenCodeTelegram-dependencies 中。更新前会停止相关服务，完成后恢复。Node 仅更新当前主版本，并验证 SQLite 兼容性。", "Dependencies are in OpenCodeTelegram-dependencies beside the app. Services stop during replacement and restart afterward. Node updates stay within the current major version and validate SQLite compatibility."))
                                    .font(.caption).foregroundStyle(.secondary)
                                ForEach(UpdatableDependency.allCases) { dependency in
                                    VStack(alignment: .leading, spacing: 6) {
                                        HStack {
                                            Text(dependency.title).fontWeight(.semibold)
                                            Text(model.dependencyVersions[dependency] ?? model.text("版本待检查", "Version not checked")).foregroundStyle(.secondary)
                                            if let latest = model.dependencyLatest[dependency] {
                                                Text(model.text("最新兼容：", "Latest compatible: ") + latest).foregroundStyle(.secondary)
                                            }
                                            Spacer()
                                            Button(model.text("检查更新", "Check updates")) { model.checkDependency(dependency) }
                                            Button(model.text("更新", "Update")) {
                                                let alert = NSAlert()
                                                alert.messageText = model.text("更新依赖：", "Update dependency: ") + dependency.title
                                                alert.informativeText = model.text("会暂时停止相关服务，更新成功后恢复。请先结束外部终端中使用该依赖的任务。", "Related services will stop and restart after the update. Finish external terminal tasks using this dependency first.")
                                                alert.addButton(withTitle: model.text("更新", "Update")); alert.addButton(withTitle: model.text("取消", "Cancel"))
                                                if alert.runModal() == .alertFirstButtonReturn { model.updateDependency(dependency) }
                                            }.buttonStyle(.borderedProminent)
                                        }
                                        Text(Runtime.resources.appendingPathComponent(dependency.relativePath).path)
                                            .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                                    }
                                }
                            }.padding(.top, 10)
                        }
                    }.padding(.top, 12).disabled(model.busy)
                }
                saveButton.disabled(model.busy)
            case 4:
                Toggle(model.text("登录 macOS 时启动桌面软件", "Launch desktop app at macOS login"), isOn: Binding(get: { model.appStartup }, set: { model.setStartup("app", enabled: $0) }))
                Toggle(model.text("登录 macOS 时启动 Bot", "Start Bot at macOS login"), isOn: Binding(get: { model.botStartup }, set: { model.setStartup("bot", enabled: $0) }))
                Text(model.text("设置在下次登录时生效，凭据仅从钥匙串读取。", "Applies at next login; credentials are read only from Keychain."))
            case 5:
                ConsoleView(model: model, console: model.console)
            case 7:
                Picker(model.text("界面语言", "Interface language"), selection: $model.settings.locale) { Text("简体中文").tag("zh"); Text("English").tag("en") }.frame(width: 260)
                    .onChange(of: model.settings.locale) { model.saveAction() }
                Text(model.text("选择后立即保存并应用。", "Changes are saved and applied immediately."))
                HStack {
                    Text(model.text("字体大小", "Font size"))
                    Slider(value: $model.fontSize, in: 12...24, step: 1).frame(width: 220)
                    Text("\(Int(model.fontSize)) pt").monospacedDigit().frame(width: 55)
                    Button(model.text("恢复默认", "Reset")) { model.fontSize = 15 }
                }
                Text(model.text("立即应用并自动保存；网页按比例缩放。", "Applied and saved immediately; the web page scales proportionally."))
                Divider()
                Button(model.text("卸载桌面软件（保留设置和日志）", "Uninstall desktop app (keep settings and logs)"), role: .destructive) {
                    NotificationCenter.default.post(name: Notification.Name("OpenCodeTelegramUninstall"), object: nil)
                }.buttonStyle(.borderedProminent).tint(.red).disabled(model.busy)
            default: EmptyView()
            }
            if model.busy { ProgressView().controlSize(.small) }
            if model.page != 0 { Text(model.message).font(.system(size: model.fontSize)).foregroundStyle(.secondary) }
        }.textFieldStyle(.roundedBorder)
    }
    var saveButton: some View { Button(model.text("保存设置", "Save settings")) { model.saveAction() }.buttonStyle(.borderedProminent) }
    func engineTag(_ title: String, color: Color) -> some View {
        Text(title).font(.system(size: model.fontSize - 1, weight: .semibold))
            .foregroundStyle(color).padding(.horizontal, 10).padding(.vertical, 5)
            .background(color.opacity(0.12), in: Capsule())
            .overlay(Capsule().stroke(color.opacity(0.25)))
            .fixedSize()
    }
    func confirmEngineUninstall(_ engine: EngineCandidate) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = model.text("卸载这份 OpenCode 安装？", "Uninstall this OpenCode installation?")
        alert.informativeText = "OpenCode \(engine.version)\n\(engine.path)\n\n" + model.text("只移除所选安装，保留配置、会话数据和日志。如果正在使用这份安装，将先停止服务。", "Only the selected installation will be removed. Configuration, session data, and logs are retained. Services using this installation will be stopped first.")
        alert.addButton(withTitle: model.text("卸载", "Uninstall"))
        alert.addButton(withTitle: model.text("取消", "Cancel"))
        if alert.runModal() == .alertFirstButtonReturn { model.uninstallExistingEngine(engine) }
    }
    var serviceCard: some View {
        let tint = model.botRunning ? Color.red : Color.green
        return VStack(spacing: 16) {
            HStack(spacing: 8) {
                Circle().fill(model.botRunning ? Color.green : Color.secondary).frame(width: 9, height: 9)
                Text(model.busy ? model.text("正在处理…", "Working…") : (model.botRunning ? model.text("服务运行中", "Service running") : model.text("服务已停止", "Service stopped")))
                    .fontWeight(.medium)
            }
            Button { model.botAction(model.botRunning ? "stop" : "start") } label: {
                ZStack {
                    Circle().fill(tint.opacity(0.1)).frame(width: 156, height: 156)
                    Circle().fill(LinearGradient(colors: [tint, tint.opacity(0.8)], startPoint: .topLeading, endPoint: .bottomTrailing))
                        .frame(width: 126, height: 126).shadow(color: tint.opacity(0.25), radius: 14, y: 7)
                    if model.busy { ProgressView().controlSize(.large).tint(.white) }
                    else { Image(systemName: model.botRunning ? "stop.fill" : "play.fill").font(.system(size: 40, weight: .semibold)).foregroundStyle(.white).offset(x: model.botRunning ? 0 : 3) }
                }.contentShape(Circle())
            }.buttonStyle(.plain).disabled(model.busy)
                .accessibilityLabel(model.botRunning ? model.text("停止服务", "Stop service") : model.text("启动服务", "Start service"))
                .help(model.botRunning ? model.text("停止服务", "Stop service") : model.text("启动服务", "Start service"))
            Text(model.botRunning ? model.text("点击停止服务", "Click to stop service") : model.text("点击启动服务", "Click to start service"))
                .font(.system(size: model.fontSize + 2, weight: .semibold))
        }.padding(26).frame(maxWidth: .infinity)
            .background(Color.white, in: RoundedRectangle(cornerRadius: 22))
            .overlay(RoundedRectangle(cornerRadius: 22).stroke(Color.black.opacity(0.04)))
    }
    var webPage: some View {
        VStack(spacing: 6) {
            HStack {
                Text(model.pages[6]).font(.system(size: model.fontSize, weight: .semibold)); Spacer()
                Menu {
                    Button(model.text("重新连接", "Reconnect")) { model.action { try await model.connect() } }
                    Button(model.text("刷新网页", "Refresh page")) { model.webReloadRevision += 1 }
                    Button(model.text("在默认浏览器打开", "Open in default browser")) { if let url = model.webURL { NSWorkspace.shared.open(url) } }
                    Button(model.text("停止网页服务", "Stop web service")) { model.action { try await model.stopWeb() } }
                    Divider(); Text(model.message)
                } label: { Text("⋯").font(.title2) }.menuStyle(.borderlessButton).frame(width: 36)
            }.padding(.horizontal, 8)
            EmbeddedWeb(model: model)
        }.padding(8).background(Color.black).environment(\.colorScheme, .dark).onAppear { if model.webURL == nil { model.action { try await model.connect() } } }
    }
}

struct ConsoleView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var console: CLIConsole
    @State private var secret = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            LiveOutput(text: console.output, fontSize: model.fontSize).frame(minHeight: 350)
            if console.running {
                SecureField(model.text("交互输入（隐藏）", "Interactive input (hidden)"), text: $secret).onSubmit { submitSecret() }
                HStack { Button(model.text("提交", "Submit")) { submitSecret() }; Button(model.text("停止命令", "Stop command")) { console.stop() } }
            } else {
                TextField(model.text("输入管理命令", "Enter a management command"), text: $model.cliInput).onSubmit { run() }
                    .onKeyPress(.upArrow) { model.cliInput = console.previous(-1); return .handled }
                    .onKeyPress(.downArrow) { model.cliInput = console.previous(1); return .handled }
                HStack {
                    Button(model.text("执行", "Run")) { run() }.disabled(model.busy)
                    Button("↑") { model.cliInput = console.previous(-1) }
                    Button("↓") { model.cliInput = console.previous(1) }
                }
            }
            Text("help · config · opencode · web · autostart · app-autostart · language · logs").font(.system(size: model.fontSize))
        }.padding(12).background(Color.black).environment(\.colorScheme, .dark)
    }
    func submitSecret() {
        do { try console.submitSecret(secret); secret = "" }
        catch { model.error = error.localizedDescription }
    }
    func run() {
        do {
            let command = model.cliInput
            let args = try CommandLineParser.parse(command)
            guard !args.isEmpty else { return }
            let sensitive = args.first == "config" && args.count > 2 && ["bot-token", "eot10.password", "opencode.password"].contains(args[2])
            console.remember(command, sensitive: sensitive)
            console.append(sensitive ? "\n> [secret command]\n" : "\n> \(command)\n")
            model.cliInput = ""
            model.action {
                if let result = try await model.nativeCommand(args) { console.append(result + "\n") }
                else { try console.start(args, settings: model.settings) }
            }
        } catch { model.error = error.localizedDescription }
    }
}

struct LiveOutput: View {
    let text: String
    let fontSize: Double
    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    Text(text).font(.system(size: fontSize, design: .monospaced)).foregroundStyle(Color.white)
                        .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                    Color.clear.frame(height: 1).id("end")
                }.padding(12)
            }.background(Color.black)
                .onChange(of: text) { _, _ in proxy.scrollTo("end", anchor: .bottom) }
                .onAppear { proxy.scrollTo("end", anchor: .bottom) }
        }
    }
}

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    let model: AppModel
    var window: NSWindow?
    override convenience init() {
        self.init(loadSettings: !CommandLine.arguments.contains("--install") && !Bundle.main.bundleURL.path.hasPrefix("/Volumes/"))
    }
    init(loadSettings: Bool) {
        model = AppModel(loadSettings: loadSettings)
        super.init()
    }
    var closing = false
    var cleanupComplete = false
    var installer: InstallerModel?
    var uninstallObserver: NSObjectProtocol?
    var uninstalling = false
    func applicationDidFinishLaunching(_ notification: Notification) {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1120, height: 760), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        self.window = window
        window.title = "opencode_telegram_bot"
        window.contentMinSize = NSSize(width: 980, height: 680)
        if CommandLine.arguments.contains("--install") || Bundle.main.bundleURL.path.hasPrefix("/Volumes/") {
            let value = InstallerModel(); installer = value
            window.contentView = NSHostingView(rootView: InstallerView(installer: value))
            window.setContentSize(NSSize(width: 664, height: 544)); window.contentMinSize = NSSize(width: 664, height: 544)
        } else { window.contentView = NSHostingView(rootView: RootView(model: model)) }
        window.delegate = self
        window.center(); window.makeKeyAndOrderFront(nil)
        let menu = NSMenu()
        let item = NSMenuItem(); menu.addItem(item)
        let appMenu = NSMenu(); item.submenu = appMenu
        appMenu.addItem(withTitle: model.text("退出 opencode_telegram_bot", "Quit opencode_telegram_bot"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        NSApp.mainMenu = menu
        uninstallObserver = NotificationCenter.default.addObserver(forName: Notification.Name("OpenCodeTelegramUninstall"), object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.confirmUninstall() }
        }
        NSApp.activate(ignoringOtherApps: true)
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if let installer {
            if installer.working { return false }
            cleanupComplete = true; NSApp.terminate(nil); return false
        }
        if closing { return false }
        closing = true
        let alert = NSAlert()
        alert.messageText = uninstalling ? model.text("正在卸载并清理相关进程…", "Uninstalling and cleaning up related processes…") : model.text("正在清理相关进程…", "Cleaning up related processes…")
        let indicator = NSProgressIndicator(frame: NSRect(x: 0, y: 0, width: 320, height: 12))
        indicator.style = .bar; indicator.isIndeterminate = true; indicator.startAnimation(nil)
        alert.accessoryView = indicator
        alert.beginSheetModal(for: sender)
        alert.buttons.forEach { $0.isEnabled = false }
        Task {
            do {
                while model.busy { try await Task.sleep(nanoseconds: 200_000_000) }
                try await model.cleanup()
                if uninstalling {
                    try StartupManager.set("bot", enabled: false); try StartupManager.set("app", enabled: false)
                    guard Bundle.main.bundleURL.pathExtension == "app", Bundle.main.bundleIdentifier == "org.opencode.telegram.desktop", !Bundle.main.bundleURL.path.hasPrefix("/Volumes/") else {
                        throw AppFailure.message(model.text("不能卸载只读安装介质中的应用。", "An app on read-only installation media cannot be uninstalled."))
                    }
                    try await Task.detached { try FileManager.default.removeItem(at: Bundle.main.bundleURL) }.value
                }
                sender.endSheet(alert.window)
                cleanupComplete = true
                NSApp.terminate(nil)
            } catch {
                sender.endSheet(alert.window)
                model.error = error.localizedDescription
                closing = false
                uninstalling = false
            }
        }
        return false
    }
    func confirmUninstall() {
        guard !closing, !model.busy, let window else { return }
        let alert = NSAlert(); alert.messageText = model.text("确定卸载桌面软件？设置和日志会保留。", "Uninstall the desktop app? Settings and logs will be kept.")
        alert.addButton(withTitle: model.text("卸载", "Uninstall")); alert.addButton(withTitle: model.text("取消", "Cancel"))
        if alert.runModal() == .alertFirstButtonReturn { uninstalling = true; _ = windowShouldClose(window) }
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if cleanupComplete { return .terminateNow }
        if closing { return .terminateCancel }
        guard let window else { return .terminateNow }
        _ = windowShouldClose(window)
        return .terminateCancel
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        guard !closing else { return false }
        // LaunchServices can send reopen before applicationDidFinishLaunching.
        // Initial launch will present the window; never dereference a nil window here.
        guard let window else { return true }
        window.deminiaturize(nil)
        window.makeKeyAndOrderFront(nil)
        sender.activate(ignoringOtherApps: true)
        return true
    }
}

@main struct Main {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        if CommandLine.arguments.contains("--start-silent") {
            app.delegate = nil
            app.setActivationPolicy(.accessory)
            Task { @MainActor in
                do { try await delegate.model.waitForSettings(); try await delegate.model.connect(); _ = try await Runtime.bot(["start", "--daemon", "--mode", "installed"], settings: delegate.model.settings); exit(0) }
                catch { fputs("Startup failed: \(error.localizedDescription)\n", stderr); exit(1) }
            }
        } else { app.setActivationPolicy(.regular) }
        app.run()
        withExtendedLifetime(delegate) {}
    }
}
