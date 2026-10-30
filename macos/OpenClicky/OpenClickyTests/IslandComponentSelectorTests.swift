//
//  IslandComponentSelectorTests.swift
//  OpenClickyTests
//
//  Which component fills the island's slot: the moment first, then the user's choice.
//

import Foundation
import SwiftUI
import Testing
@testable import OpenClicky

struct IslandComponentSelectorTests {
    struct Stub: IslandComponent {
        let id: String
        var title: String { id }
        var symbolName: String { "circle" }
        var preferredHeight: CGFloat { 45 }
        var isPlugin = false
        let available: (IslandContext) -> Bool
        func isAvailable(in context: IslandContext) -> Bool { available(context) }
        @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView { AnyView(EmptyView()) }
    }
    let shortcuts = Stub(id: "shortcuts") { _ in true }
    let transcript = Stub(id: "transcript") { $0.contextualKey == "transcript" }
    let agent = Stub(id: "agent") { $0.contextualKey == "agent" }
    var registry: [any IslandComponent] { [shortcuts, transcript, agent] }
    let now = Date(timeIntervalSince1970: 1_000_000)

    @Test func idleShowsTheDefaultAloneSoNoCapsule() {
        let s = IslandComponentSelector.select(registry: registry, order: [], defaultID: "shortcuts", override: nil, context: .idle)
        #expect(s == IslandComponentSelection(active: "shortcuts", available: ["shortcuts"]))
    }

    @Test func aLiveTakeMakesTheTranscriptActiveAndKeepsItEightSecondsAfterLanding() {
        var context = IslandContext.idle
        context.now = now
        context.isTakeLive = true
        #expect(IslandComponentSelector.select(registry: registry, order: [], defaultID: "shortcuts", override: nil, context: context).active == "transcript")
        context.isTakeLive = false
        context.takeLandedAt = now.addingTimeInterval(-7.9)
        #expect(context.contextualKey == "transcript")
        context.takeLandedAt = now.addingTimeInterval(-8.1)
        #expect(context.contextualKey == nil)
    }

    @Test func anAgentOutranksTheDefaultButNotALiveTake() {
        var context = IslandContext.idle
        context.agentActivityText = "editing Hero.swift"
        #expect(context.contextualKey == "agent")
        context.isTakeLive = true
        #expect(context.contextualKey == "transcript")
        context.isTakeLive = false
        context.agentActivityText = nil
        context.isResultCardUp = true
        #expect(context.contextualKey == "agent")
    }

    @Test func theOverrideWinsWhileAvailableAndTheOrderIsTheUsers() {
        var context = IslandContext.idle
        context.agentActivityText = "working"
        let s = IslandComponentSelector.select(registry: registry, order: ["agent", "bogus", "shortcuts"], defaultID: "shortcuts", override: "shortcuts", context: context)
        #expect(s == IslandComponentSelection(active: "shortcuts", available: ["agent", "shortcuts"]))
        let gone = IslandComponentSelector.select(registry: registry, order: [], defaultID: "shortcuts", override: "transcript", context: context)
        #expect(gone.active == "agent")
    }

    @Test func anUnknownOrUnavailableDefaultFallsBackToTheFirstAvailable() {
        let s = IslandComponentSelector.select(registry: registry, order: ["transcript", "shortcuts"], defaultID: "nope", override: nil, context: .idle)
        #expect(s.active == "shortcuts")
        let t = IslandComponentSelector.select(registry: registry, order: [], defaultID: "transcript", override: nil, context: .idle)
        #expect(t.active == "shortcuts")
    }

    @MainActor
    @Test func theRealRegistryListsTheBuiltInsThenThePlugins() {
        let ids = IslandComponentRegistry.all.map(\.id)
        #expect(ids == ["home", "shortcuts", "transcript", "agent", "system", "shelf", "agents", "media", "teacher", "timer", "notes", "clipboard"])
        // Home's height follows what is on (IslandHomeLayoutTests).
        #expect(IslandComponentRegistry.all.dropFirst().map(\.preferredHeight) == [45, 52, 52, 84, 84, 76, 84, 64, 110, 120, 130])
        #expect(IslandComponentRegistry.all.map(\.isPlugin) == [false, false, false, false, true, true, true, true, true, true, true, true])
        #expect(IslandPluginRegistry.all.map(\.id) == ["system", "shelf", "agents", "media", "teacher", "timer", "notes", "clipboard"])
        #expect(IslandComponentRegistry.all.map { $0.settingsDetail != nil } == [false, false, false, false, true, true, true, true, true, true, true, true])
    }

    @Test func theContextIsBuiltFromWhatTheAppAlreadyPublishes() {
        let now = Date()
        let c = IslandContext.make(takeState: .listening, lastTake: nil, takeLandedAt: nil, liveTranscript: "hi", agentActivityText: nil, isResultCardUp: false, engineCanRun: true, placement: .notch, now: now)
        #expect(c.isTakeLive)
        #expect(c.contextualKey == "transcript")
        #expect(c.liveTranscript == "hi")
        let d = IslandContext.make(takeState: .idle, lastTake: nil, takeLandedAt: nil, liveTranscript: "", agentActivityText: nil, isResultCardUp: true, engineCanRun: true, placement: .notch, now: now)
        #expect(d.contextualKey == "agent")
    }

