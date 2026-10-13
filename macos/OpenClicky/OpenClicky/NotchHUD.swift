//
//  NotchHUD.swift
//  OpenClicky
//
//  The notch HUD, drawn like HeyClicky's: a solid black island hanging from the top of the screen
//  around the notch, its top corners flaring into the menu bar and its bottom corners rounded, with
//  no border and no shadow. Colours and sizes come from `DS.HUD` (the refinement sheet). States:
//    collapsed — exactly the notch (or a virtual notch on other displays), docked pointer or not:
//                hovering opens the panel, whose band says the pointer is docked and whose dock
//                button releases it
//    compact   — a 380 × 37 strip that opens while OpenClicky listens / thinks / speaks, one
//                caption line taller when there is something to say under it
//    connect   — the "connect <app> to openclicky?" card that opens when a supported app or site
//                comes to the front for the first time (never / not now / connect + example prompts)
//    full      — the one short panel that opens on hover or click: the band (pointer status,
//                voice pill, Settings, quit) over one row (dock, (i), the four shortcuts); no tabs
//  The cursor buddy can dock here: it flies into the notch and lives in the HUD as a glowing badge.
//  `HUDPlacement` moves the island: the notch (above), a drawer on the left or right screen edge
//  (collapsed to a vertical handle), or hidden while idle (shortcuts only).
//

import AppKit
import Combine
import SwiftUI

// MARK: - Notch geometry

/// Where the notch is (or where a virtual one should be) on a screen, in AppKit
/// screen coordinates (bottom-left origin).
struct NotchGeometry: Equatable {
    let screenFrame: CGRect
    let hasHardwareNotch: Bool
    let notchWidth: CGFloat
    let notchHeight: CGFloat
    /// The screen minus the menu bar and Dock: where a side drawer may hang.
    var visibleFrame: CGRect = .zero

    var notchRect: CGRect {
        CGRect(x: screenFrame.midX - notchWidth / 2, y: screenFrame.maxY - notchHeight, width: notchWidth, height: notchHeight)
    }

    /// Where the docked buddy "lives": just under the notch's bottom edge.
    var dockPoint: CGPoint {
        CGPoint(x: notchRect.midX, y: notchRect.minY - 6)
    }

    static func forScreen(_ screen: NSScreen) -> NotchGeometry {
        let topSafeAreaInset = screen.safeAreaInsets.top
        if topSafeAreaInset > 0,
           let leftAuxiliaryArea = screen.auxiliaryTopLeftArea,
           let rightAuxiliaryArea = screen.auxiliaryTopRightArea {
            let hardwareNotchWidth = screen.frame.width - leftAuxiliaryArea.width - rightAuxiliaryArea.width
            return NotchGeometry(screenFrame: screen.frame, hasHardwareNotch: true, notchWidth: hardwareNotchWidth, notchHeight: topSafeAreaInset, visibleFrame: screen.visibleFrame)
        }
        return virtualNotch(screenFrame: screen.frame, visibleFrame: screen.visibleFrame)
    }

    /// The geometry the island is laid out in for a placement. A side drawer has no notch band to
    /// clear, so its cards start at the top of the island like on a display without a notch.
    static func forScreen(_ screen: NSScreen, placement: HUDPlacement) -> NotchGeometry {
        guard placement.isSideDrawer else { return forScreen(screen) }
        return NotchGeometry(screenFrame: screen.frame, hasHardwareNotch: false, notchWidth: 190, notchHeight: 0, visibleFrame: screen.visibleFrame)
    }

    /// A display without a hardware notch hangs the island from its menu bar band. A display that
    /// shows no menu bar at all (an external monitor when the menu bar lives on the built-in
    /// display, or an auto-hidden menu bar) reports `visibleFrame.maxY == frame.maxY`: then there
    /// is no band and the island starts at the very top edge, like HeyClicky's.
    static func virtualNotch(screenFrame: CGRect, visibleFrame: CGRect) -> NotchGeometry {
        let menuBarBandHeight = max(0, screenFrame.maxY - visibleFrame.maxY)
        return NotchGeometry(screenFrame: screenFrame, hasHardwareNotch: false, notchWidth: 190, notchHeight: menuBarBandHeight, visibleFrame: visibleFrame)
    }
}

// MARK: - Model

enum NotchHUDExpansion: Equatable {
    case collapsed
    case compact
    case connect
    /// A permission card ("I need accessibility permissions." → Grant): one per missing
    /// permission, and nothing else opens on the island until they are all granted.
    case permission
    /// The text composer (control tapped twice): a one-line field to type a request.
    case composer
    /// The first-run tour's card: one step at a time, after the permission cards.
    case tour
    case full
}

@MainActor
final class NotchHUDModel: ObservableObject {
    @Published private(set) var expansion: NotchHUDExpansion = .collapsed
    @Published var geometry: NotchGeometry
    @Published var placement: HUDPlacement = .notch

    // Sizes from the refinement sheet: every card and the open panel are 512 pt wide (360 pt in a
    // side drawer); heights include the menu-bar band the island hangs from.
    /// Nothing shows below the notch while idle (HeyClicky's collapsed state is the notch itself).
    let collapsedLipHeight: CGFloat = 0
    /// The busy strip is 380 × 37 and grows by one caption line; the caption strip (the (i) text
    /// while the buddy is docked) is wider and three lines tall.
    var compactWidth: CGFloat { isShowingCaption ? 400 : DS.HUD.stripWidth }
    var compactContentHeight: CGFloat {
        if isShowingCaption { return 70 }
        return DS.HUD.stripHeight + (hasCompactDetailLine ? compactDetailLineHeight : 0)
    }
    let compactDetailLineHeight: CGFloat = 26
    let connectWidth: CGFloat = 512
    let permissionWidth: CGFloat = 512
    let composerWidth: CGFloat = 460
    let composerContentHeight: CGFloat = 52
    /// A side drawer is a narrow column with its content stacked, not the notch panel moved.
    var fullWidth: CGFloat { placement.isSideDrawer ? 360 : 512 }

    /// The open panel's band row: the band plus a little air above the controls, so the pill and
    /// the round buttons never touch the top edge of the screen.
    var bandRowHeight: CGFloat { topBandHeight + 6 }

    /// The band the band row sits in: the hardware notch, or at least 34 pt on a display without
    /// one (whose menu bar band may be 0 pt when that display shows no menu bar).
    var topBandHeight: CGFloat {
        geometry.hasHardwareNotch ? geometry.notchHeight : max(geometry.notchHeight, 34)
    }

    /// The "Connect <app> to OpenClicky" card to show, if any; set by `AppConnectPromptController`.
    @Published var connectPrompt: AppConnectPrompt?

    /// The permission card to show, if any; set by `PermissionPromptController`.
    @Published var permissionPrompt: PermissionPrompt?

    /// The first-run tour's card, if the tour is on; set by `FirstRunTourController`.
    @Published var tourCard: FirstRunTourCard?

    /// The permission cards or the tour are up: hovering or clicking the island must not open the
    /// panel over them (the pointer drifts there while setting up), even while the busy strip shows.
    var isSetupInProgress: Bool { tourCard != nil || permissionPrompt != nil }

