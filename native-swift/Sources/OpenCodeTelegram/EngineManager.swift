import Foundation
import CryptoKit

struct EngineCandidate: Identifiable, Sendable {
    let path: String
    let version: String
    var id: String { path }
    var apiVersion: String { "v2" }
    func isBundled(resources: URL = Runtime.resources) -> Bool {
        URL(fileURLWithPath: path).resolvingSymlinksInPath() == resources.appendingPathComponent("dependencies/opencode/v2/opencode").resolvingSymlinksInPath()
    }
    func isActive(settings: Settings, home: URL = SettingsStore.home, resources: URL = Runtime.resources) -> Bool {
        URL(fileURLWithPath: path).resolvingSymlinksInPath() == Runtime.engine(settings, home: home, resources: resources).resolvingSymlinksInPath()
    }
}

enum EngineManager {
    static func needsUpdate(local: String?, latest: String) -> Bool {
        guard let local else { return true }
        guard local.split(separator: ".").first == latest.split(separator: ".").first else { return true }
        return local.compare(latest, options: .numeric) == .orderedAscending
    }
    static func latestVersion(_ version: String) async throws -> String {
        guard version == "v2" else { throw AppFailure.message("Only OpenCode V2 is supported.") }
        let package = "@opencode/cli-darwin-arm64"
        let url = URL(string: "https://registry.npmjs.org/\(package)/latest")!
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let (data, response) = try await session.data(for: URLRequest(url: url, timeoutInterval: 30))
        guard (response as? HTTPURLResponse)?.statusCode == 200,
              let metadata = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let latest = metadata["version"] as? String,
              versionNumber(latest) == latest,
              latest.hasPrefix("2.") else {
            throw AppFailure.message("Unable to check the latest OpenCode version.")
        }
        return latest
    }
    static func versionNumber(_ output: String) -> String? {
        guard let range = output.range(of: "(?<![0-9])2\\.[0-9]+\\.[0-9]+(?![0-9])", options: .regularExpression) else { return nil }
        return String(output[range])
    }
    static func preferred(_ candidates: [EngineCandidate], version: String) -> EngineCandidate? {
        candidates.filter { version == "v2" && $0.version.hasPrefix("2.") }.sorted {
            let order = $0.version.compare($1.version, options: .numeric)
            return order == .orderedSame ? $0.path < $1.path : order == .orderedDescending
        }.first
    }
    static func prepareUpdate(_ version: String, settings: Settings, progress: @escaping @MainActor @Sendable (EngineUpdateProgress) -> Void = { _ in }) async throws -> URL {
        guard version == "v2" else { throw AppFailure.message("Only OpenCode V2 is supported.") }
        await progress(EngineUpdateProgress(phase: .checking))
        let package = "@opencode/cli-darwin-arm64"
        let metadataURL = URL(string: "https://registry.npmjs.org/\(package)/latest")!
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let (metadata, response) = try await session.data(for: URLRequest(url: metadataURL, timeoutInterval: 30))
        guard (response as? HTTPURLResponse)?.statusCode == 200,
              let json = try JSONSerialization.jsonObject(with: metadata) as? [String: Any],
              let release = json["version"] as? String, versionNumber(release) == release,
              release.hasPrefix("2."),
              let dist = json["dist"] as? [String: Any],
              let archive = dist["tarball"] as? String, let url = URL(string: archive),
              url.scheme == "https", url.host == "registry.npmjs.org",
              let integrity = dist["integrity"] as? String, integrity.hasPrefix("sha512-") else {
            throw AppFailure.message("Unable to find a verified stable OpenCode release for \(version).")
        }
        await progress(EngineUpdateProgress(phase: .downloading))
        let delegate = EngineDownloadProgress(report: progress)
        let (download, downloadResponse) = try await delegate.start(URLRequest(url: url, timeoutInterval: 180))
        defer { try? FileManager.default.removeItem(at: download) }
        guard (downloadResponse as? HTTPURLResponse)?.statusCode == 200 else { throw AppFailure.message("OpenCode download failed.") }
        await progress(EngineUpdateProgress(phase: .verifying))
        let hash = try await Task.detached {
            Data(SHA512.hash(data: try Data(contentsOf: download))).base64EncodedString()
        }.value
        guard integrity == "sha512-" + hash else { throw AppFailure.message("OpenCode download integrity check failed.") }
        let target = try managed(version)
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        let stage = target.deletingLastPathComponent().appendingPathComponent("update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: stage, withIntermediateDirectories: true)
        do {
            await progress(EngineUpdateProgress(phase: .unpacking))
            let extracted = try await Runtime.run(URL(fileURLWithPath: "/usr/bin/tar"), ["-xzf", download.path, "-C", stage.path, "package/bin/opencode"], settings: settings)
            let binary = stage.appendingPathComponent("package/bin/opencode")
            guard extracted.code == 0, FileManager.default.fileExists(atPath: binary.path) else { throw AppFailure.message("OpenCode archive does not contain the expected executable.") }
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)
            await progress(EngineUpdateProgress(phase: .validating))
            let probe = try await Runtime.run(binary, ["--version"], settings: settings, timeout: 15)
            guard probe.code == 0, versionNumber(probe.output) == release else { throw AppFailure.message("Downloaded OpenCode version validation failed.") }
            return stage
        } catch {
            try? FileManager.default.removeItem(at: stage)
            throw error
        }
    }
    static func commitUpdate(_ stage: URL, version: String) throws -> String {
        let target = try managed(version)
        let binary = stage.appendingPathComponent("package/bin/opencode")
        guard rename(binary.path, target.path) == 0 else { throw AppFailure.message("Unable to replace managed OpenCode (errno \(errno)).") }
        return target.path
    }
    static func managed(_ version: String, home: URL = Runtime.resources) throws -> URL {
        guard version == "v2" else { throw AppFailure.message("Only OpenCode V2 is supported.") }
        return home.appendingPathComponent("dependencies/opencode/\(version)/opencode")
    }
    static func install(_ version: String, settings: Settings, home: URL = SettingsStore.home, resources: URL = Runtime.resources) async throws -> String {
        let source = resources.appendingPathComponent("dependencies/opencode/\(version)/opencode")
        guard version == "v2", FileManager.default.isExecutableFile(atPath: source.path) else { throw AppFailure.message("External V2 engine is missing. Reinstall the offline package or update the engine.") }
        return source.path
    }
    static func uninstall(_ version: String) async throws {
        let target = try managed(version)
        try await Task.detached {
            if FileManager.default.fileExists(atPath: target.path) { try FileManager.default.removeItem(at: target) }
        }.value
    }
    static func scan(settings: Settings, additionalRoots: [URL] = []) async -> [EngineCandidate] {
        let environment = Runtime.environment(settings)
        var paths = await Task.detached { EngineInstallations.searchPaths(home: FileManager.default.homeDirectoryForCurrentUser, environment: environment, additionalRoots: additionalRoots) }.value
        if !settings.executable.isEmpty { paths.insert(settings.executable) }
        if let login = try? await Runtime.run(URL(fileURLWithPath: "/bin/zsh"), ["-lc", "printf '%s' \"$PATH\""], settings: settings, timeout: 5), login.code == 0 {
            for folder in login.output.split(separator: ":") where folder.hasPrefix("/") {
                paths.insert(String(folder) + "/opencode")
            }
        }
        if let indexed = try? await Runtime.run(URL(fileURLWithPath: "/usr/bin/mdfind"), ["-0", "kMDItemFSName == 'opencode' || kMDItemFSName == 'opencode-v1' || kMDItemFSName == 'opencode-v2'"], settings: settings, timeout: 10), indexed.code == 0 {
            paths.formUnion(indexed.output.split(separator: "\0").map(String.init))
        }
        for version in ["v2"] {
            paths.insert(Runtime.resources.appendingPathComponent("dependencies/opencode/\(version)/opencode").path)
            if let url = try? managed(version) { paths.insert(url.path) }
        }
        return await probeCandidates(paths.sorted(), settings: settings)
    }
    static func probeCandidates(_ paths: [String], settings: Settings) async -> [EngineCandidate] {
        var candidates: [String] = []
        let ordered = [settings.executable] + paths.sorted().filter { $0 != settings.executable }
        for path in ordered where !path.isEmpty && FileManager.default.isExecutableFile(atPath: path) {
            candidates.append(path)
        }
        var result: [EngineCandidate] = []
        for start in stride(from: 0, to: candidates.count, by: 4) {
            let batch = Array(candidates[start..<min(start + 4, candidates.count)])
            let found = await withTaskGroup(of: EngineCandidate?.self) { group in
                for path in batch {
                    group.addTask {
                        guard let probe = try? await Runtime.run(URL(fileURLWithPath: path), ["--version"], settings: settings, timeout: 10), probe.code == 0,
                              let version = versionNumber(probe.output) else { return nil }
                        return EngineCandidate(path: path, version: version)
                    }
                }
                var found: [EngineCandidate] = []
                for await candidate in group { if let candidate { found.append(candidate) } }
                return found
            }
            result.append(contentsOf: found)
        }
        return result.sorted { $0.path < $1.path }
    }
}
