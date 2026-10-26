import AppKit
import SwiftUI

@MainActor final class AppModel: ObservableObject {
    @Published var settings = Settings()
    @Published var page = 0
    @Published var busy = false
    @Published var message = ""
    @Published var logs = ""
    @Published var cliInput = ""
    @Published var cliOutput = ""
    @Published var error = ""
    @Published var webURL: URL?
    @Published var webReloadRevision = 0
    @Published var engines: [EngineCandidate] = []
    @Published var appStartup = StartupManager.enabled("app")
    @Published var botStartup = StartupManager.enabled("bot")
    let console = CLIConsole()
    private var v1Process: Process?
    private var v1Log: FileHandle?
    private var writable = false

    init(loadSettings: Bool = true) {
        settings.locale = UserDefaults.standard.string(forKey: "interfaceLocale") ?? "zh"
        guard loadSettings else { return }
        busy = true
        Task {
            defer { busy = false }
            do {
                settings = try await Task.detached { try SettingsStore.load() }.value
                settings.locale = UserDefaults.standard.string(forKey: "interfaceLocale") ?? settings.locale
                writable = true
            } catch { self.error = error.localizedDescription }
        }
    }
    func text(_ zh: String, _ en: String) -> String { settings.locale == "en" ? en : zh }
    func waitForSettings() async throws {
        for _ in 0..<100 {
            if !busy { break }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        guard writable else { throw AppFailure.message("Keychain settings are not available; startup was cancelled.") }
    }
    var pages: [String] {
        [text("概览", "Overview"), text("机器人账号", "Bot Account"), text("加密设置", "Encryption"),
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
        do { try save(); message = text("设置已保存。", "Settings saved.") }
        catch { self.error = error.localizedDescription }
    }
    func connect() async throws {
        try save()
        guard let configured = URL(string: settings.serverURL), ["http", "https"].contains(configured.scheme ?? "") else {
            throw AppFailure.message(text("服务器地址无效。", "Invalid server URL."))
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
        if settings.apiVersion == "v1", ["127.0.0.1", "localhost", "::1"].contains(configured.host ?? "") {
            var healthy = false
            if let (_, response) = try? await URLSession.shared.data(for: healthRequest(configured)) { healthy = (response as? HTTPURLResponse)?.statusCode == 200 }
            if !healthy {
                if v1Process?.isRunning != true {
                    try FileManager.default.createDirectory(at: SettingsStore.home, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                    let file = SettingsStore.home.appendingPathComponent("web-v1.log")
                    FileManager.default.createFile(atPath: file.path, contents: nil, attributes: [.posixPermissions: 0o600])
                    v1Log = try FileHandle(forWritingTo: file)
                    let process = Process(); process.executableURL = Runtime.engine(settings)
                    process.arguments = ["web", "--hostname", configured.host ?? "127.0.0.1", "--port", String(configured.port ?? 4096)]
                    process.environment = Runtime.environment(settings); process.currentDirectoryURL = SettingsStore.home
                    process.standardOutput = v1Log; process.standardError = v1Log
                    try process.run(); v1Process = process
                    let record: [String: Any] = ["pid": process.processIdentifier, "executable": Runtime.engine(settings).resolvingSymlinksInPath().path]
                    try JSONSerialization.data(withJSONObject: record).write(to: SettingsStore.home.appendingPathComponent("v1-web-process.json"), options: .atomic)
                }
                for _ in 0..<40 {
                    try await Task.sleep(nanoseconds: 250_000_000)
                    if let (_, response) = try? await URLSession.shared.data(for: healthRequest(configured)), (response as? HTTPURLResponse)?.statusCode == 200 { healthy = true; break }
                    if v1Process?.isRunning != true { break }
                }
                guard healthy else { throw AppFailure.message(text("V1 服务未能启动，请查看 web-v1.log。", "V1 service failed to start; inspect web-v1.log.")) }
            }
        }
        webURL = authenticatedURL(URL(string: settings.serverURL)!)
        message = text("已连接", "Connected")
    }
    private func healthRequest(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url.appendingPathComponent("global/health"), timeoutInterval: 1)
        if !settings.serverPassword.isEmpty { request.setValue("Basic " + Data("\(settings.serverUsername):\(settings.serverPassword)".utf8).base64EncodedString(), forHTTPHeaderField: "Authorization") }
        return request
    }
    func stopWeb() async throws {
        if let process = v1Process, process.isRunning {
            process.terminate()
            for _ in 0..<30 { if !process.isRunning { break }; try await Task.sleep(nanoseconds: 100_000_000) }
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            v1Process = nil; try? v1Log?.close(); v1Log = nil
            try? FileManager.default.removeItem(at: SettingsStore.home.appendingPathComponent("v1-web-process.json"))
        } else if let data = try? Data(contentsOf: SettingsStore.home.appendingPathComponent("v1-web-process.json")),
                  let record = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let pid = record["pid"] as? Int32, let executable = record["executable"] as? String {
            let probe = try await Runtime.run(URL(fileURLWithPath: "/bin/ps"), ["-p", String(pid), "-o", "comm="], settings: settings, timeout: 5)
            if probe.code == 0, URL(fileURLWithPath: probe.output).resolvingSymlinksInPath().path == executable {
                kill(pid, SIGTERM)
            }
            try? FileManager.default.removeItem(at: SettingsStore.home.appendingPathComponent("v1-web-process.json"))
        }
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
            self.settings.executable = try await EngineManager.install(version, settings: self.settings)
            self.settings.apiVersion = version
            self.settings.serverURL = version == "v2" ? "http://127.0.0.1:49374" : "http://127.0.0.1:4096"
            try self.save()
            self.message = self.text("引擎已安装。", "Engine installed.")
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
            try self.save()
            if command == "start" { try await self.connect() }
            self.message = try await Runtime.bot(command == "start" ? ["start", "--daemon", "--mode", "installed"] : [command], settings: self.settings)
            self.refreshLogs()
        }
    }
    func refreshLogs() {
        let folder = SettingsStore.home.appendingPathComponent("logs")
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        if let latest = files.filter({ $0.pathExtension == "log" }).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).last {
            if let handle = try? FileHandle(forReadingFrom: latest) {
                defer { try? handle.close() }
                let size = (try? handle.seekToEnd()) ?? 0
                try? handle.seek(toOffset: size > 65536 ? size - 65536 : 0)
                logs = String(data: (try? handle.readToEnd()) ?? Data(), encoding: .utf8) ?? ""
            }
        }
    }
    func cleanup() async throws {
        console.stop()
        if FileManager.default.isExecutableFile(atPath: Runtime.resources.appendingPathComponent("runtime/bin/node").path) {
            _ = try await Runtime.bot(["stop"], settings: settings)
        }
        if FileManager.default.isExecutableFile(atPath: Runtime.engine(settings).path) { try await stopWeb() }
    }
}
