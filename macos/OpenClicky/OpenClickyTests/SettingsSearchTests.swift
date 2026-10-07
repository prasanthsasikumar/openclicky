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
        let openAtLogin = item("login", page: .general, section: "behavior", title: "open at login", detail: "start openclicky when you log in.", keywords: ["startup"])
        #expect(openAtLogin.matches("Login"))
        #expect(openAtLogin.matches("log in"))
        #expect(openAtLogin.matches("startup"))
        #expect(openAtLogin.matches("general"))
        #expect(!openAtLogin.matches("microphone"))
    }

    @Test func everyWordOfTheQueryMustMatch() {
        let sounds = item("sounds", page: .orb, section: "feedback", title: "sounds", detail: "tones when a take starts.")
        #expect(sounds.matches("take tones"))
        #expect(!sounds.matches("take haptics"))
    }

    @Test func resultsGroupByPageAndSectionInOrder() {
        let items = [
            item("a", page: .shortcuts, section: "dictation", title: "dictation key"),
            item("b", page: .shortcuts, section: "dictation", title: "cancel a take"),
            item("c", page: .shortcuts, section: "the companion", title: "hands-free"),
        ]
        let groups = SettingsSectionGroup.groups(items) { "\($0.page.title) › \($0.section)" }
        #expect(groups.map(\.title) == ["shortcuts › dictation", "shortcuts › the companion"])
        #expect(groups.first?.items.count == 2)
    }

    @Test func highlightMarksEveryOccurrence() {
        let highlighted = settingsHighlighted("tap fn + ⌃ twice; FN again", query: "fn")
        let markedRuns = highlighted.runs.filter { $0.backgroundColor != nil }
        #expect(markedRuns.count == 2)
    }
}
