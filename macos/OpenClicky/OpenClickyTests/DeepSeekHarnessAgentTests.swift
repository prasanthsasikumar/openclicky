//
//  DeepSeekHarnessAgentTests.swift
//  OpenClickyTests
//
//  Which sentences go to DeepSeek Harness, and how one of its JSON lines is read.
//

import Testing
@testable import OpenClicky

struct DeepSeekHarnessAgentTests {
    @Test func aQuestionStaysOnTheAskLane() {
        #expect(DeepSeekHarnessLane.lane(for: "这是什么") == "ask")
        #expect(DeepSeekHarnessLane.lane(for: "how does this work") == "ask")
        #expect(DeepSeekHarnessLane.lane(for: "   ") == "ask")
    }

    @Test func workGoesToTheHarness() {
        #expect(DeepSeekHarnessLane.lane(for: "帮我创建一个 hello.txt") == "agent")
        #expect(DeepSeekHarnessLane.lane(for: "fix the build") == "agent")
        #expect(DeepSeekHarnessLane.lane(for: "跑一下测试") == "agent")
    }

    @Test func aSessionAndAFinalAnswerAreRead() {
        var result = OpenClickyAgentRunResult()
        #expect(DeepSeekHarnessEvent.apply(#"{"type":"session","sessionId":"session-1"}"#, to: &result) == nil)
        #expect(result.threadId == "session-1")
        #expect(DeepSeekHarnessEvent.apply(#"{"type":"tool_call","tool":"bash"}"#, to: &result) == "using bash")
        #expect(DeepSeekHarnessEvent.apply(#"{"type":"final","text":"写好了"}"#, to: &result) == nil)
        #expect(result.text == "写好了")
        #expect(result.status == "completed")
    }
}
