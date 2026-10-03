import AppKit
import SwiftUI

@MainActor final class AppModel: ObservableObject {
    @Published var settings = Settings()
    @Published var page = 0
    @Published var fontSize: Double = UserDefaults.standard.object(forKey: "interfaceFontSize") as? Double ?? 15 {
        didSet { UserDefaults.standard.set(fontSize, forKey: "interfaceFontSize") }
    }
    @Published var busy = false
    @Published var engineProgress: EngineUpdateProgress?
    @Published var engineProgressTitle = ""
    @Published var message = ""
    @Published var logs = ""
    @Published var botRunning = false
    private var monitor: Timer?
    private var sessionLogs = SessionLogReader(folder: SettingsStore.home.appendingPathComponent("logs"))
    private var savedEncryption: String = ""
    @Published var cliInput = ""
    @Published var cliOutput = ""
    @Published var error = ""
    @Published var notice = ""
    private var noticeTask: Task<Void, Never>?
    @Published var webURL: URL?
    @Published var webReloadRevision = 0
    @Published var engines: [EngineCandidate] = []
    @Published var dependencyVersions: [UpdatableDependency: String] = [:]
    @Published var dependencyLatest: [UpdatableDependency: String] = [:]
    @Published var scanFolders = UserDefaults.standard.stringArray(forKey: "engineScanFolders") ?? []
    @Published var appStartup = StartupManager.enabled("app")
    @Published var botStartup = StartupManager.enabled("bot")
    let console = CLIConsole()
    private var writable = false

