import Foundation

enum StartupManager {
    static func agent(_ kind: String) -> URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/LaunchAgents/org.opencode.telegram.\(kind).plist")
    }
    static func enabled(_ kind: String) -> Bool { FileManager.default.fileExists(atPath: agent(kind).path) }
    static func set(_ kind: String, enabled: Bool) throws {
        guard ["app", "bot"].contains(kind) else { throw AppFailure.message("Invalid startup item.") }
        let file = agent(kind)
        if !enabled {
            if FileManager.default.fileExists(atPath: file.path) { try FileManager.default.removeItem(at: file) }
            return
        }
        guard Bundle.main.bundleURL.pathExtension == "app", let executable = Bundle.main.executableURL else {
            throw AppFailure.message("Run the installed .app before enabling login startup.")
        }
        // The helper loads credentials from Keychain; never put passwords in a plist.
        let args = kind == "bot" ? [executable.path, "--start-silent"] : ["/usr/bin/open", Bundle.main.bundleURL.path]
        let value: [String: Any] = ["Label": "org.opencode.telegram.\(kind)", "ProgramArguments": args,
            "RunAtLoad": true, "KeepAlive": false, "LimitLoadToSessionType": "Aqua"]
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try PropertyListSerialization.data(fromPropertyList: value, format: .xml, options: 0).write(to: file, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
}
