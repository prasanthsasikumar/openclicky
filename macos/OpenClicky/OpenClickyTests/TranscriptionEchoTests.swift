//
//  TranscriptionEchoTests.swift
//  OpenClickyTests
//
//  The upload transcription provider drops transcripts that are just its own prompt echoed back
//  (what the model returns for near-silent audio).
//

import Testing
@testable import OpenClicky

struct TranscriptionEchoTests {
    private let prompt = "This is a short push-to-talk transcript for a coding and product app. Expect product names, technical terms, and app-specific vocabulary such as: OpenClicky, Codex, Claude, Anthropic, OpenAI, SwiftUI, Xcode, Vercel, Next.js, localhost."

    @Test func verbatimPromptIsAnEcho() {
        #expect(OpenAIAudioTranscriptionProvider.looksLikeEchoedPrompt(prompt, prompt: prompt))
    }

    @Test func nearVerbatimPromptIsAnEcho() {
        let echoed = "This is a short push to talk transcript for a coding and product app, expect product names and technical terms such as OpenClicky, Codex, Claude."
        #expect(OpenAIAudioTranscriptionProvider.looksLikeEchoedPrompt(echoed, prompt: prompt))
    }

    @Test func realSpeechMentioningKeytermsIsKept() {
        let spoken = "Please open the OpenClicky settings and turn on hands-free mode before I deploy to Vercel."
        #expect(!OpenAIAudioTranscriptionProvider.looksLikeEchoedPrompt(spoken, prompt: prompt))
    }

    @Test func shortTranscriptsAndNoPromptAreKept() {
        #expect(!OpenAIAudioTranscriptionProvider.looksLikeEchoedPrompt("OpenClicky Codex Claude", prompt: prompt))
        #expect(!OpenAIAudioTranscriptionProvider.looksLikeEchoedPrompt(prompt, prompt: nil))
    }
}
