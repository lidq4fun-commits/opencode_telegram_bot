import Foundation
import Darwin

enum EnginePort {
    static func isOpenCode(_ command: String) -> Bool {
        let name = URL(fileURLWithPath: command.trimmingCharacters(in: .whitespacesAndNewlines)).lastPathComponent
        return ["opencode", "opencode-v1", "opencode-v2"].contains(name)
    }

    static func listeners(port: Int, settings: Settings) async throws -> Set<Int32> {
        let result = try await Runtime.run(URL(fileURLWithPath: "/usr/sbin/lsof"),
            ["-nP", "-a", "-iTCP:\(port)", "-sTCP:LISTEN", "-t"], settings: settings, timeout: 5)
        guard result.code == 0 || (result.code == 1 && result.output.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty) else {
            throw AppFailure.message("Cannot inspect server port \(port): \(result.output)")
        }
        return Set(result.output.split(whereSeparator: \.isNewline).compactMap { Int32($0) })
    }

    static func release(port: Int, settings: Settings) async throws {
        let pids = try await listeners(port: port, settings: settings)
        // Only signal processes owned by the current user; never elevate privileges.
        for pid in pids {
            let command = try await Runtime.run(URL(fileURLWithPath: "/bin/ps"), ["-p", String(pid), "-o", "comm="], settings: settings, timeout: 5)
            let user = try await Runtime.run(URL(fileURLWithPath: "/bin/ps"), ["-p", String(pid), "-o", "uid="], settings: settings, timeout: 5)
            guard command.code == 0,
                  user.code == 0, UInt32(user.output.trimmingCharacters(in: .whitespacesAndNewlines)) == getuid() else {
                throw AppFailure.message(settings.locale == "en" ? "No permission to stop the process occupying port \(port)." : "无权停止占用端口 \(port) 的进程。")
            }
        }
        for pid in pids {
            // Recheck identity and port ownership immediately before signalling.
            guard try await listeners(port: port, settings: settings).contains(pid) else { continue }
            let command = try await Runtime.run(URL(fileURLWithPath: "/bin/ps"), ["-p", String(pid), "-o", "comm="], settings: settings, timeout: 5)
            let user = try await Runtime.run(URL(fileURLWithPath: "/bin/ps"), ["-p", String(pid), "-o", "uid="], settings: settings, timeout: 5)
            guard command.code == 0, user.code == 0,
                  UInt32(user.output.trimmingCharacters(in: .whitespacesAndNewlines)) == getuid() else { continue }
            guard kill(pid, SIGTERM) == 0 || errno == ESRCH else {
                throw AppFailure.message("Cannot stop OpenCode process \(pid).")
            }
        }
        for _ in 0..<50 {
            if try await listeners(port: port, settings: settings).isEmpty { return }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        let remaining = try await listeners(port: port, settings: settings)
        for pid in remaining.intersection(pids) {
            let user = try await Runtime.run(URL(fileURLWithPath: "/bin/ps"), ["-p", String(pid), "-o", "uid="], settings: settings, timeout: 5)
            if user.code == 0, UInt32(user.output.trimmingCharacters(in: .whitespacesAndNewlines)) == getuid() {
                _ = kill(pid, SIGKILL)
            }
        }
        for _ in 0..<20 {
            if try await listeners(port: port, settings: settings).isEmpty { return }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        throw AppFailure.message(settings.locale == "en" ? "Port \(port) was not released; OpenCode may have restarted automatically." : "端口 \(port) 未释放，OpenCode 可能被后台服务自动重启。")
    }
}
