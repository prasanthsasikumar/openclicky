//
//  AccessibleElementLocatorTests.swift
//  OpenClickyTests
//
//  `point_at` for a caption that appears more than once. OCR sees three identical "Edit" buttons and
//  can only rank them by distance, which the model's own 30–100 px error decides. Accessibility knows
//  the role of each control and the title of the row it sits in, so the request itself picks the row.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct AccessibleElementLocatorTests {
    private func candidate(_ title: String, role: String = "AXButton", rows: [String] = [], x: CGFloat, y: CGFloat) -> AccessibleElementCandidate {
        AccessibleElementCandidate(role: role, title: title, containerTitles: rows, center: CGPoint(x: x, y: y))
    }

    /// A settings page listing three repositories, each row carrying its own "Edit" button.
    private var repositoryRows: [AccessibleElementCandidate] {
        [
            candidate("Edit", rows: ["clicky-experiments", "Your repositories"], x: 900, y: 300),
            candidate("Edit", rows: ["openclicky", "Your repositories"], x: 900, y: 420),
            candidate("Edit", rows: ["dotfiles", "Your repositories"], x: 900, y: 540),
        ]
    }

    @Test func theRowNamedInTheRequestWinsOverTheNearerRow() {
        // The guess sits on the first row, but the user said which repository they meant.
        let match = AccessibleElementLocator.bestMatch(
            hint: "Edit", userRequest: "how do I edit the openclicky repository",
            near: CGPoint(x: 880, y: 320), in: repositoryRows, maxDistance: 400, ambiguityMargin: 128
        )
        #expect(match?.containerTitles.first == "openclicky")
    }

    @Test func withoutDistinguishingContextSimilarCandidatesStayUnresolved() {
        // Nothing in "point at the edit button" names a row, so this is the same coin toss OCR faces.
        // Unresolved goes to the Claude grounding pass rather than guessing.
        let match = AccessibleElementLocator.bestMatch(
            hint: "Edit", userRequest: "point at the edit button",
            near: CGPoint(x: 880, y: 320), in: repositoryRows, maxDistance: 400, ambiguityMargin: 128
        )
        #expect(match == nil)
    }

    @Test func distanceStillDecidesWhenTheCandidatesAreFarApart() {
        let farApart = [repositoryRows[0], candidate("Edit", rows: ["dotfiles"], x: 900, y: 1400)]
        let match = AccessibleElementLocator.bestMatch(
            hint: "Edit", userRequest: nil,
            near: CGPoint(x: 880, y: 300), in: farApart, maxDistance: 2000, ambiguityMargin: 128
        )
        #expect(match?.containerTitles.first == "clicky-experiments")
    }

    @Test func anActionableControlBeatsAStaticLabelWithTheSameCaption() {
        // The label is nearer, but it is the button's own text, not something to point at.
        let labelAndButton = [
            candidate("Edit", role: "AXStaticText", x: 505, y: 300),
            candidate("Edit", role: "AXButton", x: 560, y: 300),
        ]
        let match = AccessibleElementLocator.bestMatch(
            hint: "Edit", userRequest: nil,
            near: CGPoint(x: 500, y: 300), in: labelAndButton, maxDistance: 400, ambiguityMargin: 128
        )
        #expect(match?.role == "AXButton")
    }

    @Test func captionsThatAreNotOnScreenAndGenericWordsMatchNothing() {
        #expect(AccessibleElementLocator.bestMatch(
            hint: "Export", userRequest: nil,
            near: CGPoint(x: 880, y: 320), in: repositoryRows, maxDistance: 400, ambiguityMargin: 128
        ) == nil)
        // "button" describes a control rather than naming one.
        #expect(AccessibleElementLocator.bestMatch(
            hint: "button", userRequest: nil,
            near: CGPoint(x: 880, y: 320), in: [candidate("button", x: 900, y: 300)], maxDistance: 400, ambiguityMargin: 128
        ) == nil)
    }

    @Test func candidatesBeyondTheRadiusAreIgnored() {
        let match = AccessibleElementLocator.bestMatch(
            hint: "Edit", userRequest: nil,
            near: CGPoint(x: 100, y: 100), in: [repositoryRows[1]], maxDistance: 300, ambiguityMargin: 128
        )
        #expect(match == nil)
    }
}
