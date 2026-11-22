//
//  PlateDoodle.swift
//  OpenClicky
//
//  A small pen drawing for each kind of job, with a pastel wash behind it. The pen is a little
//  unsteady, in the same way every time for the same card (the wobble comes from the card's id),
//  and every line is drawn twice, as a quick sketch is.
//

import SwiftUI

/// Small repeatable offsets from a seed.
struct PlateWobble {
    private var state: UInt64

    init(seed: String) {
        // FNV-1a: `hashValue` changes between launches.
        var hash: UInt64 = 1469598103934665603
        for byte in seed.utf8 { hash = (hash ^ UInt64(byte)) &* 1099511628211 }
        state = hash | 1
    }

    /// The next value in -1…1.
    mutating func next() -> CGFloat {
        state ^= state << 13
        state ^= state >> 7
        state ^= state << 17
        return CGFloat(state % 20001) / 10000 - 1
    }

    mutating func nudge(_ point: CGPoint, by amount: CGFloat) -> CGPoint {
        CGPoint(x: point.x + next() * amount, y: point.y + next() * amount)
    }
}

enum PlateDoodleGeometry {
    /// The strokes of a kind in a unit square: each a list of points, closed or open.
    static func strokes(for kind: PlateSuggestion.Kind) -> [(points: [CGPoint], closed: Bool, washed: Bool)] {
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x, y: y) }
        func ring(_ cx: CGFloat, _ cy: CGFloat, _ r: CGFloat) -> [CGPoint] {
            (0..<18).map { p(cx + r * cos(CGFloat($0) / 18 * 2 * .pi), cy + r * sin(CGFloat($0) / 18 * 2 * .pi)) }
        }
        switch kind {
        case .draft:
            return [([p(0.24, 0.1), p(0.8, 0.16), p(0.74, 0.9), p(0.18, 0.84)], true, true),
                    ([p(0.36, 0.34), p(0.66, 0.37)], false, false), ([p(0.34, 0.5), p(0.64, 0.53)], false, false), ([p(0.33, 0.66), p(0.52, 0.68)], false, false)]
        case .research:
            return [(ring(0.42, 0.4, 0.28), true, true), ([p(0.62, 0.61), p(0.9, 0.9)], false, false), ([p(0.64, 0.6), p(0.92, 0.88)], false, false)]
        case .watch:
            return [(ring(0.5, 0.5, 0.36), true, true), ([p(0.5, 0.28), p(0.5, 0.5), p(0.66, 0.6)], false, false)]
        case .list:
            return [([p(0.16, 0.18), p(0.3, 0.18), p(0.3, 0.32), p(0.16, 0.32)], true, true), ([p(0.4, 0.25), p(0.86, 0.27)], false, false),
                    ([p(0.16, 0.44), p(0.3, 0.44), p(0.3, 0.58), p(0.16, 0.58)], true, true), ([p(0.4, 0.51), p(0.8, 0.52)], false, false),
                    ([p(0.16, 0.7), p(0.3, 0.7), p(0.3, 0.84), p(0.16, 0.84)], true, true), ([p(0.4, 0.77), p(0.7, 0.78)], false, false),
                    ([p(0.18, 0.24), p(0.22, 0.29), p(0.3, 0.16)], false, false)]
        case .numbers:
            return [([p(0.14, 0.12), p(0.14, 0.88), p(0.9, 0.88)], false, false),
                    ([p(0.24, 0.88), p(0.24, 0.6), p(0.38, 0.6), p(0.38, 0.88)], true, true),
                    ([p(0.46, 0.88), p(0.46, 0.4), p(0.6, 0.4), p(0.6, 0.88)], true, true),
                    ([p(0.68, 0.88), p(0.68, 0.22), p(0.82, 0.22), p(0.82, 0.88)], true, true)]
        case .message:
            return [([p(0.12, 0.24), p(0.88, 0.24), p(0.88, 0.78), p(0.12, 0.78)], true, true), ([p(0.12, 0.26), p(0.5, 0.56), p(0.88, 0.26)], false, false)]
        case .schedule:
            return [([p(0.14, 0.2), p(0.86, 0.2), p(0.86, 0.86), p(0.14, 0.86)], true, true), ([p(0.14, 0.38), p(0.86, 0.38)], false, false),
                    ([p(0.32, 0.1), p(0.32, 0.26)], false, false), ([p(0.68, 0.1), p(0.68, 0.26)], false, false),
                    ([p(0.3, 0.6), p(0.44, 0.72), p(0.7, 0.5)], false, false)]
        case .code:
            return [([p(0.36, 0.26), p(0.12, 0.5), p(0.36, 0.74)], false, false), ([p(0.64, 0.26), p(0.88, 0.5), p(0.64, 0.74)], false, false),
                    ([p(0.56, 0.16), p(0.44, 0.84)], false, true)]
        }
    }

    static func wash(for kind: PlateSuggestion.Kind) -> Color {
        switch kind {
        case .draft, .list: return Color(hex: "#FBE9B7")
        case .research, .schedule: return Color(hex: "#D5EEDD")
        case .watch, .message: return Color(hex: "#F6D6E8")
        case .numbers, .code: return Color(hex: "#D6E4FB")
        }
    }
}

