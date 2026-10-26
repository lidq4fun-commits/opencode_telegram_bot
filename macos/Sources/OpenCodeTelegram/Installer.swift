import AppKit
import SwiftUI

@MainActor final class InstallerModel: ObservableObject {
    @Published var english = false
    @Published var destination = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true).path
    @Published var progress = 0.0
    @Published var working = false
    @Published var completed = false
    @Published var launch = false
    @Published var message = ""
    var installed: URL?
    init() { english = UserDefaults.standard.string(forKey: "interfaceLocale") == "en" }
    func text(_ zh: String, _ en: String) -> String { english ? en : zh }
    func install() {
        guard !working else { return }
        let folder = URL(fileURLWithPath: destination, isDirectory: true)
        let target = folder.appendingPathComponent("OpenCodeTelegram.app", isDirectory: true)
        let source = Bundle.main.bundleURL
        guard source.pathExtension == "app", source.standardizedFileURL != target.standardizedFileURL else {
            message = text("请选择不同于当前运行应用的位置。", "Choose a location different from the currently running app."); return
        }
        working = true
        message = text("正在安装…", "Installing…")
        Task {
            defer { working = false }
            do {
                // Installation and language selection must never require secret access.
                UserDefaults.standard.set(english ? "en" : "zh", forKey: "interfaceLocale")
                if FileManager.default.fileExists(atPath: target.path) {
                    guard Bundle(url: target)?.bundleIdentifier == "org.opencode.telegram.desktop" else { throw AppFailure.message("The destination contains an unrelated application.") }
                    message = text("正在停止旧版程序与 Bot…", "Stopping the previous app and Bot…")
                    let running = NSWorkspace.shared.runningApplications.filter { $0.bundleURL?.resolvingSymlinksInPath() == target.resolvingSymlinksInPath() }
                    for app in running { app.terminate() }
                    for _ in 0..<50 {
                        if running.allSatisfy({ $0.isTerminated }) { break }
                        try await Task.sleep(nanoseconds: 100_000_000)
                    }
                    for app in running where !app.isTerminated { app.forceTerminate() }
                    message = text("正在安装…", "Installing…")
                }
                try await Self.copyApplication(source, to: target) { fraction in
                    Task { @MainActor in self.progress = fraction }
                }
                installed = target; progress = 1; completed = true
                message = text("安装完成。", "Installation complete.")
            } catch { message = text("安装失败：", "Installation failed: ") + error.localizedDescription }
        }
    }
    static func copyApplication(_ source: URL, to target: URL, progress: @escaping (Double) -> Void) async throws {
        try await Task.detached { try copyBlocking(source, to: target, progress: progress) }.value
    }
    nonisolated private static func copyBlocking(_ source: URL, to target: URL, progress: @escaping (Double) -> Void) throws {
            let source = source.resolvingSymlinksInPath()
            guard !target.resolvingSymlinksInPath().path.hasPrefix(source.path + "/"), source != target.resolvingSymlinksInPath() else {
                throw AppFailure.message("The installation destination must be outside the source application.")
            }
            let fm = FileManager.default
            let parent = target.deletingLastPathComponent()
            try fm.createDirectory(at: parent, withIntermediateDirectories: true)
            let stage = parent.appendingPathComponent(".OpenCodeTelegram-install-\(UUID().uuidString).app")
            let backup = parent.appendingPathComponent(".OpenCodeTelegram-backup-\(UUID().uuidString).app")
            defer { try? fm.removeItem(at: stage) }
            let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey]
            guard let enumerator = fm.enumerator(atPath: source.path) else { throw AppFailure.message("Unable to read application bundle.") }
            var files: [(String, URL, URLResourceValues)] = []
            for case let relative as String in enumerator {
                let file = source.appendingPathComponent(relative)
                let values = try file.resourceValues(forKeys: Set(keys))
                files.append((relative, file, values))
            }
            let total = files.reduce(0.0) { $0 + Double($1.2.fileSize ?? 0) }
            var copied = 0.0, last = -1
            try fm.createDirectory(at: stage, withIntermediateDirectories: true)
            for (relative, file, values) in files {
                let output = stage.appendingPathComponent(relative)
                if values.isDirectory == true && values.isSymbolicLink != true { try fm.createDirectory(at: output, withIntermediateDirectories: true) }
                else { try fm.copyItem(at: file, to: output) }
                copied += Double(values.fileSize ?? 0)
                let percent = total == 0 ? 99 : Int(copied * 99 / total)
                if percent != last { last = percent; progress(Double(percent) / 100) }
            }
            if fm.fileExists(atPath: target.path) {
                guard Bundle(url: target)?.bundleIdentifier == "org.opencode.telegram.desktop" else {
                    throw AppFailure.message("The destination contains an unrelated application; it will not be replaced.")
                }
                try fm.moveItem(at: target, to: backup)
            }
            do { try fm.moveItem(at: stage, to: target) }
            catch { if fm.fileExists(atPath: backup.path) { try? fm.moveItem(at: backup, to: target) }; throw error }
            if fm.fileExists(atPath: backup.path) { try fm.removeItem(at: backup) }
    }
}

struct InstallerView: View {
    @ObservedObject var installer: InstallerModel
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Picker("语言 / Language", selection: $installer.english) { Text("简体中文").tag(false); Text("English").tag(true) }.frame(width: 280).disabled(installer.working || installer.completed)
            Text(installer.completed ? installer.text("安装完成", "Installation complete") : "opencode_telegram_bot").font(.system(size: 28, weight: .semibold))
            Text(installer.text("OpenCode 安装与启动器 · Telegram 远程控制 · 加密通信", "OpenCode installer and launcher · Telegram remote control · encrypted communications"))
            if !installer.completed {
                Text(installer.text("安装位置", "Install location"))
                HStack {
                    TextField("", text: $installer.destination)
                    Button(installer.text("浏览…", "Browse…")) {
                        let panel = NSOpenPanel(); panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.canCreateDirectories = true
                        if panel.runModal() == .OK, let path = panel.url { installer.destination = path.path }
                    }
                }.disabled(installer.working)
                if installer.working { ProgressView(value: installer.progress); Text("\(Int(installer.progress * 100))%") }
                Button(installer.text("安装", "Install")) { installer.install() }.buttonStyle(.borderedProminent).disabled(installer.working)
            } else {
                Toggle(installer.text("立即启动", "Launch now"), isOn: $installer.launch)
                Button(installer.text("完成", "Finish")) {
                    if installer.launch, let app = installer.installed {
                        NSWorkspace.shared.openApplication(at: app, configuration: .init()) { _, error in
                            Task { @MainActor in
                                if let error { installer.message = installer.text("启动失败：", "Launch failed: ") + error.localizedDescription }
                                else { NSApp.terminate(nil) }
                            }
                        }
                    } else { NSApp.terminate(nil) }
                }.buttonStyle(.borderedProminent)
            }
            Text(installer.message).font(.caption).textSelection(.enabled)
            Spacer()
        }.padding(32).frame(width: 600, height: 480).textFieldStyle(.roundedBorder)
    }
}
