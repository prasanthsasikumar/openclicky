//
//  ClickyAnalyticsTests.swift
//  OpenClickyTests
//
//  Verbatim user speech and AI replies must never reach PostHog — only non-content signals like
//  character counts. These tests pin down the exact property dictionaries the tracking calls send,
//  since PostHogSDK.shared.capture itself has no test seam.
//

import Foundation
import Testing
@testable import OpenClicky

struct ClickyAnalyticsTests {
    @Test func userMessageSentPropertiesCarryOnlyTheCharacterCount() {
        let properties = ClickyAnalytics.userMessageSentProperties(characterCount: 42)
        #expect(properties.count == 1)
        #expect(properties["character_count"] as? Int == 42)
        #expect(properties["transcript"] == nil)
    }

    @Test func aiResponseReceivedPropertiesCarryOnlyTheCharacterCount() {
        let properties = ClickyAnalytics.aiResponseReceivedProperties(characterCount: 7)
        #expect(properties.count == 1)
        #expect(properties["character_count"] as? Int == 7)
        #expect(properties["response"] == nil)
    }
}