    /// The tour just finished: the island settles back into the notch slowly, once.
    @Published private(set) var isSettlingGracefully = false
    var tourWidth: CGFloat { placement.isSideDrawer ? 360 : 512 }
    var tourHeight: CGFloat {
        // A side drawer is narrower, so the copy wraps onto more lines.
        let statusRoom: CGFloat = (tourCard?.status == nil ? 0 : 38) + (tourCard?.microphoneName == nil ? 0 : 20) + (tourCard?.field == nil ? 0 : 38)
        return cardTopInset + (placement.isSideDrawer ? 168 : 116) + statusRoom
    }

    /// Text typed out in the compact strip (the open panel's (i) while the buddy is docked); nil = none.
    @Published private(set) var captionText: String?
    /// True while the text composer is open on this island.
    @Published private(set) var isComposerOpen = false
    /// True while the busy strip has a line to show under its status (the agent's progress, or
    /// what was asked while the answer is spoken); set by the HUD manager.
    @Published private(set) var hasCompactDetailLine = false
    /// The goodbye on quit: its caption shows even over a busy strip, and nothing opens after it.
    @Published private(set) var isSayingGoodbye = false

    /// The caption strip shows only while nothing busier needs the island (the goodbye excepted).
    var isShowingCaption: Bool { captionText != nil && (!isBusy || isSayingGoodbye) }

    /// True while the cursor buddy is docked; mirrored from CompanionManager by the HUD manager.
    @Published private(set) var isCursorDocked = false
    /// Whether this screen's island is the one the buddy docks into (the notch screen).
    var hostsDockedCursor = false

    /// Displays without a notch show only the handle line, flush with the top edge of the screen
    /// (inside the menu bar band, where the notch would be), like HeyClicky's.
    let handleStripHeight: CGFloat = 12
    /// A side drawer collapses to a vertical handle this tall against the screen edge.
    let sideHandleLength: CGFloat = 64
    var collapsedWidth: CGFloat {
        switch placement {
        case .notch: return geometry.notchWidth
        case .left, .right: return handleStripHeight
        case .hidden: return 0
        }
    }
    var collapsedHeight: CGFloat {
        switch placement {
        case .notch: return geometry.hasHardwareNotch ? geometry.notchHeight + collapsedLipHeight : handleStripHeight
        case .left, .right: return sideHandleLength
        case .hidden: return 0
        }
    }
    var compactHeight: CGFloat { geometry.notchHeight + compactContentHeight }
    /// Where a card's content starts: under the notch band on a notch screen; elsewhere the island
    /// covers the menu bar and the content uses that height too.
    var cardTopInset: CGFloat { geometry.hasHardwareNotch ? geometry.notchHeight : 8 }
    var connectHeight: CGFloat {
        // 12 top + 40 icon row + 12 + 30 buttons + 16 bottom, and the example chips (26 + 12).
        let hasExamplePrompts = !(connectPrompt?.examplePrompts.isEmpty ?? true)
        return cardTopInset + 110 + (hasExamplePrompts ? 38 : 0)
    }
    var permissionHeight: CGFloat {
        // The stale-row escape hatch adds a line under the card's row.
        let contentHeight: CGFloat = permissionPrompt?.offersAccessibilityTrustReset == true ? 118 : 96
        return cardTopInset + contentHeight
    }
    var composerHeight: CGFloat { geometry.notchHeight + composerContentHeight }
    /// The band, then one row: 88 pt under a notch (Claude Design's "max 88pt"); a side drawer
    /// stacks the dock button over the shortcut grid.
    var fullHeight: CGFloat {
        bandRowHeight + (placement.isSideDrawer ? 92 : 45)
    }

    var width: CGFloat {
        switch expansion {
        case .collapsed: return collapsedWidth
        case .compact: return compactWidth
        case .connect: return connectWidth
        case .permission: return permissionWidth
        case .composer: return composerWidth
        case .tour: return tourWidth
        case .full: return fullWidth
        }
    }

    var height: CGFloat {
        switch expansion {
        case .collapsed: return collapsedHeight
        case .compact: return compactHeight
        case .connect: return connectHeight
        case .permission: return permissionHeight
        case .composer: return composerHeight
        case .tour: return tourHeight
        case .full: return fullHeight
        }
    }

    /// How far the window reaches past the island on its open sides, so the hover margin and the
    /// opening spring are not clipped.
    let windowMargin: CGFloat = 16

    /// The island's rectangle on screen (AppKit coordinates) for a size: centred under the notch,
    /// or against the chosen screen edge with its top a fifth of the way down (lifted when a tall
    /// panel would run past the bottom of the screen).
    func islandFrame(width: CGFloat, height: CGFloat) -> CGRect {
        switch placement {
        case .notch, .hidden:
            return CGRect(x: geometry.notchRect.midX - width / 2, y: geometry.screenFrame.maxY - height, width: width, height: height)
        case .left, .right:
            let area = geometry.visibleFrame.isEmpty ? geometry.screenFrame : geometry.visibleFrame
            let top = min(area.maxY, max(area.maxY - area.height * 0.2, area.minY + height))
            let x = placement == .left ? area.minX : area.maxX - width
            return CGRect(x: x, y: top - height, width: width, height: height)
        }
    }

    var islandFrame: CGRect { islandFrame(width: width, height: height) }

    /// Where the docked buddy "lives": under the notch, or just inside a side drawer's handle.
    var dockPoint: CGPoint {
        guard placement.isSideDrawer else { return geometry.dockPoint }
        let handle = islandFrame(width: collapsedWidth, height: collapsedHeight)
        return CGPoint(x: placement == .left ? handle.maxX + 6 : handle.minX - 6, y: handle.midY)
    }

    /// The window around the island: the island plus `windowMargin` on every side that does not
    /// touch the screen edge it hangs from.
    var windowFrame: CGRect {
        let island = islandFrame
        let margin = windowMargin
        switch placement {
        case .notch, .hidden:
            return CGRect(x: island.minX - margin, y: island.minY - margin, width: island.width + margin * 2, height: island.height + margin)
        case .left:
            return CGRect(x: island.minX, y: island.minY - margin, width: island.width + margin, height: island.height + margin * 2)
        case .right:
            return CGRect(x: island.minX - margin, y: island.minY - margin, width: island.width + margin, height: island.height + margin * 2)
        }
    }

    /// Hidden and idle, the island is nothing at all: no hover zone, no click target.
    var isInvisible: Bool { placement == .hidden && expansion == .collapsed }

    /// Called whenever the expansion or the panel's size changes so the window can resize itself.
    var onLayoutChanged: (() -> Void)?

    private var isHovering = false
    /// Set when something the panel opened takes over (a window comes forward): the pointer is
    /// still over the island, but it should stay closed until the pointer leaves and comes back.
    private var ignoresHoverUntilExit = false
    private var isBusy = false
    /// The user clicked the HUD: stay open until they click elsewhere or close it.
    private var isPinnedOpen = false
    private var collapseTask: Task<Void, Never>?

    init(geometry: NotchGeometry) {
        self.geometry = geometry
    }

    func setHovering(_ hovering: Bool) {
        if ignoresHoverUntilExit {
            guard !hovering else { return }
            ignoresHoverUntilExit = false
        }
        guard isHovering != hovering else { return }
        isHovering = hovering
        reconcile()
    }

