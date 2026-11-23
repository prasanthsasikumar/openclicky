//
//  PlateDeckLayout.swift
//  OpenClicky
//
//  Where the dealt cards land, as pure maths: one or two rows centred on the screen, each card a
//  little off its grid and a few degrees askew so the deck reads as cards thrown on a table, and
//  the same every time for the same count (nothing jumps when a card is taken).
//

import CoreGraphics
import SwiftUI

struct PlateDeckSlot: Equatable {
    var center: CGPoint
    var degrees: Double
}

enum PlateDeckLayout {
    static let cardWidth: CGFloat = 300
    static let minimumCardWidth: CGFloat = 230
    /// A resting card's height; a hovered one grows past it.
    static let cardHeight: CGFloat = 208
    static let columnGap: CGFloat = 34
    static let rowGap: CGFloat = 56
    static let maximumPerRow = 4

    /// How many cards each row holds: up to four in one row, otherwise two rows with the longer on top.
    static func rows(for count: Int) -> [Int] {
        guard count > 0 else { return [] }
        if count <= maximumPerRow { return [count] }
        let top = min(maximumPerRow, (count + 1) / 2)
        let capped = min(count, maximumPerRow * 2)
        return [top, capped - top]
    }

    /// The card width that lets the widest row fit the area.
    static func cardWidth(for count: Int, areaWidth: CGFloat) -> CGFloat {
        let widest = CGFloat(rows(for: count).max() ?? 1)
        let fitting = (areaWidth - 48 - (widest - 1) * columnGap) / widest
        return min(cardWidth, max(minimumCardWidth, fitting))
    }

    /// Slots in reading order, in the area's coordinates (origin top-left).
    static func slots(count: Int, in area: CGRect) -> [PlateDeckSlot] {
        let rowCounts = rows(for: count)
        guard !rowCounts.isEmpty else { return [] }
        let width = cardWidth(for: count, areaWidth: area.width)
        let totalHeight = CGFloat(rowCounts.count) * cardHeight + CGFloat(rowCounts.count - 1) * rowGap
        let firstRowCenterY = area.midY - totalHeight / 2 + cardHeight / 2
        var slots: [PlateDeckSlot] = []
        var index = 0
        for (row, rowCount) in rowCounts.enumerated() {
            let rowWidth = CGFloat(rowCount) * width + CGFloat(rowCount - 1) * columnGap
            let firstCenterX = area.midX - rowWidth / 2 + width / 2
            for column in 0..<rowCount {
                let nudge = jitter(index)
                slots.append(PlateDeckSlot(
                    center: CGPoint(x: firstCenterX + CGFloat(column) * (width + columnGap) + nudge.x,
                                    y: firstRowCenterY + CGFloat(row) * (cardHeight + rowGap) + nudge.y),
                    degrees: nudge.degrees))
                index += 1
            }
        }
        return slots
    }

    /// How far the plan slip hangs below an open card, for the pointer.
    static let slipHang: CGFloat = 132

    /// The card under the pointer. The open card also owns the slip hanging below it, so it does
    /// not close while the pointer reads the plan; otherwise the card dealt last (drawn on top)
    /// wins where two overlap.
    static func hoveredIndex(at point: CGPoint, slots: [PlateDeckSlot], cardWidth: CGFloat, openIndex: Int?) -> Int? {
        func outline(_ slot: PlateDeckSlot, isOpen: Bool) -> CGRect {
            let card = CGRect(x: slot.center.x - cardWidth / 2, y: slot.center.y - cardHeight / 2, width: cardWidth, height: cardHeight)
            return isOpen ? card.insetBy(dx: -8, dy: -8).union(card.offsetBy(dx: 0, dy: slipHang)) : card
        }
        if let openIndex, slots.indices.contains(openIndex), outline(slots[openIndex], isOpen: true).contains(point) { return openIndex }
        return slots.indices.reversed().first { outline(slots[$0], isOpen: false).contains(point) }
    }

    /// Where the bin stands: bottom centre of the deck, clear of the Dock.
    static func binCenter(in size: CGSize) -> CGPoint {
        CGPoint(x: size.width / 2, y: size.height - 158)
    }

    /// The same small offsets for the same seat, every time.
    static func jitter(_ index: Int) -> (x: CGFloat, y: CGFloat, degrees: Double) {
        let xs: [CGFloat] = [-6, 5, -3, 7, 4, -7, 2, -4]
        let ys: [CGFloat] = [12, -14, 9, -6, -10, 13, -4, 8]
        let angles: [Double] = [-5.5, 2.5, 6, -3, 4.5, -6.5, 3, -2]
        let seat = ((index % xs.count) + xs.count) % xs.count
        return (xs[seat], ys[seat], angles[seat])
    }

    /// How a card tumbles while it is still in the notch: turned well past where it will rest.
    static func notchDegrees(_ index: Int) -> Double {
        let spin: [Double] = [-70, 55, -40, 80, -60, 45, -85, 65]
        return spin[((index % spin.count) + spin.count) % spin.count]
    }
}

/// The deck's timing, on OpenClicky's motion tokens (`Motion`). Reduce motion deals and
/// gathers with a fade and no flight.
enum PlateMotion {
    /// One card leaves the notch this long after the one before it.
    static let dealStagger: TimeInterval = Motion.stagger
    static let gatherStagger: TimeInterval = 0.03
    static let gatherDuration: TimeInterval = 0.28
    /// The stamp lands and is read before the card goes.
    static let stampHold: TimeInterval = 0.5
    static let flyBackDuration: TimeInterval = Motion.structuralSlow.duration
    static let crumpleDuration: TimeInterval = 0.3
    static let throwDuration: TimeInterval = 0.5

    static func deal(reduceMotion: Bool) -> Animation { Motion.land(reduceMotion: reduceMotion) }
    static func gather(reduceMotion: Bool) -> Animation { reduceMotion ? Motion.fade : .easeIn(duration: gatherDuration) }
    static func hover(reduceMotion: Bool) -> Animation { Motion.pop(reduceMotion: reduceMotion) }
    static func settle(reduceMotion: Bool) -> Animation { Motion.settle(reduceMotion: reduceMotion) }

    /// How long until the last of `count` cards has landed.
    static func dealTime(count: Int) -> TimeInterval {
        Double(max(0, count - 1)) * dealStagger + 0.7
    }

    static func gatherTime(count: Int) -> TimeInterval {
        Double(max(0, count - 1)) * gatherStagger + gatherDuration
    }

    /// How high a crumpled card is thrown on its way to the bin.
    static func throwLift(distance: CGFloat) -> CGFloat {
        min(max(distance * 0.25, 40), 140)
    }
}
