//
//  PermissionPrompt.swift
//  OpenClicky
//
//  HeyClicky's permission cards, drawn in the notch island: one permission at a time, a row
//  of dots for progress, a line of copy, and a single blue button. The menu bar panel used to
//  carry a list of four rows with Grant buttons; the island asks for them instead, in order:
//    Microphone → Accessibility → Screen Recording → Screen Content
//  Tapping the button once takes the macOS route for that permission (the system prompt, then
//  System Settings on later taps) and the card moves to its `waiting` stage: for Accessibility
//  that stage also opens the drag helper — the little panel you drag OpenClicky out of and into
//  the Accessibility list, for when macOS doesn't add it there by itself.
//

import AVFoundation
import AppKit
import Combine
import SwiftUI

// MARK: - Model

enum PermissionStep: String, CaseIterable, Identifiable {
    case microphone
    case accessibility
    case screenRecording
    case screenContent

    var id: String { rawValue }

    var iconName: String {
        switch self {
        case .microphone: return "mic"
        case .accessibility: return "hand.raised"
        case .screenRecording: return "rectangle.dashed.badge.record"
        case .screenContent: return "eye"
        }
    }

    var headline: String {
        switch self {
        case .microphone: return "I need microphone permissions."
        case .accessibility: return "I need accessibility permissions."
        case .screenRecording: return "I need screen recording permissions."
        case .screenContent: return "I need to see your screen."
        }
    }

    /// The line under the headline before the user has tapped the button.
    var detail: String {
        switch self {
        case .microphone: return "This lets me hear you when you hold the hotkey."
        case .accessibility: return "This lets me work in any app."
        case .screenRecording: return "I only capture while you hold the hotkey."
        case .screenContent: return "Pick a display in the panel macOS shows."
        }
    }

    /// The line once the request is out and we're waiting on the user in System Settings.
    var waitingDetail: String {
        switch self {
        case .microphone: return "Switch OpenClicky on in the Microphone list."
        case .accessibility: return "Drag me into the Accessibility list."
        case .screenRecording: return "Quit and reopen after granting."
        case .screenContent: return "Waiting for the capture panel…"
        }
    }

    var buttonTitle: String {
        switch self {
        case .microphone: return "Grant Microphone"
        case .accessibility: return "Grant Accessibility"
        case .screenRecording: return "Grant Screen Recording"
        case .screenContent: return "Grant Screen Content"
        }
    }

    var waitingButtonTitle: String {
        switch self {
        case .microphone: return "Open"
        case .accessibility: return "Open"
        case .screenRecording: return "Quit & Reopen"
        case .screenContent: return "Try again"
        }
    }
}

/// The four permission flags, snapshotted from `CompanionManager` so the card logic is testable
/// without touching TCC.
struct PermissionStatus: Equatable {
    var microphone = false
    var accessibility = false
    var screenRecording = false
    var screenContent = false

    func isGranted(_ step: PermissionStep) -> Bool {
        switch step {
        case .microphone: return microphone
        case .accessibility: return accessibility
        case .screenRecording: return screenRecording
        case .screenContent: return screenContent
        }
    }

    var allGranted: Bool { PermissionStep.allCases.allSatisfy(isGranted) }
}

struct PermissionPrompt: Equatable {
    enum Stage: Equatable {
        /// The button still offers to ask for the permission.
        case ask
        /// The request is out: macOS has the ball, and the card explains where to look.
        case waiting
    }

    let step: PermissionStep
    /// Everything granted so far, for the row of progress dots.
    let status: PermissionStatus
    var stage: Stage = .ask

    var headline: String { step.headline }
    var detail: String { stage == .waiting ? step.waitingDetail : step.detail }
    var buttonTitle: String { stage == .waiting ? step.waitingButtonTitle : step.buttonTitle }

    /// Whether to offer the escape hatch for a stale Accessibility row: the list shows OpenClicky
    /// switched on, but it was approved under a signature the running app no longer has (an update
    /// signed differently, or a development build of the same bundle id), so macOS keeps the row and
    /// keeps refusing the app. Only a delete-and-re-add fixes it, and only the user can do it from
    /// System Settings — unless the app clears its own row, which is what the button does.
    var offersAccessibilityTrustReset: Bool { step == .accessibility && stage == .waiting }
}

