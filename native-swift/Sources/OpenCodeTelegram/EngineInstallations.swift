import Foundation

enum EngineUpdatePlan {
    case managed
    case standalone(URL)
    case packageManager(URL, [String], [String: String])
}

enum EngineRemovalPlan {
    case files([URL])
    case packageManager(URL, [String], [String: String])

    var identity: String {
        switch self {
        case .files(let files): return files.first?.resolvingSymlinksInPath().path ?? ""
        case .packageManager(let tool, let args, let env):
            var identityArgs = args
            if tool.lastPathComponent == "yarn", let prefix = identityArgs.firstIndex(of: "--prefix") {
                identityArgs.removeSubrange(prefix..<min(prefix + 2, identityArgs.count))
            }
            return ([tool.resolvingSymlinksInPath().path] + identityArgs + [env["BUN_INSTALL"] ?? "", env["PNPM_HOME"] ?? ""]).joined(separator: "\0")
        }
    }
}

enum EngineInstallations {
    static func removalPlan(executable: URL, version: String, environment: [String: String]) throws -> EngineRemovalPlan {
        guard !isPackaged(executable.path) else { throw AppFailure.message("Packaged application engines cannot be removed individually.") }
        let resolved = executable.resolvingSymlinksInPath()
        let attributes = try FileManager.default.attributesOfItem(atPath: resolved.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular else {
            throw AppFailure.message("Only an OpenCode executable file can be removed; directories will not be deleted.")
        }
        let update = try updatePlan(executable: executable, version: version, latest: "0", environment: environment)
        switch update {
        case .managed, .standalone:
            let files = resolved.path == executable.path ? [resolved] : [resolved, executable]
            for file in files {
                guard FileManager.default.isWritableFile(atPath: file.deletingLastPathComponent().path) else {
                    throw AppFailure.message("No permission to remove \(file.path).")
                }
            }
            return .files(files)
        case .packageManager(let tool, let args, let env):
            let package = "@opencode/cli"
            let removal: [String]
            switch tool.lastPathComponent {
            case "brew": removal = ["uninstall", args[1]]
            case "npm": removal = ["uninstall"] + Array(args.dropFirst().dropLast()) + [package]
            case "bun", "pnpm": removal = ["remove", "--global", package]
            case "yarn": removal = ["global", "remove", package] + Array(args.dropFirst(3))
            case "volta": removal = ["uninstall", package]
            default: throw AppFailure.message("Unable to safely uninstall this package-manager installation.")
            }
            return .packageManager(tool, removal, env)
        }
    }

    static func remove(_ plan: EngineRemovalPlan, settings: Settings) async throws {
        switch plan {
        case .files(let files):
            try await Task.detached {
                for file in files {
                    guard unlink(file.path) == 0 else { throw AppFailure.message("Unable to remove \(file.path) (errno \(errno)).") }
                }
            }.value
        case .packageManager(let tool, let arguments, let environment):
            let result = try await Runtime.run(tool, arguments, settings: settings, timeout: 600, environmentOverrides: environment)
            guard result.code == 0 else { throw AppFailure.message(result.output) }
        }
    }
    static func updateDestination(_ executable: URL) -> String {
        guard let cellar = executable.path.range(of: "/Cellar/") else { return executable.path }
        let prefix = String(executable.path[..<cellar.lowerBound])
        let formula = String(executable.path[cellar.upperBound...]).split(separator: "/").first.map(String.init) ?? ""
        return prefix + "/opt/" + formula + "/bin/opencode"
    }
    static func stablePath(_ path: String) -> String {
        let resolved = URL(fileURLWithPath: path).resolvingSymlinksInPath().path
        guard let cellar = resolved.range(of: "/Cellar/") else { return path }
        let prefix = String(resolved[..<cellar.lowerBound])
        let formula = String(resolved[cellar.upperBound...]).split(separator: "/").first.map(String.init) ?? ""
        for candidate in [prefix + "/bin/opencode", prefix + "/bin/opencode-v2", prefix + "/opt/" + formula + "/bin/opencode"] {
            if FileManager.default.isExecutableFile(atPath: candidate), URL(fileURLWithPath: candidate).resolvingSymlinksInPath().path == resolved {
                return candidate
            }
        }
        return path
    }
    static func isPackaged(_ path: String) -> Bool {
        let resolved = URL(fileURLWithPath: path).resolvingSymlinksInPath().path
        if resolved.contains(".app/Contents/Resources/dependencies/opencode/") { return true }
        return false
    }

    static func searchPaths(home: URL, environment: [String: String], additionalRoots: [URL] = []) -> Set<String> {
        var bins = Set((environment["PATH"] ?? "").split(separator: ":").map(String.init))
        let fixed = [".opencode/bin", ".bun/bin", ".npm-global/bin", ".npm/bin", ".local/bin", "bin",
                     ".volta/bin", ".asdf/shims", ".local/share/mise/shims", ".yarn/bin", ".config/yarn/global/node_modules/.bin",
                     "Library/pnpm", ".local/share/pnpm"]
        for folder in fixed { bins.insert(home.appendingPathComponent(folder).path) }
        bins.formUnion(["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "/usr/bin"])
        for variable in ["HOMEBREW_PREFIX", "NPM_CONFIG_PREFIX", "npm_config_prefix", "BUN_INSTALL", "VOLTA_HOME", "OPENCODE_INSTALL_DIR", "PNPM_HOME", "XDG_BIN_HOME"] {
            if let folder = environment[variable], !folder.isEmpty {
                bins.insert(folder); bins.insert(URL(fileURLWithPath: folder).appendingPathComponent("bin").path)
            }
        }
        var roots = [
            (home.appendingPathComponent(".nvm/versions/node"), "bin"),
            (home.appendingPathComponent(".fnm/node-versions"), "installation/bin"),
            (home.appendingPathComponent(".local/share/fnm/node-versions"), "installation/bin"),
            (home.appendingPathComponent("Library/Application Support/fnm/node-versions"), "installation/bin"),
            (home.appendingPathComponent(".asdf/installs/nodejs"), "bin"),
            (home.appendingPathComponent(".local/share/mise/installs/node"), "bin")
        ]
        if let root = environment["NVM_DIR"] { roots.append((URL(fileURLWithPath: root).appendingPathComponent("versions/node"), "bin")) }
        if let root = environment["FNM_DIR"] { roots.append((URL(fileURLWithPath: root).appendingPathComponent("node-versions"), "installation/bin")) }
        if let root = environment["ASDF_DATA_DIR"] { roots.append((URL(fileURLWithPath: root).appendingPathComponent("installs/nodejs"), "bin")) }
        if let root = environment["MISE_DATA_DIR"] { roots.append((URL(fileURLWithPath: root).appendingPathComponent("installs/node"), "bin")) }
        if let root = environment["XDG_DATA_HOME"] {
            roots.append((URL(fileURLWithPath: root).appendingPathComponent("fnm/node-versions"), "installation/bin"))
            roots.append((URL(fileURLWithPath: root).appendingPathComponent("mise/installs/node"), "bin"))
            bins.insert(URL(fileURLWithPath: root).appendingPathComponent("pnpm").path)
        }
        for (root, suffix) in roots {
            for version in (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? [] {
                bins.insert(version.appendingPathComponent(suffix).path)
            }
        }
        var paths = Set<String>()
        for bin in bins where bin.hasPrefix("/") {
            for name in ["opencode", "opencode-v1", "opencode-v2"] {
                paths.insert(URL(fileURLWithPath: bin).appendingPathComponent(name).path)
            }
        }
        for root in additionalRoots {
            guard let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey], options: [.skipsPackageDescendants]) else { continue }
            for case let file as URL in files where ["opencode", "opencode-v1", "opencode-v2"].contains(file.lastPathComponent) {
                paths.insert(file.path)
            }
        }
        return paths
    }

    static func updatePlan(executable: URL, version: String, latest: String, environment: [String: String]) throws -> EngineUpdatePlan {
        guard version == "v2" else { throw AppFailure.message("Only OpenCode V2 is supported.") }
        let resolved = executable.resolvingSymlinksInPath()
        let path = resolved.path
        if isPackaged(path) || path.hasPrefix(Runtime.resources.appendingPathComponent("dependencies/opencode").path + "/") { return .managed }
        guard !path.contains(".app/Contents/") else {
            throw AppFailure.message("This executable belongs to a signed application. Update it using that application's updater.")
        }
        if let module = path.components(separatedBy: "/node_modules/").last, path.contains("/node_modules/") {
            let parts = module.split(separator: "/")
            let name = parts.first?.hasPrefix("@") == true ? parts.prefix(2).joined(separator: "/") : String(parts.first ?? "")
            let known = name == "@opencode/cli" || name.hasPrefix("@opencode/cli-darwin-")
            guard known else { throw AppFailure.message("Unable to identify the OpenCode package. The original installation has not been replaced.") }
        }
        let package = "@opencode/cli@" + latest
        var extra = ["PATH": executable.deletingLastPathComponent().path + ":" + (environment["PATH"] ?? "")]
        func command(_ name: String, _ arguments: [String], preferred: [String] = []) throws -> EngineUpdatePlan {
            let paths = preferred + (extra["PATH"] ?? "").split(separator: ":").map { String($0) + "/" + name }
            guard let tool = paths.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else {
                throw AppFailure.message("\(name) is required to update this installation. The original installation has not been replaced.")
            }
            return .packageManager(URL(fileURLWithPath: tool), arguments, extra)
        }
        if let cellar = path.range(of: "/Cellar/") {
            let prefix = String(path[..<cellar.lowerBound])
            let formula = String(path[cellar.upperBound...]).split(separator: "/").first.map(String.init) ?? ""
            guard !formula.isEmpty else { throw AppFailure.message("Unable to identify the Homebrew formula.") }
            return try command("brew", ["upgrade", formula], preferred: [prefix + "/bin/brew"])
        }
        if let modules = path.range(of: "/lib/node_modules/") {
            let prefix = String(path[..<modules.lowerBound])
            extra["PATH"] = prefix + "/bin:" + (environment["PATH"] ?? "")
            return try command("npm", ["install", "--global", "--prefix", prefix, package], preferred: [prefix + "/bin/npm"])
        }
        if let global = path.range(of: "/install/global/node_modules/") {
            let prefix = String(path[..<global.lowerBound])
            extra["BUN_INSTALL"] = prefix
            return try command("bun", ["add", "--global", "--trust", package], preferred: [prefix + "/bin/bun"])
        }
        if let global = path.range(of: "/global/"), path.contains("/node_modules/") {
            let prefix = String(path[..<global.lowerBound])
            if path.contains("/pnpm/") || prefix == environment["PNPM_HOME"] {
                extra["PNPM_HOME"] = prefix
                return try command("pnpm", ["add", "--global", "--allow-build=@opencode/cli", package], preferred: [prefix + "/pnpm"])
            }
            if path.contains("/yarn/") {
                let folder = prefix + "/global"
                var arguments = ["global", "add", package, "--global-folder", folder]
                if !executable.path.contains("/node_modules/") {
                    arguments += ["--prefix", executable.deletingLastPathComponent().deletingLastPathComponent().path]
                }
                return try command("yarn", arguments)
            }
        }
        if executable.path.contains("/.volta/bin/") {
            return try command("volta", ["install", package], preferred: [executable.deletingLastPathComponent().appendingPathComponent("volta").path])
        }
        guard !path.contains("/node_modules/"), !path.contains("/shims/") else {
            throw AppFailure.message("Unable to safely identify the package manager for this installation. Update it with its original package manager.")
        }
        guard FileManager.default.isWritableFile(atPath: resolved.deletingLastPathComponent().path) else {
            throw AppFailure.message("No write permission for this installation. Update it with its original installer or administrator account.")
        }
        return .standalone(resolved)
    }

    static func replaceStandalone(_ target: URL, from stage: URL) async throws {
        try await Task.detached {
            let destination = target.deletingLastPathComponent()
            guard FileManager.default.isWritableFile(atPath: destination.path) else {
                throw AppFailure.message("No write permission for \(destination.path). The original installation was not replaced.")
            }
            let temporary = destination.appendingPathComponent(".opencode-update-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: temporary) }
            try FileManager.default.copyItem(at: stage.appendingPathComponent("package/bin/opencode"), to: temporary)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: temporary.path)
            guard rename(temporary.path, target.path) == 0 else { throw AppFailure.message("Unable to replace the selected OpenCode installation (errno \(errno)).") }
        }.value
    }
}
