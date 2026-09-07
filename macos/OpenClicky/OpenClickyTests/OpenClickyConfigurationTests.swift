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
}