    /// Clicky is speaking: the strip is a stop button, and hovering must not swap it for the panel.
    private(set) var isSpeaking = false

    func setSpeaking(_ speaking: Bool) {
        guard isSpeaking != speaking else { return }
        isSpeaking = speaking
        reconcile()
    }

    /// The step of the guide under way (1, 2, …), nil when there is none. While it is set the island
    /// is only the guide's stop strip: it stays between steps, and hovering never opens the panel.
    private(set) var guideStep: Int?

    func setGuideStep(_ step: Int?) {
        guard guideStep != step else { return }
        let guideStartedOrEnded = (guideStep == nil) != (step == nil)
        guideStep = step
        // A panel left pinned open would hide the stop strip for the whole guide.
        if step != nil { isPinnedOpen = false }
        if guideStartedOrEnded { reconcile(immediately: step != nil) } else { onLayoutChanged?() }
    }

    func setBusy(_ busy: Bool) {
        isBusy = busy
        reconcile()
        // The compact strip changes size between the busy strip and the caption strip.
        if captionText != nil { onLayoutChanged?() }
    }

    /// Shows (or clears) the caption typed into the compact strip.
    func setCaption(_ text: String?) {
        guard captionText != text else { return }
        let wasShowingCaption = captionText != nil
        captionText = text
        if wasShowingCaption != (text != nil) {
            reconcile(immediately: text != nil)
        }
    }

    func openComposer() {
        guard !isComposerOpen else { return }
        isComposerOpen = true
        reconcile(immediately: true)
    }

    func closeComposer() {
        guard isComposerOpen else { return }
        isComposerOpen = false
        isHovering = false
        reconcile(immediately: true)
    }

    func setCompactDetailLine(_ hasDetailLine: Bool) {
        guard hasCompactDetailLine != hasDetailLine else { return }
        hasCompactDetailLine = hasDetailLine
        if expansion == .compact { onLayoutChanged?() }
    }

    func setCursorDocked(_ docked: Bool) {
        guard isCursorDocked != docked else { return }
        isCursorDocked = docked
        onLayoutChanged?()
    }

    func togglePinned() {
        guard !isSetupInProgress else { return }
        isPinnedOpen.toggle()
        reconcile()
    }

    /// Opens the full panel and keeps it open until a click elsewhere (the menu bar's "open the
    /// island", the only way in when the island is hidden).
    func openPinned() {
        isPinnedOpen = true
        reconcile(immediately: true)
    }

    /// The goodbye caption takes the island over and nothing else opens until the app quits.
    func beginGoodbye() {
        isSayingGoodbye = true
        isPinnedOpen = false
        isHovering = false
        isComposerOpen = false
        reconcile(immediately: true)
        onLayoutChanged?()
    }

    func close() {
        isPinnedOpen = false
        isHovering = false
        reconcile(immediately: true)
    }

    /// Closes the panel for good this time: hovering reopens it only after the pointer has left.
    func dismiss() {
        ignoresHoverUntilExit = isHovering || expansion == .full
        close()
    }

    /// Shows (or clears) the connect card. It opens on its own and closes when answered, so it
    /// takes the island over from the busy strip; a pinned-open full panel still wins.
    func setConnectPrompt(_ prompt: AppConnectPrompt?) {
        guard connectPrompt != prompt else { return }
        connectPrompt = prompt
        reconcile(immediately: prompt != nil)
    }

    /// Shows (or clears) the tour card. It yields to the busy strip while a take or a question is
    /// under way (that is the step being tried), and comes back when it ends.
    func setTourCard(_ card: FirstRunTourCard?) {
        guard tourCard != card else { return }
        if tourCard != nil && card == nil {
            isSettlingGracefully = true
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.8) { [weak self] in self?.isSettlingGracefully = false }
        }
        let sizeChanged = tourCard?.status != card?.status || tourCard?.step != card?.step || tourCard?.microphoneName != card?.microphoneName || tourCard?.field != card?.field
        tourCard = card
        reconcile(immediately: true)
        if sizeChanged && expansion == .tour { onLayoutChanged?() }
    }

    /// Shows (or clears) the permission card. Nothing the app can do works without these, so the
    /// card outranks every other island state until the last permission lands.
    func setPermissionPrompt(_ prompt: PermissionPrompt?) {
        guard permissionPrompt != prompt else { return }
        permissionPrompt = prompt
        reconcile(immediately: true)
    }

    /// The state the island should be in once every hover grace period has run out.
    private var restingExpansion: NotchHUDExpansion {
        if permissionPrompt != nil { return .permission }
        if tourCard != nil && !isBusy { return .tour }
        if connectPrompt != nil { return .connect }
        if isBusy || captionText != nil || guideStep != nil { return .compact }
        return .collapsed
    }

    private func reconcile(immediately: Bool = false) {
        collapseTask?.cancel()
        let target: NotchHUDExpansion
        if isSayingGoodbye {
            setExpansion(.compact)
            return
        } else if isComposerOpen {
            // The composer has the keyboard: nothing swaps it out until it is closed.
            target = .composer
        } else if permissionPrompt != nil {
            // Without permissions there is nothing else worth opening the island for.
            target = .permission
        } else if isPinnedOpen && !isSetupInProgress {
            target = .full
        } else if tourCard != nil && !isBusy {
            // Like the connect card: hovering it must not swap it for the panel.
            target = .tour
        } else if connectPrompt != nil {
            // Hovering the connect card must not swap it for the full panel while the user is
            // reaching for its buttons; a click on the island still pins the full panel open.
            target = .connect
        } else if isHovering && !isSetupInProgress && !isSpeaking && guideStep == nil {
            target = .full
        } else {
            target = restingExpansion
        }
        if target == .full || target == .connect || target == .permission || target == .composer || target == .tour || immediately {
            setExpansion(target)
            return
        }
        // Grace period so the panel does not flicker when the pointer skims the edge.
        collapseTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            // Hovering or a pinned panel holds the island open, except during setup, where the
            // pointer resting on the tour card must not keep the busy strip from showing.
            guard !Task.isCancelled, let self, self.isSetupInProgress || ((self.isSpeaking || self.guideStep != nil) && !self.isPinnedOpen) || (!self.isHovering && !self.isPinnedOpen), !self.isComposerOpen else { return }
            self.setExpansion(self.restingExpansion)
        }
    }

    private func setExpansion(_ expansion: NotchHUDExpansion) {
        guard self.expansion != expansion else { return }
        self.expansion = expansion
        onLayoutChanged?()
    }

}

// MARK: - Window

/// Transparent, non-activating panel that floats above the menu bar around the notch. It can
/// become key so the text composer and the tour's field accept typing, but it never activates the app.
final class NotchHUDWindow: NSPanel {
    init() {
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 340, height: 100),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        // Pop-up-menu level (the level HeyClicky's island uses): above the menu bar and status items.
        level = .popUpMenu
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        isMovableByWindowBackground = false
        ignoresMouseEvents = false
        acceptsMouseMovedEvents = true
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

// MARK: - Manager

/// Owns one HUD window per screen. The screen with the hardware notch hosts the primary HUD (the
/// cursor docks there); every other display gets the same island drawn as a pill under its menu
/// bar, so the HUD is reachable wherever the pointer is. Each window is sized to its state so the
/// transparent area never blocks clicks elsewhere.
@MainActor
final class NotchHUDManager {
    private final class Instance {
        let screenID: CGDirectDisplayID
        let model: NotchHUDModel
        let window: NotchHUDWindow
        let hostingView: NSView

