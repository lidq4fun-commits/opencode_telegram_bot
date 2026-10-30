import XCTest
@testable import OpenCodeTelegram

final class TelegramClientTests: XCTestCase {
    func testExecutableUsesPATHAndFallsBackOnlyWhenMissing() throws {
        let (root, executable) = try fixture("exit 0")
        defer { try? FileManager.default.removeItem(at: root) }
        let tg = root.appendingPathComponent("tg")
        try FileManager.default.copyItem(at: executable, to: tg)
        XCTAssertEqual(TelegramClientConfiguration.executable(environment: ["PATH": root.path], fallback: executable), tg)
        XCTAssertEqual(TelegramClientConfiguration.executable(environment: ["PATH": ""], fallback: executable), executable)
    }
    func testLegacyBindingMigratesWithoutOverwritingDefaultConfig() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("tg-config-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let config = root.appendingPathComponent("desktop.yaml")
        let destination = root.appendingPathComponent("gotd/gotd.cli.yaml")
        try Data("bound account".utf8).write(to: config)
        try TelegramClientConfiguration.migrateConfig(config, at: destination)
        try TelegramClientConfiguration.migrateConfig(config, at: destination)
        XCTAssertEqual(try String(contentsOf: destination, encoding: .utf8), "bound account")
        try Data("updated account".utf8).write(to: config, options: .atomic)
        XCTAssertEqual(try String(contentsOf: destination, encoding: .utf8), "bound account")
        let other = root.appendingPathComponent("other.yaml")
        try Data("other account".utf8).write(to: other)
        try TelegramClientConfiguration.migrateConfig(other, at: destination)
        XCTAssertEqual(try String(contentsOf: destination, encoding: .utf8), "bound account")
        XCTAssertEqual(try FileManager.default.attributesOfItem(atPath: destination.path)[.posixPermissions] as? Int, 0o600)
    }
    private let identityJSON = "{\"schema\":1,\"data\":{\"id\":123,\"username\":\"test_account\",\"bot\":false}}"
    private let incomingJSON = "{\"peer\":{\"type\":\"user\",\"id\":45,\"username\":\"sender\"},\"message\":{\"id\":9,\"date\":1234,\"out\":false,\"text\":\"你好\"}}"

