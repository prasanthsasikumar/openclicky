//
//  OrbPanel.swift
//  OpenClicky
//
//  The orb: a small floating pill at the bottom of the screen that shows the take as it happens —
//  resting as two dashes, bars while listening, a line while the words are on their way, and a
//  transcript box above it when the words had nowhere to go. A non-activating panel on every
//  Space, sized to its content so the desktop around it stays clickable, draggable when allowed,
//  its position remembered.
//

import AppKit
import Combine
import SwiftUI

/// What the orb is showing right now.
enum OrbPhase: Equatable {
    case idle
    case listening
    case editListening
    case working(String)
    case done(String)
    case failed(String)
}

@MainActor
final class OrbModel: ObservableObject {
    @Published var phase: OrbPhase = .idle
    /// Words as they arrive while listening.
    @Published var liveTranscript = ""
    /// The transcript box above the pill: the text of the last take when it is open.
    @Published var boxText: String?
    /// The line under the box's text: why it is open.
    @Published var boxReason = "your last take"
    @Published var isBoxOpen = false
    /// 0…1 microphone level for the bars.
    @Published var audioLevel: CGFloat = 0
    /// A hint under the pill ("tap / hold to talk"), when tooltips are on.
    @Published var hint: String?
    /// Rewrites offered under the box's text ("formal", "casual", "shorter") when a model can do them.
    @Published var quickRewrites: [String] = []
    /// A rewrite is on its way; the box says so and keeps its old words until it lands.
    @Published var isRewriting = false
    /// The box's text can be pasted into the app in front.
    var onQuickRewrite: ((String) -> Void)?
    var onPasteFromBox: (() -> Void)?

    var isBusy: Bool {
        switch phase {
        case .idle, .done, .failed: return false
        case .listening, .editListening, .working: return true
        }
    }
}

/// The pill's own geometry.
enum OrbMetrics {
    static func pillSize(_ size: OrbSize) -> CGSize {
        size == .mini ? CGSize(width: 44, height: 20) : CGSize(width: 76, height: 30)
    }
    static let boxWidth: CGFloat = 380
    static let boxHeight: CGFloat = 170
    static let hintHeight: CGFloat = 22
    static let padding: CGFloat = 12
    /// Where the pill rests when nothing was dragged: this far above the bottom of the main screen.
    static let restingBottomInset: CGFloat = 18
}

private final class OrbWindow: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// A hosting view that drags the window when the pill is dragged and reports a plain click on
/// it; everything above the pill (the transcript box and its buttons) is SwiftUI's as usual.
private final class OrbHostingView<Content: View>: NSHostingView<Content> {
    var onClick: (() -> Void)?
    var isDraggable: () -> Bool = { true }
    var onDragEnded: (() -> Void)?
    /// The pill's rect in the view's coordinates (bottom-left origin), asked at each press.
    var pillRect: () -> NSRect = { .zero }
    private var dragStartScreenPoint: NSPoint?
    private var dragStartWindowOrigin: NSPoint?
    private var didDrag = false
    private var pressIsOnPill = false

    override func mouseDown(with event: NSEvent) {
        let location = convert(event.locationInWindow, from: nil)
        pressIsOnPill = pillRect().contains(location)
        guard pressIsOnPill else {
            super.mouseDown(with: event)
            return
        }
        dragStartScreenPoint = NSEvent.mouseLocation
        dragStartWindowOrigin = window?.frame.origin
        didDrag = false
    }

    override func mouseDragged(with event: NSEvent) {
        guard pressIsOnPill else {
            super.mouseDragged(with: event)
            return
        }
        guard isDraggable(), let window, let start = dragStartScreenPoint, let origin = dragStartWindowOrigin else { return }
        let now = NSEvent.mouseLocation
        let delta = NSPoint(x: now.x - start.x, y: now.y - start.y)
        if !didDrag && hypot(delta.x, delta.y) < 3 { return }
        didDrag = true
        window.setFrameOrigin(NSPoint(x: origin.x + delta.x, y: origin.y + delta.y))
    }

    override func mouseUp(with event: NSEvent) {
        guard pressIsOnPill else {
            super.mouseUp(with: event)
            return
        }
        defer { dragStartScreenPoint = nil; dragStartWindowOrigin = nil; pressIsOnPill = false }
        if didDrag { onDragEnded?() } else { onClick?() }
    }

    override var acceptsFirstResponder: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

@MainActor
final class OrbPanelManager {
    private var window: OrbWindow?
    private let model: OrbModel
    private let settings: DictationSettings
    private var cancellables: Set<AnyCancellable> = []
    private var hideWorkItem: DispatchWorkItem?
    /// Tap the orb to talk / tap to stop.
    var onOrbClicked: (() -> Void)?

