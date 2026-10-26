import XCTest
import Security
import AppKit
@testable import OpenCodeTelegram

final class NativeTests: XCTestCase {
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
        XCTAssertTrue((try? EngineManager.managed("v2").path.hasSuffix("/opencode-runtime/v2/opencode")) == true)
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
        XCTAssertEqual(settings.apiVersion, "v1")
        XCTAssertEqual(settings.serverURL, "http://127.0.0.1:4096")
        XCTAssertEqual(settings.locale, "zh")
    }
    func testOriginsRejectDifferentPortsAndSchemes() {
        XCTAssertTrue(WebRouting.sameOrigin(URL(string: "http://localhost/a"), URL(string: "http://localhost:80/b")))
        XCTAssertFalse(WebRouting.sameOrigin(URL(string: "http://localhost:49374"), URL(string: "http://localhost:4096")))
        XCTAssertFalse(WebRouting.sameOrigin(URL(string: "https://localhost"), URL(string: "http://localhost")))
    }
    func testV1ProjectRoutePreservesUnicodeWithoutCreatingASession() {
        let server = URL(string: "http://127.0.0.1:4096")!
        let path = WebRouting.target(["currentProject": ["worktree": "/Users/example/项目"]], server: server, version: "v1").path
        XCTAssertTrue(path.hasSuffix("/session"))
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