        init(screenID: CGDirectDisplayID, model: NotchHUDModel, window: NotchHUDWindow, hostingView: NSView) {
            self.screenID = screenID
            self.model = model
            self.window = window
            self.hostingView = hostingView
        }
    }

    private var instances: [Instance] = []
    #if DEBUG
    /// The UI tour keeps a pinned panel open through clicks elsewhere on the desktop.
    var debugKeepsPanelsOpen = false
    #endif
    private weak var companionManager: CompanionManager?
    private var isShown = false
    private var isBusy = false
    private var isSpeaking = false
    private var hasCompactDetailLine = false
    private var screenChangeObserver: NSObjectProtocol?
    private var clickOutsideMonitor: Any?
    private var hoverPollTimer: Timer?
    private var busyCancellables = Set<AnyCancellable>()
    /// Where the islands live (Settings → general → "where openclicky lives").
    private(set) var placement = HUDPlacement.stored

    /// The screen the primary HUD lives on: the one with a hardware notch, else the menu-bar screen.
    /// (`NSScreen.main` follows keyboard focus and can be an external display.)
    private static var primaryScreen: NSScreen? {
        NSScreen.screens.first(where: { $0.safeAreaInsets.top > 0 }) ?? NSScreen.screens.first
    }

    private static func screenID(of screen: NSScreen) -> CGDirectDisplayID {
        (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
    }

    private var primaryInstance: Instance? {
        instances.first(where: { $0.model.geometry.hasHardwareNotch }) ?? instances.first
    }

    /// Where the docked buddy lives (primary screen).
    var dockPoint: CGPoint { primaryInstance?.model.dockPoint ?? dockGeometry.dockPoint }

    /// The display the buddy docks on (AppKit global frame), so a flight from another display
    /// can be split at the display edges.
    var dockScreenFrame: CGRect { dockGeometry.screenFrame }

    private var dockGeometry: NotchGeometry {
        if let primaryInstance { return primaryInstance.model.geometry }
        return Self.primaryScreen.map(NotchGeometry.forScreen)
            ?? NotchGeometry(screenFrame: .zero, hasHardwareNotch: false, notchWidth: 190, notchHeight: 24)
    }

    /// The island under the pointer, else the primary one.
    private var instanceUnderPointer: Instance? {
        let mouseLocation = NSEvent.mouseLocation
        return instances.first(where: { $0.model.geometry.screenFrame.contains(mouseLocation) }) ?? primaryInstance
    }

    // MARK: Text composer (control tapped twice)

    /// The app that had the keyboard before the composer took it, so it gets it back on close.
    private var applicationActiveBeforeComposer: NSRunningApplication?

    /// Opens the one-line composer on the island under the pointer and gives it the keyboard.
    /// The HUD panel is non-activating, so OpenClicky activates itself for the duration.
    func openTextComposer() {
        guard let instance = instanceUnderPointer else { return }
        if instances.contains(where: { $0.model.isComposerOpen }) { return }
        let frontApplication = NSWorkspace.shared.frontmostApplication
        if frontApplication?.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            applicationActiveBeforeComposer = frontApplication
        }
        instance.model.openComposer()
        NSApp.activate(ignoringOtherApps: true)
        instance.window.makeKeyAndOrderFront(nil)
        // The field is created by SwiftUI once the island has switched state, and macOS 14's
        // cooperative activation can land late: keep putting the field in front until it takes.
        for delay in [0.05, 0.25, 0.6, 1.2] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self, weak instance] in
                guard let self, let instance, instance.model.isComposerOpen else { return }
                self.focusComposerField(in: instance.window)
            }
        }
    }

    /// Makes the composer's text field the first responder (the window must be key for typing to land).
    private func focusComposerField(in window: NSWindow) {
        guard let textField = Self.firstTextField(in: window.contentView) else { return }
        if !window.isKeyWindow { window.makeKeyAndOrderFront(nil) }
        if window.firstResponder !== textField.currentEditor() {
            window.makeFirstResponder(textField)
        }
    }

    private static func firstTextField(in view: NSView?) -> NSTextField? {
        guard let view else { return nil }
        if let textField = view as? NSTextField, textField.isEditable { return textField }
        for subview in view.subviews {
            if let textField = firstTextField(in: subview) { return textField }
        }
        return nil
    }

    /// Where the tour card's main button sits on screen (AppKit coordinates) and the display it is
    /// on: the card's right column, its first button, worked out from the card's layout (12 pt
    /// top padding, the buttons centred against a 56 pt row, a 30 pt primary button).
    func tourPrimaryButtonLocation() -> (CGPoint, CGRect)? {
        guard let instance = primaryInstance, instance.model.expansion == .tour else { return nil }
        let island = instance.model.islandFrame
        let contentTop = island.maxY - instance.model.cardTopInset - 12
        let location = CGPoint(x: island.maxX - DS.HUD.bodySidePadding - 43, y: contentTop - 13)
        return (location, instance.model.geometry.screenFrame)
    }

    /// Gives the keyboard to the tour card's field (the account step's email or code). Like the
    /// composer, the app activates and the field is made first responder until it takes.
    func focusTourField() {
        guard let instance = primaryInstance, instance.model.expansion == .tour else { return }
        NSApp.activate(ignoringOtherApps: true)
        instance.window.makeKeyAndOrderFront(nil)
        for delay in [0.05, 0.25, 0.6] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self, weak instance] in
                guard let self, let instance, instance.model.expansion == .tour else { return }
                self.focusComposerField(in: instance.window)
            }
        }
    }

    /// Closes every open panel because a window it opened is coming forward.
    func dismissPanels() {
        for instance in instances where instance.model.expansion == .full {
            instance.model.dismiss()
        }
    }

    /// Closes the composer everywhere and hands the keyboard back to the app that had it.
    func closeTextComposer() {
        guard instances.contains(where: { $0.model.isComposerOpen }) else { return }
        for instance in instances {
            instance.window.makeFirstResponder(nil)
            instance.model.closeComposer()
        }
        if let previousApplication = applicationActiveBeforeComposer, !previousApplication.isTerminated {
            previousApplication.activate()
        }
        applicationActiveBeforeComposer = nil
    }

    // MARK: Caption (the open panel's (i) while the buddy is docked)

    private var captionTypingTimer: Timer?
    private var captionDismissWorkItem: DispatchWorkItem?

    /// Types `message` into every island's compact strip one character at a time, holds it for
    /// `holdSeconds`, then lets the islands collapse again.
    func showCaption(_ message: String, holdSeconds: TimeInterval = 8) {
        captionTypingTimer?.invalidate()
        captionDismissWorkItem?.cancel()
        var typedText = ""
        instances.forEach { $0.model.setCaption("") }
        let characters = Array(message)
        var nextCharacterIndex = 0
        captionTypingTimer = Timer.scheduledTimer(withTimeInterval: 0.03, repeats: true) { [weak self] timer in
            guard let self else { timer.invalidate(); return }
            guard nextCharacterIndex < characters.count else {
                timer.invalidate()
                let dismissWorkItem = DispatchWorkItem { [weak self] in self?.clearCaption() }
                self.captionDismissWorkItem = dismissWorkItem
                DispatchQueue.main.asyncAfter(deadline: .now() + holdSeconds, execute: dismissWorkItem)
                return
            }
            typedText.append(characters[nextCharacterIndex])
            nextCharacterIndex += 1
            self.instances.forEach { $0.model.setCaption(typedText) }
        }
    }

    func clearCaption() {
        captionTypingTimer?.invalidate()
        captionTypingTimer = nil
        captionDismissWorkItem?.cancel()
        captionDismissWorkItem = nil
        instances.forEach { $0.model.setCaption(nil) }
    }

    func show(companionManager: CompanionManager) {
        self.companionManager = companionManager
        isShown = true
        // The HUD is the app's home and is shown exactly once at launch: a good moment to start
        // keeping a signed-in session fresh (refresh before the access token expires).
        OpenClickyAuthSession.shared.start()
        if screenChangeObserver == nil {
            observeScreenChanges()
            observeBusyState(of: companionManager)
            installClickOutsideMonitor()
            startHoverPolling()
        }
        syncInstancesWithScreens()
        for instance in instances {
            resizeWindow(instance)
            instance.window.orderFrontRegardless()
        }
    }

    func hide() {
        isShown = false
        instances.forEach { $0.window.orderOut(nil) }
    }

    /// Moves every island to `placement` (notch, a side drawer, or hidden while idle).
    func setPlacement(_ placement: HUDPlacement) {
        guard self.placement != placement else { return }
        self.placement = placement
        for instance in instances {
            instance.model.close()
            instance.model.placement = placement
            if let screen = NSScreen.screens.first(where: { Self.screenID(of: $0) == instance.screenID }) {
                instance.model.geometry = NotchGeometry.forScreen(screen, placement: placement)
            }
            // Jump straight to the new spot: a delayed shrink would leave the old frame on screen.
            instance.window.setFrame(instance.model.windowFrame, display: true)
            fitHostingViewToWindow(instance)
            instance.window.ignoresMouseEvents = instance.model.isInvisible
        }
    }

    /// Opens the full panel on the island under the pointer and keeps it open.
    func openPanel() {
        instanceUnderPointer?.model.openPinned()
    }

    /// Types the goodbye into every island, over whatever it was showing.
    func showGoodbye(_ message: String) {
        instances.forEach { $0.model.beginGoodbye() }
        showCaption(message, holdSeconds: 30)
    }

    /// Shows the "Connect <app> to OpenClicky" card on every screen's island (nil hides it).
    func setConnectPrompt(_ prompt: AppConnectPrompt?) {
        connectPrompt = prompt
        instances.forEach { $0.model.setConnectPrompt(prompt) }
    }
    private var connectPrompt: AppConnectPrompt?

    /// Shows the tour card on every screen's island (nil hides it).
    func setTourCard(_ card: FirstRunTourCard?) {
        tourCard = card
        instances.forEach { $0.model.setTourCard(card) }
    }
    private var tourCard: FirstRunTourCard?

    /// Shows the permission card on every screen's island (nil hides it).
    func setPermissionPrompt(_ prompt: PermissionPrompt?) {
        permissionPrompt = prompt
        instances.forEach { $0.model.setPermissionPrompt(prompt) }
    }
    private var permissionPrompt: PermissionPrompt?

    /// Creates a HUD for every attached screen and drops the ones whose screen went away.
    private func syncInstancesWithScreens() {
        guard let companionManager else { return }
        let screens = NSScreen.screens
        let liveIDs = Set(screens.map(Self.screenID(of:)))
        for instance in instances where !liveIDs.contains(instance.screenID) {
            instance.window.orderOut(nil)
        }
        instances.removeAll { !liveIDs.contains($0.screenID) }

        for screen in screens {
            let screenID = Self.screenID(of: screen)
            let geometry = NotchGeometry.forScreen(screen, placement: placement)
            if let existing = instances.first(where: { $0.screenID == screenID }) {
                if existing.model.geometry != geometry { existing.model.geometry = geometry }
                continue
            }
            let model = NotchHUDModel(geometry: geometry)
            model.placement = placement
            model.hostsDockedCursor = Self.primaryScreen.map(Self.screenID(of:)) == screenID
            model.setBusy(isBusy)
            model.setSpeaking(isSpeaking)
            model.setGuideStep(companionManager.guideStepUnderWay)
            model.setCompactDetailLine(hasCompactDetailLine)
            model.setCursorDocked(companionManager.isCursorDocked)
            model.setConnectPrompt(connectPrompt)
            model.setPermissionPrompt(permissionPrompt)
            model.setTourCard(tourCard)
            let hudWindow = NotchHUDWindow()
            // The window frame must stay authoritative. An NSHostingView used directly as the
            // content view re-fits the window to SwiftUI's fitting size (the hidden full panel),
            // so it is wrapped in a plain container view that simply fills the window.
            let hostingView = NSHostingView(rootView: NotchHUDView(model: model, companionManager: companionManager))
            hostingView.sizingOptions = []
            let containerView = NSView(frame: NSRect(origin: .zero, size: hudWindow.frame.size))
            hostingView.translatesAutoresizingMaskIntoConstraints = true
            hostingView.autoresizingMask = []
            hostingView.frame = containerView.bounds
            containerView.addSubview(hostingView)
            hudWindow.contentView = containerView
            let instance = Instance(screenID: screenID, model: model, window: hudWindow, hostingView: hostingView)
            model.onLayoutChanged = { [weak self, weak instance] in
                guard let self, let instance else { return }
                self.resizeWindow(instance)
            }
            instances.append(instance)
            resizeWindow(instance)
            if isShown { hudWindow.orderFrontRegardless() }
        }
    }

    private func observeBusyState(of companionManager: CompanionManager) {
        busyCancellables.removeAll()
        companionManager.$voiceState
            .combineLatest(companionManager.$agentActivityText)
            .map { voiceState, agentActivityText in voiceState != .idle || agentActivityText != nil }
            .removeDuplicates()
            .sink { [weak self] busy in
                guard let self else { return }
                self.isBusy = busy
                self.instances.forEach { $0.model.setBusy(busy) }
            }
            .store(in: &busyCancellables)
        companionManager.$guideStepUnderWay
            .removeDuplicates()
            .sink { [weak self] guideStep in
                self?.instances.forEach { $0.model.setGuideStep(guideStep) }
            }
            .store(in: &busyCancellables)
        companionManager.$voiceState
            .map { $0 == .responding }
            .removeDuplicates()
            .sink { [weak self] speaking in
                guard let self else { return }
                self.isSpeaking = speaking
                self.instances.forEach { $0.model.setSpeaking(speaking) }
            }
            .store(in: &busyCancellables)
        companionManager.$voiceState
            .combineLatest(companionManager.$agentActivityText, companionManager.$lastTranscript)
            .map { voiceState, agentActivityText, lastTranscript in
                NotchCompactStatusView.detailLine(voiceState: voiceState, agentActivityText: agentActivityText, lastTranscript: lastTranscript) != nil
            }
            .removeDuplicates()
            .sink { [weak self] hasDetailLine in
                guard let self else { return }
                self.hasCompactDetailLine = hasDetailLine
                self.instances.forEach { $0.model.setCompactDetailLine(hasDetailLine) }
            }
            .store(in: &busyCancellables)
        companionManager.$isCursorDocked
            .removeDuplicates()
            .sink { [weak self] docked in
                self?.instances.forEach { $0.model.setCursorDocked(docked) }
            }
            .store(in: &busyCancellables)
    }

    private func observeScreenChanges() {
        screenChangeObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.syncInstancesWithScreens()
                self.instances.forEach { self.resizeWindow($0) }
            }
        }
    }

    /// A click anywhere outside the HUD closes a pinned-open panel or the composer.
    private func installClickOutsideMonitor() {
        clickOutsideMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                #if DEBUG
                if self.debugKeepsPanelsOpen { return }
                #endif
                for instance in self.instances where instance.model.expansion == .full {
                    instance.model.close()
                }
                self.closeTextComposer()
            }
        }
    }

    /// SwiftUI's onHover does not fire reliably in a non-key window of a background app, so the
    /// pointer is polled against each island's current on-screen rectangle instead.
    private func startHoverPolling() {
        hoverPollTimer?.invalidate()
        hoverPollTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                let mouseLocation = NSEvent.mouseLocation
                for instance in self.instances {
                    let model = instance.model
                    guard !model.isInvisible else {
                        model.setHovering(false)
                        continue
                    }
                    let hoverMargin: CGFloat = model.expansion == .collapsed ? 6 : 14
                    let hoverRect = model.islandFrame.insetBy(dx: -hoverMargin, dy: -hoverMargin)
                    model.setHovering(hoverRect.contains(mouseLocation))
                }
            }
        }
    }

    private func resizeWindow(_ instance: Instance) {
        let window = instance.window
        let frame = instance.model.windowFrame
        let isGrowing = frame.width >= window.frame.width && frame.height >= window.frame.height
        if isGrowing {
            window.setFrame(frame, display: true)
            fitHostingViewToWindow(instance)
            window.ignoresMouseEvents = instance.model.isInvisible
        } else {
            // Shrink after the closing animation so the content is not clipped mid-spring (the
            // tour's slow settle into the notch takes longer).
            DispatchQueue.main.asyncAfter(deadline: .now() + (instance.model.isSettlingGracefully ? 1.5 : 0.45)) { [weak self, weak instance] in
                guard let self, let instance else { return }
                instance.window.setFrame(instance.model.windowFrame, display: true)
                self.fitHostingViewToWindow(instance)
                instance.window.ignoresMouseEvents = instance.model.isInvisible
            }
        }
    }

    /// NSHostingView neither autoresizes nor honors edge constraints against its own fitting size,
    /// so its frame is set by hand after every window resize.
    private func fitHostingViewToWindow(_ instance: Instance) {
        guard let containerView = instance.window.contentView else { return }
        instance.hostingView.frame = containerView.bounds
        instance.hostingView.needsLayout = true
        instance.hostingView.layoutSubtreeIfNeeded()
    }
}

