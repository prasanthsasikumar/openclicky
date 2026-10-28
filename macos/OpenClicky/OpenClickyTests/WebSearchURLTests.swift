//
//  WebSearchURLTests.swift
//  OpenClickyTests
//
//  `web_search`: the search page each site gets, and what counts as a query.
//

import Foundation
import Testing
@testable import OpenClicky

struct WebSearchURLTests {
    @Test func eachSiteBuildsItsOwnSearchURLWithTheQueryEncoded() {
        #expect(WebSearchSite.url(for: .web, query: "rotary engine & co").absoluteString == "https://www.google.com/search?q=rotary%20engine%20%26%20co")
        #expect(WebSearchSite.url(for: .youtube, query: "lo-fi").absoluteString == "https://www.youtube.com/results?search_query=lo-fi")
        #expect(WebSearchSite.url(for: .wikipedia, query: "Pythagoras").absoluteString == "https://en.wikipedia.org/w/index.php?search=Pythagoras")
        #expect(WebSearchSite.url(for: .maps, query: "coffee near me").absoluteString == "https://www.google.com/maps/search/coffee%20near%20me")
        #expect(WebSearchSite.url(for: .amazon, query: "usb c hub").absoluteString.hasPrefix("https://www.amazon.com/s?k="))
        // A plus is a space to every search page; it must travel as %2B.
        #expect(WebSearchSite.url(for: .web, query: "c++").absoluteString == "https://www.google.com/search?q=c%2B%2B")
    }

    @Test func queriesAreTrimmedCappedAndRefuseControlCharacters() {
        #expect(MacActionValidation.searchQuery("  cats  ") == "cats")
        #expect(MacActionValidation.searchQuery("") == nil)
        #expect(MacActionValidation.searchQuery("   ") == nil)
        #expect(MacActionValidation.searchQuery("a\u{0}b") == nil)
        #expect(MacActionValidation.searchQuery("a\nb") == nil)
        #expect(MacActionValidation.searchQuery(String(repeating: "x", count: 200)) != nil)
        #expect(MacActionValidation.searchQuery(String(repeating: "x", count: 201)) == nil)
    }
}
