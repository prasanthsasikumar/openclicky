//
//  NotchHUD.swift
//  OpenClicky
//
//  The notch HUD. Three states:
//    collapsed — a black lip tucked under the MacBook notch (or a virtual notch) with a slim handle
//    compact   — a Dynamic-Island-style strip that opens while OpenClicky listens / thinks / speaks
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
        let menuBarHeight = max(screen.frame.maxY - screen.visibleFrame.maxY, 24)
        return NotchGeometry(screenFrame: screen.frame, hasHardwareNotch: false, notchWidth: 190, notchHeight: menuBarHeight)
    }
}

// MARK: - Model

enum NotchHUDExpansion: Equatable {
    case collapsed
    case compact
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

    let collapsedLipHeight: CGFloat = 10
    let compactWidth: CGFloat = 300
    let compactContentHeight: CGFloat = 54
    let fullWidth: CGFloat = 512

    var collapsedHeight: CGFloat { geometry.notchHeight + collapsedLipHeight }
    var compactHeight: CGFloat { geometry.notchHeight + compactContentHeight }
    var fullHeight: CGFloat {
        switch activeTab {
        case .home: return geometry.notchHeight + 198
        case .agents: return geometry.notchHeight + 380
        case .settings: return geometry.notchHeight + 590
        }
    }

    var width: CGFloat {
        switch expansion {
        case .collapsed: return geometry.notchWidth
        case .compact: return compactWidth
        case .full: return fullWidth
        }
    }

    var height: CGFloat {
        switch expansion {
        case .collapsed: return collapsedHeight
        case .compact: return compactHeight
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

    private func reconcile(immediately: Bool = false) {
        collapseTask?.cancel()
        let target: NotchHUDExpansion
        if isPinnedOpen || isHovering {
            target = .full
        } else if isBusy {
            target = .compact
        } else {
            target = .collapsed
        }
        if target == .full || immediately {
            setExpansion(target)
            return
        }
        // Grace period so the panel does not flicker when the pointer skims the edge.
        collapseTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            guard !Task.isCancelled, let self, !self.isHovering, !self.isPinnedOpen else { return }
            self.setExpansion(self.isBusy ? .compact : .collapsed)
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
        level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.mainMenuWindow)) + 1)
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
    var dockPoint: CGPoint {
        if let primaryInstance { return primaryInstance.model.geometry.dockPoint }
        let geometry = Self.primaryScreen.map(NotchGeometry.forScreen)
            ?? NotchGeometry(screenFrame: .zero, hasHardwareNotch: false, notchWidth: 190, notchHeight: 24)
        return geometry.dockPoint
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
            model.setBusy(isBusy)
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

    /// A click anywhere outside the HUD closes a pinned-open panel.
    private func installClickOutsideMonitor() {
        clickOutsideMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                for instance in self.instances where instance.model.expansion == .full {
                    instance.model.close()
                }
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
    private var bottomCornerRadius: CGFloat { expansion == .collapsed ? 11 : 20 }

    var body: some View {
        ZStack(alignment: .top) {
            islandShape
                .fill(Color.black)
                .overlay(islandShape.strokeBorder(Color.white.opacity(expansion == .collapsed ? 0.05 : 0.10), lineWidth: 1))
                .frame(width: model.width, height: model.height)
                .shadow(color: Color.black.opacity(expansion == .collapsed ? 0.25 : 0.5), radius: expansion == .collapsed ? 6 : 18, x: 0, y: 8)
                .onTapGesture { model.togglePinned() }

            // Only the active layer lives in the hierarchy: hidden layers with fixed frames would
            // inflate the SwiftUI fitting size and NSHostingView would then center (and clip) the
            // content inside the smaller window.
            switch expansion {
            case .collapsed:
                Capsule()
                    .fill(handleColor.opacity(companionManager.isCursorDocked ? 0.9 : 0.55))
                    .frame(width: companionManager.isCursorDocked ? 26 : 40, height: 4)
                    .shadow(color: handleColor.opacity(companionManager.isCursorDocked ? 0.8 : 0), radius: 4)
                    .padding(.top, notchHeight + 3)
                    .allowsHitTesting(false)
                    .transition(.opacity)
            case .compact:
                NotchCompactStatusView(companionManager: companionManager)
                    .frame(width: model.compactWidth, height: model.compactContentHeight)
                    .padding(.top, notchHeight)
                    .transition(.opacity.combined(with: .scale(scale: 0.92, anchor: .top)))
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

    private var islandShape: UnevenRoundedRectangle {
        UnevenRoundedRectangle(topLeadingRadius: 0, bottomLeadingRadius: bottomCornerRadius, bottomTrailingRadius: bottomCornerRadius, topTrailingRadius: 0, style: .continuous)
    }

    private var handleColor: Color {
        companionManager.isCursorDocked ? DS.Colors.overlayCursorColor : Color.white
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
        switch companionManager.voiceState {
        case .idle: return "OpenClicky"
        case .listening: return "Listening…"
        case .processing: return companionManager.agentActivityText == nil ? "Thinking…" : "Working…"
        case .responding: return "Speaking"
        }
    }

    private var secondaryStatusText: String {
        if let agentActivityText = companionManager.agentActivityText { return agentActivityText }
        switch companionManager.voiceState {
        case .idle: return "Hold ⌃⌥ to talk"
        case .listening: return "Release ⌃⌥ when you're done"
        case .processing: return "Looking at your screen"
        case .responding: return companionManager.lastTranscript.map { "“\($0)”" } ?? ""
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
