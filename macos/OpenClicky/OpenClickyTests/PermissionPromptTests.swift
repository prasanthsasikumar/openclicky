//
//  PermissionPromptTests.swift
//  OpenClickyTests
//
//  The permission cards the notch island shows one at a time ("I need accessibility
//  permissions." → Grant → "Drag me into the Accessibility list."): which step is asked
//  for next, what the card says, and when the drag helper opens.
//

import CoreGraphics
import Testing
@testable import OpenClicky

@MainActor
struct PermissionPromptTests {

    /// Records the grant requests instead of poking System Settings, and stands in for the
    /// drag helper window.
    @MainActor
    private final class Spy: AccessibilityDragPresenting {
        var requestedSteps: [PermissionStep] = []
        var didResetAccessibilityTrust = false
        var isDragHelperShown = false
        func showAccessibilityDragHelper() { isDragHelperShown = true }
        func hideAccessibilityDragHelper() { isDragHelperShown = false }
    }

    private func makeController(_ spy: Spy) -> PermissionPromptController {
        let controller = PermissionPromptController()
        controller.dragHelper = spy
        controller.performRequest = { spy.requestedSteps.append($0) }
        controller.performAccessibilityTrustReset = { spy.didResetAccessibilityTrust = true }
        return controller
    }

    @Test func theDragHelperFollowsTheMainSettingsWindowAndStepsAsideForADialog() {
        let main = CGRect(x: 400, y: 75, width: 1320, height: 1150)
        let quitAndReopen = CGRect(x: 820, y: 420, width: 480, height: 460)
        let finderBehind = CGRect(x: 200, y: 80, width: 1700, height: 860)
        // Front to back. The dialog came first and used to be taken for the window.
        let withDialog = AccessibilityDragPanelController.mainSettingsWindow(inFrontToBackOrder: [
            (quitAndReopen, true), (main, true), (finderBehind, false),
        ])
        #expect(withDialog?.bounds == main)
        #expect(withDialog?.dialogIsOpen == true)
        // A password sheet from a Settings extension (another process) counts too.
        #expect(AccessibilityDragPanelController.mainSettingsWindow(inFrontToBackOrder: [(quitAndReopen, false), (main, true)])?.dialogIsOpen == true)
        // A window behind Settings, or one only overlapping it, is not a dialog.
        #expect(AccessibilityDragPanelController.mainSettingsWindow(inFrontToBackOrder: [(main, true), (quitAndReopen, false)])?.dialogIsOpen == false)
        #expect(AccessibilityDragPanelController.mainSettingsWindow(inFrontToBackOrder: [(finderBehind, false), (main, true)])?.dialogIsOpen == false)
        #expect(AccessibilityDragPanelController.mainSettingsWindow(inFrontToBackOrder: [(finderBehind, false)]) == nil)
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
        #expect(!spy.isDragHelperShown)
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

    @Test func theProgressDotsFollowWhatIsAlreadyGranted() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus(accessibility: true))
        let prompt = controller.currentPrompt
        #expect(prompt?.status.isGranted(.accessibility) == true)
        #expect(prompt?.status.isGranted(.microphone) == false)
        #expect(PermissionStep.allCases.count == 4)
    }

    /// Both lists take the app dragged in: the helper opens while the accessibility or the screen
    /// recording card waits, and never for the microphone.
    @Test func theDragHelperOpensWhileAccessibilityOrScreenRecordingWaits() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus())
        #expect(!spy.isDragHelperShown, "not before the user asks to grant it")
        controller.grant()
        #expect(spy.isDragHelperShown)

        controller.update(with: PermissionStatus(accessibility: true))
        #expect(!spy.isDragHelperShown, "the list accepted the drop")
        controller.grant()
        #expect(!spy.isDragHelperShown, "the microphone step has nothing to drag")

        controller.update(with: PermissionStatus(microphone: true, accessibility: true))
        controller.grant()
        #expect(spy.isDragHelperShown, "screen recording's list takes the drag too")
    }

    /// The escape hatch for a stale TCC row: the list shows OpenClicky switched on, but the
    /// signature it was approved under no longer matches, so the app is still untrusted.
    @Test func resetAndRegrantIsOfferedOnlyOnAStuckAccessibilityCard() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus())
        #expect(controller.currentPrompt?.offersAccessibilityTrustReset == false,
                "not before the user has tried granting it")

        controller.grant()
        #expect(controller.currentPrompt?.offersAccessibilityTrustReset == true)

        controller.update(with: PermissionStatus(accessibility: true))
        controller.grant()
        #expect(controller.currentPrompt?.offersAccessibilityTrustReset == false,
                "the microphone card has no TCC row to reset")
    }

    @Test func resettingAccessibilityTrustClearsTheRowAndRelaunches() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus(microphone: true))
        controller.grant()
        controller.resetAccessibilityTrust()
        #expect(spy.didResetAccessibilityTrust)
    }

    @Test func theCardCopyChangesWithTheStage() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus(microphone: true))
        #expect(controller.currentPrompt?.headline == "i need accessibility to paste and point")
        #expect(controller.currentPrompt?.detail == "macOS asks once. until then, dictated words wait on the clipboard.")
        #expect(controller.currentPrompt?.buttonTitle == "grant")

        controller.grant()
        #expect(controller.currentPrompt?.detail == "drag me into the accessibility list.")
        #expect(controller.currentPrompt?.buttonTitle == "open")
    }

    @Test func closingTheCardHidesItUntilTheNextLaunch() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus(microphone: true))
        controller.grant()
        controller.dismiss()
        #expect(controller.currentPrompt == nil)
        #expect(!spy.isDragHelperShown)

        // Polling keeps running; the card stays closed.
        controller.update(with: PermissionStatus(microphone: true))
        #expect(controller.currentPrompt == nil)
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
        // A busy CI runner can take longer than the island's grace period to open the panel.
        for _ in 0..<150 where model.expansion != .full {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        #expect(model.expansion == .full)
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
