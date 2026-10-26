import Foundation

enum CommandLineParser {
    static func parse(_ input: String) throws -> [String] {
        var args: [String] = [], token = "", quote: Character?, escaped = false, started = false
        for character in input {
            if escaped { token.append(character); escaped = false; started = true; continue }
            if character == "\\", quote != "'" { escaped = true; started = true; continue }
            if let active = quote {
                if character == active { quote = nil } else { token.append(character) }
                continue
            }
            if character == "\"" || character == "'" { quote = character; started = true; continue }
            if character.isWhitespace {
                if started { args.append(token); token = ""; started = false }
            } else { token.append(character); started = true }
        }
        guard quote == nil, !escaped else { throw AppFailure.message("Unterminated quote or escape.") }
        if started { args.append(token) }
        return args
    }
}

@MainActor final class CLIConsole: ObservableObject {
    @Published var output = ""
    @Published var running = false
    private var process: Process?
    private var input: FileHandle?
    private var stdout: Pipe?
    private var stderr: Pipe?
    private var history: [String] = []
    private var historyIndex = 0

    func append(_ value: String) {
        output += value
        if output.count > 200_000 { output = String(output.suffix(200_000)) }
    }
    func remember(_ command: String, sensitive: Bool) {
        guard !sensitive else { return }
        history.append(command); historyIndex = history.count
    }
    func previous(_ direction: Int) -> String {
        historyIndex = max(0, min(history.count, historyIndex + direction))
        return historyIndex < history.count ? history[historyIndex] : ""
    }
    func submitSecret(_ value: String) throws {
        guard let input else { throw AppFailure.message("No interactive command is running.") }
        try input.write(contentsOf: Data((value + "\n").utf8))
    }
    func start(_ arguments: [String], settings: Settings) throws {
        guard !running else { throw AppFailure.message("A command is already running.") }
        let child = Process()
        child.executableURL = Runtime.resources.appendingPathComponent("runtime/bin/node")
        child.arguments = [Runtime.resources.appendingPathComponent("bot/dist/cli.js").path] + arguments
        child.environment = Runtime.environment(settings)
        child.currentDirectoryURL = SettingsStore.home
        let inputPipe = Pipe(), out = Pipe(), err = Pipe()
        child.standardInput = inputPipe; child.standardOutput = out; child.standardError = err
        for pipe in [out, err] {
            pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                if data.isEmpty { handle.readabilityHandler = nil; return }
                let text = String(decoding: data, as: UTF8.self)
                Task { @MainActor in self?.append(text) }
            }
        }
        child.terminationHandler = { [weak self] process in
            Task { @MainActor in
                self?.append("\nExit: \(process.terminationStatus)\n")
                self?.running = false; self?.process = nil; self?.input = nil
            }
        }
        try FileManager.default.createDirectory(at: SettingsStore.home, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        do { try child.run() } catch {
            out.fileHandleForReading.readabilityHandler = nil; err.fileHandleForReading.readabilityHandler = nil
            throw error
        }
        process = child; input = inputPipe.fileHandleForWriting; stdout = out; stderr = err; running = true
    }
    func stop() {
        guard let process, process.isRunning else { return }
        process.terminate()
        Task {
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
        }
    }
}
