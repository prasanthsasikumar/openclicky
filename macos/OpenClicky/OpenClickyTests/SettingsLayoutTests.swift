//
//  SettingsLayoutTests.swift
//  OpenClickyTests
//
//  The settings layout: every row has one place, each page folds at most one group, and the
//  merged pointer picker reads and writes the two settings beneath it.
//

import SwiftUI
import Testing
@testable import OpenClicky

@MainActor
struct SettingsLayoutTests {
    private func item(_ id: String, section: String = "") -> SettingsItem {
        SettingsItem(id: id, section: section, title: id, view: AnyView(EmptyView()))
    }

    @Test func noRowIsPlacedTwice() {
        let allIDs = SettingsLayout.groups.flatMap(\.itemIDs)
        #expect(Set(allIDs).count == allIDs.count)
    }

    @Test func everyPageHasRowsAndFoldsAtMostOneGroupLast() {
        for page in DictationSettingsPage.allCases {
            let pageGroups = SettingsLayout.groups.filter { $0.page == page }
            #expect(!pageGroups.isEmpty, "\(page) has no rows")
            let foldedIndices = pageGroups.indices.filter { pageGroups[$0].isAdvanced }
            #expect(foldedIndices.count <= 1, "\(page) folds more than one group")
            if let foldedIndex = foldedIndices.first {
                #expect(pageGroups[(foldedIndex + 1)...].allSatisfy { !$0.showsOnPage }, "\(page)'s folded group is not last")
            }
        }
    }

    @Test func arrangingPlacesRowsInLayoutOrderAndDropsUnplacedOnes() {
        let arranged = SettingsLayout.arrange([
            item("orb.visible"), item("keyboard.talk"), item("dictation.engine.parakeet"), item("not.a.row"), item("dictation.engine.offline"),
        ])
        #expect(arranged.map(\.id) == ["dictation.engine.parakeet", "dictation.engine.offline", "orb.visible", "keyboard.talk"])
        #expect(arranged.map(\.page) == [.dictation, .dictation, .dictation, .clicky])
        #expect(arranged.map(\.isAdvanced) == [false, false, true, false])
        #expect(arranged[2].section == "More")
    }

    @Test func aGroupWithoutATitleKeepsTheRowsOwnSection() {
        let arranged = SettingsLayout.arrange([item("calendar.calendar.abc", section: "iCloud")])
        #expect(arranged.first?.page == .calendars)
        #expect(arranged.first?.section == "iCloud")
    }

    @Test func incognitoIsFoundBySearchButNotDrawnOnPrivacy() {
        let incognito = SettingsLayout.arrange([item("privacy.incognito")]).first
        #expect(incognito?.page == .privacy)
        #expect(incognito?.showsOnPage == false)
    }

    @Test func thePointerPickerReadsTheTwoSettings() {
        #expect(PointerShowsChoice(isPointerShown: false, presence: .always) == .whileTalking)
        #expect(PointerShowsChoice(isPointerShown: true, presence: .always) == .always)
        #expect(PointerShowsChoice(isPointerShown: true, presence: .whileMoving) == .whileMoving)
        #expect(PointerShowsChoice(isPointerShown: true, presence: .onShake) == .onShake)
        #expect(PointerShowsChoice.whileTalking.presence == nil)
        #expect(PointerShowsChoice.onShake.presence == .onShake)
    }
}
