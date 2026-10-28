//
//  CursorFlightPlannerTests.swift
//  OpenClickyTests
//
//  Cross-display flights: back to a mouse that moved from the external monitor to the built-in one.
//

import CoreGraphics
import Testing
@testable import OpenClicky

struct CursorFlightPlannerTests {
    /// This Mac's layout: the external 1920 × 1080 sits above the built-in 1512 × 982 (notch screen).
    private let builtInFrame = CGRect(x: 0, y: 0, width: 1512, height: 982)
    private let externalFrame = CGRect(x: -173, y: 982, width: 1920, height: 1080)
    private let mouseOnTheBuiltInDisplay = CGPoint(x: 756, y: 944)

    @Test func flightOnTheDestinationDisplayIsOneLeg() {
        let leg = CursorFlightPlanner.firstLeg(purpose: .toMouse, fromScreenFrame: builtInFrame, destination: mouseOnTheBuiltInDisplay, destinationScreenFrame: builtInFrame, sequence: 1)
        #expect(leg.isFinalLeg)
        #expect(leg.startScreenLocation == nil)
        #expect(leg.endScreenLocation == mouseOnTheBuiltInDisplay)
        #expect(leg.screenFrame == builtInFrame)
    }

    @Test func flyingFromTheExternalDisplayExitsAtItsBottomEdge() {
        let firstLeg = CursorFlightPlanner.firstLeg(purpose: .toMouse, fromScreenFrame: externalFrame, destination: mouseOnTheBuiltInDisplay, destinationScreenFrame: builtInFrame, sequence: 1)
        #expect(!firstLeg.isFinalLeg)
        #expect(firstLeg.screenFrame == externalFrame)
        #expect(firstLeg.endScreenLocation == CGPoint(x: 756, y: 982))

        let finalLeg = CursorFlightPlanner.finalLeg(purpose: .toMouse, afterExitPoint: firstLeg.endScreenLocation, destination: mouseOnTheBuiltInDisplay, destinationScreenFrame: builtInFrame, sequence: 2)
        #expect(finalLeg.isFinalLeg)
        #expect(finalLeg.screenFrame == builtInFrame)
        // Enters at the top edge of the built-in display, right under where it left the external one.
        #expect(finalLeg.startScreenLocation == CGPoint(x: 756, y: 982))
        #expect(finalLeg.endScreenLocation == mouseOnTheBuiltInDisplay)
    }

    @Test func exitPointIsClampedToTheDisplayWhenTheDestinationIsOffToTheSide() {
        let rightDisplay = CGRect(x: 1512, y: 0, width: 1920, height: 1080)
        let leg = CursorFlightPlanner.firstLeg(purpose: .toMouse, fromScreenFrame: builtInFrame, destination: CGPoint(x: 2500, y: 1050), destinationScreenFrame: rightDisplay, sequence: 3)
        #expect(!leg.isFinalLeg)
        #expect(leg.endScreenLocation == CGPoint(x: 1512, y: 982))
    }
}
