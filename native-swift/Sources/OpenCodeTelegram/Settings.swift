import Foundation
import Security
import LocalAuthentication

struct Settings: Codable {
    var locale = "zh"
    var botToken = ""
    var allowedUserID = ""
    var encryptionEnabled = false
    var encryptionPassword = ""
    var apiVersion = "v2"
    var serverURL = "http://127.0.0.1:49374"
    var serverUsername = "opencode"
    var serverPassword = ""
    var executable = ""
    var modelProvider = "opencode"
    var modelID = "big-pickle"
}

enum AppFailure: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self { case .message(let value): return value }
    }
}

enum SettingsStore {
    static let service = "org.opencode.telegram.desktop"
    static let home = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/OpenCodeTelegram", isDirectory: true)

    static func load(account: String = "settings") throws -> Settings {
        let context = LAContext()
        context.interactionNotAllowed = true
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service, kSecAttrAccount as String: account,
            kSecUseDataProtectionKeychain as String: false,
            // Never block window creation on a Keychain authorization dialog.
            kSecUseAuthenticationContext as String: context,
            kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        let code = SecItemCopyMatching(query as CFDictionary, &result)
        if code == errSecItemNotFound { return Settings() }
        guard code == errSecSuccess, let data = result as? Data else {
            throw AppFailure.message("Keychain: \(code)")
        }
        var settings = try JSONDecoder().decode(Settings.self, from: data)
        if settings.apiVersion != "v2" {
            settings.apiVersion = "v2"
            settings.executable = ""
            settings.serverURL = "http://127.0.0.1:49374"
        }
        return settings
    }

    static func save(_ settings: Settings, account: String = "settings") throws {
        let data = try JSONEncoder().encode(settings)
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service, kSecAttrAccount as String: account,
            kSecUseDataProtectionKeychain as String: false]
        let attributes: [String: Any] = [kSecValueData as String: data]
        var code = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if code == errSecItemNotFound {
            var item = query
            item[kSecValueData as String] = data
            code = SecItemAdd(item as CFDictionary, nil)
        }
        guard code == errSecSuccess else { throw AppFailure.message("Keychain: \(code)") }
    }
}
