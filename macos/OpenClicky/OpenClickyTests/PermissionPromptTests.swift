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

    @Test func asksForOneStepAtATimeInOrder() {
        var status = PermissionStatus()
        #expect(PermissionPromptController.nextStep(for: status) == .microphone)

        status.microphone = true
        #expect(PermissionPromptController.nextStep(for: status) == .accessibility)

        status.accessibility = true
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
        #expect(controller.currentPrompt?.step == .microphone)

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
        #expect(spy.requestedSteps == [.microphone])
        #expect(controller.currentPrompt?.stage == .waiting)

        // The poll sees the microphone land: the card moves on, back to its asking stage.
        controller.update(with: PermissionStatus(microphone: true))
        #expect(controller.currentPrompt?.step == .accessibility)
        #expect(controller.currentPrompt?.stage == .ask)
    }

    @Test func theProgressDotsFollowWhatIsAlreadyGranted() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus(microphone: true))
        let prompt = controller.currentPrompt
        #expect(prompt?.status.isGranted(.microphone) == true)
        #expect(prompt?.status.isGranted(.accessibility) == false)
        #expect(PermissionStep.allCases.count == 4)
    }

    @Test func theDragHelperOpensOnlyWhileTheAccessibilityStepIsWaiting() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus())
        controller.grant()
        #expect(!spy.isDragHelperShown, "the microphone step has nothing to drag")

        controller.update(with: PermissionStatus(microphone: true))
        #expect(!spy.isDragHelperShown, "not before the user asks to grant it")
        controller.grant()
        #expect(spy.isDragHelperShown)

        controller.update(with: PermissionStatus(microphone: true, accessibility: true))
        #expect(!spy.isDragHelperShown, "the list accepted the drop")
    }

    /// The escape hatch for a stale TCC row: the list shows OpenClicky switched on, but the
    /// signature it was approved under no longer matches, so the app is still untrusted.
    @Test func resetAndRegrantIsOfferedOnlyOnAStuckAccessibilityCard() {
        let spy = Spy()
        let controller = makeController(spy)

        controller.update(with: PermissionStatus())
        controller.grant()
        #expect(controller.currentPrompt?.offersAccessibilityTrustReset == false,
                "the microphone card has no TCC row to reset")

        controller.update(with: PermissionStatus(microphone: true))
        #expect(controller.currentPrompt?.offersAccessibilityTrustReset == false,
                "not before the user has tried granting it")

        controller.grant()
        #expect(controller.currentPrompt?.offersAccessibilityTrustReset == true)
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
        #expect(controller.currentPrompt?.headline == "I need accessibility permissions.")
        #expect(controller.currentPrompt?.detail == "This lets me work in any app.")
        #expect(controller.currentPrompt?.buttonTitle == "Grant Accessibility")

        controller.grant()
        #expect(controller.currentPrompt?.detail == "Drag me into the Accessibility list.")
        #expect(controller.currentPrompt?.buttonTitle == "Open")
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
