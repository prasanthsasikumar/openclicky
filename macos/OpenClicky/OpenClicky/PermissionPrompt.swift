//
//  PermissionPrompt.swift
//  OpenClicky
//
//  HeyClicky's permission cards, drawn in the notch island: one permission at a time, a row
//  of dashes for progress, a line of copy, grant and later. The menu bar panel used to
//  carry a list of four rows with Grant buttons; the island asks for them instead, in order:
//    Accessibility → Microphone → Screen Recording → Screen Content
//  Accessibility comes first because the pointer can show (and help) from then on.
//  Tapping the button once takes the macOS route for that permission (the system prompt, then
//  System Settings on later taps) and the card moves to its `waiting` stage.
//

import AVFoundation
import AppKit
import Combine
import SwiftUI

// MARK: - Model

enum PermissionStep: String, CaseIterable, Identifiable {
    case accessibility
    case microphone
    case screenRecording
    case screenContent

    var id: String { rawValue }

    /// The permission's name in the checklist under every card.
    var shortName: String {
        switch self {
        case .microphone: return "microphone"
        case .accessibility: return "accessibility"
        case .screenRecording: return "screen recording"
        case .screenContent: return "screen content"
        }
    }

    /// What the permission is for, in the card's own words (lowercase, one sentence).
    var headline: String {
        switch self {
        case .microphone: return "i need the microphone to hear you"
        case .accessibility: return "i need accessibility to paste and point"
        case .screenRecording: return "i need screen recording to see what you mean"
        case .screenContent: return "i need to see your screen"
        }
    }

    /// The line under the headline before the user has tapped the button: the consequence of
    /// granting (or not), not the mechanism.
    var detail: String {
        switch self {
        case .microphone: return "only while you hold a shortcut or go hands-free."
        case .accessibility: return "macOS asks once. until then, dictated words wait on the clipboard."
        case .screenRecording: return "i only look while you talk to me."
        case .screenContent: return "pick a display in the panel macOS shows."
        }
    }

    /// The line once the request is out and we're waiting on the user in System Settings.
    var waitingDetail: String {
        switch self {
        case .microphone: return "switch ATMIYA AI on in the Microphone list."
        case .accessibility: return "switch ATMIYA AI on in Accessibility settings."
        case .screenRecording: return "switch ATMIYA AI on in Screen Recording, then reopen the app."
        case .screenContent: return "waiting for the capture panel…"
        }
    }

    var buttonTitle: String { "grant" }

