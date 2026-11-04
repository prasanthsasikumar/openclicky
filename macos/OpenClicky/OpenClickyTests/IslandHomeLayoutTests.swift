//
//  IslandHomeLayoutTests.swift
//  OpenClickyTests
//
//  Home's columns and the island's size for them.
//

import CoreGraphics
import Testing
@testable import OpenClicky

struct IslandHomeLayoutTests {
    @Test func nothingOnFallsBackToTheShortcutsRow() {
        let layout = IslandHomeLayout(mediaOn: false, calendarOn: false, weatherOn: false, systemOn: false)
        #expect(layout.isEmpty)
        #expect(layout.width == nil)
        #expect(layout.height == NotchPanelBody.shortcutsHeight)
    }

    @Test func columnsSitInOrderAndTilesShareOneGroup() {
        let layout = IslandHomeLayout(mediaOn: true, calendarOn: true, weatherOn: true, systemOn: true)
        #expect(layout.columns == [.media, .calendar, .weather, .system])
        #expect(layout.groups == [[.media], [.calendar], [.weather, .system]])
    }

    /// The mirror takes now playing's place on the left while it is open, the same width, so the
    /// island does not grow; with now playing off it is simply the first column.
    @Test func theMirrorTakesNowPlayingsPlaceWhileItIsOpen() {
        let withMusic = IslandHomeLayout(mediaOn: true, calendarOn: true, weatherOn: false, systemOn: false, mirrorOpen: true)
        #expect(withMusic.columns == [.mirror, .calendar])
        #expect(withMusic.width == IslandHomeLayout(mediaOn: true, calendarOn: true, weatherOn: false, systemOn: false).width)
        let withoutMusic = IslandHomeLayout(mediaOn: false, calendarOn: true, weatherOn: true, systemOn: false, mirrorOpen: true)
        #expect(withoutMusic.columns == [.mirror, .calendar, .weather])
        #expect(IslandHomeLayout.width(of: [.mirror]) == IslandDash.mediaWidth)
        let closed = IslandHomeLayout(mediaOn: true, calendarOn: true, weatherOn: false, systemOn: false, mirrorOpen: false)
        #expect(closed.columns == [.media, .calendar])
        // Nothing else on: the mirror alone still makes a row, not the shortcuts.
        let alone = IslandHomeLayout(mediaOn: false, calendarOn: false, weatherOn: false, systemOn: false, mirrorOpen: true)
        #expect(alone.columns == [.mirror])
        #expect(!alone.isEmpty)
    }

    @Test func theWidthIsTheColumnsTheGuttersAndThePadding() {
        // 300 + 268 + (2 × 118 + 8), two gutters of 16 + 1 + 16, 20 each side.
        let everything = IslandHomeLayout(mediaOn: true, calendarOn: true, weatherOn: true, systemOn: true)
        let expected: CGFloat = 918
        #expect(everything.width == expected)
        let calendarOnly = IslandHomeLayout(mediaOn: false, calendarOn: true, weatherOn: false, systemOn: false)
        let calendarExpected: CGFloat = 308
        #expect(calendarOnly.width == calendarExpected)
        let heightExpected: CGFloat = 134
        #expect(everything.height == heightExpected)
    }
}