#if DEBUG
// MARK: - UI tour hooks (UITour.swift)

extension NotchHUDManager {
    var debugPrimaryWindow: NSWindow? { primaryInstance?.window }


    func debugCloseAll() { instances.forEach { $0.model.close() } }

    func debugOpenPrimaryPanel() { primaryInstance?.model.openPinned() }
}
#endif

// MARK: - Root view

struct NotchHUDView: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager

    private var expansion: NotchHUDExpansion { model.expansion }
    private var notchHeight: CGFloat { model.geometry.notchHeight }
    /// The hardware notch's own bottom radius when collapsed, 18 pt for the busy strip, the sheet's 22 pt island radius for cards and the panel.
    private var bottomCornerRadius: CGFloat {
        switch expansion {
        case .collapsed: return 11
        case .compact: return 18
        default: return DS.HUD.islandRadius
        }
    }
    /// How far the top corners curve outward into the menu bar (measured from HeyClicky: ~6 pt).
    /// A side drawer hangs from a screen edge instead, so it has none.
    private var topCornerFlare: CGFloat { model.placement.isSideDrawer ? 0 : 6 }

    /// Where the island sits in its window: centred at the top under the notch, or against the
    /// screen edge a side drawer hangs from (the window's margin is on the other three sides).
    private var islandAlignment: Alignment {
        switch model.placement {
        case .notch, .hidden: return .top
        case .left: return .topLeading
        case .right: return .topTrailing
        }
    }

    /// The island's outline: the notch shape, or a drawer whose screen-side corners are square.
    @ViewBuilder
    private var islandBackground: some View {
        if model.placement.isSideDrawer {
            let radius = bottomCornerRadius
            let isLeft = model.placement == .left
            UnevenRoundedRectangle(
                topLeadingRadius: isLeft ? 0 : radius,
                bottomLeadingRadius: isLeft ? 0 : radius,
                bottomTrailingRadius: isLeft ? radius : 0,
                topTrailingRadius: isLeft ? radius : 0,
                style: .continuous
            )
            .fill(DS.HUD.island)
        } else {
            NotchIslandShape(bottomCornerRadius: bottomCornerRadius, topCornerFlare: topCornerFlare)
                .fill(DS.HUD.island)
        }
    }

    var body: some View {
        ZStack(alignment: .top) {
            // Solid black, no border, no shadow: the island reads as part of the notch. On a display
            // without a notch nothing is drawn while collapsed (no fake notch over the menu bar): only
            // the thin handle below marks where to hover.
            if expansion != .collapsed || (model.placement == .notch && model.geometry.hasHardwareNotch) {
                // Collapsed, the black is the notch itself.
                islandBackground
                    .frame(width: model.width + topCornerFlare * 2, height: expansion == .collapsed ? notchHeight : model.height)
                    .onTapGesture {
                        if expansion == .compact && model.guideStep != nil { companionManager.stopGuide(); return }
                        if expansion == .compact && model.isSpeaking { companionManager.stopSpeaking(); return }
                        if expansion != .connect && expansion != .permission && expansion != .composer && expansion != .tour { model.togglePinned() }
                    }
            }

            // Only the active layer lives in the hierarchy: hidden layers with fixed frames would
            // inflate the SwiftUI fitting size and NSHostingView would then center (and clip) the
            // content inside the smaller window.
            switch expansion {
            case .collapsed:
                if model.placement == .hidden {
                    // Shortcuts only: nothing on screen until something happens.
                    EmptyView()
                } else if model.placement.isSideDrawer {
                    // The drawer's handle: a short vertical line against the screen edge.
                    Capsule()
                        .fill(Color.black.opacity(0.7))
                        .frame(width: 6, height: 44)
                        .overlay(Capsule().fill(Color.white.opacity(0.75)).frame(width: 2, height: 36))
                        .frame(width: model.width, height: model.height)
                        .allowsHitTesting(false)
                        .transition(.opacity)
                } else if model.geometry.hasHardwareNotch {
                    // The island is exactly the notch: no lip, no handle. Hovering the notch opens it.
                    EmptyView()
                } else {
                    // The subtle handle line at the top edge of a display without a notch: it sits
                    // inside the menu bar band (nothing lives in the middle of it), where the notch
                    // would be, not below it.
                    Capsule()
                        .fill(Color.black.opacity(0.7))
                        .frame(width: 44, height: 6)
                        .overlay(Capsule().fill(Color.white.opacity(0.75)).frame(width: 36, height: 2))
                        .padding(.top, 3)
                        .allowsHitTesting(false)
                        .transition(.opacity)
                }
            case .compact:
                Group {
                    if model.isShowingCaption, let captionText = model.captionText {
                        NotchCaptionView(companionManager: companionManager, captionText: captionText)
                    } else {
                        NotchCompactStatusView(companionManager: companionManager)
                    }
                }
                .frame(width: model.compactWidth, height: model.compactContentHeight)
                .padding(.top, notchHeight)
                .transition(.opacity.combined(with: .scale(scale: 0.92, anchor: .top)))
            case .composer:
                NotchComposerView(companionManager: companionManager)
                    .frame(width: model.composerWidth, height: model.composerContentHeight)
                    .padding(.top, notchHeight)
                    .transition(.opacity.combined(with: .scale(scale: 0.92, anchor: .top)))
            case .connect:
                if let connectPrompt = model.connectPrompt {
                    AppConnectPromptView(
                        prompt: connectPrompt,
                        controller: companionManager.appConnectPromptController,
                        topInset: model.cardTopInset
                    )
                        .frame(width: model.connectWidth, height: model.connectHeight)
                        .transition(.opacity.combined(with: .scale(scale: 0.92, anchor: .top)))
                }
            case .permission:
                if let permissionPrompt = model.permissionPrompt {
                    PermissionPromptView(
                        prompt: permissionPrompt,
                        controller: companionManager.permissionPromptController,
                        topInset: model.cardTopInset
                    )
                        .frame(width: model.permissionWidth, height: model.permissionHeight)
                        .transition(.opacity.combined(with: .scale(scale: 0.92, anchor: .top)))
                }
            case .tour:
                if let tourCard = model.tourCard {
                    FirstRunTourCardView(card: tourCard, controller: companionManager.firstRunTour, topInset: model.cardTopInset)
                        .frame(width: model.tourWidth, height: model.tourHeight)
                        .transition(.opacity.combined(with: .scale(scale: 0.92, anchor: .top)))
                }
            case .full:
                NotchFullPanelView(model: model, companionManager: companionManager)
                    .frame(width: model.fullWidth, height: model.fullHeight)
                    .transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .top)))
            }
        }
        .padding(.top, model.placement.isSideDrawer ? model.windowMargin : 0)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: islandAlignment)
        .contentShape(Rectangle())
        .animation(model.isSettlingGracefully ? .spring(response: 1.3, dampingFraction: 0.92) : .spring(response: 0.42, dampingFraction: 0.84), value: expansion)
        .animation(.easeInOut(duration: 0.25), value: companionManager.isCursorDocked)
        .animation(.easeInOut(duration: 0.2), value: companionManager.voiceState)
    }
}