    init(model: OrbModel, settings: DictationSettings) {
        self.model = model
        self.settings = settings
    }

    func start() {
        settings.objectWillChange
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in DispatchQueue.main.async { self?.applySettings() } }
            .store(in: &cancellables)
        // Only what changes the geometry: the level bars redraw inside the pill on their own.
        model.$phase.map { _ in () }
            .merge(with: model.$isBoxOpen.map { _ in () }, model.$boxText.map { _ in () }, model.$hint.map { _ in () })
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in DispatchQueue.main.async { self?.layout() } }
            .store(in: &cancellables)
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.layout() }
        }
        applySettings()
    }

    func stop() {
        window?.orderOut(nil)
        window = nil
        cancellables.removeAll()
    }

    private func applySettings() {
        if settings.orbVisible {
            if window == nil { makeWindow() }
            window?.appearance = settings.appearance.nsAppearance
            layout()
        } else {
            window?.orderOut(nil)
        }
    }

    private func makeWindow() {
        let window = OrbWindow(contentRect: NSRect(x: 0, y: 0, width: 100, height: 60), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.level = .floating
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        window.isMovableByWindowBackground = false
        window.hidesOnDeactivate = false
        window.isReleasedWhenClosed = false
        let hosting = OrbHostingView(rootView: OrbRootView(model: model, settings: settings))
        hosting.onClick = { [weak self] in self?.onOrbClicked?() }
        hosting.isDraggable = { [weak self] in self?.settings.orbIsDraggable ?? true }
        hosting.onDragEnded = { [weak self] in self?.rememberPosition() }
        hosting.pillRect = { [weak self] in self?.pillRectInWindow() ?? .zero }
        window.contentView = hosting
        self.window = window
    }

    /// Sizes the window to what is showing and keeps the pill where it rests.
    private func layout() {
        guard let window, settings.orbVisible else { return }
        let pill = OrbMetrics.pillSize(settings.orbSize)
        let showsBox = model.isBoxOpen && model.boxText != nil
        let showsHint = model.hint != nil && settings.tooltips
        let width = max(pill.width, showsBox ? OrbMetrics.boxWidth : (model.phase == .idle ? pill.width : 240)) + OrbMetrics.padding * 2
        var height = pill.height + OrbMetrics.padding * 2
        if showsBox { height += OrbMetrics.boxHeight + 8 }
        if showsHint { height += OrbMetrics.hintHeight }
        let anchor = restingAnchor()
        // The anchor is the pill's bottom-centre; the hint hangs below it, the box stacks above.
        let hintSpace = showsHint ? OrbMetrics.hintHeight : 0
        let frame = NSRect(x: anchor.x - width / 2, y: anchor.y - OrbMetrics.padding - hintSpace, width: width, height: height)
        window.setFrame(frame, display: true)
        if !window.isVisible { window.orderFrontRegardless() }
        scheduleHideIfIdle()
    }

    /// The pill's rect in window coordinates (bottom-left origin), from the same layout as `layout()`.
    private func pillRectInWindow() -> NSRect {
        guard let window else { return .zero }
        let pill = OrbMetrics.pillSize(settings.orbSize)
        let showsHint = model.hint != nil && settings.tooltips
        let hintSpace = showsHint ? OrbMetrics.hintHeight : 0
        let width = model.phase == .idle ? pill.width : max(pill.width, 220)
        return NSRect(x: window.frame.width / 2 - width / 2, y: hintSpace + OrbMetrics.padding, width: width, height: max(pill.height, model.phase == .idle ? pill.height : 30))
    }

    /// Bottom-centre of the pill in screen points: the remembered drag, or the main screen's bottom.
    private func restingAnchor() -> CGPoint {
        if let remembered = settings.orbPosition, NSScreen.screens.contains(where: { $0.frame.insetBy(dx: -20, dy: -20).contains(remembered) }) {
            return remembered
        }
        // The primary display (the one with the menu bar), not whichever screen holds the key
        // window: the orb rests in one place.
        let screen = NSScreen.screens.first ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        return CGPoint(x: visible.midX, y: visible.minY + OrbMetrics.restingBottomInset)
    }

    private func rememberPosition() {
        guard let window else { return }
        let showsHint = model.hint != nil && settings.tooltips
        let hintSpace = showsHint ? OrbMetrics.hintHeight : 0
        settings.orbPosition = CGPoint(x: window.frame.midX, y: window.frame.minY + OrbMetrics.padding + hintSpace)
    }

    /// "hide when not in use": the orb fades out a moment after a take finishes.
    private func scheduleHideIfIdle() {
        hideWorkItem?.cancel()
        guard let window else { return }
        guard settings.orbHidesWhenIdle else {
            window.alphaValue = 1
            return
        }
        if model.isBusy || model.isBoxOpen {
            window.alphaValue = 1
            return
        }
        let work = DispatchWorkItem { [weak self] in
            guard let self, let window = self.window, !self.model.isBusy, !self.model.isBoxOpen else { return }
            NSAnimationContext.runAnimationGroup { context in
                context.duration = self.settings.reduceAnimation ? 0 : 0.35
                window.animator().alphaValue = 0
            }
        }
        hideWorkItem = work
        window.alphaValue = 1
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6, execute: work)
    }
}

