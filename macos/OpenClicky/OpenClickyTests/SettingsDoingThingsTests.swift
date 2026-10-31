//
//  SettingsDoingThingsTests.swift
//  OpenClickyTests
//
//  The two rows "do things for me" adds to Settings: which inbox (Clicky › More) and own Spotify
//  credentials (Keys & Account). The views themselves are not tested; their pure mappings are.
//

import Testing
@testable import OpenClicky

@MainActor
struct SettingsDoingThingsTests {
    @Test func theEmailPickerMapsItsTagsToTheSetting() {
        #expect(EmailAppRow.settingValue(forTag: "auto") == nil)
        #expect(EmailAppRow.settingValue(forTag: "mail") == "mail")
        #expect(EmailAppRow.settingValue(forTag: "gmail") == "gmail")
        #expect(EmailAppRow.tag(forSetting: nil) == "auto")
        #expect(EmailAppRow.tag(forSetting: "gmail") == "gmail")
        #expect(EmailAppRow.tag(forSetting: "outlook") == "auto")
        #expect(EmailAppRow.footerText == "Nothing is ever sent by voice.")
    }

    @Test func theSpotifyKeyRowWritesTheCredentialsAndAnswersNothing() {
        #expect(KeyProvider.allCases.contains(.spotify))
        #expect(KeyProvider.spotify.title == "Spotify")
        #expect(KeyProvider.spotify.placeholder == "id:secret")
        #expect(KeyProvider.spotify.keyPath == \OpenClickyShellSettings.spotifyCredentials)
        #expect(KeyProvider.spotify.answerSource == nil)
        #expect(KeyProvider.spotify.speechVoice == nil)
        #expect(KeyProvider.spotify.detail.contains("developer.spotify.com"))
    }
}