    var waitingButtonTitle: String {
        switch self {
        case .microphone: return "open"
        case .accessibility: return "open"
        case .screenRecording: return "quit & reopen"
        case .screenContent: return "try again"
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
}

/// One line of the checklist under a card: a permission and whether it has landed.
struct PermissionChecklistItem: Equatable {
    let step: PermissionStep
    let isGranted: Bool
}

struct PermissionPrompt: Equatable {
    enum Stage: Equatable {
        /// The first card on a fresh Mac: says four permissions are coming and lists them, so
        /// the prompts that follow are expected (a tester was taken aback by them one by one).
        /// Its button grants the first permission, so it costs no extra tap.
        case overview
        /// The button still offers to ask for the permission.
        case ask
        /// The request is out: macOS has the ball, and the card explains where to look.
        case waiting
    }

    let step: PermissionStep
    /// Everything granted so far, for the row of progress dots.
    let status: PermissionStatus
    var stage: Stage = .ask

    var headline: String {
        switch stage {
        case .overview: return "four permissions to set up:"
        case .ask, .waiting: return step.headline
        }
    }

    var detail: String {
        switch stage {
        case .overview: return "macOS asks for each in turn, and i'll say what it's for. first, accessibility, so i can paste and point."
        case .ask: return step.detail
        case .waiting: return step.waitingDetail
        }
    }

    var buttonTitle: String {
        switch stage {
        case .overview: return "let's start"
        case .ask: return step.buttonTitle
        case .waiting: return step.waitingButtonTitle
        }
    }

    /// Every permission in asking order, ticked once granted: the checklist under the card.
    var checklist: [PermissionChecklistItem] {
        PermissionStep.allCases.map { PermissionChecklistItem(step: $0, isGranted: status.isGranted($0)) }
    }

}

// MARK: - Controller

/// Turns the permission flags into the one card the island should show, and performs the grant
/// the button asks for. `CompanionManager` feeds it from the same 1.5 s poll that drives the
/// flags, so the card advances on its own as each permission lands.
@MainActor
final class PermissionPromptController: ObservableObject {

    @Published private(set) var currentPrompt: PermissionPrompt?

    private static let dismissalKey = "permissionPromptsDismissed"
    private let defaults: UserDefaults

    /// The ✕ on the card: once dismissed, setup stays in Privacy settings instead of returning.
    private(set) var isDismissedForThisLaunch: Bool

    /// The overview is the first card once per launch, and only while nothing has been granted.
    private var hasShownOverview = false

    /// Performs the macOS request for a step. Injected so tests never open System Settings.
    var performRequest: @MainActor (PermissionStep) -> Void = PermissionPromptController.requestFromSystem(_:)
    private weak var notchHUDManager: NotchHUDManager?

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        isDismissedForThisLaunch = defaults.bool(forKey: Self.dismissalKey)
    }

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
        let stage: PermissionPrompt.Stage
        if currentPrompt?.step == step {
            stage = currentPrompt?.stage ?? .ask
        } else if !hasShownOverview, status == PermissionStatus() {
            stage = .overview
        } else {
            stage = .ask
        }
        if stage == .overview { hasShownOverview = true }
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

    func dismiss() {
        isDismissedForThisLaunch = true
        defaults.set(true, forKey: Self.dismissalKey)
        setPrompt(nil)
    }

    /// The gear on the card: the window's settings (quit is in the menu bar menu and on the island).
    var openSettings: () -> Void = {}

    func openPanel() {
        openSettings()
    }

    // MARK: Private

    private func setPrompt(_ prompt: PermissionPrompt?) {
        currentPrompt = prompt
        notchHUDManager?.setPermissionPrompt(prompt)
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

/// The card inside the island: the orange "!" disc, the headline and one line of copy with the
/// progress dashes under them, and grant / later on the right. "later" hides the cards permanently;
/// the Privacy page remains available for setup. 512 pt wide, sized by
/// `NotchHUDModel.permissionHeight`.
struct PermissionPromptView: View {
    let prompt: PermissionPrompt
    @ObservedObject var controller: PermissionPromptController
    /// On a hardware-notch screen the content starts under the notch band.
    var topInset: CGFloat = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .center, spacing: 14) {
                Text("!")
                    .font(.system(size: 15, weight: .bold))
                    .foregroundColor(DS.HUD.pointer)
                    .frame(width: DS.HUD.tileSize, height: DS.HUD.tileSize)
                    .background(Circle().fill(DS.HUD.pointerTint))
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 2) {
                    Text(prompt.headline)
                        .font(.system(size: 15, weight: .semibold))
                        .foregroundColor(DS.HUD.text)
                        .lineLimit(1)
                        .minimumScaleFactor(0.85)
                    Text(prompt.detail)
                        .font(.system(size: 12))
                        .foregroundColor(DS.HUD.text2)
                        .lineSpacing(2)
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                VStack(spacing: 6) {
                    Button(action: { controller.grant() }) {
                        Text(prompt.buttonTitle)
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundColor(.black)
                            .padding(.horizontal, 14)
                            .frame(minWidth: 72)
                            .frame(height: 30)
                            .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(DS.HUD.pointer))
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()

                    Button(action: { controller.dismiss() }) {
                        Text("later")
                            .font(.system(size: 12))
                            .foregroundColor(DS.HUD.text2)
                            .frame(minWidth: 72)
                            .frame(height: 24)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()
                    .help("don't show these setup cards again")
                }
                .fixedSize()
            }

            // Under the whole row, so the four names have the card's width.
            checklistRow
                .padding(.leading, DS.HUD.tileSize + 14)

        }
        .padding(.top, topInset + 12)
        .padding(.horizontal, DS.HUD.bodySidePadding)
        .padding(.bottom, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .overlay(alignment: .topTrailing) {
            // Settings, while nothing else on the island works.
            Button(action: { controller.openPanel() }) {
                Image(systemName: "gearshape.fill")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundColor(DS.HUD.text3)
                    .frame(width: DS.HUD.minimumHitSize, height: DS.HUD.minimumHitSize)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .pointerCursor()
            .help("settings")
            .accessibilityLabel("settings")
            .padding(.top, topInset + 2)
            .padding(.trailing, 10)
        }
    }

    /// The four permissions by name: a tick for the ones already granted, lit for the one being
    /// asked for, dim for the rest. The same row on every card, so the person always sees how
    /// many are left (it replaced a row of nameless dashes).
    private var checklistRow: some View {
        HStack(spacing: 12) {
            ForEach(prompt.checklist, id: \.step) { item in
                HStack(spacing: 3) {
                    Image(systemName: item.isGranted ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 9, weight: .semibold))
                    Text(item.step.shortName)
                        .font(.system(size: 10.5, weight: item.step == prompt.step ? .semibold : .regular))
                }
                .foregroundColor(checklistColor(for: item))
            }
        }
        .lineLimit(1)
        .minimumScaleFactor(0.85)
        .accessibilityElement()
        .accessibilityLabel("step \((PermissionStep.allCases.firstIndex(of: prompt.step) ?? 0) + 1) of \(PermissionStep.allCases.count): " + prompt.checklist.map { "\($0.step.shortName) \($0.isGranted ? "granted" : "not yet")" }.joined(separator: ", "))
    }

    private func checklistColor(for item: PermissionChecklistItem) -> Color {
        if item.isGranted { return DS.HUD.live }
        if item.step == prompt.step { return DS.HUD.pointer }
        return DS.HUD.text3
    }
}
