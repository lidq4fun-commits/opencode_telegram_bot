import Foundation

struct EngineCandidate: Identifiable {
    let path: String
    let version: String
    var id: String { path }
}

enum EngineManager {
    static func managed(_ version: String) throws -> URL {
        guard ["v1", "v2"].contains(version) else { throw AppFailure.message("Invalid API version.") }
        return SettingsStore.home.appendingPathComponent("opencode-runtime/\(version)/opencode")
    }
    static func install(_ version: String, settings: Settings) async throws -> String {
        let target = try managed(version)
        let source = Runtime.resources.appendingPathComponent("dependencies/opencode/\(version)/opencode")
        guard FileManager.default.isExecutableFile(atPath: source.path) else { throw AppFailure.message("Packaged \(version) engine is missing.") }
        return try await Task.detached {
            try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
            let stage = target.deletingLastPathComponent().appendingPathComponent("opencode-\(UUID().uuidString).tmp")
            defer { try? FileManager.default.removeItem(at: stage) }
            try FileManager.default.copyItem(at: source, to: stage)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: stage.path)
            // Rename atomically, including replacement of an earlier managed copy.
            guard rename(stage.path, target.path) == 0 else { throw AppFailure.message("Unable to install the managed engine (errno \(errno)).") }
            return target.path
        }.value
    }
    static func uninstall(_ version: String) async throws {
        let target = try managed(version)
        try await Task.detached {
            if FileManager.default.fileExists(atPath: target.path) { try FileManager.default.removeItem(at: target) }
        }.value
    }
    static func scan(settings: Settings) async -> [EngineCandidate] {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let names = [settings.executable, "/opt/homebrew/bin/opencode", "/usr/local/bin/opencode", "\(home)/.bun/bin/opencode", "\(home)/.opencode/bin/opencode"]
        var paths = Set(names.filter { !$0.isEmpty })
        for folder in Runtime.environment(settings)["PATH", default: ""].split(separator: ":") { paths.insert(String(folder) + "/opencode") }
        for version in ["v1", "v2"] {
            paths.insert(Runtime.resources.appendingPathComponent("dependencies/opencode/\(version)/opencode").path)
            if let url = try? managed(version) { paths.insert(url.path) }
        }
        var result: [EngineCandidate] = []
        for path in paths.sorted() where FileManager.default.isExecutableFile(atPath: path) {
            if let probe = try? await Runtime.run(URL(fileURLWithPath: path), ["--version"], settings: settings, timeout: 10), probe.code == 0,
               probe.output.range(of: "(^|\\s)[12]\\.[0-9]+\\.[0-9]+", options: .regularExpression) != nil {
                result.append(EngineCandidate(path: path, version: probe.output))
            }
        }
        return result
    }
}
