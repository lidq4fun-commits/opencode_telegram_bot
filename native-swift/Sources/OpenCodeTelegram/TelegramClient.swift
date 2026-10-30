import Foundation

struct TelegramIdentity: Decodable {
    let id: Int64
    let username: String?
    let firstName: String?
    let lastName: String?
    enum CodingKeys: String, CodingKey {
        case id, username
        case firstName = "first_name", lastName = "last_name"
    }
    var label: String {
        if let username, !username.isEmpty { return "@" + username }
        let name = [firstName, lastName].compactMap { $0 }.joined(separator: " ")
        return name.isEmpty ? String(id) : name
    }
    static func parse(_ data: Data) throws -> TelegramIdentity {
        struct Envelope: Decodable { let schema: Int; let data: TelegramIdentity }
        let envelope = try JSONDecoder().decode(Envelope.self, from: data)
        guard envelope.schema == 1 else { throw AppFailure.message("Unsupported tg output schema.") }
        return envelope.data
    }
}

struct TelegramMessage: Decodable, Identifiable {
    struct Peer: Decodable {
        let type: String
        let id: Int64
        let username: String?
        let name: String?
        var label: String { username.map { "@" + $0 } ?? name ?? "\(type):\(id)" }
    }
    struct Message: Decodable {
        let id: Int
        let date: Int
        let out: Bool
        let from: Peer?
        let text: String?
        let media: String?
    }
    let peer: Peer
    let message: Message
    var id: String { "\(peer.type):\(peer.id):\(message.id)" }
}

// tg watch emits NDJSON, unlike one-shot commands' schema/data envelope.
struct TelegramLineBuffer {
    private var pending = Data()
    mutating func append(_ data: Data) throws -> [Data] {
        pending.append(data)
        var lines: [Data] = []
        while let newline = pending.firstIndex(of: 10) {
            guard pending.distance(from: pending.startIndex, to: newline) <= 262_144 else {
                throw AppFailure.message("tg output line is too large.")
            }
            let line = Data(pending[..<newline])
            pending.removeSubrange(...newline)
            if !line.isEmpty { lines.append(line) }
        }
        guard pending.count <= 262_144 else { throw AppFailure.message("tg output line is too large.") }
        return lines
    }
}

enum TelegramCommand: String { case initialize = "init", identity = "whoami", login, watch, logout }

enum TelegramClientConfiguration {
    static func executable(environment: [String: String] = ProcessInfo.processInfo.environment,
                           fallback: URL = Runtime.resources.appendingPathComponent("dependencies/tg/tg")) -> URL {
        for directory in (environment["PATH"] ?? "").components(separatedBy: ":") where !directory.isEmpty {
            let candidate = URL(fileURLWithPath: directory).appendingPathComponent("tg")
            if FileManager.default.isExecutableFile(atPath: candidate.path) { return candidate }
        }
        return fallback
    }
    static var defaultDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/gotd")
    }
    // Preserve existing CLI configuration. Old desktop bindings use the same
    // keychain store, so migrating the config does not require another login.
    static func migrateConfig(_ config: URL, at destination: URL) throws {
        let files = FileManager.default
        guard files.fileExists(atPath: config.path) else { return }
        if destination.resolvingSymlinksInPath().standardizedFileURL == config.resolvingSymlinksInPath().standardizedFileURL { return }
        if files.fileExists(atPath: destination.path) || (try? files.destinationOfSymbolicLink(atPath: destination.path)) != nil {
            return
        }
        try files.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try files.copyItem(at: config, to: destination)
        try files.setAttributes([.posixPermissions: 0o600], ofItemAtPath: destination.path)
    }
    static func arguments(_ command: TelegramCommand, config: URL) -> [String] {
        (config == defaultDirectory.appendingPathComponent("gotd.cli.yaml") ? [] : ["--config", config.path]) + ["--output", "json", command.rawValue]
    }
    static func environment(_ inherited: [String: String]) -> [String: String] {
        // Do not inherit Bot credentials, debug flags or a CLI user's account selection.
        inherited.filter { key, _ in
            !key.hasPrefix("TG_") && !key.hasPrefix("TELEGRAM_") &&
            !key.hasPrefix("OPENCODE_") && !["BOT_TOKEN", "APP_ID", "APP_HASH"].contains(key)
        }
    }
    static func qrURL(_ text: String) -> String? {
        guard let regex = try? NSRegularExpression(pattern: "tg://login\\?token=[A-Za-z0-9_-]+"),
              let match = regex.matches(in: text, range: NSRange(text.startIndex..., in: text)).last,
              let range = Range(match.range, in: text) else { return nil }
        return String(text[range])
    }
}

