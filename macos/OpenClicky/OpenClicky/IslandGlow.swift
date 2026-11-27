//
//  IslandGlow.swift
//  OpenClicky
//
//  The light inside the island's bottom corners while Clicky is listening, thinking, speaking or
//  has an agent working, and the sweep that runs along its bottom edge while an agent works. The
//  light stays inside the black shape (`IslandSurface` clips it to the outline): light outside it
//  was cut off by the island's window 16 pt out and showed as a square of colour. Both are Core Animation layers: the slow pulse and the sweep run in the
//  render server and cost the app nothing while they loop. They exist only while something is
//  going on (nothing animates on an idle island), and under Reduce Motion they hold still.
//

import AppKit
import SwiftUI

/// What the glow says, by colour.
enum IslandGlowTone: Equatable {
    case listening, thinking, speaking, working

    /// Nil when nothing is going on: no glow, no layer, no animation.
    static func tone(voiceState: CompanionVoiceState, isAgentWorking: Bool) -> IslandGlowTone? {
        switch voiceState {
        case .listening: return .listening
        case .responding: return .speaking
        case .processing: return isAgentWorking ? .working : .thinking
        case .idle: return isAgentWorking ? .working : nil
        }
    }

    var hex: String {
        switch self {
        case .listening: return "#FF5A3C"
        case .thinking: return "#FFB224"
        case .speaking: return "#34C77B"
        case .working: return "#5B8CFF"
        }
    }

    /// One full breath of the pulse, in seconds. Listening breathes a little faster.
    var pulseSeconds: Double { self == .listening ? 1.6 : 2.4 }
}

enum IslandGlowMetrics {
    /// A corner light's radius for an island this tall.
    static func blobRadius(islandHeight: CGFloat) -> CGFloat { min(max(0, islandHeight) * 1.1, 38) }
    /// How far in from the island's side a corner light's centre sits (it sits on the bottom edge).
    static let blobInset: CGFloat = 10
    /// How tall the faint band along the inside of the bottom edge is.
    static func bandHeight(islandHeight: CGFloat) -> CGFloat { min(max(0, islandHeight) * 0.4, 12) }
    static let restOpacity: Float = 0.55
    static let peakOpacity: Float = 0.95
    /// The sweep crosses in this long, then rests until the cycle ends.
    static let sweepSeconds: Double = 1.1
    static let sweepCycleSeconds: Double = 1.6
}

/// Two soft lights inside the island's bottom corners and a faint band between them. Give it the
/// island's own frame and clip it to the island's outline.
struct IslandGlowView: NSViewRepresentable {
    let tone: IslandGlowTone
    let reduceMotion: Bool

    func makeNSView(context: Context) -> IslandGlowLayerView { IslandGlowLayerView() }

    func updateNSView(_ view: IslandGlowLayerView, context: Context) {
        view.apply(tone: tone, reduceMotion: reduceMotion)
    }
}

