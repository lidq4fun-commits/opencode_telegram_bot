import Foundation

struct CommandResult {
    var code: Int32
    var output: String
}

enum Runtime {
    static var resources: URL {
        dependencyRoot(for: Bundle.main.bundleURL)
    }
    static func dependencyRoot(for application: URL) -> URL {
        application.deletingLastPathComponent().appendingPathComponent("OpenCodeTelegram-dependencies", isDirectory: true)
    }
    static func engine(_ settings: Settings, home: URL = SettingsStore.home, resources: URL = Runtime.resources) -> URL {
        let legacy = home.appendingPathComponent("opencode-runtime").path + "/"
        if !settings.executable.isEmpty && !settings.executable.hasPrefix(legacy)
            && !settings.executable.contains(".app/Contents/Resources/dependencies/opencode/") {
            return URL(fileURLWithPath: settings.executable)
        }
        return resources.appendingPathComponent("dependencies/opencode/\(settings.apiVersion)/opencode")
    }
    static func installedEngine(_ settings: Settings, home: URL = SettingsStore.home, resources: URL = Runtime.resources) -> URL? {
        let candidate = engine(settings, home: home, resources: resources)
        guard FileManager.default.isExecutableFile(atPath: candidate.path),
              !EngineInstallations.isPackaged(candidate.path) else { return nil }
        return candidate
    }
    static func environment(_ settings: Settings, home: URL = SettingsStore.home) -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        env["OPENCODE_TELEGRAM_HOME"] = home.path
        env["OPENCODE_TELEGRAM_RUNTIME_MODE"] = "installed"
        env["OPENCODE_TELEGRAM_DESKTOP_SYNC"] = "1"
        env["TELEGRAM_BOT_TOKEN"] = settings.botToken
        env["TELEGRAM_ALLOWED_USER_ID"] = settings.allowedUserID
        env["BOT_LOCALE"] = settings.locale
        env["OPENCODE_SERVER_VERSION"] = "v2"
        env["OPENCODE_API_URL"] = settings.serverURL
        env["OPENCODE_SERVER_USERNAME"] = settings.serverUsername
        env["OPENCODE_SERVER_PASSWORD"] = settings.serverPassword
        env["OPENCODE_PASSWORD"] = settings.serverPassword
        env["OPENCODE_EXECUTABLE_PATH"] = engine(settings).path
        env["OPENCODE_MODEL_PROVIDER"] = settings.modelProvider
        env["OPENCODE_MODEL_ID"] = settings.modelID
        env["OPENCODE_TELEGRAM_EOT10_ENABLED"] = settings.encryptionEnabled ? "1" : "0"
        env["OPENCODE_TELEGRAM_EOT10_PASSWORD"] = settings.encryptionEnabled ? settings.encryptionPassword : ""
        env["OPENCODE_AUTO_RESTART_ENABLED"] = "true"
        let git = resources.appendingPathComponent("dependencies/git")
        if FileManager.default.isExecutableFile(atPath: git.appendingPathComponent("bin/git").path) {
            env["GIT_EXEC_PATH"] = git.appendingPathComponent("libexec/git-core").path
            env["GIT_TEMPLATE_DIR"] = git.appendingPathComponent("share/git-core/templates").path
        }
        env["PATH"] = [engine(settings).deletingLastPathComponent().path,
            resources.appendingPathComponent("runtime/bin").path,
            resources.appendingPathComponent("dependencies/bun").path,
            git.appendingPathComponent("bin").path,
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", env["PATH"] ?? ""].joined(separator: ":")
        return env
    }
    static func run(_ executable: URL, _ arguments: [String], settings: Settings, timeout: TimeInterval = 60, environmentOverrides: [String: String] = [:]) async throws -> CommandResult {
        try await Task.detached { try runBlocking(executable, arguments, settings: settings, timeout: timeout, environmentOverrides: environmentOverrides) }.value
    }
    private static func runBlocking(_ executable: URL, _ arguments: [String], settings: Settings, timeout: TimeInterval, environmentOverrides: [String: String]) throws -> CommandResult {
            try FileManager.default.createDirectory(at: SettingsStore.home, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: SettingsStore.home.path)
            let process = Process()
            process.executableURL = executable
            process.arguments = arguments
            process.environment = environment(settings)
            for (key, value) in environmentOverrides { process.environment?[key] = value }
            process.currentDirectoryURL = SettingsStore.home
            // A regular temporary file avoids pipe deadlocks on large CLI output.
            let output = SettingsStore.home.appendingPathComponent("command-\(UUID().uuidString).tmp")
            FileManager.default.createFile(atPath: output.path, contents: nil, attributes: [.posixPermissions: 0o600])
            defer { try? FileManager.default.removeItem(at: output) }
            let handle = try FileHandle(forWritingTo: output)
            defer { try? handle.close() }
            process.standardOutput = handle
            process.standardError = handle
            let ended = DispatchSemaphore(value: 0)
            process.terminationHandler = { _ in ended.signal() }
            try process.run()
            if ended.wait(timeout: .now() + timeout) == .timedOut {
                process.terminate()
                if ended.wait(timeout: .now() + 3) == .timedOut { kill(process.processIdentifier, SIGKILL); process.waitUntilExit() }
                throw AppFailure.message("Command timed out: \(executable.lastPathComponent)")
            }
            let text = String(data: try Data(contentsOf: output), encoding: .utf8) ?? ""
            return CommandResult(code: process.terminationStatus, output: text.trimmingCharacters(in: .whitespacesAndNewlines))
    }
    static func bot(_ args: [String], settings: Settings) async throws -> String {
        let result = try await run(resources.appendingPathComponent("runtime/bin/node"),
            [resources.appendingPathComponent("bot/dist/cli.js").path] + args, settings: settings)
        guard result.code == 0 else { throw AppFailure.message(result.output) }
        return result.output
    }
    static func engineCommand(_ args: [String], settings: Settings) async throws -> String {
        let probe = try await run(engine(settings), ["--version"], settings: settings, timeout: 15)
        guard probe.code == 0, EngineManager.versionNumber(probe.output) != nil else {
            throw AppFailure.message("Only OpenCode V2 is supported. Select or install a V2 engine.")
        }
        let result = try await run(engine(settings), args, settings: settings)
        guard result.code == 0 else { throw AppFailure.message(result.output) }
        return result.output
    }
}
