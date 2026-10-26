import AppKit
import SwiftUI

struct RootView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 8) {
                Text("opencode_telegram_bot").font(.system(size: 16, weight: .semibold)).padding(.bottom, 24)
                ForEach(0..<model.pages.count, id: \.self) { page in
                    Button { model.page = page } label: {
                        Text(model.pages[page]).frame(maxWidth: .infinity, alignment: .leading).padding(12)
                            .background(model.page == page ? Color.blue.opacity(0.12) : Color.clear)
                            .foregroundStyle(model.page == page ? Color.blue : Color.primary)
                    }.buttonStyle(.plain)
                }
                Spacer()
                Text(model.text("OpenCode 安装与启动器", "OpenCode installer and launcher")).font(.caption).foregroundStyle(.secondary)
            }.padding(16).frame(width: 188).background(Color(red: 0.94, green: 0.95, blue: 0.98))
            if model.page == 6 { webPage }
            else { ScrollView { pageContent.padding(32).frame(maxWidth: .infinity, alignment: .leading) } }
        }
        .frame(minWidth: 980, minHeight: 680)
        .background(Color(red: 0.965, green: 0.97, blue: 0.985))
        .alert(model.text("错误", "Error"), isPresented: Binding(get: { !model.error.isEmpty }, set: { if !$0 { model.error = "" } })) {
            Button(model.text("确定", "OK")) { model.error = "" }
        } message: { Text(model.error) }
    }
    @ViewBuilder var pageContent: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(model.pages[model.page]).font(.system(size: 29, weight: .semibold))
            switch model.page {
            case 0:
                Text(model.text("OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。", "OpenCode installer and launcher with Telegram remote control and encrypted communications."))
                HStack { Button(model.text("启动机器人", "Start Bot")) { model.botAction("start") }; Button(model.text("停止机器人", "Stop Bot")) { model.botAction("stop") }; Button(model.text("刷新状态", "Refresh status")) { model.botAction("status") } }.disabled(model.busy)
                Text(model.message).textSelection(.enabled)
                TextEditor(text: .constant(model.logs)).font(.system(.caption, design: .monospaced)).frame(minHeight: 350)
            case 1:
                SecureField("Bot Token", text: $model.settings.botToken)
                TextField(model.text("允许的 Telegram User ID", "Allowed Telegram User ID"), text: $model.settings.allowedUserID)
                Text(model.text("从 @BotFather 获取 Token；仅允许指定用户控制。", "Get a Token from @BotFather; only the allowed user can control this Bot."))
                saveButton
            case 2:
                Toggle(model.text("启用 EOT10 加密", "Enable EOT10 encryption"), isOn: $model.settings.encryptionEnabled)
                SecureField(model.text("加密密码（至少 12 个字符）", "Encryption passphrase (12+ characters)"), text: $model.settings.encryptionPassword)
                Text(model.text("与安卓 Bot Encryption 中的密码保持一致。凭据存储在 macOS 钥匙串。", "Use the same passphrase in Android Bot Encryption. Credentials are stored in macOS Keychain."))
                saveButton
            case 3:
                Picker(model.text("Server API 版本", "Server API version"), selection: $model.settings.apiVersion) { Text("V1").tag("v1"); Text("V2").tag("v2") }.pickerStyle(.segmented)
                    .onChange(of: model.settings.apiVersion) { _, version in model.settings.serverURL = version == "v2" ? "http://127.0.0.1:49374" : "http://127.0.0.1:4096"; model.settings.executable = "" }
                DisclosureGroup(model.text("高级设置", "Advanced settings")) {
                    VStack(spacing: 14) {
                        TextField("Server API URL", text: $model.settings.serverURL)
                        TextField(model.text("Server 用户名", "Server username"), text: $model.settings.serverUsername)
                        SecureField(model.text("Server 密码", "Server password"), text: $model.settings.serverPassword)
                        TextField(model.text("OpenCode 可执行文件", "OpenCode executable"), text: $model.settings.executable)
                        Button(model.text("选择已有 OpenCode", "Select existing OpenCode")) {
                            let panel = NSOpenPanel(); panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
                            if panel.runModal() == .OK, let url = panel.url { model.settings.executable = url.path }
                        }
                        Button(model.text("扫描已有 OpenCode", "Scan existing OpenCode")) { model.action { model.engines = await EngineManager.scan(settings: model.settings) } }
                        ForEach(model.engines.filter { $0.version.range(of: "(^|\\s)" + String(model.settings.apiVersion.suffix(1)) + "\\.", options: .regularExpression) != nil }) { engine in
                            Button("\(engine.version) · \(engine.path)") {
                                model.settings.executable = engine.path
                                model.saveAction()
                            }.font(.caption)
                        }
                        HStack {
                            ForEach(["v1", "v2"], id: \.self) { version in
                                Button(model.text("安装 ", "Install ") + version.uppercased()) { model.installEngine(version) }
                                Button(model.text("卸载 ", "Uninstall ") + version.uppercased()) {
                                    let alert = NSAlert(); alert.messageText = model.text("卸载应用管理的 ", "Remove app-managed ") + version.uppercased() + "?"
                                    alert.addButton(withTitle: model.text("卸载", "Remove")); alert.addButton(withTitle: model.text("取消", "Cancel"))
                                    if alert.runModal() == .alertFirstButtonReturn { model.uninstallEngine(version) }
                                }
                            }
                        }.disabled(model.busy)
                        TextField("Model provider", text: $model.settings.modelProvider)
                        TextField("Model ID", text: $model.settings.modelID)
                    }.padding(.top, 12)
                }
                saveButton
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
                Divider()
                Button(model.text("卸载桌面软件（保留设置和日志）", "Uninstall desktop app (keep settings and logs)")) {
                    NotificationCenter.default.post(name: Notification.Name("OpenCodeTelegramUninstall"), object: nil)
                }.disabled(model.busy)
            default: EmptyView()
            }
            if model.busy { ProgressView().controlSize(.small) }
            if model.page != 0 { Text(model.message).font(.caption).foregroundStyle(.secondary) }
        }.textFieldStyle(.roundedBorder)
    }
    var saveButton: some View { Button(model.text("保存设置", "Save settings")) { model.saveAction() }.buttonStyle(.borderedProminent) }
    var webPage: some View {
        VStack(spacing: 6) {
            HStack {
                Text(model.pages[6]).font(.system(size: 13, weight: .semibold)); Spacer()
                Menu {
                    Button(model.text("重新连接", "Reconnect")) { model.action { try await model.connect() } }
                    Button(model.text("刷新网页", "Refresh page")) { model.webReloadRevision += 1 }
                    Button(model.text("在默认浏览器打开", "Open in default browser")) { if let url = model.webURL { NSWorkspace.shared.open(url) } }
                    Button(model.text("停止网页服务", "Stop web service")) { model.action { try await model.stopWeb() } }
                    Divider(); Text(model.message)
                } label: { Text("⋯").font(.title2) }.menuStyle(.borderlessButton).frame(width: 36)
            }.padding(.horizontal, 8)
            EmbeddedWeb(model: model)
        }.padding(8).onAppear { if model.webURL == nil { model.action { try await model.connect() } } }
    }
}

struct ConsoleView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var console: CLIConsole
    @State private var secret = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextEditor(text: .constant(console.output)).font(.system(.body, design: .monospaced)).frame(minHeight: 350)
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
            Text("help · config · opencode · web · autostart · app-autostart · language · logs").font(.caption)
        }
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
