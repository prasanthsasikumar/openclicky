//
//  NotchHUD.swift
//  OpenClicky
//
//  The notch HUD, drawn like HeyClicky's: a solid black island hanging from the top of the screen
//  around the notch, its top corners flaring into the menu bar and its bottom corners rounded, with
//  no border and no shadow. Four states:
//    collapsed — exactly the notch (or a virtual notch on other displays); nothing shows below it
//    compact   — a Dynamic-Island-style strip that opens while OpenClicky listens / thinks / speaks
//    connect   — the "Connect <app> to OpenClicky" card that opens when a supported app or site
//                comes to the front for the first time (No / Not now / Yes + example prompts)
//    full      — the notch app that opens on hover or click: Home, Agents, Settings tabs
//  The cursor buddy can dock here: it flies into the notch and lives in the HUD as a glowing badge.
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
            return NotchGeometry(screenFrame: screen.frame, hasHardwareNotch: true, notchWidth: hardwareNotchWidth, notchHeight: topSafeAreaInset)
        }
        return virtualNotch(screenFrame: screen.frame, visibleFrame: screen.visibleFrame)
    }

    /// A display without a hardware notch hangs the island from its menu bar band. A display that
    /// shows no menu bar at all (an external monitor when the menu bar lives on the built-in
    /// display, or an auto-hidden menu bar) reports `visibleFrame.maxY == frame.maxY`: then there
    /// is no band and the island starts at the very top edge, like HeyClicky's.
    static func virtualNotch(screenFrame: CGRect, visibleFrame: CGRect) -> NotchGeometry {
        let menuBarBandHeight = max(0, screenFrame.maxY - visibleFrame.maxY)
        return NotchGeometry(screenFrame: screenFrame, hasHardwareNotch: false, notchWidth: 190, notchHeight: menuBarBandHeight)
    }
}

// MARK: - Model

enum NotchHUDExpansion: Equatable {
    case collapsed
    case compact
    case connect
    /// The text composer (control tapped twice): a one-line field to type a request.
    case composer
    case full
}

enum NotchHUDTab: String, CaseIterable, Identifiable {
    case home, agents, settings
    var id: String { rawValue }
}

@MainActor
final class NotchHUDModel: ObservableObject {
    @Published private(set) var expansion: NotchHUDExpansion = .collapsed
    @Published var activeTab: NotchHUDTab = .home
    @Published var geometry: NotchGeometry

    // Sizes measured from HeyClicky running next to us: the connect card is 606 × 115 pt and the
    // open Home panel 512 × 232 pt, both including the menu-bar band the island hangs from.
    /// Nothing shows below the notch while idle (HeyClicky's collapsed state is the notch itself).
    let collapsedLipHeight: CGFloat = 0
    /// The busy strip is 300 × 54; the caption strip (the (i) text while the buddy is docked) is wider.
    var compactWidth: CGFloat { isShowingCaption ? 400 : 300 }
    var compactContentHeight: CGFloat { isShowingCaption ? 70 : 54 }
    let connectWidth: CGFloat = 606
    let connectTotalHeight: CGFloat = 115
    let composerWidth: CGFloat = 460
    let composerContentHeight: CGFloat = 52
    let fullWidth: CGFloat = 512
    let homeTotalHeight: CGFloat = 232

    /// The band the tab bar sits in: the hardware notch, or at least 34 pt on a display without
    /// one (whose menu bar band may be 0 pt when that display shows no menu bar).
    var topBandHeight: CGFloat {
        geometry.hasHardwareNotch ? geometry.notchHeight : max(geometry.notchHeight, 34)
    }

    /// The "Connect <app> to OpenClicky" card to show, if any; set by `AppConnectPromptController`.
    @Published var connectPrompt: AppConnectPrompt?

    /// Text typed out in the compact strip (the Home tab's (i) while the buddy is docked); nil = none.
    @Published private(set) var captionText: String?
    /// True while the text composer is open on this island.
    @Published private(set) var isComposerOpen = false

    /// The caption strip shows only while nothing busier needs the island.
    var isShowingCaption: Bool { captionText != nil && !isBusy }