/// HeyClicky's island outline: the body is `rect` minus a `topCornerFlare` strip on each side. Its
/// top corners curve outward into the menu bar (like the hardware notch does) and its bottom corners
/// are rounded inward by `bottomCornerRadius`.
struct NotchIslandShape: Shape {
    var bottomCornerRadius: CGFloat
    var topCornerFlare: CGFloat

    var animatableData: AnimatablePair<CGFloat, CGFloat> {
        get { AnimatablePair(bottomCornerRadius, topCornerFlare) }
        set { bottomCornerRadius = newValue.first; topCornerFlare = newValue.second }
    }

    func path(in rect: CGRect) -> Path {
        let flare = min(topCornerFlare, rect.width / 2)
        let bodyMinX = rect.minX + flare
        let bodyMaxX = rect.maxX - flare
        let radius = min(bottomCornerRadius, (bodyMaxX - bodyMinX) / 2, rect.height - flare)
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        // Top-right flare: a quarter curve from the menu bar down into the island's straight side.
        path.addQuadCurve(to: CGPoint(x: bodyMaxX, y: rect.minY + flare), control: CGPoint(x: bodyMaxX, y: rect.minY))
        path.addLine(to: CGPoint(x: bodyMaxX, y: rect.maxY - radius))
        path.addArc(center: CGPoint(x: bodyMaxX - radius, y: rect.maxY - radius), radius: radius,
                    startAngle: .degrees(0), endAngle: .degrees(90), clockwise: false)
        path.addLine(to: CGPoint(x: bodyMinX + radius, y: rect.maxY))
        path.addArc(center: CGPoint(x: bodyMinX + radius, y: rect.maxY - radius), radius: radius,
                    startAngle: .degrees(90), endAngle: .degrees(180), clockwise: false)
        path.addLine(to: CGPoint(x: bodyMinX, y: rect.minY + flare))
        path.addQuadCurve(to: CGPoint(x: rect.minX, y: rect.minY), control: CGPoint(x: bodyMinX, y: rect.minY))
        path.closeSubpath()
        return path
    }
}

