//
//  BackendProxyURLFallbackTests.swift
//  OpenClickyTests
//
//  ClaudeAPI and ElevenLabsTTSClient build their backend URL from the user-configurable backend
//  base URL (shell.json or an environment override), not a compile-time literal. A malformed value
//  must fall back to the hosted backend's own endpoint instead of force-unwrapping into a crash.
//

import Foundation
import Testing
@testable import OpenClicky

struct BackendProxyURLFallbackTests {
    @Test func claudeAPIUsesTheProxyURLWhenItParses() {
        let url = ClaudeAPI.resolvedAPIURL(fromProxyURL: "https://example.com/chat")
        #expect(url.absoluteString == "https://example.com/chat")
    }

    @Test func claudeAPIFallsBackToTheHostedBackendWhenTheProxyURLDoesNotParse() {
        // An empty string is the one input that reliably fails URL(string:) on every Foundation
        // version, which is what makes it a good stand-in for "the configured backend URL is
        // malformed" in this test.
        let url = ClaudeAPI.resolvedAPIURL(fromProxyURL: "")
        #expect(url.absoluteString == "\(OpenClickyConfiguration.hostedBackendURL)/chat")
    }

    @Test func elevenLabsUsesTheProxyURLWhenItParses() {
        let url = ElevenLabsTTSClient.resolvedProxyURL(fromProxyURL: "https://example.com/tts")
        #expect(url.absoluteString == "https://example.com/tts")
    }

    @Test func elevenLabsFallsBackToTheHostedBackendWhenTheProxyURLDoesNotParse() {
        let url = ElevenLabsTTSClient.resolvedProxyURL(fromProxyURL: "")
        #expect(url.absoluteString == "\(OpenClickyConfiguration.hostedBackendURL)/tts")
    }
}
