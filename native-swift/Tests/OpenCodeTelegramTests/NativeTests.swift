import XCTest
import Security
import AppKit
@testable import OpenCodeTelegram

final class NativeTests: XCTestCase {
    func testEncryptionDefaultsOffAndGuidanceRequiresSpecialClient() throws {
        XCTAssertFalse(Settings().encryptionEnabled)
        var enabled = Settings()
        enabled.encryptionEnabled = true
        let stored = try JSONEncoder().encode(enabled)
        XCTAssertTrue(try JSONDecoder().decode(Settings.self, from: stored).encryptionEnabled)
        XCTAssertTrue(EncryptionGuidance.chinese.contains("普通官方 Telegram 不支持"))
        XCTAssertTrue(EncryptionGuidance.chinese.contains("EOT10"))
        XCTAssertEqual(EncryptionGuidance.release.host, "github.com")
        XCTAssertTrue(EncryptionGuidance.release.path.hasSuffix("desktop-1.0.15-android-11.0.22"))
        XCTAssertTrue(EncryptionGuidance.androidSource.path.hasSuffix("OpenCodeTelegram-Android-v11.0.22-source.zip"))
    }
    func testActualDependencyArchivesVerifyAndNodeSQLiteRemainsCompatible() async throws {
        guard let path = ProcessInfo.processInfo.environment["MACOS_TEST_DEPENDENCIES"] else {
            throw XCTSkip("Set MACOS_TEST_DEPENDENCIES to validate live official Node/Bun downloads without installing them.")
        }
        let resources = URL(fileURLWithPath: path)
        for dependency in UpdatableDependency.allCases {
            let current = try await DependencyUpdater.current(dependency, settings: Settings(), resources: resources)
            let release = try await DependencyUpdater.release(dependency, current: current)
            let original = try Data(contentsOf: resources.appendingPathComponent(dependency.relativePath))
            let stage = try await DependencyUpdater.prepare(dependency, release: release, settings: Settings(), resources: resources) { _ in }
            defer { try? FileManager.default.removeItem(at: stage) }
            XCTAssertTrue(FileManager.default.isExecutableFile(atPath: stage.appendingPathComponent(release.archiveMember).path))
            XCTAssertEqual(try Data(contentsOf: resources.appendingPathComponent(dependency.relativePath)), original)
        }
    }
    func testDependencyVersionsRejectPrereleasesAndKeepNodeMajor() throws {
        XCTAssertEqual(DependencyUpdater.version("v24.15.0\n"), "24.15.0")
        XCTAssertEqual(DependencyUpdater.version("1.4.2"), "1.4.2")
        XCTAssertNil(DependencyUpdater.version("1.5.0-canary"))
        XCTAssertNil(DependencyUpdater.version("../../bin/node"))
        XCTAssertEqual(try DependencyUpdater.newestCompatibleNode(["v26.1.0", "v24.9.0", "v24.15.0", "v24.16.0-rc.1"], current: "24.10.0"), "24.15.0")
        XCTAssertThrowsError(try DependencyUpdater.newestCompatibleNode(["26.1.0"], current: "24.10.0"))
        XCTAssertThrowsError(try DependencyUpdater.newestCompatibleNode(["24.1.0"], current: "unknown"))
    }
    func testDependencyReplacementRetainsRecoveryCopyAndDoesNotModifyApp() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let target = root.appendingPathComponent("node")
        let stage = root.appendingPathComponent("new-node")
        try Data("old runtime".utf8).write(to: target)
        try Data("new runtime".utf8).write(to: stage)
        let backup = try DependencyUpdater.replace(target, from: stage)
        XCTAssertEqual(try Data(contentsOf: target), Data("new runtime".utf8))
        XCTAssertEqual(try Data(contentsOf: backup), Data("old runtime".utf8))
        XCTAssertEqual(rename(backup.path, target.path), 0)
        XCTAssertEqual(try Data(contentsOf: target), Data("old runtime".utf8))
        XCTAssertThrowsError(try DependencyUpdater.replace(target, from: stage))
        XCTAssertEqual(try Data(contentsOf: target), Data("old runtime".utf8))
    }
    func testDynamicShellPathPreservesUserConfigurationAndSwitchesEngine() throws {
        let original = "# user settings\nexport PATH=/usr/bin:/bin\n"
        let first = URL(fileURLWithPath: "/Applications/Engine One/opencode")
        let second = URL(fileURLWithPath: "/Applications/Engine 'Two/opencode")
        let initial = try EngineShellPath.content(original, executable: first)
        let updated = try EngineShellPath.content(initial, executable: second)
        XCTAssertTrue(updated.hasPrefix(original))
        XCTAssertFalse(updated.contains("Engine One"))
        XCTAssertEqual(updated.components(separatedBy: EngineShellPath.begin).count, 2)
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = ["-f", "-c", updated + "printf '%s' \"$PATH\""]
        process.standardOutput = output
        try process.run()
        let result = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
        XCTAssertEqual(result, "/Applications/Engine 'Two:/usr/bin:/bin")
        XCTAssertThrowsError(try EngineShellPath.content(EngineShellPath.begin, executable: first))
    }
    func testInternalCommandPathPrioritizesSelectedEngine() {
        var settings = Settings()
        settings.executable = "/custom/selected/opencode"
        XCTAssertEqual(Runtime.environment(settings)["PATH"]?.components(separatedBy: ":").first, "/custom/selected")
    }
    @MainActor func testOfflineInstallerCopiesExternalDependenciesAndPreservesUserData() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("media/OpenCodeTelegram.app")
        let target = root.appendingPathComponent("installation/OpenCodeTelegram.app")
        let dependencies = Runtime.dependencyRoot(for: source)
        for name in ["runtime/bin/node", "bot/dist/cli.js", "dependencies/opencode/v2/opencode", "dependency-sources.json"] {
            let file = dependencies.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data("offline fixture".utf8).write(to: file)
        }
        try FileManager.default.createDirectory(at: source.appendingPathComponent("Contents/Resources"), withIntermediateDirectories: true)
        let data = root.appendingPathComponent("user-data/settings.json")
        try FileManager.default.createDirectory(at: data.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("keep credentials".utf8).write(to: data)
        try await InstallerModel.copyApplication(source, to: target) { _ in }
        let installed = Runtime.dependencyRoot(for: target)
        XCTAssertEqual(try Data(contentsOf: installed.appendingPathComponent("dependencies/opencode/v2/opencode")), Data("offline fixture".utf8))
        XCTAssertFalse(FileManager.default.fileExists(atPath: target.appendingPathComponent("Contents/Resources/dependencies").path))
        XCTAssertEqual(try Data(contentsOf: data), Data("keep credentials".utf8))
    }
    @MainActor func testWebSidebarEntryFollowsOverviewWithoutChangingPageIdentity() {
        XCTAssertEqual(AppModel.sidebarPageOrder, [0, 6, 1, 2, 3, 4, 5, 7])
        let model = AppModel(loadSettings: false)
        model.settings.locale = "zh"
        XCTAssertEqual(model.pages[AppModel.sidebarPageOrder[1]], "OpenCode 网页端")
        XCTAssertEqual(model.pages[1], "机器人绑定")
        XCTAssertEqual(Set(AppModel.sidebarPageOrder), Set(model.pages.indices))
        model.settings.locale = "en"
        XCTAssertEqual(model.pages[1], "Bot Binding")
    }
    func testEngineBadgesDistinguishBuiltInManagedAndBuildCopies() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home")
        let resources = Runtime.dependencyRoot(for: root.appendingPathComponent("Application.app"))
        let bundled = resources.appendingPathComponent("dependencies/opencode/v2/opencode")
        let managed = home.appendingPathComponent("opencode-runtime/v2/opencode")
        let material = root.appendingPathComponent("payload/dependencies/opencode/v2/opencode")
        for path in [bundled, managed, material] {
            try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data("fixture".utf8).write(to: path)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: path.path)
        }
        let builtIn = EngineCandidate(path: bundled.path, version: "2.0.22")
        let installed = EngineCandidate(path: managed.path, version: "2.0.22")
        let buildCopy = EngineCandidate(path: material.path, version: "2.0.22")
        XCTAssertTrue(builtIn.isBundled(resources: resources))
        XCTAssertFalse(installed.isBundled(resources: resources))
        XCTAssertFalse(buildCopy.isBundled(resources: resources))
        var settings = Settings()
        XCTAssertFalse(installed.isActive(settings: settings, home: home, resources: resources))
        XCTAssertTrue(builtIn.isActive(settings: settings, home: home, resources: resources))
        settings.executable = managed.path
        XCTAssertTrue(builtIn.isActive(settings: settings, home: home, resources: resources))
        XCTAssertFalse(installed.isActive(settings: settings, home: home, resources: resources))
        settings.executable = bundled.path
        XCTAssertTrue(builtIn.isActive(settings: settings, home: home, resources: resources))
        XCTAssertFalse(installed.isActive(settings: settings, home: home, resources: resources))
        settings.executable = ""
        try FileManager.default.removeItem(at: managed)
        XCTAssertTrue(builtIn.isActive(settings: settings, home: home, resources: resources))
    }
    func testPortReleaseStopsNonOpenCodeListenerOnIsolatedPort() async throws {
        let process = Process()
        let pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
        process.arguments = ["-u", "-c", "import socket,time; s=socket.socket(); s.bind(('127.0.0.1',0)); s.listen(); print(s.getsockname()[1],flush=True); time.sleep(60)"]
        process.standardOutput = pipe
        try process.run()
        defer { if process.isRunning { process.terminate() } }
        var bytes = Data()
        while true {
            let byte = pipe.fileHandleForReading.readData(ofLength: 1)
            if byte.isEmpty || byte == Data([10]) { break }
            bytes.append(byte)
        }
        let port = try XCTUnwrap(Int(String(decoding: bytes, as: UTF8.self)))
        let owners = try await EnginePort.listeners(port: port, settings: Settings())
        XCTAssertTrue(owners.contains(process.processIdentifier))
        try await EnginePort.release(port: port, settings: Settings())
        let remaining = try await EnginePort.listeners(port: port, settings: Settings())
        XCTAssertTrue(remaining.isEmpty)
    }
    func testPortOwnerIdentificationDoesNotMatchUnrelatedPrograms() {
        XCTAssertTrue(EnginePort.isOpenCode(" /usr/local/bin/opencode\n"))
        XCTAssertTrue(EnginePort.isOpenCode("/Applications/Example.app/Contents/Resources/opencode-v1"))
        XCTAssertFalse(EnginePort.isOpenCode("/usr/bin/node"))
        XCTAssertFalse(EnginePort.isOpenCode("/tmp/not-opencode"))
        XCTAssertFalse(EnginePort.isOpenCode("/tmp/opencode serve"))
    }
    func testScannerDoesNotHidePackagedEnginesBuildMaterialsOrAliases() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let packaged = root.appendingPathComponent("Example.app/Contents/Resources/dependencies/opencode/v2/opencode")
        let material = root.appendingPathComponent("payload/dependencies/opencode/v2/opencode")
        let alias = root.appendingPathComponent("alias/opencode")
        for binary in [packaged, material] {
            try FileManager.default.createDirectory(at: binary.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data("#!/bin/sh\necho 2.0.22\n".utf8).write(to: binary)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)
        }
        try FileManager.default.createDirectory(at: alias.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: material)
        let expected = Set([packaged.path, material.path, alias.path])
        let found = await EngineManager.probeCandidates(Array(expected), settings: Settings())
        XCTAssertEqual(Set(found.map(\.path)), expected)
    }
    func testBuildMaterialsCanBeSelectedAndUninstalledLikeOtherStandaloneFiles() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        for name in ["dependency-sources.json", "runtime/bin/node", "bot/dist/cli.js", "dependencies/opencode/v2/opencode"] {
            let file = root.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data("fixture".utf8).write(to: file)
        }
        let engine = root.appendingPathComponent("dependencies/opencode/v2/opencode")
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: engine.path)
        XCTAssertFalse(EngineInstallations.isPackaged(engine.path))
        XCTAssertNoThrow(try EngineInstallations.removalPlan(executable: engine, version: "v2", environment: [:]))
        var settings = Settings(); settings.executable = engine.path
        XCTAssertNotNil(Runtime.installedEngine(settings))
        XCTAssertTrue(FileManager.default.fileExists(atPath: engine.path))
    }
    func testExternalV2UsesOneCopyAndDoesNotPopulateLibrary() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home")
        let resources = root.appendingPathComponent("resources")
        let bundled = resources.appendingPathComponent("dependencies/opencode/v2/opencode")
        try FileManager.default.createDirectory(at: bundled.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\necho 2.0.22\n".utf8).write(to: bundled)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: bundled.path)
        let settings = Settings()
        XCTAssertEqual(Runtime.engine(settings, home: home, resources: resources), bundled)
        XCTAssertEqual(Runtime.installedEngine(settings, home: home, resources: resources), bundled)
        let installed = try await EngineManager.install("v2", settings: settings, home: home, resources: resources)
        XCTAssertEqual(Runtime.installedEngine(settings, home: home, resources: resources)?.path, installed)
        XCTAssertEqual(installed, bundled.path)
        XCTAssertFalse(FileManager.default.fileExists(atPath: home.path))
        try FileManager.default.removeItem(atPath: installed)
        XCTAssertNil(Runtime.installedEngine(settings, home: home, resources: resources))
        do {
            _ = try await EngineManager.install("v2", settings: settings, home: home, resources: resources)
            XCTFail("Missing external dependency should require reinstall or update")
        } catch {}
        try Data("#!/bin/sh\necho 2.0.22\n".utf8).write(to: bundled)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: bundled.path)
        let restored = try await EngineManager.install("v2", settings: settings, home: home, resources: resources)
        XCTAssertEqual(restored, installed)
        XCTAssertEqual(Runtime.installedEngine(settings, home: home, resources: resources)?.path, restored)
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: restored)), try Data(contentsOf: bundled))
    }
    func testV2EnvironmentPreservesExistingData() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let existing = root.appendingPathComponent("existing-v2.db")
        try Data("preserved v2 database".utf8).write(to: existing)
        let settings = Settings()
        XCTAssertEqual(Runtime.environment(settings, home: root)["OPENCODE_DB"], ProcessInfo.processInfo.environment["OPENCODE_DB"])
        XCTAssertEqual(try String(contentsOf: existing), "preserved v2 database")
    }
    func testV1EngineIsRejected() throws {
        XCTAssertThrowsError(try EngineManager.managed("v1"))
        XCTAssertNil(EngineManager.versionNumber("1.18.34"))
        XCTAssertEqual(EngineManager.versionNumber("2.0.22"), "2.0.22")
    }
    func testStandaloneUninstallRemovesOnlyExecutableAndItsSelectedLink() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let binary = root.appendingPathComponent("opencode-real")
        let alias = root.appendingPathComponent("opencode")
        let data = root.appendingPathComponent("settings.json")
        try Data("binary".utf8).write(to: binary)
        try Data("preserve configuration and sessions".utf8).write(to: data)
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: binary)
        let plan = try EngineInstallations.removalPlan(executable: alias, version: "v2", environment: [:])
        try await EngineInstallations.remove(plan, settings: Settings())
        XCTAssertFalse(FileManager.default.fileExists(atPath: binary.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: alias.path))
        XCTAssertEqual(try String(contentsOf: data), "preserve configuration and sessions")
        XCTAssertTrue(FileManager.default.fileExists(atPath: root.path))
    }
    func testUninstallCannotRecursivelyRemoveDirectories() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        XCTAssertThrowsError(try EngineInstallations.removalPlan(executable: root, version: "v2", environment: [:]))
        do {
            try await EngineInstallations.remove(.files([root]), settings: Settings())
            XCTFail("A directory must never be removed")
        } catch { XCTAssertTrue(FileManager.default.fileExists(atPath: root.path)) }
    }
    func testNpmUninstallTargetsOnlyOriginalGlobalPackage() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let tool = root.appendingPathComponent("bin/npm")
        let binary = root.appendingPathComponent("lib/node_modules/@opencode/cli/bin/opencode")
        try FileManager.default.createDirectory(at: tool.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: binary.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: tool)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: tool.path)
        try Data("binary".utf8).write(to: binary)
        let alias = root.appendingPathComponent("bin/opencode")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: binary)
        let plan = try EngineInstallations.removalPlan(executable: alias, version: "v2", environment: [:])
        let sameInstallation = try EngineInstallations.removalPlan(executable: binary, version: "v2", environment: [:])
        XCTAssertEqual(plan.identity, sameInstallation.identity)
        guard case .packageManager(let manager, let args, _) = plan else { return XCTFail("Expected npm uninstall") }
        XCTAssertEqual(manager.resolvingSymlinksInPath(), tool.resolvingSymlinksInPath())
        XCTAssertEqual(args, ["uninstall", "--global", "--prefix", root.resolvingSymlinksInPath().path, "@opencode/cli"])
        XCTAssertFalse(args.contains("--force"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: binary.path))
    }
    func testLiveScanFindsExternalCurlInstallation() async throws {
        guard ProcessInfo.processInfo.environment["OPENCODE_TEST_SCAN"] == "1" else {
            throw XCTSkip("Set OPENCODE_TEST_SCAN=1 to exercise local installation discovery.")
        }
        let binary = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".opencode/bin/opencode")
        guard FileManager.default.isExecutableFile(atPath: binary.path) else { throw XCTSkip("No external curl installation available.") }
        let engines = await EngineManager.scan(settings: Settings())
        XCTAssertTrue(engines.contains { URL(fileURLWithPath: $0.path).resolvingSymlinksInPath() == binary.resolvingSymlinksInPath() })
    }
    @MainActor func testNoticeDismissesAutomaticallyAndNewNoticeReplacesOldTimer() async throws {
        let model = AppModel(loadSettings: false)
        model.showNotice("first", duration: 10_000_000)
        model.showNotice("second", duration: 200_000_000)
        try await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(model.notice, "second")
        try await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(model.notice, "")
    }
    func testScanIncludesVersionManagersCustomPrefixesAndUnindexedDirectories() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root.appendingPathComponent(".nvm/versions/node/v24/bin"), withIntermediateDirectories: true)
        try Data("test".utf8).write(to: root.appendingPathComponent(".nvm/versions/node/v24/bin/opencode"))
        let custom = root.appendingPathComponent("custom/deep/bin/opencode")
        try FileManager.default.createDirectory(at: custom.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("test".utf8).write(to: custom)
        let paths = EngineInstallations.searchPaths(home: root, environment: ["PATH": "/example/bin", "NPM_CONFIG_PREFIX": "/custom/npm", "PNPM_HOME": "/custom/pnpm"], additionalRoots: [root.appendingPathComponent("custom")])
        let normalized = Set(paths.map { URL(fileURLWithPath: $0).resolvingSymlinksInPath().path })
        XCTAssertTrue(normalized.contains(root.appendingPathComponent(".nvm/versions/node/v24/bin/opencode").resolvingSymlinksInPath().path))
        XCTAssertTrue(paths.contains(root.appendingPathComponent(".opencode/bin/opencode").path))
        XCTAssertTrue(paths.contains("/example/bin/opencode"))
        XCTAssertTrue(paths.contains("/custom/npm/bin/opencode"))
        XCTAssertTrue(paths.contains("/custom/pnpm/opencode"))
        XCTAssertTrue(normalized.contains(custom.resolvingSymlinksInPath().path))
    }
    func testNpmUpdateUsesOriginalPrefixAndTool() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let npm = root.appendingPathComponent("bin/npm")
        try FileManager.default.createDirectory(at: npm.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: npm)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: npm.path)
        let binary = root.appendingPathComponent("lib/node_modules/@opencode/cli/bin/opencode")
        let plan = try EngineInstallations.updatePlan(executable: binary, version: "v2", latest: "2.0.22", environment: ["PATH": "/usr/bin"])
        guard case .packageManager(let tool, let args, let env) = plan else { return XCTFail("Expected npm update") }
        XCTAssertEqual(tool, npm)
        XCTAssertEqual(args, ["install", "--global", "--prefix", root.path, "@opencode/cli@2.0.22"])
        XCTAssertTrue(env["PATH"]?.hasPrefix(root.appendingPathComponent("bin").path) == true)
    }
    func testHomebrewUpdateUsesOriginalFormula() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let brew = root.appendingPathComponent("bin/brew")
        try FileManager.default.createDirectory(at: brew.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: brew)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: brew.path)
        let binary = root.appendingPathComponent("Cellar/opencode-v2/2.0.9/bin/opencode")
        let plan = try EngineInstallations.updatePlan(executable: binary, version: "v2", latest: "2.0.22", environment: [:])
        guard case .packageManager(let tool, let args, _) = plan else { return XCTFail("Expected Homebrew update") }
        XCTAssertEqual(tool, brew)
        XCTAssertEqual(args, ["upgrade", "opencode-v2"])
    }
    func testStandaloneUpdatePreservesExternalLocationAndSymlink() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let target = root.appendingPathComponent("external/opencode")
        let stage = root.appendingPathComponent("stage")
        let download = stage.appendingPathComponent("package/bin/opencode")
        try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: download.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("old".utf8).write(to: target)
        try Data("new".utf8).write(to: download)
        let alias = root.appendingPathComponent("opencode")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: target)
        let plan = try EngineInstallations.updatePlan(executable: alias, version: "v2", latest: "2.0.22", environment: [:])
        guard case .standalone(let resolved) = plan else { return XCTFail("Expected standalone update") }
        XCTAssertEqual(resolved.resolvingSymlinksInPath(), target.resolvingSymlinksInPath())
        try await EngineInstallations.replaceStandalone(resolved, from: stage)
        XCTAssertEqual(try String(contentsOf: target), "new")
        XCTAssertEqual(try String(contentsOf: alias), "new")
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: alias.path), target.path)
    }
    func testUnknownPackageAndSignedAppAreNotOverwritten() {
        XCTAssertThrowsError(try EngineInstallations.updatePlan(executable: URL(fileURLWithPath: "/custom/project/node_modules/tool/bin/opencode"), version: "v2", latest: "2.0.22", environment: [:]))
        XCTAssertThrowsError(try EngineInstallations.updatePlan(executable: URL(fileURLWithPath: "/Applications/Other.app/Contents/MacOS/opencode"), version: "v2", latest: "2.0.22", environment: [:]))
    }
    func testCancelledWebNavigationDoesNotShowErrorButConnectionFailuresDo() {
        XCTAssertFalse(WebRouting.shouldReportNavigationError(NSError(domain: NSURLErrorDomain, code: NSURLErrorCancelled)))
        XCTAssertTrue(WebRouting.shouldReportNavigationError(NSError(domain: NSURLErrorDomain, code: NSURLErrorCannotConnectToHost)))
        XCTAssertTrue(WebRouting.shouldReportNavigationError(NSError(domain: NSURLErrorDomain, code: NSURLErrorTimedOut)))
        XCTAssertTrue(WebRouting.shouldReportNavigationError(NSError(domain: "OtherDomain", code: -999)))
    }
    func testWebEndpointChangeAndReloadProduceOneNavigationToNewEndpoint() {
        let previous = URL(string: "http://127.0.0.1:4096")!
        let configured = URL(string: "http://127.0.0.1:49374")!
        let current = previous.appendingPathComponent("session/old")
        XCTAssertEqual(WebRouting.navigationTarget(configured: configured, loaded: previous, current: current, reload: true), configured)
        XCTAssertNil(WebRouting.navigationTarget(configured: configured, loaded: configured, current: configured, reload: false))
        let session = configured.appendingPathComponent("session/current")
        XCTAssertEqual(WebRouting.navigationTarget(configured: configured, loaded: configured, current: session, reload: true), session)
        XCTAssertNil(WebRouting.navigationTarget(configured: nil, loaded: previous, current: current, reload: true))
        XCTAssertEqual(WebRouting.navigationTarget(configured: configured, loaded: nil, current: nil, reload: true), configured)
    }
    func testUpdateIsSkippedForCurrentOrNewerLocalVersions() {
        XCTAssertFalse(EngineManager.needsUpdate(local: "2.0.22", latest: "2.0.22"))
        XCTAssertFalse(EngineManager.needsUpdate(local: "2.0.23", latest: "2.0.22"))
        XCTAssertTrue(EngineManager.needsUpdate(local: "2.0.9", latest: "2.0.22"))
        XCTAssertTrue(EngineManager.needsUpdate(local: nil, latest: "2.0.22"))
        XCTAssertFalse(EngineManager.needsUpdate(local: "1.18.34", latest: "1.18.34"))
        XCTAssertTrue(EngineManager.needsUpdate(local: "1.9.0", latest: "1.18.34"))
        XCTAssertTrue(EngineManager.needsUpdate(local: "2.0.22", latest: "1.18.34"))
    }
    @MainActor func testCurrentEngineShowsNoticeWithoutInstalling() async throws {
        guard let path = ProcessInfo.processInfo.environment["OPENCODE_TEST_CURRENT_ENGINE"] else {
            throw XCTSkip("Set OPENCODE_TEST_CURRENT_ENGINE to test the current-version update flow.")
        }
        let model = AppModel(loadSettings: false)
        model.settings.executable = path
        model.settings.apiVersion = "v2"
        let latest = try await EngineManager.latestVersion("v2")
        let probe = try await Runtime.run(URL(fileURLWithPath: path), ["--version"], settings: model.settings)
        let local = EngineManager.versionNumber(probe.output)
        guard !EngineManager.needsUpdate(local: local, latest: latest) else {
            throw XCTSkip("The supplied engine is no longer current.")
        }
        model.updateEngine()
        for _ in 0..<6000 {
            if !model.busy { break }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTAssertFalse(model.busy)
        XCTAssertNil(model.engineProgress)
        XCTAssertTrue(model.error.isEmpty, model.error)
        XCTAssertTrue(model.notice.contains(local ?? latest))
        XCTAssertEqual(model.settings.executable, path)
    }
    @MainActor func testFailedInstallDismissesProgressAndReportsError() async {
        let model = AppModel(loadSettings: false)
        model.installEngine("invalid")
        XCTAssertTrue(model.busy)
        for _ in 0..<100 {
            if !model.busy { break }
            await Task.yield()
        }
        XCTAssertFalse(model.busy)
        XCTAssertNil(model.engineProgress)
        XCTAssertFalse(model.error.isEmpty)
    }
    func testDownloadProgressUsesRealByteCounts() {
        XCTAssertEqual(EngineUpdateProgress(phase: .downloading, received: 25, expected: 100).fraction, 0.25)
        XCTAssertEqual(EngineUpdateProgress(phase: .downloading, received: 120, expected: 100).fraction, 1)
        XCTAssertNil(EngineUpdateProgress(phase: .downloading, received: 25, expected: -1).fraction)
        XCTAssertNil(EngineUpdateProgress(phase: .verifying).fraction)
        XCTAssertNil(EngineUpdateProgress(phase: .installing).fraction)
    }

    @MainActor func testVerifiedOpenCodeUpdateDownload() async throws {
        guard ProcessInfo.processInfo.environment["OPENCODE_TEST_DOWNLOAD"] == "1" else {
            throw XCTSkip("Set OPENCODE_TEST_DOWNLOAD=1 to verify the live update download.")
        }
        var updates: [EngineUpdateProgress] = []
        let stage = try await EngineManager.prepareUpdate("v2", settings: Settings()) { updates.append($0) }
        defer { try? FileManager.default.removeItem(at: stage) }
        XCTAssertTrue(updates.contains { $0.phase == .downloading && $0.received > 0 })
        XCTAssertTrue(updates.contains { $0.phase == .verifying })
        XCTAssertTrue(updates.contains { $0.phase == .unpacking })
        XCTAssertTrue(updates.contains { $0.phase == .validating })
        let binary = stage.appendingPathComponent("package/bin/opencode")
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: binary.path))
        let result = try await Runtime.run(binary, ["--version"], settings: Settings())
        XCTAssertEqual(result.code, 0)
        XCTAssertTrue(EngineManager.versionNumber(result.output)?.hasPrefix("2.") == true)
    }
    func testEngineVersionParsingAndBranchDetection() {
        XCTAssertEqual(EngineManager.versionNumber("opencode 2.0.22\n"), "2.0.22")
        XCTAssertNil(EngineManager.versionNumber("1.18.34"))
        XCTAssertNil(EngineManager.versionNumber("unexpected output"))
        XCTAssertNil(EngineManager.versionNumber("12.0.22"))
        XCTAssertEqual(EngineCandidate(path: "/engine", version: "2.0.22").apiVersion, "v2")
    }

    func testScanChoosesNewestEngineInSelectedBranch() {
        let candidates = [
            EngineCandidate(path: "/v2-old", version: "2.0.9"),
            EngineCandidate(path: "/v1", version: "1.18.34"),
            EngineCandidate(path: "/v2-new", version: "2.0.22")
        ]
        XCTAssertEqual(EngineManager.preferred(candidates, version: "v2")?.path, "/v2-new")
        XCTAssertNil(EngineManager.preferred(candidates, version: "v1"))
        XCTAssertNil(EngineManager.preferred([], version: "v2"))
        XCTAssertNil(EngineManager.preferred([candidates[1]], version: "v2"))
    }
    func testServiceStateUsesRuntimeRunDirectory() {
        let home = URL(fileURLWithPath: "/example/app")
        XCTAssertEqual(RuntimeMonitor.serviceFile(home: home).path, "/example/app/run/bot-service.json")
    }

    func testForegroundLogsSkipHistoryWithoutChangingDiskLogs() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("bot.log")
        try Data("old history\n".utf8).write(to: file)
        var reader = SessionLogReader(folder: folder)
        XCTAssertEqual(reader.readNew(folder: folder), "")
        let handle = try FileHandle(forWritingTo: file)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data("new event\n".utf8))
        try handle.close()
        XCTAssertEqual(reader.readNew(folder: folder), "new event\n")
        XCTAssertEqual(reader.readNew(folder: folder), "")
        XCTAssertEqual(try String(contentsOf: file), "old history\nnew event\n")
        var reopened = SessionLogReader(folder: folder)
        XCTAssertEqual(reopened.readNew(folder: folder), "")
        try Data("rotated event\n".utf8).write(to: folder.appendingPathComponent("bot-next.log"))
        XCTAssertEqual(reopened.readNew(folder: folder), "rotated event\n")
        try Data("reset\n".utf8).write(to: file)
        XCTAssertEqual(reopened.readNew(folder: folder), "reset\n")
    }
    @MainActor func testReopenBeforeWindowCreationDoesNotCrash() {
        let delegate = AppDelegate(loadSettings: false)
        XCTAssertNil(delegate.window)
        XCTAssertTrue(delegate.applicationShouldHandleReopen(NSApplication.shared, hasVisibleWindows: false))
        XCTAssertEqual(delegate.applicationShouldTerminate(NSApplication.shared), .terminateNow)
        delegate.closing = true
        XCTAssertFalse(delegate.applicationShouldHandleReopen(NSApplication.shared, hasVisibleWindows: false))
    }
    func testRealLoginKeychainInsertUpdateRead() throws {
        let account = "test-" + UUID().uuidString
        defer {
            SecItemDelete([kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: SettingsStore.service, kSecAttrAccount as String: account,
                kSecUseDataProtectionKeychain as String: false] as CFDictionary)
        }
        var settings = Settings(); settings.locale = "en"
        try SettingsStore.save(settings, account: account)
        XCTAssertEqual(try SettingsStore.load(account: account).locale, "en")
        settings.locale = "zh"
        try SettingsStore.save(settings, account: account)
        XCTAssertEqual(try SettingsStore.load(account: account).locale, "zh")
    }
    @MainActor func testActualAppBundleCopyRetainsSignature() async throws {
        guard let path = ProcessInfo.processInfo.environment["MACOS_TEST_APP"] else {
            throw XCTSkip("Set MACOS_TEST_APP to exercise the actual application bundle.")
        }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let target = root.appendingPathComponent("OpenCodeTelegram.app")
        try await InstallerModel.copyApplication(URL(fileURLWithPath: path), to: target, progress: { _ in })
        let result = try await Runtime.run(URL(fileURLWithPath: "/usr/bin/codesign"),
            ["--verify", "--deep", "--strict", target.path], settings: Settings(), timeout: 60)
        XCTAssertEqual(result.code, 0, result.output)
        XCTAssertTrue(FileManager.default.isExecutableFile(atPath: target.appendingPathComponent("Contents/MacOS/OpenCodeTelegram").path))
    }
    func testQuotedCLIArgumentsDoNotInvokeAShell() throws {
        XCTAssertEqual(try CommandLineParser.parse("config set opencode.executable '/Applications/My Tools/opencode'"),
                       ["config", "set", "opencode.executable", "/Applications/My Tools/opencode"])
        XCTAssertEqual(try CommandLineParser.parse("config set model.id '$(touch /tmp/not-executed)'"),
                       ["config", "set", "model.id", "$(touch /tmp/not-executed)"])
        XCTAssertEqual(try CommandLineParser.parse("config set opencode.password \"\""), ["config", "set", "opencode.password", ""])
    }
    func testMalformedCLIInputIsRejected() {
        XCTAssertThrowsError(try CommandLineParser.parse("config set model.id 'unterminated"))
        XCTAssertThrowsError(try CommandLineParser.parse("config set model.id trailing\\"))
    }
    func testInvalidEngineVersionCannotEscapeManagedDirectory() {
        XCTAssertThrowsError(try EngineManager.managed("../../elsewhere"))
        XCTAssertThrowsError(try EngineManager.managed("v3"))
        XCTAssertTrue((try? EngineManager.managed("v2").path.hasSuffix("/OpenCodeTelegram-dependencies/dependencies/opencode/v2/opencode")) == true)
    }
    func testEncryptionEnvironmentIsDisabledWithoutPlaintextSettingsFile() {
        var settings = Settings(); settings.encryptionPassword = "test-passphrase"
        let disabled = Runtime.environment(settings)
        XCTAssertEqual(disabled["OPENCODE_TELEGRAM_EOT10_ENABLED"], "0")
        XCTAssertEqual(disabled["OPENCODE_TELEGRAM_EOT10_PASSWORD"], "")
        settings.encryptionEnabled = true
        XCTAssertEqual(Runtime.environment(settings)["OPENCODE_TELEGRAM_EOT10_PASSWORD"], "test-passphrase")
        XCTAssertEqual(Runtime.environment(settings)["OPENCODE_TELEGRAM_DESKTOP_SYNC"], "1")
    }
    func testDefaultConfigurationMatchesWindowsBaseline() {
        let settings = Settings()
        XCTAssertEqual(settings.apiVersion, "v2")
        XCTAssertEqual(settings.serverURL, "http://127.0.0.1:49374")
        XCTAssertEqual(settings.locale, "zh")
    }
    func testOriginsRejectDifferentPortsAndSchemes() {
        XCTAssertTrue(WebRouting.sameOrigin(URL(string: "http://localhost/a"), URL(string: "http://localhost:80/b")))
        XCTAssertFalse(WebRouting.sameOrigin(URL(string: "http://localhost:49374"), URL(string: "http://localhost:4096")))
        XCTAssertFalse(WebRouting.sameOrigin(URL(string: "https://localhost"), URL(string: "http://localhost")))
    }
    func testProjectWithoutSessionUsesServerRoot() {
        let server = URL(string: "http://127.0.0.1:4096")!
        let path = WebRouting.target(["currentProject": ["worktree": "/Users/example/项目"]], server: server, version: "v1").path
        XCTAssertEqual(path, "")
        XCTAssertFalse(path.contains("ses_"))
    }
    func testV2SessionRouteUsesNativeServerKey() {
        let server = URL(string: "http://127.0.0.1:49374")!
        let url = WebRouting.target(["currentSession": ["id": "ses_shared"]], server: server, version: "v2")
        XCTAssertEqual(url.path, "/server/aHR0cDovLzEyNy4wLjAuMTo0OTM3NA/session/ses_shared")
    }
    @MainActor func testInstallerCopiesFilesAndSymlinksWithoutChangingSource() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("Source.app")
        try FileManager.default.createDirectory(at: source.appendingPathComponent("Contents/Resources"), withIntermediateDirectories: true)
        let file = source.appendingPathComponent("Contents/Resources/file with spaces.txt")
        try Data("payload".utf8).write(to: file)
        try FileManager.default.createSymbolicLink(atPath: source.appendingPathComponent("Contents/Resources/link").path, withDestinationPath: "file with spaces.txt")
        let target = root.appendingPathComponent("Installed.app")
        try await InstallerModel.copyApplication(source, to: target, progress: { _ in })
        XCTAssertEqual(try Data(contentsOf: target.appendingPathComponent("Contents/Resources/file with spaces.txt")), Data("payload".utf8))
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: target.appendingPathComponent("Contents/Resources/link").path), "file with spaces.txt")
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))
    }
    @MainActor func testInstallerRejectsCopyingInsideSourceApplication() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        do {
            try await InstallerModel.copyApplication(root, to: root.appendingPathComponent("nested.app"), progress: { _ in })
            XCTFail("Nested installation must fail.")
        } catch { XCTAssertTrue(error.localizedDescription.contains("outside")) }
    }
}
