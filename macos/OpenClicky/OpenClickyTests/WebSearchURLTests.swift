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

    @Test func youtubeOpensAWatchLinkAndSearchesATitle() {
        let watch = YouTubePage.watchURL(from: "https://www.youtube.com/watch?v=abc123")
        #expect(watch?.absoluteString == "https://www.youtube.com/watch?v=abc123")
        #expect(YouTubePage.watchURL(from: "https://youtu.be/abc123")?.host == "youtu.be")
        #expect(YouTubePage.watchURL(from: "https://www.youtube.com/shorts/abc123")?.path == "/shorts/abc123")
        #expect(YouTubePage.watchURL(from: "lo-fi radio") == nil)
        #expect(YouTubePage.watchURL(from: "https://evil.example/watch?v=abc") == nil)
        #expect(YouTubePage.channelSearchURL(name: "MKBHD").absoluteString.contains("search_query=MKBHD"))
        #expect(YouTubePage.channelSearchURL(name: "MKBHD").absoluteString.contains("sp=EgIQAg%3D%3D"))
    }
}
