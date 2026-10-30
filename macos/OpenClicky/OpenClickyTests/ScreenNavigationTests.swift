//
//  ScreenNavigationTests.swift
//  OpenClickyTests
//
//  The pure parts of moving around by voice: how far a scroll goes, which clicks are left to the
//  person, the coordinate flip for events, and the Spotlight query a spoken name becomes.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct ScreenNavigationTests {

    @Test func aPageIsMostOfTheWindowAndDownIsNegative() {
        let viewport = CGSize(width: 1200, height: 800)
        let down = ScreenNavigation.scrollSteps(direction: .down, amount: .page, viewport: viewport)
        #expect(down.count == 1)
        #expect(down[0].vertical == -640)
        #expect(down[0].horizontal == 0)
        #expect(ScreenNavigation.scrollSteps(direction: .up, amount: .little, viewport: viewport)[0].vertical == 200)
        #expect(ScreenNavigation.scrollSteps(direction: .right, amount: .page, viewport: viewport)[0].horizontal == -960)
        // "To the bottom" is many big steps, enough for any page.
        let toTheBottom = ScreenNavigation.scrollSteps(direction: .down, amount: .end, viewport: viewport)
        #expect(toTheBottom.count == 10)
        #expect(toTheBottom.allSatisfy { $0.vertical < -1000 })
    }

    /// "Scroll down slowly" glides a page; "keep scrolling" moves a little at a time until stopped.
    @Test func aSlowScrollGlidesOnePageAndAContinuousOneTicks() {
        let viewport = CGSize(width: 1200, height: 800)
        let slow = ScreenNavigation.scrollSteps(direction: .down, amount: .slow, viewport: viewport)
        #expect(slow.count == ScreenNavigation.slowGlideStepCount)
        #expect(slow.map(\.vertical).reduce(0, +) == -640)
        #expect(slow.allSatisfy { $0.vertical <= 0 && $0.horizontal == 0 })
        // Gentle at both ends, fastest in the middle.
        #expect(abs(slow[0].vertical) < abs(slow[22].vertical))
        #expect(abs(slow[44].vertical) < abs(slow[22].vertical))
        #expect(ScreenNavigation.scrollSteps(direction: .up, amount: .continuous, viewport: viewport).map(\.vertical) == [ScreenNavigation.continuousStepPixels])
        #expect(ScreenNavigation.glideSteps(distance: 7, count: 3).reduce(0, +) == 7)
    }

    @Test func aContinuousScrollEndsWhenThePersonTakesOver() {
        let point = CGPoint(x: 500, y: 400)
        #expect(!ScreenNavigation.continuousScrollShouldStop(frontPID: 7, scrollingPID: 7, pointer: CGPoint(x: 510, y: 405), scrollPoint: point))
        #expect(ScreenNavigation.continuousScrollShouldStop(frontPID: 7, scrollingPID: 7, pointer: CGPoint(x: 700, y: 400), scrollPoint: point))
        #expect(ScreenNavigation.continuousScrollShouldStop(frontPID: 8, scrollingPID: 7, pointer: point, scrollPoint: point))
        #expect(ScreenNavigation.continuousScrollShouldStop(frontPID: nil, scrollingPID: 7, pointer: point, scrollPoint: point))
        // Nothing settled to look at while it moves, so the model is told to say so and wait for "stop".
        let continuous = MacActionOutcome.scrolled(.down, .continuous)
        #expect(!continuous.thenLooksAtScreen)
        #expect(continuous.toolOutput.contains("stop_scrolling"))
        #expect(MacActionOutcome.scrolled(.down, .slow).thenLooksAtScreen)
    }

    @Test func clicksThatCommitSomethingAreLeftToThePerson() {
        #expect(ScreenNavigation.clickIsRisky(label: "send button", text: "Send"))
        #expect(ScreenNavigation.clickIsRisky(label: "checkout", text: "Proceed to checkout"))
        #expect(ScreenNavigation.clickIsRisky(label: "button", text: "Place order"))
        #expect(ScreenNavigation.clickIsRisky(label: "account", text: "Delete account"))
        #expect(ScreenNavigation.clickIsRisky(label: "menu", text: "Sign out"))
        // Whole words only: a link to someone's posts or a sort order is fine.
        #expect(!ScreenNavigation.clickIsRisky(label: "first link", text: "Used Nissan R34 Skyline for Sale"))
        #expect(!ScreenNavigation.clickIsRisky(label: "posts tab", text: "Posts"))
        #expect(!ScreenNavigation.clickIsRisky(label: "sign in", text: "Sign in"))
        #expect(!ScreenNavigation.clickIsRisky(label: "orders", text: "Your Orders"))
        // A cookie banner has to be clickable to browse by voice at all (2026-10-29).
        #expect(!ScreenNavigation.clickIsRisky(label: "Accept All button", text: "Accept All"))
    }

    /// "Scroll up" on a page that could not move was answered "Scrolled up." twice (2026-10-29).
    @Test func aScrollIsLookedAtAtOnceAndAMissingPlayerPointsAtTheVideo() {
        let scrolled = MacActionOutcome.scrolled(.up, .page)
        #expect(scrolled.thenLooksAtScreen)
        #expect(scrolled.looksWithoutWaitingForAPage)
        #expect(scrolled.toolOutput.contains("didn't move"))
        #expect(!MacActionOutcome.searched(query: "x", site: .web).looksWithoutWaitingForAPage)
        // "Stop the video" went to the music players and stopped there.
        let nothingPlaying = MacActionOutcome.failed("nothing is playing")
        #expect(nothingPlaying.toolOutput.contains("call click on that video"))
        #expect(nothingPlaying.spokenSentence == "That didn't work: nothing is playing.")
    }

    @Test func videosAndPicturesAreAimedAtTheImageNotTheCaption() {
        #expect(ScreenNavigation.namesAPicture("first video"))
        #expect(ScreenNavigation.namesAPicture("video thumbnail"))
        #expect(ScreenNavigation.namesAPicture("play button"))
        #expect(!ScreenNavigation.namesAPicture("first link"))
        #expect(!ScreenNavigation.namesAPicture("Accept All button"))
    }

    @Test func appKitPointsFlipToEventCoordinates() {
        #expect(ScreenNavigation.eventPoint(fromAppKit: CGPoint(x: 371, y: 635), primaryScreenHeight: 982) == CGPoint(x: 371, y: 347))
    }

    @Test func theTopmostOrdinaryWindowOwnsAClick() {
        let windows = [
            OnScreenWindow(ownerPID: 1, bounds: CGRect(x: 0, y: 0, width: 2000, height: 40), layer: 25),
            OnScreenWindow(ownerPID: 2, bounds: CGRect(x: 100, y: 100, width: 400, height: 400)),
            OnScreenWindow(ownerPID: 3, bounds: CGRect(x: 0, y: 0, width: 1000, height: 1000)),
        ]
        #expect(ScreenNavigation.ownerOfWindow(at: CGPoint(x: 200, y: 200), in: windows) == 2)
        #expect(ScreenNavigation.ownerOfWindow(at: CGPoint(x: 20, y: 20), in: windows) == 3)
        #expect(ScreenNavigation.ownerOfWindow(at: CGPoint(x: 5000, y: 20), in: windows) == nil)
    }

    @Test func aSpokenNameBecomesAQueryNothingSaidCanReshape() {
        #expect(ScreenNavigation.spotlightQuery(for: "Tax PDF") == "kMDItemFSName == \"*tax*\"cd && kMDItemFSName == \"*pdf*\"cd")
        #expect(ScreenNavigation.spotlightQuery(for: "a\" || kMDItemFSName == \"*") == "kMDItemFSName == \"*a*\"cd && kMDItemFSName == \"*kmditemfsname*\"cd")
        #expect(ScreenNavigation.spotlightQuery(for: "  !! ") == nil)
    }

    @Test func foundFilesSkipLibrariesAndPutExactNamesFirst() {
        let found = ScreenNavigation.rankFoundFiles([
            "/Users/tester/Library/Caches/budget.json",
            "/Users/tester/Documents/old budget.xlsx",
            "/Users/tester/.hidden/budget.txt",
            "/Users/tester/Desktop/Budget.numbers",
            "/Applications/Budget.app/Contents/budget.plist",
            "/Users/tester/Documents/budget 2026.xlsx",
        ], query: "budget")
        #expect(found.map(\.lastPathComponent) == ["Budget.numbers", "budget 2026.xlsx", "old budget.xlsx"])
        // A word that starts a word beats the same letters inside one.
        let riti = ScreenNavigation.rankFoundFiles(["/Users/tester/Docs/WritingPageView.cs", "/Users/tester/Desktop/Riti"], query: "riti")
        #expect(riti.map(\.lastPathComponent) == ["Riti", "WritingPageView.cs"])
        let tax = ScreenNavigation.rankFoundFiles(["/Users/tester/syntax notes.txt", "/Users/tester/2025 Tax return.pdf"], query: "tax return")
        #expect(tax.map(\.lastPathComponent) == ["2025 Tax return.pdf", "syntax notes.txt"])
    }
}