// MARK: - Compact status strip

/// The busy strip (380 × 37): the indicator on the left — level bars while listening, a spinner
/// while thinking, the pointer while speaking — and what is happening, in a few words, on the
/// right. One caption line opens under it when there is something to read: the agent's progress,
/// or what was asked while the answer is spoken.
struct NotchCompactStatusView: View {
    @ObservedObject var companionManager: CompanionManager

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                NotchBuddyBadge(companionManager: companionManager)
                    .frame(width: 20, height: 18)
                Spacer(minLength: 8)
                Text(statusText)
                    .font(.system(size: 12))
                    .foregroundColor(DS.HUD.text2)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if companionManager.guideStepUnderWay != nil {
                    // During a guide the whole strip stops the guide on a click; this says so in words,
                    // because an icon alone was not enough for someone new to it.
                    HStack(spacing: 5) {
                        Image(systemName: "stop.circle.fill")
                            .font(.system(size: 15))
                        Text("stop guide")
                            .font(.system(size: 12, weight: .semibold))
                    }
                    .foregroundColor(DS.HUD.text)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("stop the guide")
                } else if isSpeaking {
                    // The whole strip stops the voice on a click; this says so.
                    Image(systemName: "stop.circle.fill")
                        .font(.system(size: 15))
                        .foregroundColor(DS.HUD.text)
                        .accessibilityLabel("stop speaking")
                }
            }
            .frame(height: DS.HUD.stripHeight)

            if let detailLine = Self.detailLine(
                voiceState: companionManager.voiceState,
                agentActivityText: companionManager.agentActivityText,
                lastTranscript: companionManager.lastTranscript
            ) {
                Text(detailLine)
                    .font(.system(size: 14))
                    .foregroundColor(DS.HUD.text)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14)
        .overlay {
            // During a guide, a click anywhere on the strip ends it.
            if companionManager.guideStepUnderWay != nil {
                Color.clear
                    .contentShape(Rectangle())
                    .onTapGesture { companionManager.stopGuide() }
                    .pointerCursor()
                    .help("click to stop the guide")
            } else if isSpeaking {
                // Only while speaking, so a click on the busy strip otherwise still opens the panel.
                Color.clear
                    .contentShape(Rectangle())
                    .onTapGesture { companionManager.stopSpeaking() }
                    .pointerCursor()
                    .help("click to stop speaking")
            }
        }
    }

    private var isSpeaking: Bool { companionManager.voiceState == .responding }

    /// The strip's caption line, or nil for a strip with only its status. The HUD manager asks the
    /// same question to size the island, so it is static and takes the published values.
    static func detailLine(voiceState: CompanionVoiceState, agentActivityText: String?, lastTranscript: String?) -> String? {
        if voiceState == .idle { return nil }
        if let agentActivityText, !agentActivityText.isEmpty { return agentActivityText }
        if voiceState == .responding, let lastTranscript, !lastTranscript.isEmpty { return "“\(lastTranscript)”" }
        return nil
    }

    private var statusText: String {
        if let guideStep = companionManager.guideStepUnderWay {
            switch companionManager.voiceState {
            case .processing: return "step \(guideStep + 1) · looking"
            case .idle: return "step \(guideStep) · your turn"
            default: return "step \(guideStep)"
            }
        }
        let isDictating = companionManager.isDictatingToFrontApp
        switch companionManager.voiceState {
        case .idle:
            if companionManager.agentActivityText != nil { return "an agent is working" }
            return companionManager.isAlwaysListening ? "hands-free · just talk" : "openclicky"
        case .listening:
            if isDictating { return "dictating" }
            return companionManager.isAlwaysListening ? "listening · hands-free" : "listening"
        case .processing:
            if isDictating { return "typing it in" }
            return companionManager.agentActivityText == nil ? "looking at your screen" : "working"
        case .responding:
            return companionManager.answeringSourceName.map { "speaking · \($0)" } ?? "speaking"
        }
    }
}

