//
//  PlateDeck.swift
//  OpenClicky
//
//  The deck: the screen dims and the day's cards fly out of the notch one after another,
//  tumbling, and land askew in one or two rows with a waste-paper bin below them. A card under
//  the pointer straightens and shows its plan. Saying yes stamps the card and sends it back into
//  the notch (the agent has it); binning it crumples it into a ball that is thrown into the bin;
//  closing gathers what is left back into the notch.
//

import AppKit
import Combine
import SwiftUI

@MainActor
final class PlateDeckModel: ObservableObject {
    /// Where one card is in its life on the table.
    enum Phase: Equatable {
        case inNotch, resting
        /// Said yes to: the stamp is on it.
        case stamped
        /// On its way back into the notch.
        case flyingBack
        /// Binned: the sheet is being crumpled where it lay.
        case crumpling
    }

    /// A crumpled card in the air.
    struct Ball: Identifiable, Equatable {
        let id: String
        let start: CGPoint
        var progress: CGFloat = 0
    }

    @Published var cards: [PlateSuggestion]
    @Published var phases: [String: Phase] = [:]
    @Published var balls: [Ball] = []
    @Published var hoveredID: String?
    @Published var adjustingID: String?
    @Published var adjustment = ""
    @Published var isDimmed = false
    /// The bin just caught a ball: it rocks.
    @Published var binTilt: Double = 0
    @Published var isCelebrating = false

    let reduceMotion: Bool
    /// Where the cards come from and go back to, in the deck's coordinates (origin top-left).
    let notchPoint: CGPoint
    /// The deck's size: the screen it covers.
    let size: CGSize
    var sounds: PlateSoundPlayer?
    var onTake: (PlateSuggestion) -> Void = { _ in }
    var onSkip: (PlateSuggestion) -> Void = { _ in }
    /// The deck has gathered and can be taken off the screen.
    var onClosed: () -> Void = {}
    private var isClosing = false
    private var tookAny = false
    /// Where the pointer is over the deck, nil when it is elsewhere. The deck works out the
    /// hovered card itself: SwiftUI's hover does not follow a card that moves under a still
    /// pointer, and goes quiet when the panel is not the key window.
    private var pointer: CGPoint?

    /// Where the cards rest: under the island's header, above the bin.
    var area: CGRect { CGRect(x: 0, y: 92, width: size.width, height: max(200, size.height - 92 - 250)) }
    var slots: [PlateDeckSlot] { PlateDeckLayout.slots(count: cards.count, in: area) }
    var cardWidth: CGFloat { PlateDeckLayout.cardWidth(for: cards.count, areaWidth: area.width) }
    var binCenter: CGPoint { PlateDeckLayout.binCenter(in: size) }

    init(cards: [PlateSuggestion], notchPoint: CGPoint, size: CGSize, reduceMotion: Bool) {
        self.cards = cards
        self.notchPoint = notchPoint
        self.size = size
        self.reduceMotion = reduceMotion
    }

    func phase(of card: PlateSuggestion) -> Phase { phases[card.id] ?? .inNotch }

    func deal() {
        sounds?.prepare()
        withAnimation(Motion.swap) { isDimmed = true }
        for (index, card) in cards.enumerated() {
            let delay = 0.1 + Double(index) * PlateMotion.dealStagger
            withAnimation(PlateMotion.deal(reduceMotion: reduceMotion).delay(delay)) { phases[card.id] = .resting }
            sounds?.play(.place, after: delay + 0.28)
        }
    }

    /// Gathers the cards into the notch, last dealt first, then reports closed.
    func close() {
        guard !isClosing else { return }
        isClosing = true
        hoveredID = nil
        adjustingID = nil
        let remaining = cards.filter { phase(of: $0) == .resting }
        for (order, card) in remaining.reversed().enumerated() {
            withAnimation(PlateMotion.gather(reduceMotion: reduceMotion).delay(Double(order) * PlateMotion.gatherStagger)) {
                phases[card.id] = .inNotch
            }
        }
        let gatherTime = PlateMotion.gatherTime(count: remaining.count)
        withAnimation(.easeIn(duration: 0.2).delay(max(0, gatherTime - 0.15))) { isDimmed = false }
        DispatchQueue.main.asyncAfter(deadline: .now() + gatherTime + 0.12) { [weak self] in self?.onClosed() }
    }

