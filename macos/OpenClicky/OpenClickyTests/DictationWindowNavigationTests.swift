//
//  DictationWindowNavigationTests.swift
//  OpenClickyTests
//
//  The one window's navigation: your pages and the settings pages share one sidebar; every entry
//  point lands on the page it asks for, and a settings search gives way to a page.
//

import Testing
@testable import OpenClicky

@MainActor
struct DictationWindowNavigationTests {
    @Test func everySectionAndSettingsPageCanBeSelected() {
        let model = DictationWindowModel()
        for section in DictationSection.allCases {
            model.select(section)
            #expect(model.destination == .section(section))
        }
        for page in DictationSettingsPage.allCases {
            model.select(.settings(page))
            #expect(model.destination == .settings(page))
        }
    }

    @Test func aSettingsPageWinsOverASection() {
        let model = DictationWindowModel()
        model.open(section: .words, settingsPage: .dictation)
        #expect(model.destination == .settings(.dictation))
    }

    @Test func openingWithNothingKeepsWhereTheWindowWas() {
        let model = DictationWindowModel()
        model.select(.settings(.orb))
        model.searchText = "paste"
        model.open()
        #expect(model.destination == .settings(.orb))
        #expect(model.searchText == "paste")
    }

    @Test func openingAPageClearsALeftoverSearch() {
        // Otherwise "change engine" or the island's "open settings…" would leave results on screen.
        let model = DictationWindowModel()
        model.searchText = "microphone"
        model.open(settingsPage: .account)
        #expect(!model.isSearching)
        #expect(model.destination == .settings(.account))
    }

    @Test func whitespaceIsNotASearch() {
        let model = DictationWindowModel()
        model.searchText = "   "
        #expect(!model.isSearching)
        model.searchText = "  key "
        #expect(model.trimmedSearchText == "key")
    }

    @Test func yourPagesAreTwoAndOpenOnYourWords() {
        // Record and history are one page; dictionary, snippets and styles are another.
        #expect(DictationSection.allCases.map(\.sidebarTitle) == ["Your words", "How it writes"])
        #expect(DictationWindowModel().destination == .section(.words))
    }

    @Test func theSidebarListsYourPagesAfterVoiceAndBeforeTheIslandPages() {
        let sidebarTitles = DictationSettingsPage.pagesBeforeYourPages.map(\.title)
            + DictationSection.allCases.map(\.sidebarTitle)
            + DictationSettingsPage.pagesAfterYourPages.map(\.title)
        #expect(sidebarTitles == ["General", "Dictation", "Clicky", "Voice", "Your words", "How it writes", "Appearance", "Music", "Widgets", "Calendar", "Tools", "The Orb", "Shortcuts",
                                  "Keys & Plans", "Account", "Privacy & Security", "About"])
    }

    @Test func thereAreFifteenSettingsPages() {
        #expect(DictationSettingsPage.allCases.map(\.title) == ["General", "Dictation", "Clicky", "Voice", "Appearance", "Music", "Widgets", "Calendar", "Tools", "The Orb", "Shortcuts",
                                                                "Keys & Plans", "Account", "Privacy & Security", "About"])
    }
}
