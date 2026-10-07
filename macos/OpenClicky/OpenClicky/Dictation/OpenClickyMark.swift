//
//  OpenClickyMark.swift
//  OpenClicky
//
//  The mark: a ring whose top-right corner is drawn square, like a pin turned on its side, with
//  the pointer inside it aiming up and to the left. One path, filled even-odd so the ring stays
//  open. Drawn by the menu bar and the window, and the same proportions are in the app icon
//  (scripts/make-app-icon.py) so they agree.
//

import AppKit
import SwiftUI

enum OpenClickyMark {
    // Proportions in a unit square, y down, measured off the icon artwork.
    static let innerRadius: CGFloat = 0.4125
    static let arrowTip = CGPoint(x: 0.3125, y: 0.3125)
    static let arrowRight = CGPoint(x: 0.7475, y: 0.475)
    static let arrowNotch = CGPoint(x: 0.505, y: 0.52)
    static let arrowBottom = CGPoint(x: 0.4775, y: 0.7525)

    /// The mark in `rect` for a flipped (y down) context. Fill with the even-odd rule.
    static func bezierPath(in rect: CGRect) -> NSBezierPath {
        let path = NSBezierPath()
        path.windingRule = .evenOdd
        path.append(NSBezierPath(cgPath: OpenClickyMarkShape().path(in: rect).cgPath))
        return path
    }
}

struct OpenClickyMarkShape: Shape {
    func path(in rect: CGRect) -> Path {
        let side = min(rect.width, rect.height)
        let x0 = rect.midX - side / 2, y0 = rect.midY - side / 2
        func p(_ point: CGPoint) -> CGPoint { CGPoint(x: x0 + point.x * side, y: y0 + point.y * side) }
        let center = CGPoint(x: x0 + side / 2, y: y0 + side / 2)
        var path = Path()
        // The outer edge: three quarters of a circle, then the square top-right corner.
        path.move(to: CGPoint(x: center.x + side / 2, y: center.y))
        path.addArc(center: center, radius: side / 2, startAngle: .degrees(0), endAngle: .degrees(270), clockwise: false)
        path.addLine(to: CGPoint(x: x0 + side, y: y0))
        path.closeSubpath()
        // The hole.
        let inner = OpenClickyMark.innerRadius * side
        path.addEllipse(in: CGRect(x: center.x - inner, y: center.y - inner, width: inner * 2, height: inner * 2))
        // The pointer.
        path.move(to: p(OpenClickyMark.arrowTip))
        path.addLine(to: p(OpenClickyMark.arrowRight))
        path.addLine(to: p(OpenClickyMark.arrowNotch))
        path.addLine(to: p(OpenClickyMark.arrowBottom))
        path.closeSubpath()
        return path
    }
}
