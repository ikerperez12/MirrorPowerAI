import Foundation
import Security

/// Minimal Keychain wrapper. The API key is sensitive, so it lives in the
/// Keychain rather than UserDefaults/plain files. The "system context" text
/// is not sensitive but is stored alongside it for convenience.
enum KeychainHelper {
    private static let service = "com.mirrorpowerai.app"
    private static let apiKeyAccount = "gemini-api-key"
    private static let contextAccount = "system-context"

    static func saveAPIKey(_ value: String) { save(value, account: apiKeyAccount) }
    static func loadAPIKey() -> String? { load(account: apiKeyAccount) }

    static func saveContext(_ value: String) { save(value, account: contextAccount) }
    static func loadContext() -> String? { load(account: contextAccount) }

    private static func save(_ value: String, account: String) {
        let data = Data(value.utf8)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(query as CFDictionary)

        var attributes = query
        attributes[kSecValueData as String] = data
        SecItemAdd(attributes as CFDictionary, nil)
    }

    private static func load(account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
}
