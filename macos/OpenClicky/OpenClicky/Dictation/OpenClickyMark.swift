//
//  OpenClickyMark.swift
//  OpenClicky
//
//  The mark, as one path: a rounded pointer (the buddy's arrow, softened) with a dot at its tip —
//  "click", said once. Drawn by the app icon, the menu bar, the orb and the window so they agree.
//

import AppKit
import SwiftUI

enum OpenClickyMark {
    /// The pointer in a unit square, y down (flipped), with the dot.
    static func bezierPath(in rect: CGRect) -> NSBezierPath {
        let path = NSBezierPath()
        let w = rect.width, h = rect.height, x0 = rect.minX, y0 = rect.minY
        func p(_ fx: CGFloat, _ fy: CGFloat) -> CGPoint { CGPoint(x: x0 + fx * w, y: y0 + fy * h) }
        // The arrow: a rounded, chunky pointer leaning right.
        path.move(to: p(0.22, 0.10))
        path.line(to: p(0.22, 0.86))
        path.line(to: p(0.42, 0.66))
        path.line(to: p(0.56, 0.96))
        path.line(to: p(0.70, 0.90))
        path.line(to: p(0.56, 0.62))
        path.line(to: p(0.84, 0.60))
        path.close()
        // The dot: the click, up and to the right of the tip.
        path.appendOval(in: CGRect(x: x0 + 0.70 * w, y: y0 + 0.08 * h, width: 0.22 * w, height: 0.22 * h))
        return path
    }

    static func shape() -> some Shape { OpenClickyMarkShape() }
}

struct OpenClickyMarkShape: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        let w = rect.width, h = rect.height, x0 = rect.minX, y0 = rect.minY
        func p(_ fx: CGFloat, _ fy: CGFloat) -> CGPoint { CGPoint(x: x0 + fx * w, y: y0 + fy * h) }
        path.move(to: p(0.22, 0.10))
        path.addLine(to: p(0.22, 0.86))
        path.addLine(to: p(0.42, 0.66))
        path.addLine(to: p(0.56, 0.96))
        path.addLine(to: p(0.70, 0.90))
        path.addLine(to: p(0.56, 0.62))
        path.addLine(to: p(0.84, 0.60))
        path.closeSubpath()
        path.addEllipse(in: CGRect(x: x0 + 0.70 * w, y: y0 + 0.08 * h, width: 0.22 * w, height: 0.22 * h))
        return path
    }
}
