//
//  OpenClickyConfigurationTests.swift
//  OpenClickyTests
//
//  Bring your own key: the user's provider keys from shell.json travel as headers and reach the
//  CLI under OpenClicky names, never as OPENAI_API_KEY.
//

import Foundation
import Testing
@testable import OpenClicky

struct OpenClickyConfigurationTests {
    @Test func providerKeyHeadersComeFromTheSettings() {
        var settings = OpenClickyShellSettings()
        settings.openaiApiKey = " sk-user "
        settings.anthropicApiKey = ""
        let headers = OpenClickyConfiguration.providerKeyHeaders(from: settings)
        #expect(headers == ["x-openclicky-openai-key": "sk-user"])
        #expect(OpenClickyConfiguration.usesOwnKeys(settings))
        #expect(!OpenClickyConfiguration.usesOwnKeys(OpenClickyShellSettings()))
    }

    @Test func cliEnvironmentCarriesTheUserKeysUnderOpenClickyNames() {
        var settings = OpenClickyShellSettings()
        settings.openaiApiKey = "sk-user"
        settings.token = "tok"
        let environment = OpenClickyConfiguration.cliProcessEnvironment(from: settings)
        #expect(environment["OPENCLICKY_OPENAI_KEY"] == "sk-user")
        #expect(environment["OPENCLICKY_TOKEN"] == "tok")
        #expect(environment["OPENAI_API_KEY"] == nil)
        #expect(environment["OPENCLICKY_ANTHROPIC_KEY"] == nil)
    }

    @Test func resolvedCuaDriverBinAnExplicitSettingWins() {
        var settings = OpenClickyShellSettings()
        settings.cuaDriverBin = " /custom/path/cua-driver "
        let resolved = OpenClickyConfiguration.resolvedCuaDriverBin(
            from: settings,
            environment: ["CUA_DRIVER_BIN": "/should/be/ignored"],
            isExecutable: { _ in true }
        )
        #expect(resolved == "/custom/path/cua-driver")
    }

    @Test func resolvedCuaDriverBinAnExplicitEmptyStringDisablesIt() {
        var settings = OpenClickyShellSettings()
        settings.cuaDriverBin = ""
        let resolved = OpenClickyConfiguration.resolvedCuaDriverBin(
            from: settings,
            environment: ["CUA_DRIVER_BIN": "/should/also/be/ignored"],
            isExecutable: { _ in true }
        )
        #expect(resolved == nil)
    }

    @Test func resolvedCuaDriverBinFallsBackToTheEnvironmentThenAutoDiscovery() {
        let settings = OpenClickyShellSettings()
        // Unset in shell.json: the environment variable wins next.
        #expect(OpenClickyConfiguration.resolvedCuaDriverBin(
            from: settings, environment: ["CUA_DRIVER_BIN": "/env/cua-driver"], isExecutable: { _ in true }
        ) == "/env/cua-driver")
        // Neither set: the first candidate the injected check reports executable wins, never the
        // real filesystem (this must pass whether or not CuaDriver happens to be installed here).
        #expect(OpenClickyConfiguration.resolvedCuaDriverBin(
            from: settings, environment: [:], isExecutable: { $0.hasSuffix("CuaDriver.app/Contents/MacOS/cua-driver") }
        ) == "/Applications/CuaDriver.app/Contents/MacOS/cua-driver")
        // Nothing found anywhere: nil.
        #expect(OpenClickyConfiguration.resolvedCuaDriverBin(
            from: settings, environment: [:], isExecutable: { _ in false }
        ) == nil)
    }

    @Test func writableRootsReachTheCLIOnlyWhenTheUserSetsThem() {
        // Unset, the agent applies its own default (the home folder); a list here overrides it.
        #expect(OpenClickyConfiguration.cliProcessEnvironment(from: OpenClickyShellSettings())["OPENCLICKY_WRITABLE_ROOTS"] == nil)

        var settings = OpenClickyShellSettings()
        settings.writableRoots = ["~/Desktop", "/Volumes/Work"]
        let environment = OpenClickyConfiguration.cliProcessEnvironment(from: settings)
        #expect(environment["OPENCLICKY_WRITABLE_ROOTS"] == "~/Desktop,/Volumes/Work")
    }

    // MARK: - shell.json permissions

    /// shell.json carries the session token, refresh token, and the user's own provider keys, so a
    /// fresh write must land at 0600 (owner-only), never the OS default of 0644 (world-readable).
    /// Exercises the pure helper against a temp directory rather than the user's real ~/.openclicky.
    @Test func writingSettingsProducesAnOwnerOnlyFile() throws {
        let temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("openclicky-settings-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryDirectory) }
        let fileURL = temporaryDirectory.appendingPathComponent("shell.json")

        try OpenClickyConfiguration.writeShellSettingsData(Data("{}".utf8), toFileAt: fileURL)

        let fileAttributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
        let filePermissions = fileAttributes[.posixPermissions] as? NSNumber
        #expect(filePermissions?.uint16Value == 0o600)

        let directoryAttributes = try FileManager.default.attributesOfItem(atPath: temporaryDirectory.path)
        let directoryPermissions = directoryAttributes[.posixPermissions] as? NSNumber
        #expect(directoryPermissions?.uint16Value == 0o700)
    }

    /// The case that actually matters on a machine that already has a loosely-permissioned
    /// shell.json from before this fix: the very next write must tighten it, not just a fresh file.
    @Test func writingSettingsTightensAnExistingWorldReadableFile() throws {
        let temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("openclicky-settings-test-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryDirectory) }
        try FileManager.default.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o755])
        let fileURL = temporaryDirectory.appendingPathComponent("shell.json")
        try FileManager.default.createFile(atPath: fileURL.path, contents: Data("{}".utf8), attributes: [.posixPermissions: 0o644])

        try OpenClickyConfiguration.writeShellSettingsData(Data("{\"token\":\"tok\"}".utf8), toFileAt: fileURL)

        let fileAttributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
        #expect((fileAttributes[.posixPermissions] as? NSNumber)?.uint16Value == 0o600)

        let directoryAttributes = try FileManager.default.attributesOfItem(atPath: temporaryDirectory.path)
        #expect((directoryAttributes[.posixPermissions] as? NSNumber)?.uint16Value == 0o700)
    }
}
