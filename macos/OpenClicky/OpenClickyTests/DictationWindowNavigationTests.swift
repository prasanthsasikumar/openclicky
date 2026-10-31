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

    @Test func theWindowOpensOnDictation() {
        #expect(DictationWindowModel().destination == .settings(.dictation))
        #expect(DictationSection.allCases.map(\.sidebarTitle) == ["Your words", "How it writes"])
    }

    @Test func theSidebarListsSixPages() {
        #expect(DictationSettingsPage.sidebarPages.map(\.title) == ["Dictation", "Clicky", "Island", "Keys & Account", "Privacy", "General"])
    }

    @Test func pagesOpenedFromARowKeepTheirParentSelected() {
        #expect(DictationDestination.settings(.orb).sidebarPage == .dictation)
        #expect(DictationDestination.section(.words).sidebarPage == .dictation)
        #expect(DictationDestination.section(.writing).sidebarPage == .dictation)
        #expect(DictationDestination.settings(.calendars).sidebarPage == .island)
        #expect(DictationDestination.settings(.privacy).sidebarPage == .privacy)
    }

    @Test func backReturnsThroughTheRowsOpened() {
        let model = DictationWindowModel()
        model.select(.settings(.dictation))
        #expect(!model.canGoBack)
        model.push(.settings(.orb))
        #expect(model.destination == .settings(.orb))
        #expect(model.canGoBack)
        model.goBack()
        #expect(model.destination == .settings(.dictation))
        #expect(!model.canGoBack)
    }

    @Test func aSidebarClickForgetsTheWayBack() {
        let model = DictationWindowModel()
        model.push(.section(.words))
        model.select(.settings(.clicky))
        #expect(!model.canGoBack)
    }
}
