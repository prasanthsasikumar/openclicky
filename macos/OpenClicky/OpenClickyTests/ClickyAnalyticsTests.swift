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

    @Test func aiResponsePropertiesCarryTheLocalRungOutcome() {
        // Which tier answered (never what was said): how the on-device rung's share gets measured.
        let properties = ClickyAnalytics.aiResponseReceivedProperties(characterCount: 12, localRung: "local_action")
        #expect(properties["character_count"] as? Int == 12)
        #expect(properties["local_rung"] as? String == "local_action")
        #expect(ClickyAnalytics.aiResponseReceivedProperties(characterCount: 1, localRung: nil)["local_rung"] == nil)
    }
}