    func take(_ card: PlateSuggestion) {
        guard phase(of: card) == .resting, !isClosing else { return }
        var taken = card
        if adjustingID == card.id {
            let typed = adjustment.trimmingCharacters(in: .whitespacesAndNewlines)
            taken.adjustment = typed.isEmpty ? nil : typed
        }
        tookAny = true
        release(card)
        withAnimation(Motion.bounce(reduceMotion: reduceMotion)) { phases[card.id] = .stamped }
        sounds?.play(.stamp, after: 0.05)
        after(PlateMotion.stampHold) {
            withAnimation(Motion.structuralSlow.animation) { self.phases[card.id] = .flyingBack }
            self.after(PlateMotion.flyBackDuration) {
                self.remove(card)
                self.onTake(taken)
                self.afterADecision()
            }
        }
    }

    func skip(_ card: PlateSuggestion) {
        guard phase(of: card) == .resting, !isClosing else { return }
        guard let index = cards.firstIndex(where: { $0.id == card.id }), index < slots.count else { return }
        let start = slots[index].center
        release(card)
        withAnimation(.easeIn(duration: PlateMotion.crumpleDuration)) { phases[card.id] = .crumpling }
        sounds?.play(.crumple)
        after(PlateMotion.crumpleDuration * 0.7) {
            guard !self.reduceMotion else { return }
            self.balls.append(Ball(id: card.id, start: start))
            // One turn of the run loop so the ball exists at its start before it is thrown.
            DispatchQueue.main.async {
                guard let ballIndex = self.balls.firstIndex(where: { $0.id == card.id }) else { return }
                withAnimation(.easeIn(duration: PlateMotion.throwDuration)) { self.balls[ballIndex].progress = 1 }
            }
        }
        after(PlateMotion.crumpleDuration * 0.7 + (reduceMotion ? 0 : PlateMotion.throwDuration)) {
            self.balls.removeAll { $0.id == card.id }
            self.sounds?.play(.binDrop)
            self.rockTheBin()
            self.remove(card)
            self.onSkip(card)
            self.afterADecision()
        }
    }

    func beginAdjusting(_ card: PlateSuggestion) {
        adjustment = ""
        withAnimation(PlateMotion.hover(reduceMotion: reduceMotion)) { adjustingID = card.id }
    }

    func cancelAdjusting() {
        withAnimation(PlateMotion.hover(reduceMotion: reduceMotion)) { adjustingID = nil }
    }

    /// The pointer moved over the deck (nil: it left).
    func pointerMoved(to point: CGPoint?) {
        pointer = point
        updateHover()
    }

    private func updateHover() {
        guard !isClosing else { return }
        let resting = cards.filter { phase(of: $0) == .resting }.map(\.id)
        let hovered = pointer.flatMap {
            PlateDeckLayout.hoveredIndex(at: $0, slots: slots, cardWidth: cardWidth, openIndex: cards.firstIndex { $0.id == hoveredID })
        }.map { cards[$0].id }.flatMap { resting.contains($0) ? $0 : nil }
        guard hovered != hoveredID else { return }
        withAnimation(PlateMotion.hover(reduceMotion: reduceMotion)) { hoveredID = hovered }
    }

    private func release(_ card: PlateSuggestion) {
        if adjustingID == card.id { adjustingID = nil }
        if hoveredID == card.id { withAnimation(Motion.hover) { hoveredID = nil } }
    }

    private func remove(_ card: PlateSuggestion) {
        withAnimation(PlateMotion.settle(reduceMotion: reduceMotion)) { cards.removeAll { $0.id == card.id } }
        phases[card.id] = nil
    }

    /// The last card handled ends the deck: with a small burst when something was taken on.
    private func afterADecision() {
        guard cards.isEmpty else { return updateHover() }
        guard tookAny, !reduceMotion else { return close() }
        sounds?.play(.allDone)
        withAnimation(Motion.pop.animation) { isCelebrating = true }
        after(0.75) { self.close() }
    }

    private func rockTheBin() {
        guard !reduceMotion else { return }
        withAnimation(.easeOut(duration: 0.08)) { binTilt = 7 }
        after(0.08) { withAnimation(Motion.bounce.animation) { self.binTilt = 0 } }
    }

    private func after(_ seconds: TimeInterval, _ work: @escaping @MainActor () -> Void) {
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { [weak self] in
            guard self != nil else { return }
            work()
        }
    }
}

struct PlateDeckView: View {
    @ObservedObject var model: PlateDeckModel

