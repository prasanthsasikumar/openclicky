import Foundation
import Testing
@testable import OpenClicky

/// The recorded notices are found by their exact sentence, so a sentence changed in the code would
/// quietly go back to the system voice. These fail instead: record the line again with
/// scripts/record-voice-lines.py and update RecordedVoice.notices.
@MainActor
struct RecordedVoiceTests {
    @Test func theAccountAndKeyNoticesHaveTheirRecordings() {
        let expected: [(sentence: String, line: RecordedVoice.Line)] = [
            (ProviderRouter.needsAnthropicKeySentence, .noticeNeedsKeyAnswer),
            (ProviderRouter.needsOpenAIKeySentence, .noticeNeedsKeyOpenAI),
            (OpenClickyAuthSession.spokenSignInExpiredSentence, .noticeSignInExpired),
            (OpenClickyAuthSession.ownKeyRefusedSentence, .noticeOwnKeyRefused),
            (OpenClickyAuthSession.askAgainSentence, .noticeAskAgain),
            (OpenClickyAuthSession.unreachableMessage, .noticeUnreachableRetry),
            (AccountLimitError.personalLimit.message, .noticeMonthAllowanceUsed),
            (AccountLimitError.dailyLimit.message, .noticeDayAllowanceUsed),
            (AccountLimitError.monthlyBudget.message, .noticeFreeBudgetUsed),
            (AccountLimitError.notOnPlan.message, .noticeNeedsOwnKey),
            (AccountLimitError.accountsFull.message, .noticeAccountsFull),
            (AccountLimitError.blocked.message, .noticeAccountPaused),
            (AccountLimitError.confirmEmail.message, .noticeConfirmEmail),
            (AccountLimitError.deviceLimit.message, .noticeDeviceLimit),
            (AccountLimitError.guestTrialEndedMessage, .noticeGuestTrialEnded),
            (AccountLimitError.serviceTroubleMessage, .noticeServiceTrouble),
            (BillingSummary.switchedOffMessage, .noticeSwitchedOff),
            (CompanionManager.emptyAnswerSentence, .noticeEmptyAnswer),
        ]
        for (sentence, line) in expected {
            #expect(RecordedVoice.line(forSpokenText: sentence) == line, "\(sentence)")
        }
    }

    @Test func everyLineIsInTheBundle() {
        for line in RecordedVoice.Line.allCases {
            #expect(Bundle.main.url(forResource: line.rawValue, withExtension: "mp3") != nil, "\(line.rawValue).mp3")
        }
    }
}