// MARK: - Drag helper

/// The "I'm OpenClicky — drag me into the list above" panel. A protocol so tests can watch it
/// open and close without a window on screen.
@MainActor
protocol AccessibilityDragPresenting: AnyObject {
    func showAccessibilityDragHelper()
    func hideAccessibilityDragHelper()
}

// MARK: - Controller

/// Turns the permission flags into the one card the island should show, and performs the grant
/// the button asks for. `CompanionManager` feeds it from the same 1.5 s poll that drives the
/// flags, so the card advances on its own as each permission lands.
@MainActor
final class PermissionPromptController: ObservableObject {

    @Published private(set) var currentPrompt: PermissionPrompt?

    /// The ✕ on the card: the user gets on with their day and we stop nagging until relaunch.
    private(set) var isDismissedForThisLaunch = false

    /// Performs the macOS request for a step. Injected so tests never open System Settings.
    var performRequest: @MainActor (PermissionStep) -> Void = PermissionPromptController.requestFromSystem(_:)
    /// Clears the app's own Accessibility row and relaunches. Injected so tests never touch TCC.
    var performAccessibilityTrustReset: @MainActor () -> Void = PermissionPromptController.resetAccessibilityTrustInTCC
    weak var dragHelper: AccessibilityDragPresenting?
    private weak var notchHUDManager: NotchHUDManager?

    /// The first permission that is still missing, in the order the cards ask for them.
    static func nextStep(for status: PermissionStatus) -> PermissionStep? {
        PermissionStep.allCases.first { !status.isGranted($0) }
    }

    func start(notchHUDManager: NotchHUDManager) {
        self.notchHUDManager = notchHUDManager
        setPrompt(currentPrompt)
    }

    func stop() {
        setPrompt(nil)
    }

    /// Called on every permission refresh. Keeps the card's stage while it is asking for the
    /// same permission; moves on (back to `ask`) as soon as that one is granted.
    func update(with status: PermissionStatus) {
        guard !isDismissedForThisLaunch, let step = Self.nextStep(for: status) else {
            setPrompt(nil)
            return
        }
        let stage = currentPrompt?.step == step ? (currentPrompt?.stage ?? .ask) : .ask
        setPrompt(PermissionPrompt(step: step, status: status, stage: stage))
    }

    /// The card's button: ask macOS for the current permission, then wait on the user.
    func grant() {
        guard let prompt = currentPrompt else { return }
        if prompt.step == .screenRecording && prompt.stage == .waiting {
            // Screen Recording only takes effect after a restart, so the second tap does that.
            WindowPositionManager.relaunchApp()
            return
        }
        performRequest(prompt.step)
        setPrompt(PermissionPrompt(step: prompt.step, status: prompt.status, stage: .waiting))
    }

    /// "Still not working?" on the accessibility card: delete the stale row and start over.
    func resetAccessibilityTrust() {
        guard currentPrompt?.offersAccessibilityTrustReset == true else { return }
        performAccessibilityTrustReset()
    }

    func dismiss() {
        isDismissedForThisLaunch = true
        setPrompt(nil)
    }

    /// The gear on the card: the menu bar panel, for settings and Quit.
    func openPanel() {
        NotificationCenter.default.post(name: .clickyShowPanel, object: nil)
    }

    // MARK: Private

    private func setPrompt(_ prompt: PermissionPrompt?) {
        currentPrompt = prompt
        notchHUDManager?.setPermissionPrompt(prompt)
        if prompt?.step == .accessibility && prompt?.stage == .waiting {
            dragHelper?.showAccessibilityDragHelper()
        } else {
            dragHelper?.hideAccessibilityDragHelper()
        }
    }