/// The (i) caption while the buddy is docked: the badge with the text typing out next to it.
struct NotchCaptionView: View {
    @ObservedObject var companionManager: CompanionManager
    let captionText: String

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            NotchBuddyBadge(companionManager: companionManager)
                .frame(width: 20, height: 20)
            Text(captionText)
                .font(.system(size: 13))
                .foregroundColor(DS.HUD.text)
                .lineLimit(3)
                .multilineTextAlignment(.leading)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }
}

/// The pointer as the HUD draws it: the orange arrow with its glow, dimmed in the band's status
/// while it rests in the notch.
struct NotchPointerMark: View {
    var isDimmed: Bool

    var body: some View {
        Triangle()
            .fill(DS.HUD.pointer)
            .frame(width: 12, height: 12)
            .rotationEffect(.degrees(-35))
            .shadow(color: isDimmed ? .clear : DS.HUD.pointer, radius: 4, x: 0, y: 0)
            .opacity(isDimmed ? 0.5 : 1)
            .frame(width: 14, height: 14)
    }
}

/// The text composer (control tapped twice): one line, ↩ sends, esc closes. The request goes
/// wherever a spoken one would (the Realtime session, or the Claude teacher lane).
struct NotchComposerView: View {
    @ObservedObject var companionManager: CompanionManager
    @State private var requestText = ""

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "text.cursor")
                .font(.system(size: 13, weight: .semibold))
                .foregroundColor(DS.HUD.pointer)
            NotchComposerTextField(
                text: $requestText,
                placeholder: "ask clicky anything about your screen…",
                onSubmit: submitRequest,
                onEscape: { companionManager.notchHUDManager.closeTextComposer() }
            )
            Text(requestText.isEmpty ? "esc" : "↩ send")
                .font(.system(size: 9.5, weight: .medium, design: .monospaced))
                .foregroundColor(Color.white.opacity(0.5))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(RoundedRectangle(cornerRadius: 4, style: .continuous).fill(Color.white.opacity(0.12)))
        }
        .padding(.horizontal, 18)
        .onAppear { requestText = "" }
    }

    private func submitRequest() {
        let trimmedRequestText = requestText.trimmingCharacters(in: .whitespacesAndNewlines)
        companionManager.notchHUDManager.closeTextComposer()
        guard !trimmedRequestText.isEmpty else { return }
        companionManager.submitTypedRequest(trimmedRequestText)
    }
}

/// The composer's field is an AppKit text field: the HUD is a non-activating panel that receives
/// the keyboard without the app being active, and AppKit's first responder is what makes typing
/// land there (SwiftUI focus needs an active app). ↩ submits, esc cancels, through the delegate.
struct NotchComposerTextField: NSViewRepresentable {
    @Binding var text: String
    let placeholder: String
    var isSecure: Bool = false
    let onSubmit: () -> Void
    let onEscape: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSTextField {
        let textField: NSTextField = isSecure ? NSSecureTextField() : NSTextField()
        textField.isBordered = false
        textField.isBezeled = false
        textField.drawsBackground = false
        textField.focusRingType = .none
        textField.font = .systemFont(ofSize: 13)
        textField.textColor = .white
        textField.placeholderAttributedString = NSAttributedString(
            string: placeholder,
            attributes: [.foregroundColor: NSColor.white.withAlphaComponent(0.45), .font: NSFont.systemFont(ofSize: 13)]
        )
        textField.lineBreakMode = .byTruncatingHead
        textField.cell?.usesSingleLineMode = true
        textField.cell?.isScrollable = true
        textField.delegate = context.coordinator
        textField.setContentHuggingPriority(.defaultLow, for: .horizontal)
        textField.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return textField
    }

    func updateNSView(_ textField: NSTextField, context: Context) {
        context.coordinator.parent = self
        if textField.stringValue != text { textField.stringValue = text }
    }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: NotchComposerTextField

        init(_ parent: NotchComposerTextField) { self.parent = parent }

        func controlTextDidChange(_ notification: Notification) {
            guard let textField = notification.object as? NSTextField else { return }
            parent.text = textField.stringValue
        }

        func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            if commandSelector == #selector(NSResponder.insertNewline(_:)) {
                parent.onSubmit()
                return true
            }
            // Escape reaches the field editor as cancelOperation: or, through the standard key
            // bindings, as complete: (autocompletion); either one closes the composer.
            if commandSelector == #selector(NSResponder.cancelOperation(_:)) || commandSelector == #selector(NSTextView.complete(_:)) {
                parent.onEscape()
                return true
            }
            return false
        }
    }
}

/// The docked buddy, or the current voice-state indicator while busy.
struct NotchBuddyBadge: View {
    @ObservedObject var companionManager: CompanionManager

    var body: some View {
        switch companionManager.voiceState {
        case .listening:
            BlueCursorWaveformView(audioPowerLevel: companionManager.currentAudioPowerLevel)
        case .processing:
            BlueCursorSpinnerView()
        case .idle, .responding:
            if companionManager.isCursorDocked {
                Button(action: { companionManager.setCursorDocked(false) }) {
                    NotchPointerMark(isDimmed: false)
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .help("release the pointer")
            } else {
                NotchPointerMark(isDimmed: false)
            }
        }
    }
}