    var body: some View {
        GeometryReader { _ in
            let area = model.area
            let slots = model.slots
            let cardWidth = model.cardWidth
            let bin = model.binCenter
            ZStack {
                Color.black.opacity(model.isDimmed ? 0.58 : 0)
                    .contentShape(Rectangle())
                    .onTapGesture { model.close() }
                    .accessibilityLabel("close the offers")

                PlateBin(part: .back)
                    .frame(width: 92, height: 104)
                    .rotationEffect(.degrees(model.binTilt), anchor: .bottom)
                    .position(bin)
                    .opacity(model.isDimmed ? 1 : 0)
                    .offset(y: model.isDimmed ? 0 : 30)
                    .accessibilityHidden(true)

                ForEach(model.balls) { ball in
                    PlatePaperBall(seed: ball.id)
                        .frame(width: 46, height: 46)
                        .modifier(PlateThrow(start: ball.start, end: CGPoint(x: bin.x, y: bin.y - 26), progress: ball.progress))
                        .accessibilityHidden(true)
                }

                PlateBin(part: .front)
                    .frame(width: 92, height: 104)
                    .rotationEffect(.degrees(model.binTilt), anchor: .bottom)
                    .position(bin)
                    .opacity(model.isDimmed ? 1 : 0)
                    .offset(y: model.isDimmed ? 0 : 30)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)

                ForEach(Array(model.cards.enumerated()), id: \.element.id) { index, card in
                    let slot = index < slots.count ? slots[index] : PlateDeckSlot(center: CGPoint(x: area.midX, y: area.midY), degrees: 0)
                    let look = look(for: card, index: index, slot: slot)
                    PlateCardView(
                        suggestion: card, width: cardWidth, isOpen: look.isOpen, isAdjusting: model.adjustingID == card.id,
                        isStamped: model.phase(of: card) == .stamped || model.phase(of: card) == .flyingBack,
                        reduceMotion: model.reduceMotion,
                        adjustment: $model.adjustment,
                        onDoIt: { model.take(card) }, onAdjust: { model.beginAdjusting(card) },
                        onCancelAdjust: { model.cancelAdjusting() }, onSkip: { model.skip(card) })
                        .scaleEffect(look.scale)
                        .rotationEffect(.degrees(look.degrees))
                        .opacity(look.opacity)
                        .position(look.center)
                        .zIndex(look.isOpen || model.phase(of: card) == .stamped ? 10 : Double(index) * 0.01)
                        .allowsHitTesting(model.phase(of: card) == .resting)
                }

                if model.isCelebrating {
                    PlateSparkles()
                        .frame(width: 320, height: 320)
                        .position(x: area.midX, y: area.midY)
                        .transition(.opacity)
                        .accessibilityHidden(true)
                }
            }
        }
        .ignoresSafeArea()
    }

    private struct CardLook {
        var center: CGPoint
        var degrees: Double
        var scale: CGFloat
        var opacity: Double
        var isOpen: Bool
    }

    private func look(for card: PlateSuggestion, index: Int, slot: PlateDeckSlot) -> CardLook {
        let flies = !model.reduceMotion
        let inNotch = CardLook(center: flies ? model.notchPoint : slot.center, degrees: flies ? PlateDeckLayout.notchDegrees(index) : slot.degrees,
                               scale: flies ? 0.08 : 0.96, opacity: 0, isOpen: false)
        switch model.phase(of: card) {
        case .inNotch:
            return inNotch
        case .flyingBack:
            return CardLook(center: inNotch.center, degrees: 0, scale: inNotch.scale, opacity: 0, isOpen: false)
        case .stamped:
            return CardLook(center: slot.center, degrees: 0, scale: 1.04, opacity: 1, isOpen: false)
        case .crumpling:
            return CardLook(center: slot.center, degrees: slot.degrees + (flies ? 24 : 0), scale: flies ? 0.18 : 0.96, opacity: 0, isOpen: false)
        case .resting:
            let isOpen = model.hoveredID == card.id || model.adjustingID == card.id
            return CardLook(center: slot.center, degrees: isOpen ? 0 : slot.degrees, scale: isOpen ? 1.03 : 1, opacity: 1, isOpen: isOpen)
        }
    }
}

/// Carries a view along a thrown arc, turning and shrinking a little as it goes.
struct PlateThrow: ViewModifier, Animatable {
    let start: CGPoint
    let end: CGPoint
    var progress: CGFloat

    var animatableData: CGFloat {
        get { progress }
        set { progress = newValue }
    }

    func body(content: Content) -> some View {
        let distance = hypot(end.x - start.x, end.y - start.y)
        content
            .rotationEffect(.degrees(Double(progress) * 420))
            .scaleEffect(1 - 0.3 * progress)
            .position(Motion.arcPoint(from: start, to: end, lift: PlateMotion.throwLift(distance: distance), progress: progress))
    }
}

/// A ring of four-point stars that opens and fades: every card handled.
struct PlateSparkles: View {
    @State private var isOpen = false