    /// True while the cursor buddy is docked; mirrored from CompanionManager by the HUD manager.
    @Published private(set) var isCursorDocked = false
    /// Whether this screen's island is the one the buddy docks into (the notch screen).
    var hostsDockedCursor = false
    /// The strip under the notch that shows the docked buddy (HeyClicky's triangle under the notch).
    let dockedBadgeStripHeight: CGFloat = 26

    /// Displays without a notch show only the handle line, flush with the top edge of the screen
    /// (inside the menu bar band, where the notch would be), like HeyClicky's.
    let handleStripHeight: CGFloat = 12
    var collapsedHeight: CGFloat {
        if hostsDockedCursor && isCursorDocked { return geometry.notchHeight + dockedBadgeStripHeight }
        return geometry.hasHardwareNotch ? geometry.notchHeight + collapsedLipHeight : handleStripHeight
    }
    var compactHeight: CGFloat { geometry.notchHeight + compactContentHeight }
    var connectHeight: CGFloat { max(connectTotalHeight, geometry.notchHeight + 78) }
    var composerHeight: CGFloat { geometry.notchHeight + composerContentHeight }
    var fullHeight: CGFloat {
        switch activeTab {
        case .home: return max(homeTotalHeight, topBandHeight + 195)
        case .agents: return topBandHeight + 380
        case .settings: return topBandHeight + 590
        }
    }

    var width: CGFloat {
        switch expansion {
        case .collapsed: return geometry.notchWidth
        case .compact: return compactWidth
        case .connect: return connectWidth
        case .composer: return composerWidth
        case .full: return fullWidth
        }
    }

    var height: CGFloat {
        switch expansion {
        case .collapsed: return collapsedHeight
        case .compact: return compactHeight
        case .connect: return connectHeight
        case .composer: return composerHeight
        case .full: return fullHeight
        }
    }

    /// Called whenever the expansion or tab changes so the window can resize itself.
    var onLayoutChanged: (() -> Void)?

    private var isHovering = false
    private var isBusy = false
    /// The user clicked the HUD: stay open until they click elsewhere or close it.
    private var isPinnedOpen = false
    private var collapseTask: Task<Void, Never>?

    init(geometry: NotchGeometry) {
        self.geometry = geometry
    }

