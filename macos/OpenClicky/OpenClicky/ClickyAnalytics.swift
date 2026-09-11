//
//  ClickyAnalytics.swift
//  OpenClicky
//
//  Centralized PostHog analytics wrapper. All event names and properties
//  are defined here so instrumentation is consistent and easy to audit.
//
//  Never send verbatim user speech or AI replies here — only non-content signals (that a turn
//  happened, its length in characters). An earlier version of this file's header claimed that
//  putting analytics behind a PostHogAPIKey Info.plist entry was the fix for that; it was not —
//  whenever that key was present, `trackUserMessageSent`/`trackAIResponseReceived` still sent the
//  full transcript and response text to PostHog. The plist key only gates whether analytics runs
//  at all, not what leaves the machine once it does, so the content itself had to come out of the
//  event properties below.
//

import Foundation
import PostHog

enum ClickyAnalytics {

    // MARK: - Setup

    /// OpenClicky: analytics are opt-in. Upstream shipped the original developer's PostHog key and
    /// sent transcripts + responses to it; here nothing is set up unless `PostHogAPIKey` is present in
    /// Info.plist. Before setup, every `PostHogSDK.shared.capture` call is a no-op.
    static func configure() {
        guard let apiKey = AppBundleConfiguration.stringValue(forKey: "PostHogAPIKey") else {
            print("📊 Analytics disabled (no PostHogAPIKey in Info.plist)")
            return
        }
        let config = PostHogConfig(
            apiKey: apiKey,
            host: AppBundleConfiguration.stringValue(forKey: "PostHogHost") ?? "https://us.i.posthog.com"
        )
        PostHogSDK.shared.setup(config)
    }

    // MARK: - App Lifecycle

    /// Fired once on every app launch in applicationDidFinishLaunching.
    static func trackAppOpened() {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "unknown"
        PostHogSDK.shared.capture("app_opened", properties: [
            "app_version": version
        ])
    }

    // MARK: - Onboarding

    /// User clicked the Start button to begin onboarding for the first time.
    static func trackOnboardingStarted() {
        PostHogSDK.shared.capture("onboarding_started")
    }

    /// User clicked "Watch Onboarding Again" from the panel footer.
    static func trackOnboardingReplayed() {
        PostHogSDK.shared.capture("onboarding_replayed")
    }

    /// The onboarding video finished playing to the end.
    static func trackOnboardingVideoCompleted() {
        PostHogSDK.shared.capture("onboarding_video_completed")
    }

    /// The 40s onboarding demo interaction where OpenClicky points at something.
    static func trackOnboardingDemoTriggered() {
        PostHogSDK.shared.capture("onboarding_demo_triggered")
    }

    // MARK: - Permissions

    /// All three permissions (accessibility, screen recording, mic) are granted.
    static func trackAllPermissionsGranted() {
        PostHogSDK.shared.capture("all_permissions_granted")
    }

    /// A single permission was granted. Called when polling detects a change.
    static func trackPermissionGranted(permission: String) {
        PostHogSDK.shared.capture("permission_granted", properties: [
            "permission": permission
        ])
    }

    // MARK: - Voice Interaction

    /// User pressed the push-to-talk shortcut (control+option) to start talking.
    static func trackPushToTalkStarted() {
        PostHogSDK.shared.capture("push_to_talk_started")
    }

    /// User released the shortcut — transcript is being finalized.
    static func trackPushToTalkReleased() {
        PostHogSDK.shared.capture("push_to_talk_released")
    }

    /// Transcription completed and the user's message is being sent to the AI. Takes only the
    /// character count, never the transcript itself — verbatim user speech must never reach
    /// PostHog. The event name and the `character_count` property stay the same as before so
    /// existing dashboards keep working; only the `transcript` property (the actual content) is gone.
    static func trackUserMessageSent(characterCount: Int) {
        PostHogSDK.shared.capture("user_message_sent", properties: userMessageSentProperties(characterCount: characterCount))
    }

    /// Claude responded and the response is being spoken via TTS. Takes only the character count,
    /// never the response text itself — verbatim AI replies must never reach PostHog.
    static func trackAIResponseReceived(characterCount: Int) {
        PostHogSDK.shared.capture("ai_response_received", properties: aiResponseReceivedProperties(characterCount: characterCount))
    }

    /// Pulled out as a pure function (rather than inlined into the `capture` call above) so a test
    /// can assert on the exact property set reaching PostHog — in particular, that no key here ever
    /// carries message content — without needing a real PostHog SDK instance.
    static func userMessageSentProperties(characterCount: Int) -> [String: Any] {
        ["character_count": characterCount]
    }

    /// See `userMessageSentProperties`: same reasoning, for the AI's side of the turn.
    static func aiResponseReceivedProperties(characterCount: Int) -> [String: Any] {
        ["character_count": characterCount]
    }

    /// Claude's response included a [POINT:x,y:label] coordinate tag,
    /// so the buddy is flying to point at a UI element.
    static func trackElementPointed(elementLabel: String?) {
        PostHogSDK.shared.capture("element_pointed", properties: [
            "element_label": elementLabel ?? "unknown"
        ])
    }

    // MARK: - Errors

    /// An error occurred during the AI response pipeline.
    static func trackResponseError(error: String) {
        PostHogSDK.shared.capture("response_error", properties: [
            "error": error
        ])
    }

    /// An error occurred during TTS playback.
    static func trackTTSError(error: String) {
        PostHogSDK.shared.capture("tts_error", properties: [
            "error": error
        ])
    }
}
