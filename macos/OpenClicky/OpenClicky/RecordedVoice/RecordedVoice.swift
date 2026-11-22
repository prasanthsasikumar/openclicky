//
//  RecordedVoice.swift
//  OpenClicky
//
//  The lines OpenClicky says the same way every time, recorded once in its answering voice
//  (OpenAI speech, marin) and bundled, so they sound good with no key and no network: the
//  first-run tour's narration, and the notices that used to be read by the Mac's system voice
//  ("i didn't get an answer…", "add your email for a free account…", the allowance notes).
//  Anything not recorded here is spoken the way it was before.
//

import AVFoundation
import Foundation

@MainActor
final class RecordedVoice {
    enum Line: String, CaseIterable {
        case tourHello = "tour-hello"
        case tourDictate = "tour-dictate"
        case tourDictateDone = "tour-dictate-done"
        case tourTalk = "tour-talk"
        case tourTalkDone = "tour-talk-done"
        case tourAct = "tour-act"
        case tourActDone = "tour-act-done"
        case tourAccount = "tour-account"
        case tourDone = "tour-done"
        case noticeEmptyAnswer = "notice-empty-answer"
        case noticeNoScreen = "notice-no-screen"
        case noticeScreenRecordingOff = "notice-screen-recording-off"
        case noticeNoInternet = "notice-no-internet"
        case noticeStoppedPartway = "notice-stopped-partway"
        case noticeWentWrong = "notice-went-wrong"
        case noticeRealtimeUnavailable = "notice-realtime-unavailable"
        // Account, sign-in and key notices, said before there is any voice to say them in.
        case noticeNeedsKeyAnswer = "notice-needs-key-answer"
        case noticeNeedsKeyOpenAI = "notice-needs-key-openai"
        case noticeSignInExpired = "notice-sign-in-expired"
        case noticeOwnKeyRefused = "notice-own-key-refused"
        case noticeAskAgain = "notice-ask-again"
        case noticeUnreachableRetry = "notice-unreachable-retry"
        case noticeBackendTrouble = "notice-backend-trouble"
        case noticeMonthAllowanceUsed = "notice-month-allowance-used"
        case noticeDayAllowanceUsed = "notice-day-allowance-used"
        case noticeFreeBudgetUsed = "notice-free-budget-used"
        case noticeNeedsOwnKey = "notice-needs-own-key"
        case noticeAccountsFull = "notice-accounts-full"
        case noticeAccountPaused = "notice-account-paused"
        case noticeConfirmEmail = "notice-confirm-email"
        case noticeDeviceLimit = "notice-device-limit-mac"
        case noticeGuestTrialEnded = "notice-guest-trial-ended"
        case noticeServiceTrouble = "notice-service-trouble"
        case noticeSwitchedOff = "notice-switched-off"
        // The one-minute Settings tour (SettingsTour.swift).
        case settingsTourIntro = "settings-tour-intro"
        case settingsTourDictation = "settings-tour-dictation"
        case settingsTourLanguage = "settings-tour-language"
        case settingsTourVoice = "settings-tour-voice"
        case settingsTourPointer = "settings-tour-pointer"
        case settingsTourWords = "settings-tour-words"
        case settingsTourShortcuts = "settings-tour-shortcuts"
        case settingsTourKeys = "settings-tour-keys"
        case settingsTourDone = "settings-tour-done"
    }

    /// The fixed sentences the app speaks, by their exact text (any case), and their recording.
    static let notices: [String: Line] = [
        "i didn't get an answer. please ask again.": .noticeEmptyAnswer,
        "i couldn't see your screen just now. please ask again.": .noticeNoScreen,
        "screen recording is off for openclicky. turn it on in system settings, privacy and security, screen recording.": .noticeScreenRecordingOff,
        "i couldn't reach openclicky. check your internet and ask again.": .noticeNoInternet,
        "the answer stopped partway. please ask again.": .noticeStoppedPartway,
        "something went wrong getting your answer. please ask again.": .noticeWentWrong,
        "realtime voice is unavailable right now.": .noticeRealtimeUnavailable,
        // Recorded by scripts/record-voice-lines.py; RecordedVoiceTests fails when one of these
        // sentences changes in the code, as the reminder to record it again.
        "i can answer that once you add your email for a free account, in settings, account. a key from openai, anthropic, openrouter, gemini and others works too.": .noticeNeedsKeyAnswer,
        "i can do that once you add your email for a free account, in settings, account. your own openai key works too.": .noticeNeedsKeyOpenAI,
        "your openclicky sign-in has expired. sign in again in settings, account.": .noticeSignInExpired,
        "your own api key was refused. check it in settings, keys & plans.": .noticeOwnKeyRefused,
        "please ask that again.": .noticeAskAgain,
        "couldn't reach openclicky right now, try again in a minute.": .noticeUnreachableRetry,
        "something went wrong talking to the openclicky backend. check the backend and your token in the settings file.": .noticeBackendTrouble,
        "you've used this month's free allowance, it comes back on the 1st. dictation still works.": .noticeMonthAllowanceUsed,
        "you've used today's free allowance, it comes back tomorrow. dictation still works.": .noticeDayAllowanceUsed,
        "openclicky's free allowance is used up for now, you can keep going with your own key.": .noticeFreeBudgetUsed,
        "that needs your own key for now.": .noticeNeedsOwnKey,
        "openclicky's free accounts are full right now, you can use your own key instead.": .noticeAccountsFull,
        "this account is paused. dictation still works.": .noticeAccountPaused,
        "confirm your email to keep going: type the code from the email in settings, account.": .noticeConfirmEmail,
        "this mac already has two openclicky accounts, sign in with one of them.": .noticeDeviceLimit,
        "your free trial has ended. add your email in settings, keys and account, to keep going for free.": .noticeGuestTrialEnded,
        "openclicky's free service is having trouble, try again in a minute.": .noticeServiceTrouble,
        "this login isn't switched on for openclicky yet. write to hello@flowsxr.com, or use your own key.": .noticeSwitchedOff,
    ]

    static func line(forSpokenText text: String) -> Line? {
        notices[text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()]
    }

    private var player: AVAudioPlayer?
    /// Tells whoever played the current line that it ended. Held here because the player's
    /// delegate is weak.
    private var finishObserver: AudioPlayerFinishObserver?

    var isPlaying: Bool { player?.isPlaying ?? false }

    /// Plays `line`, cutting off whatever recording was playing. False when it is not bundled.
    /// `onFinished` runs once when the line ends: played to the end, cut off by the next line, or
    /// stopped (the companion uses it to go back to idle after a spoken notice).
    @discardableResult
    func play(_ line: Line, onFinished: (() -> Void)? = nil) -> Bool {
        guard let url = Bundle.main.url(forResource: line.rawValue, withExtension: "mp3"),
              let newPlayer = try? AVAudioPlayer(contentsOf: url) else {
            AppLog.append("recorded voice: \(line.rawValue).mp3 is not in the bundle")
            return false
        }
        stopCurrentLine()
        newPlayer.volume = 0.85
        let newFinishObserver = AudioPlayerFinishObserver()
        newFinishObserver.onFinish = onFinished
        newPlayer.delegate = newFinishObserver
        finishObserver = newFinishObserver
        player = newPlayer
        if !newPlayer.play() { newFinishObserver.finishOnce() }
        return true
    }

    func stop() {
        stopCurrentLine()
    }

    /// A stopped AVAudioPlayer never calls its delegate, so the end is reported here.
    private func stopCurrentLine() {
        player?.stop()
        finishObserver?.finishOnce()
        finishObserver = nil
    }
}
