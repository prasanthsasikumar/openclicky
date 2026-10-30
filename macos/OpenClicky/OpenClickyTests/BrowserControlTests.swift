//
//  BrowserControlTests.swift
//  OpenClickyTests
//
//  Which tab "next", "the newest" or "the Cars & Bids tab" means, the click tool's new_tab
//  argument, and the AppleScript string a link becomes.
//

import Foundation
import Testing
@testable import OpenClicky

struct BrowserControlTests {

    private let tabs = [
        BrowserTab(title: "skyline r34 - Google Search", url: URL(string: "https://www.google.com/search?q=skyline")),
        BrowserTab(title: "Used Nissan R34 Skyline for Sale", url: URL(string: "https://carsandbids.com/search/nissan")),
        BrowserTab(title: "YouTube", url: URL(string: "https://www.youtube.com/")),
    ]

    @Test func positionsWrapLikeControlTab() {
        #expect(BrowserControl.tabIndex(for: .next, tabs: tabs, activeIndex: 1) == 2)
        #expect(BrowserControl.tabIndex(for: .next, tabs: tabs, activeIndex: 3) == 1)
        #expect(BrowserControl.tabIndex(for: .previous, tabs: tabs, activeIndex: 1) == 3)
        #expect(BrowserControl.tabIndex(for: .previous, tabs: tabs, activeIndex: 2) == 1)
        #expect(BrowserControl.tabIndex(for: .newest, tabs: tabs, activeIndex: 1) == 3)
        #expect(BrowserControl.tabIndex(for: .first, tabs: tabs, activeIndex: 3) == 1)
        #expect(BrowserControl.tabIndex(for: .next, tabs: [], activeIndex: 1) == nil)
    }

    @Test func aNamedTabIsFoundBySiteOrTitle() {
        #expect(BrowserControl.tabIndex(for: .named("cars and bids"), tabs: tabs, activeIndex: 1) == 2)
        #expect(BrowserControl.tabIndex(for: .named("youtube"), tabs: tabs, activeIndex: 1) == 3)
        #expect(BrowserControl.tabIndex(for: .named("reddit"), tabs: tabs, activeIndex: 1) == nil)
    }

    @Test func clickOffersNewTabOnTopOfPointAtsArguments() {
        let pointParameters: [String: Any] = ["type": "object", "properties": ["x": ["type": "integer"]], "required": ["x"]]
        let parameters = RealtimeVoiceClient.clickToolParameters(pointParameters: pointParameters)
        let properties = parameters["properties"] as? [String: Any] ?? [:]
        #expect(properties["x"] != nil)
        #expect((properties["new_tab"] as? [String: Any])?["type"] as? String == "boolean")
        #expect(parameters["required"] as? [String] == ["x"])
    }

    @Test func aLinkBecomesAnAppleScriptStringItCannotEscape() {
        #expect(BrowserControl.appleScriptLiteral("https://a.com/?q=\"x\"\\") == #""https://a.com/?q=\"x\"\\""#)
    }
}