    private func fixture(_ script: String) throws -> (URL, URL) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("tg-test-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let executable = root.appendingPathComponent("fake-client")
        try Data(("#!/bin/sh\n" + script).utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)
        return (root, executable)
    }
    @MainActor private func wait(_ predicate: () -> Bool) async throws {
        for _ in 0..<200 {
            if predicate() { return }
            try await Task.sleep(nanoseconds: 25_000_000)
        }
        XCTFail("Timed out waiting for fake tg")
    }
    func testIdentityEnvelopeAndNoPhoneDisplay() throws {
        let identity = try TelegramIdentity.parse(Data(identityJSON.utf8))
        XCTAssertEqual(identity.id, 123)
        XCTAssertEqual(identity.label, "@test_account")
        XCTAssertThrowsError(try TelegramIdentity.parse(Data("{\"schema\":2,\"data\":{\"id\":123}}".utf8)))
    }
    func testWatchParsesChunkedUTF8AndMultipleLines() throws {
        let data = Data((incomingJSON + "\n" + incomingJSON + "\n").utf8)
        var buffer = TelegramLineBuffer()
        var lines: [Data] = []
        for byte in data { lines += try buffer.append(Data([byte])) }
        XCTAssertEqual(lines.count, 2)
        let event = try JSONDecoder().decode(TelegramMessage.self, from: lines[0])
        XCTAssertEqual(event.message.text, "你好")
        XCTAssertEqual(event.peer.label, "@sender")
        XCTAssertEqual(event.id, "user:45:9")
    }
    func testWatchRejectsOversizedLine() {
        var buffer = TelegramLineBuffer()
        XCTAssertThrowsError(try buffer.append(Data(repeating: 65, count: 262_145)))
    }
    func testGroupNameAndSenderFollowOfficialWatchSchema() throws {
        let data = Data("{\"peer\":{\"type\":\"chat\",\"id\":45,\"name\":\"Test group\"},\"message\":{\"id\":9,\"date\":1234,\"out\":false,\"from\":{\"type\":\"user\",\"id\":7,\"name\":\"Sender\"},\"media\":\"photo\"}}".utf8)
        let event = try JSONDecoder().decode(TelegramMessage.self, from: data)
        XCTAssertEqual(event.peer.label, "Test group")
        XCTAssertEqual(event.message.from?.label, "Sender")
        XCTAssertEqual(event.message.media, "photo")
    }
    func testArgumentsAndEnvironmentDoNotLeakBotCredentials() {
        let config = URL(fileURLWithPath: "/private/Account With Spaces/config.yaml")
        XCTAssertEqual(TelegramClientConfiguration.arguments(.login, config: config),
            ["--config", config.path, "--output", "json", "login"])
        XCTAssertEqual(TelegramClientConfiguration.arguments(.identity, config: TelegramClientConfiguration.defaultDirectory.appendingPathComponent("gotd.cli.yaml")), ["--output", "json", "whoami"])
        let environment = TelegramClientConfiguration.environment([
            "PATH": "/usr/bin", "HOME": "/home/user", "TG_PASSWORD": "do-not-inherit",
            "TG_ACCOUNT": "another", "BOT_TOKEN": "secret", "APP_HASH": "secret",
            "TELEGRAM_BOT_TOKEN": "secret", "OPENCODE_TELEGRAM_EOT10_PASSWORD": "secret"
        ])
        XCTAssertEqual(environment, ["PATH": "/usr/bin", "HOME": "/home/user"])
    }
    func testQRParsingAndRendering() {
        let token = "tg://login?token=test_only-123"
        XCTAssertEqual(TelegramClientConfiguration.qrURL("or open: " + token + "\n"), token)
        XCTAssertEqual(TelegramClientConfiguration.qrURL(token + "\nor open: tg://login?token=updated"), "tg://login?token=updated")
        XCTAssertNil(TelegramClientConfiguration.qrURL("do not parse https://example.org"))
        XCTAssertNotNil(TelegramClientView.qrImage(token))
    }
    @MainActor func testStartupWithoutConfigDoesNotRunLogin() async {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let client = TelegramClient(executable: root.appendingPathComponent("does-not-exist"), directory: root)
        client.startAutomatically()
        XCTAssertEqual(client.state, .signedOut)
        XCTAssertNil(client.operation)
        XCTAssertFalse(FileManager.default.fileExists(atPath: root.path))
        await client.shutdown()
    }
    @MainActor func testAuthorizedStartupWatchesIncomingMessagesAndStopsOnExit() async throws {
        let outgoing = incomingJSON.replacingOccurrences(of: "false", with: "true").replacingOccurrences(of: "\"id\":9", with: "\"id\":10")
        let (root, executable) = try fixture("""
        for arg in "$@"; do command="$arg"; done
        case "$command" in
          whoami) printf '%s\\n' '\(identityJSON)' ;;
          watch)
            printf '%s' "$$" > listener.pid
            printf '%s\\n' 'Watching default for new messages' >&2
            printf '%s\\n' '\(incomingJSON)' '\(outgoing)'
            exec /bin/sleep 30 ;;
          *) exit 2 ;;
        esac
        """)
        defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("account")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try Data("fixture".utf8).write(to: directory.appendingPathComponent("gotd.cli.yaml"))
        let client = TelegramClient(executable: executable, directory: directory)
        client.startAutomatically()
        try await wait { client.state == .watching && client.messages.count == 1 }
        XCTAssertEqual(client.identity?.id, 123)
        XCTAssertEqual(client.messages[0].message.text, "你好")
        let pid = try XCTUnwrap(Int32(String(contentsOf: directory.appendingPathComponent("listener.pid"), encoding: .utf8)))
        await client.shutdown()
        XCTAssertNotEqual(kill(pid, 0), 0)
        XCTAssertEqual(client.state, .stopped)
        XCTAssertNil(client.operation)
        XCTAssertTrue(client.messages.isEmpty)
        client.startAutomatically()
        XCTAssertNil(client.operation)
    }
    @MainActor func testUserInitiatedQRLoginWithLocalPasswordThenWatch() async throws {
        let (root, executable) = try fixture("""
        for arg in "$@"; do command="$arg"; done
        case "$command" in
          init) : > gotd.cli.yaml ;;
          login)
            printf '%s\\n' 'or open: tg://login?token=test-only-token' >&2
            /bin/sleep 0.2
            printf '%s' '2FA password: ' >&2
            read -r password
            [ "$password" = 'test-password' ] || exit 3
            printf '%s\\n' '\(identityJSON)' ;;
          watch)
            printf '%s\\n' 'Watching default for new messages' >&2
            exec /bin/sleep 30 ;;
          *) exit 2 ;;
        esac
        """)
        defer { try? FileManager.default.removeItem(at: root) }
        let client = TelegramClient(executable: executable, directory: root.appendingPathComponent("account"))
        client.login()
        try await wait { client.qrURL != nil }
        XCTAssertEqual(client.qrURL, "tg://login?token=test-only-token")
        try await wait { client.state == .passwordRequired }
        XCTAssertNil(client.qrURL)
        client.submitPassword("test-password")
        try await wait { client.state == .watching }
        XCTAssertEqual(client.identity?.label, "@test_account")
        await client.shutdown()
    }
    @MainActor func testCancelledLoginAndShutdownDoNotResumeWatching() async throws {
        let (root, executable) = try fixture("printf '%s\\n' 'or open: tg://login?token=test-only-token' >&2\nexec /bin/sleep 30\n")
        defer { try? FileManager.default.removeItem(at: root) }
        try Data("fixture".utf8).write(to: root.appendingPathComponent("gotd.cli.yaml"))
        let client = TelegramClient(executable: executable, directory: root)
        client.login()
        try await wait { client.qrURL != nil }
        client.stopListening()
        try await wait { client.operation == nil && client.state == .signedOut }
        XCTAssertNil(client.qrURL)
        await client.shutdown()
        client.login(); client.check(); client.startListening()
        XCTAssertNil(client.operation)
        XCTAssertEqual(client.state, .stopped)
    }
    @MainActor func testLogoutStopsListenerAndClearsIdentity() async throws {
        let (root, executable) = try fixture("""
        for arg in "$@"; do command="$arg"; done
        case "$command" in
          whoami) printf '%s\\n' '\(identityJSON)' ;;
          watch) printf '%s\\n' 'Watching default for new messages' >&2; exec /bin/sleep 30 ;;
          logout) printf '%s\\n' '{"schema":1,"data":{"ok":true}}' ;;
          *) exit 2 ;;
        esac
        """)
        defer { try? FileManager.default.removeItem(at: root) }
        try Data("fixture".utf8).write(to: root.appendingPathComponent("gotd.cli.yaml"))
        let client = TelegramClient(executable: executable, directory: root)
        client.startAutomatically()
        try await wait { client.state == .watching }
        client.logout()
        try await wait { client.state == .signedOut }
        XCTAssertNil(client.identity)
        XCTAssertNil(client.operation)
        await client.shutdown()
    }
    @MainActor func testUnauthorizedSessionDoesNotLaunchInteractiveLogin() async throws {
        let (root, executable) = try fixture("printf '%s\\n' 'not authorized: run `tg login` first' >&2\nexit 1\n")
        defer { try? FileManager.default.removeItem(at: root) }
        try Data("fixture".utf8).write(to: root.appendingPathComponent("gotd.cli.yaml"))
        let client = TelegramClient(executable: executable, directory: root)
        client.startAutomatically()
        try await wait { client.state == .signedOut }
        XCTAssertNil(client.operation)
        XCTAssertNil(client.qrURL)
        await client.shutdown()
    }
}