    /// `tccutil reset Accessibility <bundle id>` removes this app's row entirely — the only way
    /// back from a row whose recorded signature no longer matches this build, and the same thing as
    /// selecting OpenClicky in the list and pressing "−". The app then has to relaunch: the trust
    /// answer it was given at launch is now stale either way.
    static func resetAccessibilityTrustInTCC() {
        guard let bundleIdentifier = Bundle.main.bundleIdentifier else { return }
        let resetProcess = Process()
        resetProcess.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
        resetProcess.arguments = ["reset", "Accessibility", bundleIdentifier]
        do {
            try resetProcess.run()
            resetProcess.waitUntilExit()
        } catch {
            print("⚠️ Accessibility trust reset failed: \(error)")
            return
        }
        WindowPositionManager.relaunchApp()
    }

    /// The real macOS route for each permission. Microphone is asked for in-process, Accessibility
    /// and Screen Recording go through `WindowPositionManager` (the system prompt once per launch,
    /// System Settings after that). Screen Content has no system call of its own — `CompanionManager`
    /// overrides `performRequest` for that one, because taking a screenshot is what opens its picker.
    static func requestFromSystem(_ step: PermissionStep) {
        switch step {
        case .microphone:
            if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
                AVCaptureDevice.requestAccess(for: .audio) { _ in }
            } else if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") {
                NSWorkspace.shared.open(url)
            }
        case .accessibility:
            WindowPositionManager.requestAccessibilityPermission()
        case .screenRecording:
            WindowPositionManager.requestScreenRecordingPermission()
        case .screenContent:
            break
        }
    }
}

// MARK: - View

/// The card inside the island: progress dots, headline, one line of copy, one blue button.
/// Sized from HeyClicky's (424 × 168 pt including the menu-bar band).
struct PermissionPromptView: View {
    let prompt: PermissionPrompt
    @ObservedObject var controller: PermissionPromptController
    /// On a hardware-notch screen the content starts under the notch band.
    var topInset: CGFloat = 0

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                progressDots
                HStack(spacing: 10) {
                    Spacer()
                    cornerButton(systemImage: "gearshape.fill") { controller.openPanel() }
                    cornerButton(systemImage: "xmark") { controller.dismiss() }
                }
                .padding(.trailing, 16)
            }
            .frame(height: 20)

            Spacer(minLength: 10)

            Text(prompt.headline)
                .font(.system(size: 15, weight: .bold))
                .foregroundColor(.white)
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .padding(.horizontal, 24)

            Text(prompt.detail)
                .font(.system(size: 11, weight: .medium))
                .foregroundColor(Color.white.opacity(0.6))
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .padding(.top, 4)
                .padding(.horizontal, 24)

            Button(action: { controller.grant() }) {
                Text(prompt.buttonTitle)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundColor(.white)
                    .padding(.horizontal, 18)
                    .frame(height: 28)
                    .background(Capsule().fill(DS.Colors.blue500))
            }
            .buttonStyle(.plain)
            .pointerCursor()
            .padding(.top, 14)

            if prompt.offersAccessibilityTrustReset {
                Button(action: { controller.resetAccessibilityTrust() }) {
                    Text("Already switched on? Reset it and try again")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundColor(Color.white.opacity(0.45))
                        .underline()
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .help("Removes OpenClicky from the Accessibility list and relaunches, so it can be granted again")
                .padding(.top, 8)
            }

            Spacer(minLength: 14)
        }
        .padding(.top, topInset)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    /// One dash per permission: filled for the ones already granted, half-lit for the one being
    /// asked for, dim for the rest.
    private var progressDots: some View {
        HStack(spacing: 5) {
            ForEach(PermissionStep.allCases) { step in
                Capsule()
                    .fill(dashColor(for: step))
                    .frame(width: 16, height: 3)
            }
        }
    }

    private func dashColor(for step: PermissionStep) -> Color {
        if prompt.status.isGranted(step) { return DS.Colors.blue500 }
        if step == prompt.step { return DS.Colors.blue500.opacity(0.55) }
        return Color.white.opacity(0.18)
    }

    private func cornerButton(systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(Color.white.opacity(0.5))
                .frame(width: 18, height: 18)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }
}
