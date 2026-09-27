//
//  VoiceReliabilityTests.swift
//  OpenClickyTests
//
//  The rules behind five fixes found by using the sibling app Saathi for a morning: when a
//  Realtime connection is trusted, reused or replaced; what a refusal says; the one reply language;
//  the selected text in the screen context; and what makes a shell.json edit reconnect.
//

import Foundation
import Testing
@testable import OpenClicky

struct RealtimeConnectionRuleTests {

    /// OpenAI caps a Realtime session at 60 minutes. A socket that old is replaced before a turn,
    /// not discovered dead in the middle of one.
    @Test func aConnectionNearTheHourCapIsReplaced() {
        let now = Date()
        #expect(!RealtimeVoiceClient.isTooOld(connectedAt: now.addingTimeInterval(-10 * 60), now: now))
        #expect(RealtimeVoiceClient.isTooOld(connectedAt: now.addingTimeInterval(-50 * 60), now: now))
        #expect(RealtimeVoiceClient.isTooOld(connectedAt: nil, now: now))
    }

    /// Sleep can leave a socket that still looks open. One the server has been quiet on is pinged
    /// before a turn; one heard from a moment ago is trusted, so a press mid-conversation costs nothing.
    @Test func aQuietConnectionIsPingedAndABusyOneIsTrusted() {
        let now = Date()
        #expect(!RealtimeVoiceClient.needsPing(lastServerEventAt: now.addingTimeInterval(-5), now: now))
        #expect(RealtimeVoiceClient.needsPing(lastServerEventAt: now.addingTimeInterval(-90), now: now))
        #expect(RealtimeVoiceClient.needsPing(lastServerEventAt: nil, now: now))
    }

    /// Every session is minted by the backend and billed, so the keep-warm loop reconnects in the
    /// background only while OpenClicky is actually being used.
    @Test func keepWarmReconnectsOnlyWhileRecentlyUsed() {
        let now = Date()
        #expect(RealtimeVoiceClient.isRecentlyUsed(lastTurnStartedAt: now.addingTimeInterval(-60), now: now))
        #expect(!RealtimeVoiceClient.isRecentlyUsed(lastTurnStartedAt: now.addingTimeInterval(-3 * 60 * 60), now: now))
    }

    /// A refused handshake reached the user as nothing at all, or as "bad server response".
    @Test func aRefusedHandshakeSaysWhatWasRefused() {
        let rejected = RealtimeVoiceClient.handshakeFailureDescription(statusCode: 401, underlyingError: "bad server response")
        #expect(rejected.contains("401"))
        #expect(rejected.contains("rejected"))
        #expect(RealtimeVoiceClient.handshakeFailureDescription(statusCode: 429, underlyingError: "x").contains("rate limit"))
        #expect(RealtimeVoiceClient.handshakeFailureDescription(statusCode: nil, underlyingError: "offline") == "offline")
    }

    /// The backend's own error text, with the status, rather than a raw JSON body.
    @Test func aBackendRefusalUsesTheBackendsOwnWords() {
        let body = Data(#"{"error":"invalid OpenAI key"}"#.utf8)
        let description = RealtimeVoiceClient.backendRefusalDescription(statusCode: 401, body: body)
        #expect(description.contains("invalid OpenAI key"))
        #expect(description.contains("401"))
        #expect(!description.contains("{"))
        let nested = Data(#"{"error":{"message":"quota exceeded"}}"#.utf8)
        #expect(RealtimeVoiceClient.backendRefusalDescription(statusCode: 500, body: nested).contains("quota exceeded"))
    }
}

struct ReplyLanguageTests {

    /// Unset is English; a region is dropped because the transcription models take a bare code.
    @Test func theConfiguredLanguageBecomesABareCode() {
        #expect(ReplyLanguage.normalizedCode(nil) == "en")
        #expect(ReplyLanguage.normalizedCode("  ") == "en")
        #expect(ReplyLanguage.normalizedCode("en-US") == "en")
        #expect(ReplyLanguage.normalizedCode("hi_IN") == "hi")
        #expect(ReplyLanguage.normalizedCode("TA") == "ta")
    }

    /// "Speak English unless the user speaks another language" let the model drift; the rule holds
    /// one language whatever the conversation does.
    @Test func theRuleHoldsOneLanguageWhateverTheConversationDoes() {
        let rule = ReplyLanguage.instruction(for: "en")
        #expect(rule.contains("Always speak and write in English"))
        #expect(rule.contains("whatever language any earlier reply in this conversation was in"))
        #expect(rule.contains("say in English that you did not catch that"))
        #expect(ReplyLanguage.instruction(for: "hi").contains("Always speak and write in Hindi"))
    }

    @Test func theRealtimePromptNoLongerInvitesSwitching() {
        #expect(!RealtimeVoiceClient.defaultInstructions.contains("unless the user speaks another language"))
    }

    /// Both lanes' prompts end with the rule, and skills still follow it.
    @Test func everyPromptEndsWithTheLanguageRule() {
        let prompt = CompanionManager.withReplyLanguage("base", code: "de")
        #expect(prompt.hasPrefix("base"))
        #expect(prompt.contains("Always speak and write in German"))
        let withSkills = CompanionManager.composeTalkInstructions(base: prompt, skillsBlock: "## Skill: X")
        #expect(withSkills.hasSuffix("## Skill: X"))
    }

    @Test func thePickerOffersEnglishFirst() {
        #expect(ReplyLanguage.choices.first?.code == "en")
        #expect(Set(ReplyLanguage.choices.map(\.code)).count == ReplyLanguage.choices.count)
    }
}

struct SelectedTextReaderTests {

    /// Asked about a highlighted word, the answer was about whatever sat under the pointer.
    @Test func theSelectionIsNamedAsWhatThisMeans() {
        let line = SelectedTextReader.contextLine(for: "reconnects")
        #expect(line.contains("«reconnects»"))
        #expect(line.contains("they almost certainly mean the selection"))
    }

    @Test func aSelectionIsTidiedAndCapped() {
        #expect(SelectedTextReader.tidied("  two\n  words ") == "two words")
        #expect(SelectedTextReader.tidied(" \n ") == nil)
        let long = SelectedTextReader.tidied(String(repeating: "a", count: 700))
        #expect(long?.count == SelectedTextReader.maximumCharacters + 1)
        #expect(long?.hasSuffix("…") == true)
    }
}

struct SettingsFileReloadTests {

    /// Only what a live session was opened with triggers a reconnect: a changed key or token does,
    /// a changed workspace does not.
    @Test func onlyCredentialChangesMakeTheSessionStale() {
        var settings = OpenClickyShellSettings()
        let original = OpenClickyConfiguration.credentialFingerprint(of: settings)
        settings.workspace = "/tmp/elsewhere"
        settings.language = "hi"
        #expect(OpenClickyConfiguration.credentialFingerprint(of: settings) == original)
        settings.openaiApiKey = "sk-new"
        #expect(OpenClickyConfiguration.credentialFingerprint(of: settings) != original)
        var tokenChanged = OpenClickyShellSettings()
        tokenChanged.token = "new-token"
        #expect(OpenClickyConfiguration.credentialFingerprint(of: tokenChanged) != original)
    }
}
