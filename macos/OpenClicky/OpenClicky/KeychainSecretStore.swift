//
//  KeychainSecretStore.swift
//  ATMIYA AI (forked from OpenClicky, MIT)
//
//  API keys and tokens are stored in the macOS Keychain (never in plaintext on disk, never logged).
//  Non-secret settings stay in ~/.openclicky/shell.json. Secrets in shell.json are migrated into the
//  Keychain on first read and removed from the file. Keys are stored with
//  kSecAttrAccessibleWhenUnlockedThisDeviceOnly so they never sync to iCloud or leave this Mac.
//

import Foundation
import Security

nonisolated enum KeychainSecretStore {
    /// Keychain service for this app's secrets.
    static let service = "org.atmiya.app.secrets"

    /// Account names for each secret field kept out of shell.json.
    enum Key {
        static let openaiApiKey = "openaiApiKey"
        static let anthropicApiKey = "anthropicApiKey"
        static let geminiApiKey = "geminiApiKey"
        static let openRouterApiKey = "openRouterApiKey"
        static let groqApiKey = "groqApiKey"
        static let mistralApiKey = "mistralApiKey"
        static let xaiApiKey = "xaiApiKey"
        static let openCodeApiKey = "openCodeApiKey"
        static let sarvamKey = "sarvamKey"
        static let elevenLabsApiKey = "elevenLabsApiKey"
        static let googleCloudApiKey = "googleCloudApiKey"
        static let amazonPollyCredentials = "amazonPollyCredentials"
        static let composioApiKey = "composioApiKey"
        static let spotifyCredentials = "spotifyCredentials"
        static let token = "token"
        static let refreshToken = "refreshToken"
    }

    /// All field names whose values are secrets, pulled from shell.json into the Keychain.
    static let secretFields: [String] = [
        Key.openaiApiKey, Key.anthropicApiKey, Key.geminiApiKey, Key.openRouterApiKey, Key.groqApiKey,
        Key.mistralApiKey, Key.xaiApiKey, Key.openCodeApiKey, Key.sarvamKey, Key.elevenLabsApiKey,
        Key.googleCloudApiKey, Key.amazonPollyCredentials, Key.composioApiKey, Key.spotifyCredentials,
        Key.token, Key.refreshToken,
    ]

    static func read(_ account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data, let value = String(data: data, encoding: .utf8), !value.isEmpty else { return nil }
        return value
    }

    @discardableResult
    static func write(_ account: String, _ value: String) -> Bool {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
        SecItemDelete(base as CFDictionary)
        var add = base
        add[kSecValueData as String] = Data(value.utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    @discardableResult
    static func delete(_ account: String) -> Bool {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
        return SecItemDelete(query as CFDictionary) == errSecSuccess
    }
}
