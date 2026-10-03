import Foundation
import CryptoKit

enum UpdatableDependency: String, CaseIterable, Identifiable, Sendable {
    case node, bun
    var id: String { rawValue }
    var title: String { self == .node ? "Node.js" : "Bun" }
    var relativePath: String { self == .node ? "runtime/bin/node" : "dependencies/bun/bun" }
}

struct DependencyRelease: Sendable {
    let version: String
    let archive: URL
    let archiveMember: String
    let checksum: String
    let sha512: Bool
}

enum DependencyUpdater {
    static func version(_ output: String) -> String? {
        let value = output.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalized = value.hasPrefix("v") ? String(value.dropFirst()) : value
        guard normalized.range(of: "^[0-9]+\\.[0-9]+\\.[0-9]+$", options: .regularExpression) != nil else { return nil }
        return normalized
    }
    static func newestCompatibleNode(_ versions: [String], current: String) throws -> String {
        guard let current = version(current), let major = current.split(separator: ".").first else {
            throw AppFailure.message("Cannot determine the current Node major version.")
        }
        guard let latest = versions.compactMap(version).filter({ $0.split(separator: ".").first == major })
            .max(by: { $0.compare($1, options: .numeric) == .orderedAscending }) else {
            throw AppFailure.message("No compatible stable Node release was found.")
        }
        return latest
    }
    static func read(_ url: URL) async throws -> Data {
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let (data, response) = try await session.data(for: URLRequest(url: url, timeoutInterval: 30))
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw AppFailure.message("Dependency release metadata could not be downloaded.")
        }
        return data
    }
    static func current(_ dependency: UpdatableDependency, settings: Settings, resources: URL = Runtime.resources) async throws -> String {
        let result = try await Runtime.run(resources.appendingPathComponent(dependency.relativePath), ["--version"], settings: settings, timeout: 15)
        guard result.code == 0, let value = version(result.output) else { throw AppFailure.message("Unable to read \(dependency.title) version.") }
        return value
    }
    static func release(_ dependency: UpdatableDependency, current: String) async throws -> DependencyRelease {
        switch dependency {
        case .node:
            let data = try await read(URL(string: "https://nodejs.org/dist/index.json")!)
            guard let releases = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else {
                throw AppFailure.message("Invalid Node release metadata.")
            }
            let available = releases.filter { ($0["files"] as? [String])?.contains("osx-arm64-tar") == true }.compactMap { $0["version"] as? String }
            let latest = try newestCompatibleNode(available, current: current)
            let name = "node-v\(latest)-darwin-arm64.tar.gz"
            let sums = try await read(URL(string: "https://nodejs.org/dist/v\(latest)/SHASUMS256.txt")!)
            guard let line = String(decoding: sums, as: UTF8.self).split(separator: "\n").first(where: { $0.split(whereSeparator: { $0.isWhitespace }).last == Substring(name) }),
                  let sum = line.split(whereSeparator: { $0.isWhitespace }).first, sum.count == 64 else {
                throw AppFailure.message("Node archive checksum is missing.")
            }
            return DependencyRelease(version: latest, archive: URL(string: "https://nodejs.org/dist/v\(latest)/\(name)")!, archiveMember: "node-v\(latest)-darwin-arm64/bin/node", checksum: String(sum), sha512: false)
        case .bun:
            let data = try await read(URL(string: "https://registry.npmjs.org/@oven/bun-darwin-aarch64/latest")!)
            guard let metadata = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let raw = metadata["version"] as? String, let latest = version(raw),
                  let dist = metadata["dist"] as? [String: Any], let archive = dist["tarball"] as? String,
                  let url = URL(string: archive), url.scheme == "https", url.host == "registry.npmjs.org",
                  let checksum = dist["integrity"] as? String, checksum.hasPrefix("sha512-") else {
                throw AppFailure.message("Invalid Bun release metadata or checksum.")
            }
            return DependencyRelease(version: latest, archive: url, archiveMember: "package/bin/bun", checksum: String(checksum.dropFirst(7)), sha512: true)
        }
    }
    static func prepare(_ dependency: UpdatableDependency, release: DependencyRelease, settings: Settings,
                        resources: URL = Runtime.resources,
                        progress: @escaping @MainActor @Sendable (EngineUpdateProgress) -> Void) async throws -> URL {
        let target = resources.appendingPathComponent(dependency.relativePath)
        let stage = target.deletingLastPathComponent().appendingPathComponent(".dependency-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: stage, withIntermediateDirectories: true)
        do {
            await progress(EngineUpdateProgress(phase: .downloading))
            let download = EngineDownloadProgress(report: progress)
            let (file, response) = try await download.start(URLRequest(url: release.archive, timeoutInterval: 180))
            defer { try? FileManager.default.removeItem(at: file) }
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw AppFailure.message("Dependency download failed.") }
            await progress(EngineUpdateProgress(phase: .verifying))
            let checksum = try await Task.detached {
                let bytes = try Data(contentsOf: file)
                return release.sha512 ? Data(SHA512.hash(data: bytes)).base64EncodedString() : SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
            }.value
            guard checksum == release.checksum else { throw AppFailure.message("Dependency checksum mismatch; the installed dependency was not changed.") }
            await progress(EngineUpdateProgress(phase: .unpacking))
            let extracted = try await Runtime.run(URL(fileURLWithPath: "/usr/bin/tar"), ["-xzf", file.path, "-C", stage.path, release.archiveMember], settings: settings)
            let binary = stage.appendingPathComponent(release.archiveMember)
            guard extracted.code == 0 else { throw AppFailure.message("Unable to extract dependency executable.") }
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)
            await progress(EngineUpdateProgress(phase: .validating))
            let probe = try await Runtime.run(binary, ["--version"], settings: settings, timeout: 15)
            guard probe.code == 0, version(probe.output) == release.version else { throw AppFailure.message("Dependency executable validation failed.") }
            if dependency == .node {
                let sqlite = try await Runtime.run(binary, ["-e", "const D=require(process.argv[1]);const d=new D(':memory:');if(d.prepare('select 1 as ok').get().ok!==1)process.exit(1);d.close()", resources.appendingPathComponent("bot/node_modules/better-sqlite3").path], settings: settings, timeout: 20)
                guard sqlite.code == 0 else { throw AppFailure.message("Updated Node is incompatible with the Bot SQLite module; the installed Node was not changed.") }
            }
            return stage
        } catch {
            try? FileManager.default.removeItem(at: stage)
            throw error
        }
    }
    static func replace(_ target: URL, from binary: URL) throws -> URL {
        let backup = target.deletingLastPathComponent().appendingPathComponent(".dependency-backup-\(UUID().uuidString)")
        try FileManager.default.copyItem(at: target, to: backup)
        guard rename(binary.path, target.path) == 0 else {
            try? FileManager.default.removeItem(at: backup)
            throw AppFailure.message("Dependency replacement failed; check installation directory permissions.")
        }
        return backup
    }
}