    init(loadSettings: Bool = true) {
        settings.locale = UserDefaults.standard.string(forKey: "interfaceLocale") ?? "zh"
        guard loadSettings else { return }
        busy = true
        Task { [self] in
            defer { busy = false }
            do {
                settings = try await Task.detached { try SettingsStore.load() }.value
                settings.locale = UserDefaults.standard.string(forKey: "interfaceLocale") ?? settings.locale
                writable = true
                if FileManager.default.isExecutableFile(atPath: Runtime.engine(settings).path) {
                    try EngineShellPath.sync(Runtime.engine(settings))
                }
                savedEncryption = encryptionSignature
                refreshRuntime()
                monitor = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                    Task { @MainActor in self?.refreshRuntime() }
                }
            } catch { self.error = error.localizedDescription }
        }
    }
    func text(_ zh: String, _ en: String) -> String { settings.locale == "en" ? en : zh }
    func showNotice(_ text: String, duration: UInt64 = 5_000_000_000) {
        noticeTask?.cancel()
        notice = text
        noticeTask = Task { [weak self] in
            do { try await Task.sleep(nanoseconds: duration) }
            catch { return }
            self?.notice = ""
        }
    }
    func waitForSettings() async throws {
        for _ in 0..<100 {
            if !busy { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        guard writable else { throw AppFailure.message("Keychain settings are not available; startup was cancelled.") }
    }
    static let sidebarPageOrder = [0, 6, 1, 2, 3, 4, 5, 7]
    var pages: [String] {
        [text("概览", "Overview"), text("机器人绑定", "Bot Binding"), text("加密设置", "Encryption"),
         text("OpenCode 引擎", "OpenCode Engine"), text("启动设置", "Startup Settings"), "Bot CLI",
         text("OpenCode 网页端", "OpenCode Web"), text("语言与外观", "Language & Appearance")]
    }
    func save() throws {
        guard writable else { throw AppFailure.message(text("钥匙串未能读取，禁止覆盖配置。", "Keychain could not be read; settings will not be overwritten.")) }
        if settings.encryptionEnabled && settings.encryptionPassword.count < 12 {
            throw AppFailure.message(text("加密密码至少需要 12 个字符。", "Encryption passphrase must contain at least 12 characters."))
        }
        try SettingsStore.save(settings)
    }
    func action(_ operation: @escaping () async throws -> Void) {
        guard !busy else { return }
        busy = true
        Task {
            defer { busy = false }
            do { try await operation() }
            catch { self.error = error.localizedDescription }
        }
    }
    func saveAction() {
        action {
            defer { self.refreshRuntime() }
            let restart = self.botRunning && self.savedEncryption != self.encryptionSignature
            try self.save()
            if FileManager.default.isExecutableFile(atPath: Runtime.engine(self.settings).path) {
                try EngineShellPath.sync(Runtime.engine(self.settings))
            }
            if restart {
                _ = try await Runtime.bot(["stop"], settings: self.settings)
                _ = try await Runtime.bot(["start", "--daemon", "--mode", "installed"], settings: self.settings)
            }
            self.savedEncryption = self.encryptionSignature
            self.refreshRuntime()
            self.message = self.text("设置已保存。", "Settings saved.")
        }
    }
    private var encryptionSignature: String { "\(settings.encryptionEnabled):\(settings.encryptionPassword)" }
    func refreshRuntime() {
        let file = RuntimeMonitor.serviceFile(home: SettingsStore.home)
        if let data = try? Data(contentsOf: file),
           let record = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let pid = record["pid"] as? Int32, pid > 0 {
            botRunning = kill(pid, 0) == 0 || errno == EPERM
        } else { botRunning = false }
        refreshLogs()
    }
    func connect() async throws {
        try save()
        guard let configured = URL(string: settings.serverURL), ["http", "https"].contains(configured.scheme ?? "") else {
            throw AppFailure.message(text("服务器地址无效。", "Invalid server URL."))
        }
        if ["127.0.0.1", "localhost", "::1"].contains(configured.host ?? "") {
            try await stopWeb()
            try await EnginePort.release(port: configured.port ?? 4096, settings: settings)
        }
        if settings.apiVersion == "v2", ["127.0.0.1", "localhost", "::1"].contains(configured.host ?? "") {
            if settings.serverPassword.isEmpty {
                settings.serverPassword = UUID().uuidString + UUID().uuidString
                try save()
            }
            _ = try await Runtime.engineCommand(["service", "unset", "disabled"], settings: settings)
            _ = try await Runtime.engineCommand(["service", "set", "password", settings.serverPassword], settings: settings)
            _ = try await Runtime.engineCommand(["service", "set", "env", "OPENCODE_SERVER_USERNAME", "opencode"], settings: settings)
            let output = try await Runtime.engineCommand(["service", "start"], settings: settings)
            guard let actual = output.components(separatedBy: .newlines).compactMap({ URL(string: $0.trimmingCharacters(in: .whitespaces)) }).last(where: { $0.scheme == "http" && ["127.0.0.1", "localhost", "::1"].contains($0.host ?? "") }) else {
                throw AppFailure.message(text("服务未返回有效地址。", "Service did not return a valid endpoint."))
            }
            settings.serverURL = actual.absoluteString
            settings.serverUsername = "opencode"
            var request = URLRequest(url: actual.appendingPathComponent("api/info"))
            request.setValue("Basic " + Data("opencode:\(settings.serverPassword)".utf8).base64EncodedString(), forHTTPHeaderField: "Authorization")
            let (_, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw AppFailure.message(text("服务认证失败。", "Service authentication failed.")) }
            try save()
        }
        webURL = authenticatedURL(URL(string: settings.serverURL)!)
        message = text("已连接", "Connected")
    }
    func stopWeb() async throws {
        if settings.apiVersion == "v2", let url = URL(string: settings.serverURL), ["127.0.0.1", "localhost", "::1"].contains(url.host ?? "") {
            _ = try await Runtime.engineCommand(["service", "stop"], settings: settings)
        }
    }
    func setStartup(_ kind: String, enabled: Bool) {
        do {
            try save(); try StartupManager.set(kind, enabled: enabled)
            appStartup = StartupManager.enabled("app"); botStartup = StartupManager.enabled("bot")
            message = text("登录启动设置已保存，下次登录生效。", "Login startup settings saved; changes apply at next login.")
        } catch { self.error = error.localizedDescription }
    }
    func installEngine(_ version: String) {
        action {
            self.engineProgressTitle = self.text("安装 OpenCode", "Install OpenCode")
            self.engineProgress = EngineUpdateProgress(phase: .installing)
            defer { self.engineProgress = nil }
            self.settings.executable = try await EngineManager.install(version, settings: self.settings)
            self.settings.apiVersion = version
            self.settings.serverURL = "http://127.0.0.1:49374"
            try self.save()
            self.message = self.text("引擎已安装。", "Engine installed.")
            try EngineShellPath.sync(Runtime.engine(self.settings))
        }
    }
    func scanEngines() {
        action {
            self.engineProgressTitle = self.text("扫描 OpenCode", "Scan OpenCode")
            self.engineProgress = EngineUpdateProgress(phase: .scanning)
            defer { self.engineProgress = nil }
            self.message = self.text("正在扫描本机 OpenCode…", "Scanning local OpenCode installations…")
            self.engines = await EngineManager.scan(settings: self.settings, additionalRoots: self.scanFolders.map { URL(fileURLWithPath: $0) })
            self.message = self.text("扫描完成，请在结果中选择要应用的 OpenCode。", "Scan complete. Choose an OpenCode installation from the results.")
        }
    }
    func addScanFolder(_ folder: URL) {
        guard !busy else { return }
        if !scanFolders.contains(folder.path) {
            scanFolders.append(folder.path)
            UserDefaults.standard.set(scanFolders, forKey: "engineScanFolders")
        }
        scanEngines()
    }
    func applyExistingEngine(_ candidate: EngineCandidate) {
        action {
            try await self.applyEngine(path: candidate.path, version: candidate.apiVersion)
            self.message = self.text("已应用 OpenCode ", "Applied OpenCode ") + candidate.version + self.text("，配置和网页地址已同步。", "; configuration and web URL synchronized.")
        }
    }
    func updateEngine() {
        action { [self] in
            let version = "v2"
            self.engineProgressTitle = self.text("更新 OpenCode", "Update OpenCode")
            self.engineProgress = EngineUpdateProgress(phase: .checking)
            defer { self.engineProgress = nil }
            let executable = Runtime.engine(self.settings)
            var local: String?
            if FileManager.default.isExecutableFile(atPath: executable.path) {
                let probe = try await Runtime.run(executable, ["--version"], settings: self.settings, timeout: 15)
                if probe.code == 0 { local = EngineManager.versionNumber(probe.output) }
            }
            let latest = try await EngineManager.latestVersion(version)
            let installed = Runtime.installedEngine(self.settings) != nil
            if installed && !EngineManager.needsUpdate(local: local, latest: latest) {
                self.message = self.text("已经是最新版本，无需更新。", "Already up to date; no update needed.")
                self.showNotice(self.message + "\nOpenCode \(local ?? latest)")
                return
            }
            if !installed, EngineInstallations.isPackaged(executable.path), local != nil, !EngineManager.needsUpdate(local: local, latest: latest) {
                try self.save()
                self.engineProgress = EngineUpdateProgress(phase: .installing)
                let target = try await EngineManager.install(version, settings: self.settings)
                try await self.applyEngine(path: target, version: version)
                self.engines = await EngineManager.scan(settings: self.settings, additionalRoots: self.scanFolders.map { URL(fileURLWithPath: $0) })
                self.message = self.text("已恢复安装 ", "Installation restored: ") + version.uppercased() + self.text("，配置和网页地址已同步。", "; configuration and web URL synchronized.")
                self.showNotice(self.message)
                return
            }
            let plan = try EngineInstallations.updatePlan(executable: executable, version: version, latest: latest, environment: Runtime.environment(self.settings))
            if case .packageManager = plan {
                self.engineProgress = EngineUpdateProgress(phase: .installing)
                self.message = self.text("正在通过原安装工具更新当前引擎…", "Updating the selected engine using its original package manager…")
                try await self.applyEngine(path: EngineInstallations.updateDestination(executable), version: version, updatePlan: plan, latest: latest)
                self.message = self.text("当前 OpenCode 安装已更新，配置和网页地址已同步。", "Selected OpenCode installation updated; configuration and web URL synchronized.")
                return
            }
            self.message = self.text("正在下载并校验最新 OpenCode…", "Downloading and verifying the latest OpenCode…")
            let stage = try await EngineManager.prepareUpdate(version, settings: self.settings) { [weak self] progress in
                guard let self, self.engineProgress != nil else { return }
                if progress.phase == .downloading, progress.received > 0, self.engineProgress?.phase != .downloading { return }
                self.engineProgress = progress
            }
            defer { try? FileManager.default.removeItem(at: stage) }
            self.engineProgress = EngineUpdateProgress(phase: .installing)
            let target: String
            if case .managed = plan { target = (try EngineManager.managed(version)).path }
            else { target = executable.path }
            try await self.applyEngine(path: target, version: version, stage: stage, updatePlan: plan, latest: latest)
            self.message = self.text("OpenCode 已更新，配置和网页地址已同步。", "OpenCode updated; configuration and web URL synchronized.")
        }
    }
    private func applyEngine(path: String, version: String, stage: URL? = nil, updatePlan: EngineUpdatePlan? = nil, latest: String? = nil) async throws {
        try save()
        refreshRuntime()
        let restartBot = botRunning
        let previous = settings
        defer { refreshRuntime() }
        if restartBot { _ = try await Runtime.bot(["stop"], settings: settings) }
        do {
            try await stopWeb()
            if let updatePlan {
                switch updatePlan {
                case .managed:
                    if let stage { _ = try EngineManager.commitUpdate(stage, version: version) }
                case .standalone(let target):
                    guard let stage else { throw AppFailure.message("Missing verified OpenCode download.") }
                    try await EngineInstallations.replaceStandalone(target, from: stage)
                case .packageManager(let tool, let arguments, let environment):
                    let result = try await Runtime.run(tool, arguments, settings: settings, timeout: 600, environmentOverrides: environment)
                    guard result.code == 0 else { throw AppFailure.message(result.output) }
                }
                if let latest {
                    let probe = try await Runtime.run(URL(fileURLWithPath: path), ["--version"], settings: settings, timeout: 15)
                    let installed = EngineManager.versionNumber(probe.output)
                    guard probe.code == 0, !EngineManager.needsUpdate(local: installed, latest: latest) else {
                        throw AppFailure.message(text("原安装位置的版本校验失败；未切换到其他安装。", "Version validation failed at the original installation; no other installation was selected."))
                    }
                }
            }
            settings.executable = path
            settings.apiVersion = version
            settings.serverURL = "http://127.0.0.1:49374"
            webURL = nil
            if engineProgress != nil { engineProgress = EngineUpdateProgress(phase: .connecting) }
            try await connect()
            try EngineShellPath.sync(Runtime.engine(settings))
            webReloadRevision += 1
            if restartBot { _ = try await Runtime.bot(["start", "--daemon", "--mode", "installed"], settings: settings) }
        } catch {
            settings = previous
            try? save()
            try? EngineShellPath.sync(Runtime.engine(settings))
            webURL = nil
            if restartBot {
                try? await connect()
                _ = try? await Runtime.bot(["start", "--daemon", "--mode", "installed"], settings: settings)
            }
            throw error
        }
    }
    func uninstallEngine(_ version: String) {
        action {
            if self.settings.apiVersion == version {
                _ = try await Runtime.bot(["stop"], settings: self.settings)
                try await self.stopWeb()
            }
            try await EngineManager.uninstall(version)
            if self.settings.executable == (try EngineManager.managed(version)).path { self.settings.executable = "" }
            try self.save(); self.message = self.text("已卸载应用管理的引擎；包内和外部引擎保持不变。", "Managed engine removed; packaged and external engines remain unchanged.")
        }
    }
    func uninstallExistingEngine(_ candidate: EngineCandidate) {
        action {
            try self.save()
            let executable = URL(fileURLWithPath: candidate.path)
            let environment = Runtime.environment(self.settings)
            let plan = try EngineInstallations.removalPlan(executable: executable, version: candidate.apiVersion, environment: environment)
            let current = Runtime.engine(self.settings)
            let currentPlan = try? EngineInstallations.removalPlan(executable: current, version: self.settings.apiVersion, environment: environment)
            let active = current.resolvingSymlinksInPath() == executable.resolvingSymlinksInPath() || currentPlan?.identity == plan.identity
            self.engineProgressTitle = self.text("卸载 OpenCode", "Uninstall OpenCode")
            self.engineProgress = EngineUpdateProgress(phase: .uninstalling)
            defer { self.engineProgress = nil; self.refreshRuntime() }
            if active {
                _ = try await Runtime.bot(["stop"], settings: self.settings)
                try await self.stopWeb()
                self.webURL = nil
            }
            try await EngineInstallations.remove(plan, settings: self.settings)
            if active {
                self.settings.executable = ""
                try self.save()
                self.webReloadRevision += 1
            }
            self.engines = await EngineManager.scan(settings: self.settings, additionalRoots: self.scanFolders.map { URL(fileURLWithPath: $0) })
            self.message = self.text("所选安装已卸载，配置、会话数据和日志已保留。", "Selected installation removed; configuration, session data, and logs retained.")
            self.showNotice(self.message)
        }
    }
    func authenticatedURL(_ url: URL) -> URL {
        guard settings.apiVersion == "v2", !settings.serverPassword.isEmpty else { return url }
        var parts = URLComponents(url: url, resolvingAgainstBaseURL: false)!
        var query = (parts.queryItems ?? []).filter { $0.name != "auth_token" }
        query.append(URLQueryItem(name: "auth_token", value: Data("opencode:\(settings.serverPassword)".utf8).base64EncodedString()))
        parts.queryItems = query
        return parts.url!
    }
    func botAction(_ command: String) {
        action {
            defer { self.refreshRuntime() }
            try self.save()
            if command == "start" { try await self.connect() }
            let output = try await Runtime.bot(command == "start" ? ["start", "--daemon", "--mode", "installed"] : [command], settings: self.settings)
            self.message = command == "start" ? self.text("服务已启动。", "Service started.") : (command == "stop" ? self.text("服务已停止。", "Service stopped.") : output)
            self.savedEncryption = self.encryptionSignature
            self.refreshRuntime()
        }
    }
    func refreshLogs() {
        let folder = SettingsStore.home.appendingPathComponent("logs")
        let added = sessionLogs.readNew(folder: folder)
        if !added.isEmpty { logs = String((logs + added).suffix(65536)) }
    }
    func cleanup() async throws {
        noticeTask?.cancel()
        monitor?.invalidate(); monitor = nil
        console.stop()
        if FileManager.default.isExecutableFile(atPath: Runtime.resources.appendingPathComponent("runtime/bin/node").path) {
            _ = try await Runtime.bot(["stop"], settings: settings)
        }
        if FileManager.default.isExecutableFile(atPath: Runtime.engine(settings).path) { try await stopWeb() }
    }
}
