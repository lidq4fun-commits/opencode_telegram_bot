import Foundation

extension AppModel {
    func checkDependency(_ dependency: UpdatableDependency) {
        action {
            self.engineProgressTitle = self.text("检查依赖：", "Check dependency: ") + dependency.title
            self.engineProgress = EngineUpdateProgress(phase: .checking)
            defer { self.engineProgress = nil }
            let current = try await DependencyUpdater.current(dependency, settings: self.settings)
            self.dependencyVersions[dependency] = current
            let release = try await DependencyUpdater.release(dependency, current: current)
            self.dependencyLatest[dependency] = release.version
            self.message = self.text("版本检查完成。", "Version check complete.")
        }
    }
    func updateDependency(_ dependency: UpdatableDependency) {
        action {
            guard !self.console.running else {
                throw AppFailure.message(self.text("请先停止软件内 CLI 命令，再更新依赖。", "Stop the in-app CLI command before updating dependencies."))
            }
            self.engineProgressTitle = self.text("更新依赖：", "Update dependency: ") + dependency.title
            self.engineProgress = EngineUpdateProgress(phase: .checking)
            defer { self.engineProgress = nil; self.refreshRuntime() }
            let current = try await DependencyUpdater.current(dependency, settings: self.settings)
            self.dependencyVersions[dependency] = current
            let release = try await DependencyUpdater.release(dependency, current: current)
            self.dependencyLatest[dependency] = release.version
            guard release.version.compare(current, options: .numeric) == .orderedDescending else {
                self.message = self.text("当前依赖已是最新兼容版本。", "Dependency is already at the latest compatible version.")
                return
            }
            let stage = try await DependencyUpdater.prepare(dependency, release: release, settings: self.settings) { progress in
                self.engineProgress = progress
            }
            defer { try? FileManager.default.removeItem(at: stage) }
            self.refreshRuntime()
            let restartBot = self.botRunning
            let restartWeb = self.webURL != nil || restartBot
            let target = Runtime.resources.appendingPathComponent(dependency.relativePath)
            var backup: URL?
            do {
                if restartBot { _ = try await Runtime.bot(["stop"], settings: self.settings) }
                try await self.stopWeb()
                let users = try await Runtime.run(URL(fileURLWithPath: "/usr/sbin/lsof"), ["-t", target.path], settings: self.settings, timeout: 15)
                guard [0, 1].contains(users.code), users.output.isEmpty else {
                    throw AppFailure.message(self.text("依赖仍被其他进程使用，请结束外部任务后重试；未替换文件。", "The dependency is still in use. Finish external tasks and retry; no file was replaced."))
                }
                self.engineProgress = EngineUpdateProgress(phase: .installing)
                backup = try DependencyUpdater.replace(target, from: stage.appendingPathComponent(release.archiveMember))
                if restartWeb { try await self.connect(); self.webReloadRevision += 1 }
                if restartBot { _ = try await Runtime.bot(["start", "--daemon", "--mode", "installed"], settings: self.settings) }
                if let backup { try? FileManager.default.removeItem(at: backup) }
                self.dependencyVersions[dependency] = release.version
                self.message = self.text("依赖已更新，原先运行的服务已恢复。", "Dependency updated; previously running services restored.")
            } catch {
                if let backup {
                    _ = try? await Runtime.bot(["stop"], settings: self.settings)
                    try? await self.stopWeb()
                    guard rename(backup.path, target.path) == 0 else {
                        throw AppFailure.message("Dependency rollback failed. Recovery executable: \(backup.path). \(error.localizedDescription)")
                    }
                }
                if restartWeb { try? await self.connect() }
                if restartBot { _ = try? await Runtime.bot(["start", "--daemon", "--mode", "installed"], settings: self.settings) }
                throw error
            }
        }
    }
}
