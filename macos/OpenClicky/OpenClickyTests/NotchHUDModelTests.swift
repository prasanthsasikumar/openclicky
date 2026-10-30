//
//  NotchHUDModelTests.swift
//  OpenClickyTests
//
//  Hovering the island only peeks; a click opens the panel; cards and setup outrank both.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct NotchHUDModelTests {
    private func notchModel() -> NotchHUDModel {
        NotchHUDModel(geometry: NotchGeometry(screenFrame: CGRect(x: 0, y: 0, width: 1512, height: 982), hasHardwareNotch: true, notchWidth: 190, notchHeight: 33, visibleFrame: CGRect(x: 0, y: 0, width: 1512, height: 949)))
    }

    /// The busy strip opens after the island's 0.4 s grace period; a busy CI runner can take longer.
    private func waitFor(_ model: NotchHUDModel, _ expansion: NotchHUDExpansion) async throws {
        for _ in 0..<150 where model.expansion != expansion {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    @Test func hoveringPeeksAndNeverOpensThePanel() {
        let model = notchModel()
        model.setHovering(true)
        #expect(model.expansion == .peek)
        // Sideways (the default): the band's own height, 170 pt either side of the camera.
        #expect(model.width == 530)
        #expect(model.height == 33)
        model.peekDirection = .down
        #expect(model.width == 230)   // 190 notch + 20 a side
        #expect(model.height == 57)   // 33 notch + 24 line
    }

    @Test func aClickOpensThePanelAndAnotherClickCloses() {
        let model = notchModel()
        model.setHovering(true)
        model.islandClicked()
        #expect(model.expansion == .full)
        model.islandClicked()
        #expect(model.expansion == .peek)   // still hovering
        model.setHovering(false)
        model.close()
        #expect(model.expansion == .collapsed)
    }

    @Test func aClickOnTheBusyStripOpensThePanelButNotWhileSpeakingOrInAGuide() async throws {
        let model = notchModel()
        model.setBusy(true)
        try await waitFor(model, .compact)
        #expect(model.expansion == .compact)
        model.islandClicked()
        #expect(model.expansion == .full)
        model.close()
        #expect(model.expansion == .compact)
        model.setSpeaking(true)
        model.islandClicked()
        #expect(model.expansion == .compact)
        model.setSpeaking(false)
        model.setGuideStep(1)
        model.islandClicked()
        #expect(model.expansion == .compact)
    }

    @Test func setupAndCardsOutrankThePeek() {
        let model = notchModel()
        model.setPermissionPrompt(PermissionPrompt(step: .accessibility, status: PermissionStatus()))
        model.setHovering(true)
        #expect(model.expansion == .permission)
        model.setPermissionPrompt(nil)
        model.setConnectPrompt(AppConnectPrompt(skillId: "youtube", appName: "YouTube", integration: "youtube", frontBundleIdentifier: nil, examplePrompts: []))
        #expect(model.expansion == .connect)
    }

    @Test func aDisplayWithoutAMenuBarBandStillHasRoomForThePeekLine() {
        let screenFrame = CGRect(x: -173, y: 982, width: 1920, height: 1080)
        let model = NotchHUDModel(geometry: NotchGeometry.virtualNotch(screenFrame: screenFrame, visibleFrame: screenFrame))
        model.setHovering(true)
        #expect(model.peekHeight == 58)   // 34 band + 24 line
        model.setBusy(true)
        #expect(model.compactHeight == 42)   // 34 band + 8 lip
    }

    @Test func sideDrawersStillOpenOnHover() {
        let model = notchModel()
        model.placement = .left
        model.setHovering(true)
        #expect(model.expansion == .full)
    }

    @Test func theSlotFollowsTheContextAndTheUsersOverrideSurvivesPartialTranscripts() {
        let model = notchModel()
        model.registry = IslandComponentSelectorTests().registry
        #expect(model.selection.active == "shortcuts")
        #expect(!model.showsCapsule)
        #expect(model.fullHeight == model.bandRowHeight + 45)
        var context = IslandContext.idle
        context.isTakeLive = true
        context.liveTranscript = "hello"
        model.setContext(context)
        #expect(model.selection.active == "transcript")
        #expect(model.showsCapsule)
        #expect(model.fullHeight == model.bandRowHeight + 45)   // the capsule lives in the band, not in a row of its own
        model.selectComponent("shortcuts")
        context.liveTranscript = "hello there"
        model.setContext(context)
        #expect(model.selection.active == "shortcuts")      // override survives a partial
        context.isTakeLive = false
        context.takeLandedAt = nil
        model.setContext(context)
        #expect(model.selection.active == "shortcuts")      // contextual key changed: override cleared, default wins
        context.agentActivityText = "working"
        model.setContext(context)
        #expect(model.selection.active == "agent")          // a fresh contextual key is followed again
    }

    @Test func theBusyStripKeepsItsWordsOutFromUnderTheCamera() throws {
        // 480 wide around a 190 pt notch: two 137 pt columns either side of a 206 pt gap.
        let columns = try #require(NotchCompactStatusView.columns(stripWidth: 480, notchWidth: 190))
        #expect(columns.side == 137)
        #expect(columns.gap == 206)
        #expect(columns.side * 2 + columns.gap == 480)
        // Without a camera there are no columns: the row is one line with today's spacing.
        #expect(NotchCompactStatusView.columns(stripWidth: 380, notchWidth: 0) == nil)
        let model = notchModel()
        model.setBusy(true)
        #expect(model.compactWidth == 480)
    }

    @Test func pressIsAFlagTheViewScalesOn() {
        let model = notchModel()
        model.setPressed(true)
        #expect(model.isPressed)
        model.setPressed(false)
        #expect(!model.isPressed)
    }

    @Test func aPluginJoinsTheSlotOnlyWhenTurnedOn() {
        let defaults = UserDefaults(suiteName: "island-model-switchboard")!
        defaults.removePersistentDomain(forName: "island-model-switchboard")
        let registry = IslandComponentSelectorTests().registry + [IslandComponentSelectorTests.Stub(id: "agents", isPlugin: true) { _ in true }]
        let model = NotchHUDModel(geometry: notchModel().geometry, registry: registry, componentSettings: IslandComponentSettings(defaults: defaults))
        #expect(model.selection.available == ["shortcuts"])
        var settings = IslandComponentSettings(defaults: defaults)
        settings.enabledPlugins = ["agents"]
        model.componentSettings = settings
        #expect(model.selection.available == ["shortcuts", "agents"])
        #expect(model.showsCapsule)
        // System and Now playing are drawn on home, and tools have toolbar buttons: none of
        // them is a stop in the capsule.
        #expect(model.capsuleIDs == ["shortcuts", "agents"])
    }

    @Test func aDragOverTheIslandUnfoldsItOnlyWhenTheShelfIsOn() async throws {
        let defaults = UserDefaults(suiteName: "island-model-shelf")!
        defaults.removePersistentDomain(forName: "island-model-shelf")
        let registry = IslandComponentSelectorTests().registry + [IslandComponentSelectorTests.Stub(id: "shelf", isPlugin: true) { _ in true }]
        let model = NotchHUDModel(geometry: notchModel().geometry, registry: registry, componentSettings: IslandComponentSettings(defaults: defaults))
        model.setDragHovering(true)
        #expect(model.expansion == .collapsed)
        var settings = IslandComponentSettings(defaults: defaults)
        settings.enabledPlugins = ["shelf"]
        model.componentSettings = settings
        model.setDragHovering(true)
        #expect(model.expansion == .full)
        #expect(model.selection.active == "shelf")
        model.setContext(.idle)                       // the manager's context never clears the drag
        #expect(model.context.isDragHovering)
        model.setDragHovering(false)
        try await waitFor(model, .collapsed)
        #expect(model.expansion == .collapsed)
        model.setDragOut(true)
        #expect(model.expansion == .full)
        model.setDragOut(false)
        model.holdOpenAfterDrop()
        #expect(model.expansion == .full)
        try await waitFor(model, .collapsed)
        #expect(model.expansion == .collapsed)
    }

    @Test func aDropKeepsTheShelfInFrontAndTheIslandOpenWhileThePointerStays() async throws {
        let defaults = UserDefaults(suiteName: "island-model-shelf-hold")!
        defaults.removePersistentDomain(forName: "island-model-shelf-hold")
        let registry = IslandComponentSelectorTests().registry + [IslandComponentSelectorTests.Stub(id: "shelf", isPlugin: true) { _ in true }]
        var settings = IslandComponentSettings(defaults: defaults)
        settings.enabledPlugins = ["shelf"]
        let model = NotchHUDModel(geometry: notchModel().geometry, registry: registry, componentSettings: settings)
        model.setDragHovering(true)
        model.setDragHovering(false)
        model.holdOpenAfterDrop()
        #expect(model.expansion == .full)
        #expect(model.selection.active == "shelf")      // the new tile is what the hold shows
        model.setHovering(true)
        try await Task.sleep(nanoseconds: 1_700_000_000)
        #expect(model.expansion == .full)                // the hold outlasts 1.5 s while the pointer stays
        model.setHovering(false)
        try await waitFor(model, .collapsed)
        #expect(model.expansion == .collapsed)
    }
}