// MARK: - SwiftUI

struct OrbRootView: View {
    @ObservedObject var model: OrbModel
    @ObservedObject var settings: DictationSettings

    var body: some View {
        VStack(spacing: 8) {
            if model.isBoxOpen, let text = model.boxText {
                OrbTranscriptBox(text: text, reason: model.isRewriting ? "rewriting…" : model.boxReason, theme: settings.orbTheme,
                                 quickRewrites: model.isRewriting ? [] : model.quickRewrites,
                                 onRewrite: { model.onQuickRewrite?($0) },
                                 onPaste: model.onPasteFromBox.map { paste in { paste() } },
                                 onCopy: {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(text, forType: .string)
                    if !settings.orbRestsExpanded { model.isBoxOpen = false }
                }, onClose: { model.isBoxOpen = false })
                .frame(width: OrbMetrics.boxWidth, height: OrbMetrics.boxHeight)
                .transition(.opacity)
            }
            OrbPillView(model: model, settings: settings)
            if settings.tooltips, let hint = model.hint {
                Text(hint)
                    .font(.system(size: 10, weight: .medium))
                    .foregroundStyle(.secondary)
                    .frame(height: OrbMetrics.hintHeight - 6)
            }
        }
        .padding(OrbMetrics.padding)
        .animation(settings.reduceAnimation ? nil : .easeOut(duration: 0.18), value: model.phase)
    }
}

private struct OrbPillView: View {
    @ObservedObject var model: OrbModel
    @ObservedObject var settings: DictationSettings

    private var size: CGSize { OrbMetrics.pillSize(settings.orbSize) }

    private var fill: Color {
        switch settings.orbTheme {
        case .black: return Color(red: 0.16, green: 0.16, blue: 0.16)
        case .coral: return Color(red: 0.89, green: 0.33, blue: 0.21)
        case .mist: return Color(white: 0.93)
        }
    }

    private var ink: Color {
        switch settings.orbTheme {
        case .black, .coral: return Color.white.opacity(0.92)
        case .mist: return Color(white: 0.2)
        }
    }

    var body: some View {
        let isExpanded = model.phase != .idle
        let width = isExpanded ? max(size.width, 220) : size.width
        ZStack {
            Capsule(style: .continuous)
                .fill(fill)
                .shadow(color: .black.opacity(0.22), radius: 8, y: 3)
                .overlay(Capsule(style: .continuous).strokeBorder(Color.white.opacity(settings.orbTheme == .mist ? 0.9 : 0.08), lineWidth: 1))
            content
                .foregroundStyle(ink)
        }
        .frame(width: width, height: isExpanded ? max(size.height, 30) : size.height)
        .help(hintText)
    }

    private var hintText: String {
        "tap or hold \(settings.dictationKey.keycapLabel) to talk · tap the orb to talk"
    }