    var body: some View {
        ZStack {
            ForEach(0..<10, id: \.self) { index in
                let angle = Double(index) / 10 * 2 * .pi
                let reach: CGFloat = index.isMultiple(of: 2) ? 130 : 86
                PlateStar()
                    .fill(index.isMultiple(of: 3) ? Color(hex: "#FBE9B7") : Color.white)
                    .frame(width: index.isMultiple(of: 2) ? 22 : 14, height: index.isMultiple(of: 2) ? 22 : 14)
                    .scaleEffect(isOpen ? 1 : 0.2)
                    .rotationEffect(.degrees(isOpen ? 40 : 0))
                    .offset(x: isOpen ? cos(angle) * reach : 0, y: isOpen ? sin(angle) * reach : 0)
                    .opacity(isOpen ? 0 : 1)
            }
        }
        .onAppear {
            withAnimation(.easeOut(duration: 0.7)) { isOpen = true }
        }
    }
}

struct PlateStar: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let outer = min(rect.width, rect.height) / 2
        for index in 0..<8 {
            let radius = index.isMultiple(of: 2) ? outer : outer * 0.32
            let angle = CGFloat(index) / 8 * 2 * .pi - .pi / 2
            let point = CGPoint(x: center.x + cos(angle) * radius, y: center.y + sin(angle) * radius)
            if index == 0 { path.move(to: point) } else { path.addLine(to: point) }
        }
        path.closeSubpath()
        return path
    }
}

// MARK: - Window

/// A borderless panel over one screen, just under the island's window so the island's header
/// stays above the dimmed desktop. It takes the keyboard (esc closes; "change it" types) without
/// activating the app.
final class PlateDeckPanel: NSPanel {
    var onEscape: () -> Void = {}

    init(frame: CGRect) {
        super.init(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        isFloatingPanel = true
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        level = NSWindow.Level(rawValue: NSWindow.Level.popUpMenu.rawValue - 1)
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { onEscape() } else { super.keyDown(with: event) }
    }

    override func cancelOperation(_ sender: Any?) { onEscape() }
}

/// Reports where the pointer is over the deck, in the deck's coordinates (origin top-left),
/// whether or not the panel is the key window.
final class PlatePointerTrackingView: NSView {
    var onPointer: (CGPoint?) -> Void = { _ in }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseMoved(with event: NSEvent) { report(event) }
    override func mouseEntered(with event: NSEvent) { report(event) }
    override func mouseExited(with event: NSEvent) { onPointer(nil) }

    private func report(_ event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        onPointer(CGPoint(x: point.x, y: bounds.height - point.y))
    }
}

@MainActor
final class PlateDeckWindowController {
    private var panel: PlateDeckPanel?
    private(set) var model: PlateDeckModel?

    var isOpen: Bool { panel != nil }

    /// `screenFrame` and `notchPoint` are in AppKit screen coordinates.
    func show(cards: [PlateSuggestion], screenFrame: CGRect, notchPoint: CGPoint, reduceMotion: Bool, sounds: PlateSoundPlayer, takesKeyboard: Bool = true,
              onTake: @escaping (PlateSuggestion) -> Void, onSkip: @escaping (PlateSuggestion) -> Void, onClosed: @escaping () -> Void) {
        hide()
        let model = PlateDeckModel(cards: cards, notchPoint: CGPoint(x: notchPoint.x - screenFrame.minX, y: screenFrame.maxY - notchPoint.y), size: screenFrame.size, reduceMotion: reduceMotion)
        model.sounds = sounds
        model.onTake = onTake
        model.onSkip = onSkip
        model.onClosed = { [weak self] in
            self?.hide()
            onClosed()
        }
        let panel = PlateDeckPanel(frame: screenFrame)
        panel.onEscape = { [weak model] in model?.close() }
        let host = NSHostingView(rootView: PlateDeckView(model: model))
        let tracker = PlatePointerTrackingView(frame: CGRect(origin: .zero, size: screenFrame.size))
        tracker.onPointer = { [weak model] in model?.pointerMoved(to: $0) }
        host.frame = tracker.bounds
        host.autoresizingMask = [.width, .height]
        tracker.addSubview(host)
        panel.contentView = tracker
        panel.setFrame(screenFrame, display: true)
        // The demo plays itself and must not take the keyboard from whoever is typing.
        if takesKeyboard { panel.makeKeyAndOrderFront(nil) } else { panel.orderFrontRegardless() }
        self.panel = panel
        self.model = model
        // One turn of the run loop so the cards are laid out in the notch before they fly.
        DispatchQueue.main.async { model.deal() }
    }

    /// Asks the deck to gather and close (the island's ✕).
    func close() { model?.close() }

    func hide() {
        panel?.orderOut(nil)
        panel = nil
        model = nil
    }
}
