//
//  AnswerKeyTakePolisherTests.swift
//  OpenClickyTests
//
//  Clean-up works on any answer key, not only Sarvam, OpenAI or Anthropic.
//

@testable import OpenClicky
import Testing

struct AnswerKeyTakePolisherTests {
    @Test func aGeminiKeyAlonePolishes() {
        let credentials = ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: nil, geminiKey: "g")
        #expect(AnswerKeyTakePolisher.firstAvailable(credentials: credentials)?.source == .gemini)
    }

    @Test func groqIsTriedFirstBecauseItIsQuickest() {
        let credentials = ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: nil,
                                              openRouterKey: "o", geminiKey: "g", groqKey: "q", mistralKey: "m", xaiKey: "x")
        #expect(AnswerKeyTakePolisher.firstAvailable(credentials: credentials)?.source == .groq)
    }

    @Test func everyKeyedAnswerSourceButTheBigTwoCanPolish() {
        for source in [AnswerSource.openRouter, .gemini, .groq, .mistral, .xai, .openCode] {
            var credentials = ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: nil)
            switch source {
            case .openRouter: credentials.openRouterKey = "k"
            case .gemini: credentials.geminiKey = "k"
            case .groq: credentials.groqKey = "k"
            case .mistral: credentials.mistralKey = "k"
            case .openCode: credentials.openCodeKey = "k"
            default: credentials.xaiKey = "k"
            }
            #expect(AnswerKeyTakePolisher.firstAvailable(credentials: credentials)?.source == source)
        }
    }

    @Test func noKeysNoPolisher() {
        #expect(AnswerKeyTakePolisher.firstAvailable(credentials: ProviderCredentials(openAIKey: nil, anthropicKey: nil, accountToken: nil)) == nil)
    }

    @Test func slowAnswerModelsGetAQuickOneForTidying() {
        #expect(AnswerKeyTakePolisher.polishModel(for: .openRouter) == "openai/gpt-4.1-mini")
        #expect(AnswerKeyTakePolisher.polishModel(for: .mistral) == "mistral-small-latest")
    }
}
