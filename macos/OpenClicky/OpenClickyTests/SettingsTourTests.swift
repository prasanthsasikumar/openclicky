//
//  SettingsTourTests.swift
//  OpenClickyTests
//
//  The one-minute Settings tour has a recording for every step and stays about a minute long, and
//  the pointer starts hiding until a shake once OpenClicky is familiar.
//

import AVFoundation
import Foundation
import Testing
@testable import OpenClicky

struct SettingsTourTests {
    @Test func everyStepHasItsRecording() throws {
        for step in SettingsTourStep.all {
            #expect(Bundle.main.url(forResource: step.line.rawValue, withExtension: "mp3") != nil, "\(step.line.rawValue).mp3 is missing")
        }
    }

    @Test func theNarrationIsAboutAMinute() throws {
        let totalSeconds = try SettingsTourStep.all.reduce(0.0) { total, step in
            let url = try #require(Bundle.main.url(forResource: step.line.rawValue, withExtension: "mp3"))
            return total + (try AVAudioPlayer(contentsOf: url).duration)
        }
        #expect(totalSeconds > 40 && totalSeconds < 75)
    }

    @Test func everyStepPointsAtARowTheLayoutPlaces() {
        for step in SettingsTourStep.all {
            guard let targetID = step.targetID else { continue }
            let group = SettingsLayout.groups.first { $0.itemIDs.contains(targetID) }
            #expect(group != nil, "\(targetID) has no place")
            #expect(group?.isAdvanced == false, "\(targetID) is folded away")
            if case .settings(let page) = step.destination { #expect(group?.page == page, "\(targetID) is not on \(page)") }
        }
    }

    @Test func theTourEndsWhereItCanBeReplayed() {
        #expect(SettingsTourStep.all.last?.targetID == "general.settingsTour")
        #expect(SettingsTourStep.all.last?.destination == .settings(.general))
    }

    @Test func sidebarTargetsAreNamedByPage() {
        #expect(SettingsTourTargets.sidebarTargetID(for: .settings(.clicky)) == "sidebar.page.clicky")
        // Your words has no sidebar row of its own: the tour clicks Dictation.
        #expect(SettingsTourTargets.sidebarTargetID(for: .section(.words)) == "sidebar.page.dictation")
        #expect(SettingsTourTargets.sidebarTargetID(for: .settings(.orb)) == "sidebar.page.dictation")
    }
}

struct PointerPresenceDefaultTests {
    @Test func thePointerStaysForTheFirstFewUses() {
        #expect(PointerPresenceDefault.presenceToSwitchTo(useCount: 4, personChoseOne: false, current: .always) == nil)
    }

    @Test func thenItWaitsForAShake() {
        #expect(PointerPresenceDefault.presenceToSwitchTo(useCount: 5, personChoseOne: false, current: .always) == .onShake)
        #expect(PointerPresenceDefault.presenceToSwitchTo(useCount: 40, personChoseOne: false, current: .always) == .onShake)
    }

    @Test func aChoiceThePersonMadeIsKept() {
        #expect(PointerPresenceDefault.presenceToSwitchTo(useCount: 9, personChoseOne: true, current: .always) == nil)
        #expect(PointerPresenceDefault.presenceToSwitchTo(useCount: 9, personChoseOne: false, current: .whileMoving) == nil)
    }
}
