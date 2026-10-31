//
//  SettingsSearchTests.swift
//  OpenClickyTests
//
//  Settings search: which items a query matches, how results group into "page › section",
//  and which words get the highlighter.
//

import SwiftUI
import Testing
@testable import OpenClicky

@MainActor
struct SettingsSearchTests {
    private func item(_ id: String, page: DictationSettingsPage, section: String, title: String, detail: String = "", keywords: [String] = []) -> SettingsItem {
        SettingsItem(id: id, page: page, section: section, title: title, detail: detail, keywords: keywords, view: AnyView(EmptyView()))
    }

    @Test func matchesTitleDetailKeywordsAndPageCaseInsensitively() {
        let openAtLogin = item("login", page: .general, section: "Startup", title: "Open at login", detail: "Start OpenClicky when you log in.", keywords: ["startup"])
        #expect(openAtLogin.matches("Login"))
        #expect(openAtLogin.matches("log in"))
        #expect(openAtLogin.matches("startup"))
        #expect(openAtLogin.matches("general"))
        #expect(!openAtLogin.matches("microphone"))
    }

    @Test func everyWordOfTheQueryMustMatch() {
        let sounds = item("sounds", page: .dictation, section: "More", title: "Sounds", detail: "Tones when a take starts.")
        #expect(sounds.matches("take tones"))
        #expect(!sounds.matches("take haptics"))
    }

    @Test func resultsGroupByPageInOrder() {
        let items = [
            item("a", page: .dictation, section: "", title: "Dictation key"),
            item("b", page: .dictation, section: "More", title: "Cancel a take"),
            item("c", page: .clicky, section: "Pointer", title: "Show the pointer"),
        ]
        let groups = SettingsSectionGroup.groups(items) { $0.page.title }
        #expect(groups.map(\.title) == ["Dictation", "Clicky"])
        #expect(groups.first?.items.count == 2)
    }

    @Test func aSearchOnlyItemIsListedUnderItsOwnPageName() {
        var dictionary = item("d", page: .dictation, section: "How it writes", title: "Dictionary", keywords: ["spelling"])
        dictionary.searchOnlyGroup = "How it writes"
        #expect(dictionary.matches("spelling"))
        #expect(dictionary.matches("how it writes"))
        #expect(!dictionary.matches("dictation key"))
        #expect(SettingsSectionGroup.groups([dictionary]) { $0.searchGroupTitle }.map(\.title) == ["How it writes"])
    }

    @Test func pagesWhoseRowsAreNamedForTheQueryComeFirst() {
        var dictionary = item("d", page: .dictation, section: "How it writes", title: "Dictionary")
        dictionary.searchOnlyGroup = "How it writes"
        let matches = [
            item("p", page: .privacy, section: "What it keeps", title: "Delete all takes", detail: "Your dictionary stays."),
            dictionary,
        ]
        #expect(SettingsSectionGroup.searchResultGroups(matches, query: "dictionary").map(\.title) == ["How it writes", "Privacy"])
        #expect(SettingsSectionGroup.searchResultGroups(matches, query: "stays").map(\.title) == ["Privacy", "How it writes"])
    }

    @Test func aFooterJoinsTheSectionBeforeIt() {
        var footer = item("f", page: .privacy, section: "Where things live", title: "Privacy summary")
        footer.chrome = .footer
        let items = [item("a", page: .privacy, section: "Where things live", title: "History"), footer]
        let groups = SettingsSectionGroup.groups(items)
        #expect(groups.count == 1)
        #expect(groups.first?.items.map(\.id) == ["a"])
        #expect(groups.first?.footers.map(\.id) == ["f"])
    }

    @Test func aPageOpenedFromARowIsListedUnderItsParent() {
        let theme = item("t", page: .orb, section: "Look", title: "Theme")
        #expect(theme.searchGroupTitle == "Dictation › The orb")
        #expect(theme.matches("orb theme"))
    }

    @Test func onlyAPageFoldsItsAdvancedGroup() {
        var sounds = item("s", page: .dictation, section: "More", title: "Sounds")
        sounds.isAdvanced = true
        let input = item("i", page: .dictation, section: "Microphone", title: "Input")
        #expect(SettingsSectionGroup.groups([input, sounds], foldsAdvanced: true).map(\.isAdvanced) == [false, true])
        // Search ignores the fold.
        #expect(SettingsSectionGroup.groups([input, sounds]).map(\.isAdvanced) == [false, false])
    }

    @Test func highlightMarksEveryOccurrence() {
        let highlighted = settingsHighlighted("tap fn + ⌃ twice; FN again", query: "fn")
        let markedRuns = highlighted.runs.filter { $0.backgroundColor != nil }
        #expect(markedRuns.count == 2)
    }
}