struct PlateDoodle: View {
    let kind: PlateSuggestion.Kind
    var seed: String = ""

    var body: some View {
        Canvas { context, size in
            var wobble = PlateWobble(seed: seed + kind.rawValue)
            let ink = GraphicsContext.Shading.color(PlatePaper.blue)
            let amount = size.width * 0.018
            func path(_ points: [CGPoint], closed: Bool, shift: CGPoint = .zero) -> Path {
                var path = Path()
                for (index, unit) in points.enumerated() {
                    let point = wobble.nudge(CGPoint(x: unit.x * size.width + shift.x, y: unit.y * size.height + shift.y), by: amount)
                    if index == 0 { path.move(to: point) } else { path.addLine(to: point) }
                }
                if closed { path.closeSubpath() }
                return path
            }
            for stroke in PlateDoodleGeometry.strokes(for: kind) {
                if stroke.washed && stroke.closed {
                    // The wash sits a little off the line, like a quick marker fill.
                    context.fill(path(stroke.points, closed: true, shift: CGPoint(x: 1.5, y: 1.5)), with: .color(PlateDoodleGeometry.wash(for: kind)))
                }
                context.stroke(path(stroke.points, closed: stroke.closed), with: ink, style: StrokeStyle(lineWidth: 1.9, lineCap: .round, lineJoin: .round))
                context.stroke(path(stroke.points, closed: stroke.closed), with: .color(PlatePaper.blue.opacity(0.45)), style: StrokeStyle(lineWidth: 1.1, lineCap: .round, lineJoin: .round))
            }
        }
    }
}

/// A crumpled sheet: an uneven round outline with a few creases.
struct PlatePaperBall: View {
    var seed: String

    var body: some View {
        Canvas { context, size in
            var wobble = PlateWobble(seed: seed)
            let center = CGPoint(x: size.width / 2, y: size.height / 2)
            let radius = min(size.width, size.height) / 2 - 2
            var outline = Path()
            var corners: [CGPoint] = []
            for index in 0..<11 {
                let angle = CGFloat(index) / 11 * 2 * .pi
                let reach = radius * (0.84 + 0.16 * wobble.next())
                let point = CGPoint(x: center.x + cos(angle) * reach, y: center.y + sin(angle) * reach)
                corners.append(point)
                if index == 0 { outline.move(to: point) } else { outline.addLine(to: point) }
            }
            outline.closeSubpath()
            context.fill(outline, with: .color(PlatePaper.paper))
            for index in stride(from: 0, to: corners.count, by: 2) {
                var crease = Path()
                crease.move(to: corners[index])
                crease.addLine(to: wobble.nudge(center, by: radius * 0.35))
                context.stroke(crease, with: .color(PlatePaper.ink3.opacity(0.55)), lineWidth: 1)
            }
            context.stroke(outline, with: .color(PlatePaper.ink2.opacity(0.7)), style: StrokeStyle(lineWidth: 1.2, lineJoin: .round))
        }
    }
}

/// A wire waste-paper bin, drawn in two parts so a ball can fall between the back rim and the front.
struct PlateBin: View {
    enum Part { case back, front }
    let part: Part

    var body: some View {
        Canvas { context, size in
            let stroke = StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round)
            let ink = GraphicsContext.Shading.color(Color.white.opacity(0.85))
            let rim = CGRect(x: size.width * 0.06, y: size.height * 0.04, width: size.width * 0.88, height: size.height * 0.2)
            switch part {
            case .back:
                context.fill(Path(ellipseIn: rim), with: .color(Color.white.opacity(0.08)))
                var back = Path()
                back.addArc(center: CGPoint(x: rim.midX, y: rim.midY), radius: rim.width / 2, startAngle: .degrees(180), endAngle: .degrees(360), clockwise: false,
                            transform: CGAffineTransform(translationX: 0, y: rim.midY).scaledBy(x: 1, y: rim.height / rim.width).translatedBy(x: 0, y: -rim.midY))
                context.stroke(back, with: ink, style: stroke)
            case .front:
                var body = Path()
                body.move(to: CGPoint(x: rim.minX, y: rim.midY))
                body.addLine(to: CGPoint(x: size.width * 0.2, y: size.height * 0.94))
                body.addQuadCurve(to: CGPoint(x: size.width * 0.8, y: size.height * 0.94), control: CGPoint(x: size.width * 0.5, y: size.height * 1.02))
                body.addLine(to: CGPoint(x: rim.maxX, y: rim.midY))
                body.addQuadCurve(to: CGPoint(x: rim.minX, y: rim.midY), control: CGPoint(x: rim.midX, y: rim.maxY + rim.height * 0.5))
                context.fill(body, with: .color(Color.black.opacity(0.55)))
                context.stroke(body, with: ink, style: stroke)
                for fraction in [0.3, 0.5, 0.7] as [CGFloat] {
                    var slat = Path()
                    slat.move(to: CGPoint(x: rim.minX + rim.width * fraction, y: rim.maxY + 2))
                    slat.addLine(to: CGPoint(x: size.width * (0.2 + 0.6 * fraction), y: size.height * 0.95))
                    context.stroke(slat, with: .color(Color.white.opacity(0.4)), lineWidth: 1.2)
                }
            }
        }
    }
}