    @ViewBuilder
    private var content: some View {
        switch model.phase {
        case .idle:
            if settings.orbLook == .classic {
                OrbMarkShape().fill(ink).frame(width: size.height * 0.5, height: size.height * 0.5)
            } else if settings.orbLook == .pixel {
                OrbMarkShape().fill(ink).frame(width: size.height * 0.55, height: size.height * 0.55)
                    .mask(OrbPixelGrid().fill(.black))
            } else {
                HStack(spacing: 6) {
                    dash; dash
                }
            }
        case .listening, .editListening:
            HStack(spacing: 8) {
                OrbLevelBars(level: model.audioLevel, color: ink)
                    .frame(width: 34, height: 16)
                Text(model.liveTranscript.isEmpty ? (model.phase == .editListening ? "say your edit…" : "listening…") : model.liveTranscript)
                    .font(.system(size: 12, weight: .medium))
                    .lineLimit(1)
                    .truncationMode(.head)
                    .frame(maxWidth: 160, alignment: .leading)
            }
            .padding(.horizontal, 12)
        case .working(let message):
            HStack(spacing: 8) {
                ProgressView().controlSize(.small).tint(ink)
                Text(message).font(.system(size: 12, weight: .medium)).lineLimit(1)
            }
            .padding(.horizontal, 12)
        case .done(let message):
            HStack(spacing: 6) {
                Image(systemName: "checkmark").font(.system(size: 11, weight: .bold))
                Text(message).font(.system(size: 12, weight: .medium)).lineLimit(1)
            }
            .padding(.horizontal, 12)
        case .failed(let message):
            HStack(spacing: 6) {
                Image(systemName: "exclamationmark.circle").font(.system(size: 11, weight: .bold))
                Text(message).font(.system(size: 12, weight: .medium)).lineLimit(1)
            }
            .padding(.horizontal, 12)
        }
    }

    private var dash: some View {
        RoundedRectangle(cornerRadius: 2).fill(ink.opacity(0.9)).frame(width: settings.orbSize == .mini ? 6 : 9, height: 2.5)
    }
}

/// OpenClicky's mark, small: a rounded pointer with a dot — the buddy's arrow, simplified.
struct OrbMarkShape: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        let w = rect.width, h = rect.height
        path.move(to: CGPoint(x: rect.minX + w * 0.2, y: rect.minY + h * 0.1))
        path.addLine(to: CGPoint(x: rect.minX + w * 0.2, y: rect.minY + h * 0.9))
        path.addLine(to: CGPoint(x: rect.minX + w * 0.42, y: rect.minY + h * 0.68))
        path.addLine(to: CGPoint(x: rect.minX + w * 0.6, y: rect.minY + h * 0.98))
        path.addLine(to: CGPoint(x: rect.minX + w * 0.74, y: rect.minY + h * 0.9))
        path.addLine(to: CGPoint(x: rect.minX + w * 0.56, y: rect.minY + h * 0.62))
        path.addLine(to: CGPoint(x: rect.minX + w * 0.86, y: rect.minY + h * 0.6))
        path.closeSubpath()
        return path
    }
}

/// Five bars that follow the microphone level.
struct OrbLevelBars: View {
    let level: CGFloat
    let color: Color
    @State private var phase: CGFloat = 0

    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 30)) { timeline in
            let time = timeline.date.timeIntervalSinceReferenceDate
            HStack(spacing: 3) {
                ForEach(0..<5, id: \.self) { index in
                    let wobble = (sin(time * 9 + Double(index) * 1.3) + 1) / 2
                    let height = 3 + (level * 12) * (0.5 + 0.5 * wobble)
                    RoundedRectangle(cornerRadius: 1.5).fill(color).frame(width: 3, height: max(3, height))
                }
            }
        }
    }
}

private struct OrbTranscriptBox: View {
    let text: String
    let reason: String
    let theme: OrbTheme
    var quickRewrites: [String] = []
    var onRewrite: (String) -> Void = { _ in }
    var onPaste: (() -> Void)?
    let onCopy: () -> Void
    let onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ScrollView {
                Text(text)
                    .font(.system(size: 13))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            if !quickRewrites.isEmpty {
                HStack(spacing: 6) {
                    ForEach(quickRewrites, id: \.self) { rewrite in
                        Button(rewrite) { onRewrite(rewrite) }
                            .buttonStyle(.plain)
                            .font(.system(size: 10, weight: .medium))
                            .padding(.horizontal, 8).padding(.vertical, 3)
                            .background(Capsule().fill(Color.primary.opacity(0.08)))
                            .pointerCursor()
                    }
                    Spacer()
                }
            }
            HStack {
                Text(reason).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                Spacer()
                if let onPaste { Button("paste", action: onPaste).controlSize(.small) }
                Button("copy", action: onCopy).controlSize(.small)
                Button(action: onClose) { Image(systemName: "xmark") }.controlSize(.small)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.regularMaterial))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.primary.opacity(0.08)))
        .shadow(color: .black.opacity(0.18), radius: 12, y: 4)
    }
}

/// The pixel look: the mark seen through a 3 pt grid.
struct OrbPixelGrid: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        let cell: CGFloat = 3
        var y = rect.minY
        while y < rect.maxY {
            var x = rect.minX
            while x < rect.maxX {
                path.addRect(CGRect(x: x, y: y, width: cell - 0.8, height: cell - 0.8))
                x += cell
            }
            y += cell
        }
        return path
    }
}
