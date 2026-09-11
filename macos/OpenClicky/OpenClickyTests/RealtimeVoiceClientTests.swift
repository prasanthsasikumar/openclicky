//
//  RealtimeVoiceClientTests.swift
//  OpenClickyTests
//
//  `model` in the backend's realtime session response is server-supplied, not a compile-time
//  literal — a malformed value must not force-unwrap the websocket URL into a crash.
//

import Foundation
import Testing
@testable import OpenClicky

struct RealtimeVoiceClientTests {
    @Test func realtimeSocketURLBuildsANormalModelName() {
        let url = RealtimeVoiceClient.realtimeSocketURL(forModel: "gpt-realtime-mini")
        #expect(url?.absoluteString == "wss://api.openai.com/v1/realtime?model=gpt-realtime-mini")
    }

    /// The point of this fix is that `openConnection` never force-unwraps a URL built from the
    /// backend-supplied `model` field. On the Foundation version this app ships against, `URL(string:)`
    /// percent-encodes almost anything into a valid URL rather than returning nil for this fixed
    /// "wss://api.openai.com/v1/realtime?model=<value>" shape (confirmed by hand for spaces,
    /// newlines, NUL, RTL override characters, emoji, and stray query-syntax characters) — so the
    /// "gpt-realtime" fallback branch is not reachable through this exact string today. The
    /// guard-based fix is still correct: it removes the crash risk outright rather than depending on
    /// today's parser behavior, and this test pins down that a wide range of adversarial values a
    /// hostile or buggy backend could send all still produce a well-formed, non-nil URL.
    @Test func realtimeSocketURLNeverCrashesOnAdversarialModelNames() {
        let adversarialModelNames = [
            "",
            " ",
            "gpt realtime",
            "gpt\nrealtime",
            "gpt\u{0000}realtime",
            "\u{202E}reversed",
            "gpt&model=something-else",
            String(repeating: "x", count: 10_000)
        ]
        for modelName in adversarialModelNames {
            let url = RealtimeVoiceClient.realtimeSocketURL(forModel: modelName)
            #expect(url != nil)
            #expect(url?.scheme == "wss")
            #expect(url?.host == "api.openai.com")
        }
    }
}
