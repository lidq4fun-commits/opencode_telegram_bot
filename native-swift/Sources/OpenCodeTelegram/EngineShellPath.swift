import Foundation

enum EngineShellPath {
    static let begin = "# >>> OpenCodeTelegram selected engine >>>"
    static let end = "# <<< OpenCodeTelegram selected engine <<<"

    static func content(_ existing: String, executable: URL) throws -> String {
        let directory = executable.deletingLastPathComponent().path
        guard !directory.contains("\n"), !directory.contains("\r"), !directory.contains(":") else {
            throw AppFailure.message("Engine PATH directories cannot contain line breaks or colons.")
        }
        var lines = existing.components(separatedBy: "\n")
        if let first = lines.firstIndex(of: begin) {
            guard let last = lines[first...].firstIndex(of: end) else {
                throw AppFailure.message("Incomplete OpenCodeTelegram PATH block; shell configuration was not overwritten.")
            }
            lines.removeSubrange(first...last)
        }
        let quoted = "'" + directory.replacingOccurrences(of: "'", with: "'\\''") + "'"
        let prefix = lines.joined(separator: "\n")
        let block = "\(begin)\nexport PATH=\(quoted):\"$PATH\"\nrehash 2>/dev/null || true\n\(end)\n"
        if existing.contains(block) { return existing }
        return prefix + (prefix.hasSuffix("\n") || prefix.isEmpty ? "" : "\n") +
            block
    }

    static func sync(_ executable: URL, home: URL = FileManager.default.homeDirectoryForCurrentUser) throws {
        guard FileManager.default.isExecutableFile(atPath: executable.path) else {
            throw AppFailure.message("Selected OpenCode executable is missing; terminal PATH was not changed.")
        }
        let custom = ProcessInfo.processInfo.environment["ZDOTDIR"]
        let directory = custom.map { URL(fileURLWithPath: $0) } ?? home
        var updates: [(URL, String, String, Bool)] = []
        for name in [".zprofile", ".zshrc"] {
            let file = directory.appendingPathComponent(name).resolvingSymlinksInPath()
            let existed = FileManager.default.fileExists(atPath: file.path)
            let existing = existed ? try String(contentsOf: file, encoding: .utf8) : ""
            let updated = try content(existing, executable: executable)
            updates.append((file, existing, updated, existed))
        }
        var committed: [(URL, String, String, Bool)] = []
        do {
            for update in updates where update.1 != update.2 {
                try update.2.write(to: update.0, atomically: true, encoding: .utf8)
                committed.append(update)
            }
        } catch {
            for update in committed.reversed() {
                if update.3 { try? update.1.write(to: update.0, atomically: true, encoding: .utf8) }
                else { try? FileManager.default.removeItem(at: update.0) }
            }
            throw error
        }
    }
}
