import XCTest
@testable import OpenCodeTelegram

final class SetupWizardTests: XCTestCase {
    private let token = "123456:fake_test_token"
    private func response(_ request: URLRequest, _ json: String, status: Int = 200) -> (Data, URLResponse) {
        (Data(json.utf8), HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!)
    }
    func testCompletionMetadataOnlyChangesAfterExplicitCompletion() throws {
        let name = "setup-test-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        XCTAssertFalse(SetupRules.completed(defaults: defaults))
        SetupRules.markCompleted(defaults: defaults)
        XCTAssertTrue(SetupRules.completed(defaults: defaults))
        XCTAssertTrue(SetupRules.completed(defaults: try XCTUnwrap(UserDefaults(suiteName: name))))
        XCTAssertEqual(defaults.persistentDomain(forName: name)?.keys.sorted(), ["setupWizardCompleted"])
    }
    func testBundledChoiceIsFirstWithoutImplicitlySelectingAnEngine() {
        let resources = URL(fileURLWithPath: "/test/resources")
        let bundled = EngineCandidate(path: resources.appendingPathComponent("dependencies/opencode/v2/opencode").path, version: "2.0.22")
        let external = EngineCandidate(path: "/custom/opencode", version: "2.0.23")
        XCTAssertEqual(SetupRules.engineChoices([external, bundled], resources: resources).map(\.path), [bundled.path, external.path])
    }
    func testCompletingAgainKeepsOneVersionIndependentMarker() throws {
        let name = "setup-once-test-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        SetupRules.markCompleted(defaults: defaults)
        SetupRules.markCompleted(defaults: defaults)
        XCTAssertTrue(SetupRules.completed(defaults: defaults))
        XCTAssertEqual(defaults.persistentDomain(forName: name)?.keys.sorted(), ["setupWizardCompleted"])
    }
    func testBindingRejectsEmptyTokenInvalidUserOrInjection() {
        XCTAssertTrue(SetupRules.validBotFields(token: token, userID: "123"))
        for invalid in ["", "abc", "123:bad/extra", "123:x\n", " 123:x"] {
            XCTAssertFalse(SetupRules.validBotFields(token: invalid, userID: "123"))
        }
        for invalid in ["", "0", "-123", "group", "123\n", "999999999999999999999999", "１２３"] {
            XCTAssertFalse(SetupRules.validBotFields(token: token, userID: invalid))
        }
    }
    func testEncryptionCanBeOffButEnabledRequiresValidPassphrase() {
        var settings = Settings()
        XCTAssertTrue(SetupRules.encryptionReady(settings))
        settings.encryptionEnabled = true
        XCTAssertFalse(SetupRules.encryptionReady(settings))
        settings.encryptionPassword = "short"
        XCTAssertFalse(SetupRules.encryptionReady(settings))
        settings.encryptionPassword = "test-long-passphrase"
        XCTAssertTrue(SetupRules.encryptionReady(settings))
    }
    func testAutomaticBotStartupRequiresConfigurationAndAvoidsDuplicateDaemon() {
        var settings = Settings()
        XCTAssertFalse(SetupRules.shouldStartBot(settings, running: false))
        settings.botToken = token; settings.allowedUserID = "123"
        XCTAssertTrue(SetupRules.shouldStartBot(settings, running: false))
        XCTAssertFalse(SetupRules.shouldStartBot(settings, running: true))
    }
    @MainActor func testWizardStartsAtTelegramAndCannotSkipLoginOrBinding() {
        let model = AppModel(loadSettings: false)
        XCTAssertEqual(model.page, 6)
        XCTAssertEqual(model.setupStep, .telegram)
        XCTAssertFalse(model.setupCanContinue)
        model.setupRequired = true
        model.advanceSetup()
        XCTAssertEqual(model.setupStep, .telegram)
        model.setupStep = .bot
        model.settings.botToken = token; model.settings.allowedUserID = "123"
        XCTAssertFalse(model.setupBindingVerified)
        XCTAssertFalse(model.setupCanContinue)
        model.setupStep = .engine
        model.engines = [EngineCandidate(path: "/test/opencode", version: "2.0.22")]
        model.setupEnginePath = "/test/opencode"
        XCTAssertFalse(model.setupCanContinue)
        model.setupStep = .encryption
        XCTAssertTrue(model.setupCanContinue)
        model.settings.encryptionEnabled = true
        XCTAssertFalse(model.setupCanContinue)
    }
    func testBindingVerifiesBotAndAllowedPrivateChatWithoutSendingMessages() async throws {
        var methods: [String] = []
        let verified = try await BotBindingVerifier.verify(token: token, userID: "123") { request in
            methods.append(request.url!.lastPathComponent)
            XCTAssertEqual(request.httpMethod, "POST")
            if methods.count == 1 {
                return self.response(request, "{\"ok\":true,\"result\":{\"is_bot\":true,\"username\":\"test_bot\"}}")
            }
            let body = try JSONSerialization.jsonObject(with: request.httpBody!) as? [String: String]
            XCTAssertEqual(body, ["chat_id": "123"])
            return self.response(request, "{\"ok\":true,\"result\":{\"type\":\"private\",\"id\":123}}")
        }
        XCTAssertEqual(verified.username, "test_bot")
        XCTAssertEqual(methods, ["getMe", "getChat"])
    }
    func testBindingRejectsUnknownPrivateChatAndInvalidTokenWithoutLeaks() async {
        for status in [401, 400] {
            do {
                _ = try await BotBindingVerifier.verify(token: token, userID: "123") { request in
                    if status == 400 && request.url!.lastPathComponent == "getMe" {
                        return self.response(request, "{\"ok\":true,\"result\":{\"is_bot\":true,\"username\":\"test_bot\"}}")
                    }
                    return self.response(request, "{\"ok\":false,\"description\":\"do not show \(self.token)\"}", status: status)
                }
                XCTFail("Expected binding rejection")
            } catch { XCTAssertFalse(error.localizedDescription.contains(token)) }
        }
    }
    func testNetworkErrorDoesNotExposeCredentialBearingURL() async {
        do {
            _ = try await BotBindingVerifier.verify(token: token, userID: "123") { request in
                throw NSError(domain: "network", code: 1, userInfo: [NSLocalizedDescriptionKey: request.url!.absoluteString])
            }
            XCTFail("Expected failure")
        } catch {
            XCTAssertFalse(error.localizedDescription.contains(token))
            XCTAssertFalse(error.localizedDescription.contains("api.telegram.org"))
        }
    }
    func testWrongChatTypeOrIDCannotCompleteBinding() async {
        for chat in ["{\"type\":\"group\",\"id\":123}", "{\"type\":\"private\",\"id\":456}"] {
            do {
                _ = try await BotBindingVerifier.verify(token: token, userID: "123") { request in
                    if request.url!.lastPathComponent == "getMe" {
                        return self.response(request, "{\"ok\":true,\"result\":{\"is_bot\":true,\"username\":\"test_bot\"}}")
                    }
                    return self.response(request, "{\"ok\":true,\"result\":\(chat)}")
                }
                XCTFail("Expected failure")
            } catch { XCTAssertFalse(error.localizedDescription.contains(token)) }
        }
    }
}