    func setHovering(_ hovering: Bool) {
        guard isHovering != hovering else { return }
        isHovering = hovering
        reconcile()
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

    func setCursorDocked(_ docked: Bool) {
        guard isCursorDocked != docked else { return }
        isCursorDocked = docked
        onLayoutChanged?()
    }

    func togglePinned() {
        isPinnedOpen.toggle()
        reconcile()
    }

    func close() {
        isPinnedOpen = false
        isHovering = false
        reconcile(immediately: true)
    }

    func select(_ tab: NotchHUDTab) {
        activeTab = tab
        onLayoutChanged?()
    }

    /// Shows (or clears) the connect card. It opens on its own and closes when answered, so it
    /// takes the island over from the busy strip; a pinned-open full panel still wins.
    func setConnectPrompt(_ prompt: AppConnectPrompt?) {
        guard connectPrompt != prompt else { return }
        connectPrompt = prompt
        reconcile(immediately: prompt != nil)
    }

    /// The state the island should be in once every hover grace period has run out.
    private var restingExpansion: NotchHUDExpansion {
        if connectPrompt != nil { return .connect }
        if isBusy || captionText != nil { return .compact }
        return .collapsed
    }

    private func reconcile(immediately: Bool = false) {
        collapseTask?.cancel()
        let target: NotchHUDExpansion
        if isComposerOpen {
            // The composer has the keyboard: nothing swaps it out until it is closed.
            target = .composer
        } else if isPinnedOpen {
            target = .full
        } else if connectPrompt != nil {
            // Hovering the connect card must not swap it for the full panel while the user is
            // reaching for its buttons; a click on the island still pins the full panel open.
            target = .connect
        } else if isHovering {
            target = .full
        } else {
            target = restingExpansion
        }
        if target == .full || target == .connect || target == .composer || immediately {
            setExpansion(target)
            return
        }
        // Grace period so the panel does not flicker when the pointer skims the edge.
        collapseTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            guard !Task.isCancelled, let self, !self.isHovering, !self.isPinnedOpen, !self.isComposerOpen else { return }
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
/// become key so the Agents/Settings tabs accept text input, but it never activates the app.
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
    private weak var companionManager: CompanionManager?
    private var isShown = false
    private var isBusy = false
    private var screenChangeObserver: NSObjectProtocol?
    private var clickOutsideMonitor: Any?
    private var hoverPollTimer: Timer?
    private var busyCancellables = Set<AnyCancellable>()

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
    var dockPoint: CGPoint { dockGeometry.dockPoint }

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

    // MARK: Caption (the Home tab's (i) while the buddy is docked)

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

    /// Shows the "Connect <app> to OpenClicky" card on every screen's island (nil hides it).
    func setConnectPrompt(_ prompt: AppConnectPrompt?) {
        connectPrompt = prompt
        instances.forEach { $0.model.setConnectPrompt(prompt) }
    }
    private var connectPrompt: AppConnectPrompt?

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
            let geometry = NotchGeometry.forScreen(screen)
            if let existing = instances.first(where: { $0.screenID == screenID }) {
                if existing.model.geometry != geometry { existing.model.geometry = geometry }
                continue
            }
            let model = NotchHUDModel(geometry: geometry)
            model.hostsDockedCursor = Self.primaryScreen.map(Self.screenID(of:)) == screenID
            model.setBusy(isBusy)
            model.setCursorDocked(companionManager.isCursorDocked)
            model.setConnectPrompt(connectPrompt)
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
                    let geometry = model.geometry
                    let hoverMargin: CGFloat = model.expansion == .collapsed ? 6 : 14
                    let islandRect = NSRect(
                        x: geometry.notchRect.midX - model.width / 2 - hoverMargin,
                        y: geometry.screenFrame.maxY - model.height - hoverMargin,
                        width: model.width + hoverMargin * 2,
                        height: model.height + hoverMargin
                    )
                    model.setHovering(islandRect.contains(mouseLocation))
                }
            }
        }
    }

    private func targetFrame(for model: NotchHUDModel) -> NSRect {
        let geometry = model.geometry
        let hoverMargin: CGFloat = 16
        let width = model.width + hoverMargin * 2
        let height = model.height + hoverMargin
        return NSRect(
            x: geometry.notchRect.midX - width / 2,
            y: geometry.screenFrame.maxY - height,
            width: width,
            height: height
        )
    }

    private func resizeWindow(_ instance: Instance) {
        let window = instance.window
        let frame = targetFrame(for: instance.model)
        let isGrowing = frame.width >= window.frame.width && frame.height >= window.frame.height
        if isGrowing {
            window.setFrame(frame, display: true)
            fitHostingViewToWindow(instance)
        } else {
            // Shrink after the closing animation so the content is not clipped mid-spring.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.45) { [weak self, weak instance] in
                guard let self, let instance else { return }
                instance.window.setFrame(self.targetFrame(for: instance.model), display: true)
                self.fitHostingViewToWindow(instance)
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

// MARK: - Root view

struct NotchHUDView: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager

    private var expansion: NotchHUDExpansion { model.expansion }
    private var notchHeight: CGFloat { model.geometry.notchHeight }
    /// The hardware notch's own bottom radius when collapsed; HeyClicky's 16 pt once open.
    private var bottomCornerRadius: CGFloat { expansion == .collapsed ? 11 : 16 }
    /// How far the top corners curve outward into the menu bar (measured from HeyClicky: ~6 pt).
    private let topCornerFlare: CGFloat = 6

    var body: some View {
        ZStack(alignment: .top) {
            // Solid black, no border, no shadow: the island reads as part of the notch. On a display
            // without a notch nothing is drawn while collapsed (no fake notch over the menu bar): only
            // the thin handle below marks where to hover.
            if expansion != .collapsed || model.geometry.hasHardwareNotch {
                // Collapsed on a notch screen the black stops at the notch's bottom edge: the docked
                // buddy's triangle floats below it on its own, like HeyClicky's.
                NotchIslandShape(bottomCornerRadius: bottomCornerRadius, topCornerFlare: topCornerFlare)
                    .fill(Color.black)
                    .frame(width: model.width + topCornerFlare * 2, height: expansion == .collapsed ? notchHeight : model.height)
                    .onTapGesture { if expansion != .connect && expansion != .composer { model.togglePinned() } }
            }

            // Only the active layer lives in the hierarchy: hidden layers with fixed frames would
            // inflate the SwiftUI fitting size and NSHostingView would then center (and clip) the
            // content inside the smaller window.
            switch expansion {
            case .collapsed:
                if model.hostsDockedCursor && companionManager.isCursorDocked {
                    // The docked buddy peeks out under the notch; a click releases it.
                    Button(action: { companionManager.setCursorDocked(false) }) {
                        Triangle()
                            .fill(DS.Colors.overlayCursorColor)
                            .frame(width: 16, height: 16)
                            .rotationEffect(.degrees(-35))
                            .shadow(color: DS.Colors.overlayCursorColor, radius: 8, x: 0, y: 0)
                            .frame(width: 30, height: 24)
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()
                    .help("Release the cursor")
                    .padding(.top, notchHeight + 1)
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
                        topInset: model.geometry.hasHardwareNotch ? notchHeight : 8
                    )
                        .frame(width: model.connectWidth, height: model.connectHeight)
                        .transition(.opacity.combined(with: .scale(scale: 0.92, anchor: .top)))
                }
            case .full:
                NotchFullPanelView(model: model, companionManager: companionManager)
                    .frame(width: model.fullWidth, height: model.fullHeight)
                    .transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .top)))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .contentShape(Rectangle())
        .animation(.spring(response: 0.42, dampingFraction: 0.84), value: expansion)
        .animation(.spring(response: 0.42, dampingFraction: 0.84), value: model.activeTab)
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

struct NotchCompactStatusView: View {
    @ObservedObject var companionManager: CompanionManager

    var body: some View {
        HStack(spacing: 12) {
            NotchBuddyBadge(companionManager: companionManager)
                .frame(width: 30, height: 30)

            VStack(alignment: .leading, spacing: 2) {
                Text(primaryStatusText)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundColor(.white)
                    .lineLimit(1)
                Text(secondaryStatusText)
                    .font(.system(size: 11))
                    .foregroundColor(Color.white.opacity(0.55))
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 8)
    }

    private var primaryStatusText: String {
        let isDictating = companionManager.isDictatingToFrontApp
        switch companionManager.voiceState {
        case .idle: return companionManager.isAlwaysListening ? "Hands-free" : "OpenClicky"
        case .listening: return isDictating ? "Dictating…" : "Listening…"
        case .processing:
            if isDictating { return "Typing…" }
            return companionManager.agentActivityText == nil ? "Thinking…" : "Working…"
        case .responding: return "Speaking"
        }
    }

    private var secondaryStatusText: String {
        if let agentActivityText = companionManager.agentActivityText { return agentActivityText }
        let isDictating = companionManager.isDictatingToFrontApp
        switch companionManager.voiceState {
        case .idle: return companionManager.isAlwaysListening ? "Just talk — I'm listening" : "Hold ⌃⌥ to talk · tap ⌃ twice to type"
        case .listening:
            if isDictating { return "Release fn + ⌃ to type it into the app" }
            return companionManager.isAlwaysListening ? "Just talk — I'm listening" : "Release ⌃⌥ when you're done"
        case .processing: return isDictating ? "Transcribing what you said" : "Looking at your screen"
        case .responding: return companionManager.lastTranscript.map { "“\($0)”" } ?? ""
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
                .frame(width: 30, height: 30)
            Text(captionText)
                .font(.system(size: 12, weight: .medium))
                .foregroundColor(.white)
                .lineLimit(3)
                .multilineTextAlignment(.leading)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 10)
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
                .foregroundColor(DS.Colors.overlayCursorColor)
            NotchComposerTextField(
                text: $requestText,
                placeholder: "Ask OpenClicky anything about your screen…",
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
    let onSubmit: () -> Void
    let onEscape: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSTextField {
        let textField = NSTextField()
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
                    Triangle()
                        .fill(DS.Colors.overlayCursorColor)
                        .frame(width: 16, height: 16)
                        .rotationEffect(.degrees(-35))
                        .shadow(color: DS.Colors.overlayCursorColor, radius: 8, x: 0, y: 0)
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .help("Release the cursor")
            } else {
                Image(systemName: "waveform.circle.fill")
                    .font(.system(size: 20, weight: .medium))
                    .foregroundColor(Color.white.opacity(0.85))
            }
        }
    }
}
