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
}
