//
//  ClaudeAPIRequestTests.swift
//  OpenClickyTests
//
//  The Anthropic request body the answer client builds: an ordinary answer is capped at 1,024
//  tokens as before; a drawing asks for 8,192, no thinking on the models that think by default,
//  and says what it is to the backend in a header.
//

import Foundation
import Testing
@testable import OpenClicky

@Suite struct ClaudeAPIRequestTests {
    @Test func aDrawingRequestAsksForMoreTokensAndNoThinking() {
        let options = AnswerRequestOptions(maxTokens: 8192, purpose: .drawing, thinkingOff: true)
        let body = ClaudeAPI.anthropicRequestBody(model: "claude-sonnet-5-5", systemPrompt: "s", messages: [], tools: [], options: options)
        #expect(body["max_tokens"] as? Int == 8192)
        #expect((body["thinking"] as? [String: String])?["type"] == "between_tools")
        #expect(body["tools"] == nil)
        #expect(ClaudeAPI.extraHeaders(for: options) == ["X-OpenClicky-Purpose": "drawing"])
    }

    @Test func anOrdinaryRequestIsUnchanged() {
        let body = ClaudeAPI.anthropicRequestBody(model: "claude-sonnet-5-5", systemPrompt: "s", messages: [], tools: [["name": "open_app"]], options: .init())
        #expect(body["max_tokens"] as? Int == 1024)
        #expect(body["thinking"] == nil)
        #expect((body["tools"] as? [[String: Any]])?.count == 1)
        #expect(body["stream"] as? Bool == true)
        #expect(ClaudeAPI.extraHeaders(for: .init()).isEmpty)
    }

    @Test func haikuNeverGetsTheThinkingKey() {
        let body = ClaudeAPI.anthropicRequestBody(model: "claude-haiku-4-5", systemPrompt: "s", messages: [], tools: [],
                                                  options: .init(maxTokens: 8192, purpose: .drawing, thinkingOff: true))
        #expect(body["thinking"] == nil)
        #expect(body["max_tokens"] as? Int == 8192)
    }
}
