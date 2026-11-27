//
//  PermissionPromptTests.swift
//  OpenClickyTests
//
//  The permission cards the notch island shows one at a time ("I need accessibility
//  permissions." → Grant): which step is asked for next, what the card says, and whether a
//  dismissal stays respected.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct PermissionPromptTests {

    /// Records grant requests instead of poking System Settings.
    @MainActor
    private final class Spy {
        var requestedSteps: [PermissionStep] = []
    }

    private func makeController(_ spy: Spy, defaults: UserDefaults? = nil) -> PermissionPromptController {
        let isolatedDefaults = defaults ?? UserDefaults(suiteName: "PermissionPromptTests.\(UUID().uuidString)")!
        let controller = PermissionPromptController(defaults: isolatedDefaults)
        controller.performRequest = { spy.requestedSteps.append($0) }
        return controller
    }

    @Test func asksForOneStepAtATimeInOrder() {
        var status = PermissionStatus()
        // Accessibility first: from then on the pointer can show and help with the rest.
        #expect(PermissionPromptController.nextStep(for: status) == .accessibility)

        status.accessibility = true
        #expect(PermissionPromptController.nextStep(for: status) == .microphone)

        status.microphone = true
        #expect(PermissionPromptController.nextStep(for: status) == .screenRecording)

        // Screen Content is never asked for before Screen Recording: capturing is what
        // triggers its picker.
        status.screenRecording = true
        #expect(PermissionPromptController.nextStep(for: status) == .screenContent)

        status.screenContent = true
        #expect(PermissionPromptController.nextStep(for: status) == nil)
    }

    @Test func theCardClosesItselfOnceEverythingIsGranted() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus())
        #expect(controller.currentPrompt?.step == .accessibility)

        controller.update(with: PermissionStatus(microphone: true, accessibility: true,
                                                 screenRecording: true, screenContent: true))
        #expect(controller.currentPrompt == nil)
    }

    @Test func grantingAdvancesToTheNextStepAndResetsTheStage() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus())
        controller.grant()
        #expect(spy.requestedSteps == [.accessibility])
        #expect(controller.currentPrompt?.stage == .waiting)

        // The poll sees accessibility land: the card moves on, back to its asking stage.
        controller.update(with: PermissionStatus(accessibility: true))
        #expect(controller.currentPrompt?.step == .microphone)
        #expect(controller.currentPrompt?.stage == .ask)
    }

    /// A tester on a fresh Mac was taken aback by four prompts in a row: the first card now says
    /// how many are coming and lists them, and its one button still grants the first.
    @Test func theFirstCardListsAllFourPermissionsBeforeAsking() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus())
        let overview = controller.currentPrompt
        #expect(overview?.stage == .overview)
        #expect(overview?.step == .accessibility)
        #expect(overview?.headline.contains("four") == true)
        #expect(overview?.buttonTitle.contains("start") == true)
        #expect(overview?.detail.contains("accessibility") == true)
        #expect(overview?.checklist.map(\.step) == PermissionStep.allCases)
        #expect(overview?.checklist.allSatisfy { !$0.isGranted } == true)

        controller.grant()
        #expect(spy.requestedSteps == [.accessibility])
        #expect(controller.currentPrompt?.stage == .waiting)
    }

    @Test func theOverviewIsSkippedOnceAnythingIsGrantedAndNeverComesBack() {
        let spy = Spy()
        let controller = makeController(spy)

        // A relaunch after Screen Recording: something is granted already, no overview.
        controller.update(with: PermissionStatus(accessibility: true))
        #expect(controller.currentPrompt?.stage == .ask)

        let freshController = makeController(spy)
        freshController.update(with: PermissionStatus())
        freshController.grant()
        freshController.update(with: PermissionStatus(accessibility: true))
        #expect(freshController.currentPrompt?.step == .microphone)
        #expect(freshController.currentPrompt?.stage == .ask)
        // The checklist on a later card shows what landed.
        #expect(freshController.currentPrompt?.checklist.first { $0.step == .accessibility }?.isGranted == true)
        #expect(freshController.currentPrompt?.checklist.first { $0.step == .microphone }?.isGranted == false)
        // Taken away and brought back (the poll reports no change): still no overview.
        freshController.update(with: PermissionStatus())
        #expect(freshController.currentPrompt?.stage == .ask)
    }

    @Test func everyPermissionHasAShortNameForTheChecklist() {
        #expect(PermissionStep.allCases.map(\.shortName) == ["accessibility", "microphone", "screen recording", "screen content"])
    }

    @Test func theProgressDotsFollowWhatIsAlreadyGranted() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus(accessibility: true))
        let prompt = controller.currentPrompt
        #expect(prompt?.status.isGranted(.accessibility) == true)
        #expect(prompt?.status.isGranted(.microphone) == false)
        #expect(PermissionStep.allCases.count == 4)
    }

    @Test func theCardCopyChangesWithTheStage() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus(microphone: true))
        #expect(controller.currentPrompt?.headline == "i need accessibility to paste and point")
        #expect(controller.currentPrompt?.detail == "macOS asks once. until then, dictated words wait on the clipboard.")
        #expect(controller.currentPrompt?.buttonTitle == "grant")

        controller.grant()
        #expect(controller.currentPrompt?.detail == "switch ATMIYA AI on in Accessibility settings.")
        #expect(controller.currentPrompt?.buttonTitle == "open")
    }

    @Test func closingTheCardHidesItAcrossLaunches() {
        let spy = Spy()
        let suiteName = "PermissionPromptTests.dismissal.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let controller = makeController(spy, defaults: defaults)

        controller.update(with: PermissionStatus(microphone: true))
        controller.grant()
        controller.dismiss()
        #expect(controller.currentPrompt == nil)

        // Polling keeps running; the card stays closed.
        controller.update(with: PermissionStatus(microphone: true))
        #expect(controller.currentPrompt == nil)

        let relaunchedController = makeController(spy, defaults: defaults)
        relaunchedController.update(with: PermissionStatus(microphone: true))
        #expect(relaunchedController.currentPrompt == nil)
    }

    /// Reaching past the island while Clicky speaks keeps the speaking strip (its stop button);
    /// hovering opens the panel again once the voice is done.
    @Test func hoveringWhileSpeakingKeepsTheStopStrip() async throws {
        let geometry = NotchGeometry(screenFrame: CGRect(x: 0, y: 0, width: 1512, height: 982),
                                     hasHardwareNotch: true, notchWidth: 190, notchHeight: 32)
        let model = NotchHUDModel(geometry: geometry)
        model.setBusy(true)
        model.setSpeaking(true)
        model.setHovering(true)
        try await Task.sleep(nanoseconds: 600_000_000)
        #expect(model.expansion == .compact)

        model.setSpeaking(false)
        model.setBusy(false)
        // The island settles 1.8 s after speaking ends, and CI's runner has stretched that past
        // the 3 s this used to allow (2026-11-04): ten seconds is still far from a hang. Hovering
        // only peeks; a click is what opens the panel.
        for _ in 0..<500 where model.expansion != .peek {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        #expect(model.expansion == .peek)
    }

    @Test func thePermissionCardOutranksTheConnectCardAndTheBusyStrip() {
        let geometry = NotchGeometry(screenFrame: CGRect(x: 0, y: 0, width: 1512, height: 982),
                                     hasHardwareNotch: true, notchWidth: 190, notchHeight: 32)
        let model = NotchHUDModel(geometry: geometry)

        model.setConnectPrompt(AppConnectPrompt(skillId: "youtube", appName: "YouTube",
                                                integration: "youtube", frontBundleIdentifier: nil,
                                                examplePrompts: []))
        #expect(model.expansion == .connect)

        model.setPermissionPrompt(PermissionPrompt(step: .accessibility, status: PermissionStatus()))
        #expect(model.expansion == .permission)
        #expect(model.width == model.permissionWidth)

        // Talking, and a connect card waiting behind it, both stay out of the way.
        model.setBusy(true)
        #expect(model.expansion == .permission, "permissions block everything else")

        // Once the last permission lands the island goes back to what it was showing.
        model.setPermissionPrompt(nil)
        #expect(model.expansion == .connect)
    }
}