// Pipes are drained concurrently; private output stays in memory, never in Bot logs.
@MainActor final class TelegramProcess {
    private var process: Process?
    private var input: FileHandle?
    private var runID = UUID()
    private var timeout: Task<Void, Never>?
    var running: Bool { process != nil }

    func start(executable: URL, arguments: [String], directory: URL, timeout seconds: UInt64?,
               output: @escaping (Data, Bool) -> Void, completion: @escaping (Int32, Bool) -> Void) throws {
        guard process == nil else { throw AppFailure.message("A tg operation is already running.") }
        let child = Process(), stdout = Pipe(), stderr = Pipe(), stdin = Pipe()
        child.executableURL = executable
        child.arguments = arguments
        child.currentDirectoryURL = directory
        child.environment = TelegramClientConfiguration.environment(ProcessInfo.processInfo.environment)
        child.standardOutput = stdout; child.standardError = stderr; child.standardInput = stdin
        try child.run()
        let id = UUID(); runID = id
        process = child; input = stdin.fileHandleForWriting
        let readers = DispatchGroup()
        for (pipe, isError) in [(stdout, false), (stderr, true)] {
            readers.enter()
            DispatchQueue.global(qos: .utility).async { [weak self] in
                while true {
                    let data = pipe.fileHandleForReading.availableData
                    if data.isEmpty { break }
                    DispatchQueue.main.async {
                        guard let self, self.runID == id else { return }
                        output(data, isError)
                    }
                }
                readers.leave()
            }
        }
        DispatchQueue.global(qos: .utility).async { [weak self] in
            child.waitUntilExit()
            readers.wait()
            DispatchQueue.main.async {
                guard let self, self.runID == id else { return }
                self.timeout?.cancel(); self.timeout = nil
                self.process = nil; self.input = nil
                completion(child.terminationStatus, child.terminationReason == .uncaughtSignal)
            }
        }
        if let seconds {
            timeout = Task { [weak self] in
                do { try await Task.sleep(nanoseconds: seconds * 1_000_000_000) }
                catch { return }
                guard let self, self.runID == id else { return }
                self.timeout = nil
                await self.stop()
                completion(-1, false)
            }
        }
    }
    func submitPassword(_ value: String) throws {
        guard !value.contains("\n"), !value.contains("\r"), let input else {
            throw AppFailure.message("No valid tg password prompt is active.")
        }
        try input.write(contentsOf: Data((value + "\n").utf8))
    }
    func stop() async {
        runID = UUID(); timeout?.cancel(); timeout = nil
        guard let child = process else { return }
        process = nil
        try? input?.close(); input = nil
        if child.isRunning { child.interrupt() }
        for _ in 0..<30 {
            if !child.isRunning { return }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        if child.isRunning { kill(child.processIdentifier, SIGKILL) }
        for _ in 0..<10 {
            if !child.isRunning { break }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
    }
}

@MainActor final class TelegramClient: ObservableObject {
    enum State { case idle, signedOut, checking, loggingIn, passwordRequired, connecting, watching, stopped, failed }
    @Published private(set) var state: State = .idle
    @Published private(set) var identity: TelegramIdentity?
    @Published private(set) var qrURL: String?
    @Published private(set) var messages: [TelegramMessage] = []
    @Published private(set) var operation: TelegramCommand?
    @Published private(set) var failure: String = ""
    private let runner = TelegramProcess()
    private let executable: URL
    private let directory: URL
    private let legacyConfig: URL?
    private var stdout = Data()
    private var stderr = ""
    private var lines = TelegramLineBuffer()
    private var closed = false
    private var transition = false
    var working: Bool { transition || (operation != nil && operation != .watch) }

    init(executable: URL = TelegramClientConfiguration.executable(),
         directory: URL = TelegramClientConfiguration.defaultDirectory, legacyConfig: URL? = nil) {
        self.executable = executable; self.directory = directory
        self.legacyConfig = legacyConfig ?? (directory == TelegramClientConfiguration.defaultDirectory
            ? SettingsStore.home.appendingPathComponent("tg/gotd.cli.yaml") : nil)
    }
    var configFile: URL { directory.appendingPathComponent("gotd.cli.yaml") }
    private func prepareConfig() -> Bool {
        do {
            if let legacyConfig { try TelegramClientConfiguration.migrateConfig(legacyConfig, at: configFile) }
            return true
        } catch {
            state = .failed; failure = "TG 登录配置迁移失败：\(configFile.path)"
            return false
        }
    }
    func startAutomatically() {
        guard !closed, state == .idle else { return }
        guard prepareConfig() else { return }
        // No config means no login yet: do not launch an interactive flow on startup.
        guard FileManager.default.fileExists(atPath: configFile.path) else { state = .signedOut; return }
        check()
    }
    func check() {
        guard !closed, !working else { return }
        guard prepareConfig() else { return }
        guard FileManager.default.fileExists(atPath: configFile.path) else { state = .signedOut; return }
        transition = true
        Task {
            await runner.stop(); operation = nil; transition = false
            guard !closed else { return }
            run(.identity)
        }
    }
    func login() {
        guard !closed, !working else { return }
        guard prepareConfig() else { return }
        identity = nil; messages = []; failure = ""
        run(FileManager.default.fileExists(atPath: configFile.path) ? .login : .initialize)
    }
    func startListening() {
        guard !closed, !working, !runner.running, identity != nil else { return }
        run(.watch)
    }
    func stopListening() {
        guard !closed, !transition else { return }
        transition = true
        Task {
            await runner.stop(); operation = nil; qrURL = nil
            transition = false
            guard !closed else { return }
            state = identity == nil ? .signedOut : .stopped
        }
    }
    func logout() {
        guard !closed, !working else { return }
        transition = true
        Task {
            await runner.stop(); operation = nil; transition = false
            guard !closed else { return }
            run(.logout)
        }
    }
    func submitPassword(_ value: String) {
        guard state == .passwordRequired, !value.isEmpty else { return }
        guard !value.contains("\n"), !value.contains("\r") else { return }
        do { try runner.submitPassword(value); stderr = ""; state = .loggingIn }
        catch { fail("password") }
    }
    func clearMessages() { messages = [] }
    func shutdown() async {
        closed = true
        await runner.stop()
        operation = nil; qrURL = nil; stdout = Data(); stderr = ""; messages = []; identity = nil
        state = .stopped
    }
    private func run(_ command: TelegramCommand) {
        guard !closed else { return }
        stdout = Data(); stderr = ""; lines = TelegramLineBuffer(); qrURL = nil; failure = ""
        operation = command
        state = command == .login ? .loggingIn : command == .watch ? .connecting : .checking
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
            try runner.start(executable: executable,
                arguments: TelegramClientConfiguration.arguments(command, config: configFile), directory: directory,
                timeout: command == .watch ? nil : command == .login ? 300 : 30,
                output: { [weak self] data, isError in self?.consume(data, isError: isError, command: command) },
                completion: { [weak self] code, _ in self?.finished(command, code: code) })
        } catch { fail("launch") }
    }
    private func consume(_ data: Data, isError: Bool, command: TelegramCommand) {
        guard !closed, !transition else { return }
        if isError {
            stderr = String((stderr + String(decoding: data, as: UTF8.self)).suffix(32_768))
            if command == .login {
                if let url = TelegramClientConfiguration.qrURL(stderr) { qrURL = url }
                if stderr.contains("2FA password:") { qrURL = nil; state = .passwordRequired }
            }
            if command == .watch, stderr.contains("Watching ") { state = .watching }
            return
        }
        do {
            if command == .watch {
                for line in try lines.append(data) {
                    let event = try JSONDecoder().decode(TelegramMessage.self, from: line)
                    // This page is an incoming-message monitor, not a local history archive.
                    if !event.message.out {
                        messages.removeAll { $0.id == event.id }
                        messages.append(event)
                        if messages.count > 200 { messages.removeFirst(messages.count - 200) }
                    }
                }
            } else {
                guard stdout.count + data.count <= 262_144 else { throw AppFailure.message("tg output is too large.") }
                stdout.append(data)
            }
        } catch {
            transition = true
            Task {
                await runner.stop(); transition = false
                if !closed { fail("output") }
            }
        }
    }
    private func finished(_ command: TelegramCommand, code: Int32) {
        guard !closed else { return }
        operation = nil; qrURL = nil
        defer { stdout = Data(); stderr = "" }
        guard code == 0 else {
            if stderr.contains("not authorized") || stderr.contains("AUTH_KEY_UNREGISTERED") || stderr.contains("SESSION_REVOKED") {
                identity = nil; state = .signedOut
            } else { fail(code == -1 ? "timeout" : command.rawValue) }
            return
        }
        switch command {
        case .initialize:
            // The config was initialized, not authenticated. Only a user click enters here.
            run(.login)
        case .identity, .login:
            do {
                identity = try TelegramIdentity.parse(stdout)
                startListening()
            }
            catch { fail("output") }
        case .watch: state = .stopped
        case .logout: identity = nil; messages = []; state = .signedOut
        }
    }
    private func fail(_ reason: String) {
        operation = nil; qrURL = nil; state = .failed; failure = reason
    }
}