final class IslandGlowLayerView: NSView {
    private let left = CAGradientLayer()
    private let right = CAGradientLayer()
    private let band = CAGradientLayer()
    private var tone: IslandGlowTone?
    private var isStill = false

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.masksToBounds = true
        for blob in [left, right] {
            blob.type = .radial
            blob.startPoint = CGPoint(x: 0.5, y: 0.5)
            blob.endPoint = CGPoint(x: 1, y: 1)
            layer?.addSublayer(blob)
        }
        // Brightest on the bottom edge, gone a few points up (AppKit layers: y = 0 is the bottom).
        band.startPoint = CGPoint(x: 0.5, y: 0)
        band.endPoint = CGPoint(x: 0.5, y: 1)
        layer?.addSublayer(band)
    }

    required init?(coder: NSCoder) { nil }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        let radius = IslandGlowMetrics.blobRadius(islandHeight: bounds.height)
        let inset = IslandGlowMetrics.blobInset
        // AppKit layers have their origin at the bottom-left: each light is centred on the bottom edge.
        left.frame = CGRect(x: inset - radius, y: -radius, width: radius * 2, height: radius * 2)
        right.frame = CGRect(x: bounds.width - inset - radius, y: -radius, width: radius * 2, height: radius * 2)
        band.frame = CGRect(x: 0, y: 0, width: bounds.width, height: IslandGlowMetrics.bandHeight(islandHeight: bounds.height))
        CATransaction.commit()
    }

    func apply(tone: IslandGlowTone, reduceMotion: Bool) {
        guard tone != self.tone || reduceMotion != isStill else { return }
        self.tone = tone
        isStill = reduceMotion
        let color = NSColor(Color(hex: tone.hex))
        CATransaction.begin()
        // The colour changes with the island's own timing when the state changes.
        CATransaction.setAnimationDuration(Motion.structural.duration)
        CATransaction.setAnimationTimingFunction(CAMediaTimingFunction(controlPoints: 0.23, 1, 0.32, 1))
        for blob in [left, right] {
            blob.colors = [color.withAlphaComponent(0.9).cgColor, color.withAlphaComponent(0.25).cgColor, color.withAlphaComponent(0).cgColor]
            blob.locations = [0, 0.45, 1]
        }
        band.colors = [color.withAlphaComponent(0.35).cgColor, color.withAlphaComponent(0).cgColor]
        CATransaction.commit()

        layer?.removeAnimation(forKey: "pulse")
        if reduceMotion {
            layer?.opacity = IslandGlowMetrics.peakOpacity * 0.8
        } else {
            layer?.opacity = IslandGlowMetrics.restOpacity
            let pulse = CABasicAnimation(keyPath: "opacity")
            pulse.fromValue = IslandGlowMetrics.restOpacity
            pulse.toValue = IslandGlowMetrics.peakOpacity
            pulse.duration = tone.pulseSeconds / 2
            pulse.autoreverses = true
            pulse.repeatCount = .infinity
            pulse.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            layer?.add(pulse, forKey: "pulse")
        }
    }
}

/// A highlight that crosses the island's bottom edge again and again while an agent works.
struct IslandWorkingSweep: NSViewRepresentable {
    let reduceMotion: Bool

    func makeNSView(context: Context) -> IslandSweepLayerView { IslandSweepLayerView() }

    func updateNSView(_ view: IslandSweepLayerView, context: Context) {
        view.setStill(reduceMotion)
    }
}

final class IslandSweepLayerView: NSView {
    private let highlight = CAGradientLayer()
    private var isStill: Bool?

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.masksToBounds = true
        highlight.startPoint = CGPoint(x: 0, y: 0.5)
        highlight.endPoint = CGPoint(x: 1, y: 0.5)
        highlight.colors = [NSColor.white.withAlphaComponent(0).cgColor, NSColor.white.withAlphaComponent(0.7).cgColor, NSColor.white.withAlphaComponent(0).cgColor]
        layer?.addSublayer(highlight)
    }

    required init?(coder: NSCoder) { nil }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func layout() {
        super.layout()
        restart()
    }

    func setStill(_ still: Bool) {
        guard still != isStill else { return }
        isStill = still
        restart()
    }

    private func restart() {
        highlight.removeAnimation(forKey: "sweep")
        let width = max(40, bounds.width * 0.3)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        highlight.frame = CGRect(x: (bounds.width - width) / 2, y: 0, width: width, height: bounds.height)
        highlight.opacity = isStill == true ? 0.5 : 1
        CATransaction.commit()
        guard isStill != true, bounds.width > 0 else { return }
        let travel = CABasicAnimation(keyPath: "position.x")
        travel.fromValue = -width / 2
        travel.toValue = bounds.width + width / 2
        travel.duration = IslandGlowMetrics.sweepSeconds
        travel.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
        travel.fillMode = .both
        let cycle = CAAnimationGroup()
        cycle.animations = [travel]
        cycle.duration = IslandGlowMetrics.sweepCycleSeconds
        cycle.repeatCount = .infinity
        cycle.beginTime = CACurrentMediaTime() + 0.3
        highlight.add(cycle, forKey: "sweep")
        // Parked off the edge between crossings.
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        highlight.position.x = -width
        CATransaction.commit()
    }
}
