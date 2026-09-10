//
//  BrowserTabLocatorTests.swift
//  OpenClickyTests
//
//  "Where is the Reddit tab?": a 16 px favicon is not recognizable in a screenshot (the Realtime
//  model, Claude and OCR all took the "reditt" menu bar item instead), so browser tabs are resolved
//  from the browser itself: the tab list (title + URL) picks the tab, Accessibility gives its frame.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct BrowserTabLocatorTests {
    private let tabs = [
        BrowserTab(title: "changelog — OpenClicky", url: URL(string: "https://github.com/prasanthsasikumar/openclicky/commits")),
        BrowserTab(title: "Made a small ambient music website : r/VibeCodeDevs", url: URL(string: "https://www.reddit.com/r/VibeCodeDevs/comments/1w8wc0e/")),
        BrowserTab(title: "Usage - OpenAI API", url: URL(string: "https://platform.openai.com/usage")),
        BrowserTab(title: "GitHub", url: URL(string: "https://github.com/")),
        BrowserTab(title: "New Tab", url: URL(string: "chrome://newtab/")),
    ]

    @Test func onlyRequestsAboutATabAreHandled() {
        #expect(BrowserTabLocator.mentionsTab(["Reddit tab", "", "Where is the reddit tab? Point at it."]))
        #expect(BrowserTabLocator.mentionsTab(["browser tab", "", nil]))
        #expect(!BrowserTabLocator.mentionsTab(["New button", "New", "where is the button to create a new repository"]))
        // "table" is not "tab".
        #expect(!BrowserTabLocator.mentionsTab(["pricing table", "", "show me the table"]))
    }

    @Test func siteNameInTheHostWinsOverTitles() {
        // "Made …" never says reddit; the URL does.
        #expect(BrowserTabLocator.bestMatch(for: ["Reddit tab", "reddit", "Where is the Reddit tab? Point at it."], in: tabs) == 1)
        #expect(BrowserTabLocator.bestMatch(for: ["OpenAI usage tab", "", "where's my openai usage tab"], in: tabs) == 2)
    }

    @Test func titleWordsMatchWhenTheHostDoesNot() {
        #expect(BrowserTabLocator.bestMatch(for: ["changelog tab", "changelog", nil], in: tabs) == 0)
    }

    @Test func genericWordsNeverPickATab() {
        // "tab", "new" and "point" are in every request or too generic to identify a tab.
        #expect(BrowserTabLocator.bestMatch(for: ["the tab", "", "point at the tab"], in: tabs) == nil)
        #expect(BrowserTabLocator.bestMatch(for: ["settings tab", "", "where is the settings tab"], in: tabs) == nil)
    }

    @Test func tabFramesPairWithTabsByOrderOnly() {
        // Chrome's tab buttons come without titles; they pair with the tab list left to right and
        // only when the counts agree (a mismatch means the strip is scrolled or mid-update).
        let frames = [CGRect(x: 93, y: -1050, width: 94, height: 41), CGRect(x: -59, y: -1050, width: 94, height: 41), CGRect(x: 17, y: -1050, width: 94, height: 41)]
        #expect(BrowserTabLocator.frame(ofTabAt: 2, in: frames, tabCount: 3) == CGRect(x: 93, y: -1050, width: 94, height: 41))
        #expect(BrowserTabLocator.frame(ofTabAt: 0, in: frames, tabCount: 3) == CGRect(x: -59, y: -1050, width: 94, height: 41))
        #expect(BrowserTabLocator.frame(ofTabAt: 1, in: frames, tabCount: 4) == nil)
    }

    @Test func aWindowAboveTheBrowserHidesTheTab() {
        // Front-to-back on-screen windows, as CGWindowList reports them: Terminal in front, Chrome behind.
        let terminal = OnScreenWindow(ownerPID: 10, bounds: CGRect(x: 0, y: 135, width: 1710, height: 850))
        let chrome = OnScreenWindow(ownerPID: 20, bounds: CGRect(x: 0, y: 30, width: 1710, height: 1050))
        let windows = [terminal, chrome]
        // The tab strip shows above the terminal: visible.
        #expect(!BrowserTabLocator.isHidden(point: CGPoint(x: 480, y: 50), ofWindowOwnedBy: 20, in: windows))
        // A point inside the terminal's area is covered.
        #expect(BrowserTabLocator.isHidden(point: CGPoint(x: 480, y: 300), ofWindowOwnedBy: 20, in: windows))
        // A point that is in no Chrome window at all (another display, or off-screen) counts as hidden.
        #expect(BrowserTabLocator.isHidden(point: CGPoint(x: 480, y: 5), ofWindowOwnedBy: 20, in: windows))
        // Our own overlay windows never hide anything.
        let overlay = OnScreenWindow(ownerPID: ProcessInfo.processInfo.processIdentifier, bounds: CGRect(x: 0, y: 0, width: 1710, height: 1112))
        #expect(!BrowserTabLocator.isHidden(point: CGPoint(x: 480, y: 50), ofWindowOwnedBy: 20, in: [overlay, terminal, chrome]))
    }

    @Test func globalPointsMapIntoTheCapturedScreenshot() {
        // External display above the built-in one: AppKit frame (-173, 982, 1920×1080), captured at 1280×720.
        let capture = CompanionScreenCapture(
            imageData: Data(), label: "test", isCursorScreen: true,
            displayWidthInPoints: 1920, displayHeightInPoints: 1080,
            displayFrame: CGRect(x: -173, y: 982, width: 1920, height: 1080),
            screenshotWidthInPixels: 1280, screenshotHeightInPixels: 720
        )
        // The tab centre in CG global coordinates (top-left origin), 20 pt below the display's top edge.
        let point = capture.screenshotPoint(forGlobalPoint: CGPoint(x: 1331, y: -1060), primaryDisplayHeight: 982)
        #expect(abs(point.x - (1331 + 173) * 1280 / 1920) < 0.01)
        #expect(abs(point.y - 20 * 720 / 1080) < 0.01)
        #expect(capture.containsScreenshotPoint(point))
        // A tab on the built-in display (below) maps under the captured screenshot and is not pointed at.
        let onOtherDisplay = capture.screenshotPoint(forGlobalPoint: CGPoint(x: 292, y: 30), primaryDisplayHeight: 982)
        #expect(!capture.containsScreenshotPoint(onOtherDisplay))
    }
}