    @Test func aLandedTakeKeepsItsOwnWordsForEightSecondsFromLandingNotFromItsStart() {
        let now = Date()
        // A 20 s take that landed 3 s ago: the window counts from the landing, and the words are
        // the take's formatted text, not the live buffer the orb has already cleared.
        let take = TakeRecord(createdAt: now.addingTimeInterval(-23), rawText: "landed words", formattedText: "Landed words.")
        let c = IslandContext.make(takeState: .idle, lastTake: take, takeLandedAt: now.addingTimeInterval(-3), liveTranscript: "", agentActivityText: nil, isResultCardUp: false, engineCanRun: true, placement: .notch, now: now)
        #expect(c.contextualKey == "transcript")
        #expect(c.landedTranscript == "Landed words.")
        // The landing time is remembered by the manager at the live → idle flip.
        var live = IslandContext.idle
        live.isTakeLive = true
        var idle = IslandContext.idle
        #expect(IslandContext.landingTime(previous: live, next: idle, now: now) == now)
        #expect(IslandContext.landingTime(previous: idle, next: idle, now: now) == nil)
        idle.takeLandedAt = now.addingTimeInterval(-2)
        #expect(IslandContext.landingTime(previous: idle, next: idle, now: now) == now.addingTimeInterval(-2))
        #expect(IslandContext.landingTime(previous: idle, next: live, now: now) == nil)
    }

    @Test func theFollowUpPillShowsOnlyWithACardUpAndNoAgentRunning() {
        #expect(AgentIslandView.showsFollowUp(isRunning: false, isResultCardUp: true))
        #expect(!AgentIslandView.showsFollowUp(isRunning: true, isResultCardUp: true))
        #expect(!AgentIslandView.showsFollowUp(isRunning: false, isResultCardUp: false))
    }

    @Test func settingsRoundTripWithDefaults() {
        let defaults = UserDefaults(suiteName: "IslandComponentSelectorTests")!
        defaults.removePersistentDomain(forName: "IslandComponentSelectorTests")
        var settings = IslandComponentSettings(defaults: defaults)
        #expect(settings.defaultID == "home")
        #expect(settings.order == [])
        settings.defaultID = "agent"
        settings.order = ["agent", "shortcuts"]
        let again = IslandComponentSettings(defaults: defaults)
        #expect(again.defaultID == "agent")
        #expect(again.order == ["agent", "shortcuts"])
    }

    @Test func aDisabledComponentIsNeverAvailableAndADisabledDefaultFallsBackToShortcuts() {
        let system = Stub(id: "system") { _ in true }
        let registry: [any IslandComponent] = [shortcuts, transcript, agent, system]
        let off = IslandComponentSelector.select(registry: registry, order: [], defaultID: "system", override: "system", context: .idle, enabled: ["shortcuts", "transcript", "agent"])
        #expect(off == IslandComponentSelection(active: "shortcuts", available: ["shortcuts"]))
        let on = IslandComponentSelector.select(registry: registry, order: [], defaultID: "system", override: nil, context: .idle, enabled: ["shortcuts", "transcript", "agent", "system"])
        #expect(on == IslandComponentSelection(active: "system", available: ["shortcuts", "system"]))
    }

    @Test func pluginsAreOffUntilTurnedOn() {
        let defaults = UserDefaults(suiteName: "island-switchboard-tests")!
        defaults.removePersistentDomain(forName: "island-switchboard-tests")
        var settings = IslandComponentSettings(defaults: defaults)
        struct Optional: IslandComponent {
            let id = "system"; let title = "System"; let symbolName = "gauge"; let preferredHeight: CGFloat = 70; let isPlugin = true
            func isAvailable(in context: IslandContext) -> Bool { true }
            @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView { AnyView(EmptyView()) }
        }
        let registry: [any IslandComponent] = [Stub(id: "shortcuts") { _ in true }, Optional()]
        #expect(settings.enabledPlugins == [])
        #expect(settings.isEnabled("shortcuts", in: registry))
        #expect(!settings.isEnabled("system", in: registry))
        settings.enabledPlugins = ["system"]
        #expect(settings.isEnabled("system", in: registry))
        #expect(IslandComponentSettings.enabledIDs(in: registry, settings: settings) == ["shortcuts", "system"])
    }

    @Test func aStoredDefaultThatIsSwitchedOffReadsAsHomeInSettings() {
        let defaults = UserDefaults(suiteName: "island-settings-default-fallback")!
        defaults.removePersistentDomain(forName: "island-settings-default-fallback")
        var settings = IslandComponentSettings(defaults: defaults)
        let registry: [any IslandComponent] = [Stub(id: "shortcuts") { _ in true }, Stub(id: "system", isPlugin: true) { _ in true }]
        settings.defaultID = "system"
        #expect(settings.resolvedDefaultID(in: registry) == "home")
        settings.enabledPlugins = ["system"]
        #expect(settings.resolvedDefaultID(in: registry) == "system")
    }

    @Test func aDragOverTheIslandOutranksEveryOtherMoment() {
        var context = IslandContext.idle
        context.isTakeLive = true
        context.isDragHovering = true
        #expect(context.contextualKey == "shelf")
        context.isDragHovering = false
        #expect(context.contextualKey == "transcript")
    }

    @Test func playingMusicIsTheMomentAfterTheAgent() {
        var context = IslandContext.idle
        context.isMediaPlaying = true
        // The track is on home, so home is the moment.
        #expect(context.contextualKey == "home")
        context.agentActivityText = "working"
        #expect(context.contextualKey == "agent")
    }
}
