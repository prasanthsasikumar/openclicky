//
//  TakeFormatterTests.swift
//  OpenClickyTests
//
//  The local formatting pass: spoken shortcuts, dictionary terms, fillers, sentence case, and the
//  guards around a model's answer.
//

import Foundation
import Testing
@testable import OpenClicky

struct TakeFormatterTests {
    private func context(style: DictationStyle? = nil, dictionary: [DictionaryTerm] = [], shortcuts: [SpokenShortcut] = []) -> TakeFormattingContext {
        TakeFormattingContext(
            style: style ?? DictationStyle.seeded().first { $0.id == "other" }!,
            dictionary: dictionary, shortcuts: shortcuts, appName: "Notes", language: .auto, script: .native)
    }

    @Test func aTakeThatIsATriggerBecomesItsReplacement() {
        let shortcut = SpokenShortcut(trigger: "my sign-off", replacement: "Warm regards,\nPrasanth")
        let result = TakeFormatter.formatLocally("My sign off.", context: context(shortcuts: [shortcut]))
        #expect(result.text == "Warm regards,\nPrasanth")
        #expect(result.expandedShortcut == shortcut)
    }

    @Test func aTriggerInsideALongerSentenceDoesNotExpand() {
        let shortcut = SpokenShortcut(trigger: "my sign-off", replacement: "x")
        let result = TakeFormatter.formatLocally("add my sign off at the end", context: context(shortcuts: [shortcut]))
        #expect(result.expandedShortcut == nil)
        #expect(result.text == "Add my sign off at the end")
    }

    @Test func dictionaryTermsReplaceWholeWordsOnly() {
        let term = DictionaryTerm(written: "Aaditya Kshatriya", heardAs: ["aditya shatriya", "aditya"])
        let text = TakeFormatter.applyDictionary(to: "ask aditya shatriya and Aditya about adityanath", terms: [term])
        #expect(text == "ask Aaditya Kshatriya and Aaditya Kshatriya about adityanath")
    }

    @Test func fillersAreRemovedWithTheirComma() {
        #expect(TakeFormatter.removeFillers(from: "um, so I think we should uh ship it friday") == "so I think we should ship it friday")
        #expect(TakeFormatter.removeFillers(from: "the drum is loud") == "the drum is loud")
    }

    @Test func sentenceCaseCapitalisesWithoutAddingPunctuation() {
        #expect(TakeFormatter.sentenceCased("hello there. how are you") == "Hello there. How are you")
        #expect(TakeFormatter.sentenceCased("3 kg of atta") == "3 kg of atta")
        #expect(TakeFormatter.sentenceCased("done!") == "Done!")
        #expect(TakeFormatter.sentenceCased("git status") == "Git status")
    }

    @Test func theCasualStyleKeepsLowercase() {
        let casual = DictationStyle.seeded().first { $0.id == "personal-messaging" }!
        let result = TakeFormatter.formatLocally("um lemme know if that works", context: context(style: casual))
        #expect(result.text == "lemme know if that works")
    }

    @Test func modelAnswersAreUnwrapped() {
        #expect(TakeFormatter.unwrapModelAnswer("\"Hello.\"") == "Hello.")
        #expect(TakeFormatter.unwrapModelAnswer("```\nHello.\n```") == "Hello.")
        #expect(TakeFormatter.unwrapModelAnswer("  Hello.\n") == "Hello.")
    }

    @Test func aRewriteThatIsNotTheTakeIsRejected() {
        let raw = "can you send me that doc when you get a sec thanks"
        #expect(TakeFormatter.looksLikeTheSameTake("Can you send me that doc when you get a sec? Thanks.", raw: raw))
        #expect(!TakeFormatter.looksLikeTheSameTake("Sure! Here is the document you asked for, attached below with a summary of each section and next steps for the team to review before Friday's launch meeting.", raw: raw))
        #expect(!TakeFormatter.looksLikeTheSameTake("", raw: raw))
    }

    @Test func theSystemPromptCarriesStyleDictionaryAndScript() {
        var ctx = context(dictionary: [DictionaryTerm(written: "FlowsXR", heardAs: ["flows xr"])])
        ctx.language = DictationLanguage.named("ml-IN")
        ctx.script = .roman
        let prompt = TakeFormatter.systemPrompt(for: ctx)
        #expect(prompt.contains("Style — other apps"))
        #expect(prompt.contains("\"FlowsXR\" (heard as \"flows xr\")"))
        #expect(prompt.contains("Malayalam (ml)"))
        #expect(prompt.contains("roman letters"))
        #expect(prompt.contains("Notes"))
    }

    @Test func withoutAModelTheLocalResultIsMarkedDegraded() async {
        let result = await TakeFormatter.format("hello there", context: context(), polisher: nil, wantsModel: true)
        #expect(result.text == "Hello there")
        #expect(result.formattingDegraded)
        let local = await TakeFormatter.format("hello there", context: context(), polisher: nil, wantsModel: false)
        #expect(!local.formattingDegraded)
    }

    private struct FixedPolisher: TakePolisher {
        let answer: String
        var displayName: String { "fixed" }
        func polish(system: String, user: String) async throws -> String { answer }
    }

    @Test func aModelAnswerReplacesTheLocalResult() async {
        let result = await TakeFormatter.format("um so the launch review is tomorrow at three pm", context: context(), polisher: FixedPolisher(answer: "The launch review is tomorrow at 3 PM."), wantsModel: true)
        #expect(result.text == "The launch review is tomorrow at 3 PM.")
        #expect(!result.formattingDegraded)
    }

    private struct LimitPolisher: TakePolisher {
        var displayName: String { "limit" }
        func polish(system: String, user: String) async throws -> String { throw AccountPolishError.limit(.dailyLimit) }
    }

    @Test func aLimitedAccountStillGetsTheLocallyFormattedTake() async {
        let result = await TakeFormatter.format("hello there", context: context(), polisher: LimitPolisher(), wantsModel: true)
        #expect(result.text == "Hello there")
        #expect(result.formattingDegraded)
    }
}
