//
//  AppConnectPromptTests.swift
//  OpenClickyTests
//
//  The "Connect <app> to OpenClicky" card: example prompts read from a skill body, and the
//  remembered answers (Yes / No / Not now) that decide whether the card opens again.
//

import Foundation
import Testing
@testable import OpenClicky

struct AppConnectPromptTests {

    private let body = """
    ## Layout
    The header runs across the top.

    ## Common tasks
    - Play/pause: K or space; skip 10 seconds: J and L.
    - Captions: the CC button at the bottom-right of the player, or press C.
    - A task with no colon at all

    ## Pointing hints
    - "How do I change the speed / quality?" — point at the gear at the bottom-right corner of the player.
    - “How do I turn on subtitles?” — point at the CC button.
    - "how do i change the speed / quality?" — a duplicate in another case.
    - A hint without a question.
    """

    @Test func extractsQuotedPointingHintsThenCommonTaskLeads() {
        let examples = AppSkillExamples.extract(from: body)
        #expect(examples == [
            "How do I change the speed / quality?",
            "How do I turn on subtitles?",
            "Play/pause",
            "Captions",
        ])
    }

    @Test func limitsAndHandlesBodiesWithoutSections() {
        #expect(AppSkillExamples.extract(from: body, limit: 1) == ["How do I change the speed / quality?"])
        #expect(AppSkillExamples.extract(from: "Just prose, no bullets.") == [])
    }

    @MainActor
    @Test func promptOpensOncePerSkillAndRemembersAnswers() throws {
        let suiteName = "AppConnectPromptTests-\(UUID().uuidString)"
        let userDefaults = try #require(UserDefaults(suiteName: suiteName))
        defer { userDefaults.removePersistentDomain(forName: suiteName) }

        let youtube = try #require(SkillFile.parse(
            "---\nname: YouTube\ndescription: d\nsites: [youtube.com]\nintegration: youtube\nsurfaces: [talk]\n---\n\(body)", id: "youtube"))
        let terminal = try #require(SkillFile.parse(
            "---\nname: Terminal\ndescription: d\napps: [com.apple.Terminal]\nsurfaces: [talk]\n---\n\(body)", id: "terminal"))
        let front = FrontAppContext(bundleIdentifier: "com.google.Chrome", appName: "Google Chrome",
                                    url: URL(string: "https://www.youtube.com/watch?v=x"), windowTitle: nil, isBrowser: true)

        let controller = AppConnectPromptController(userDefaults: userDefaults)
        let prompt = try #require(controller.prompt(for: front, skills: [youtube]))
        #expect(prompt.skillId == "youtube")
        #expect(prompt.appName == "YouTube")
        #expect(prompt.frontBundleIdentifier == "com.google.Chrome")
        #expect(prompt.integration == "youtube")
        #expect(youtube.integration == "youtube")

        // An app skill without an `integration` (nothing to connect) never gets the card.
        #expect(terminal.integration == nil)
        let terminalFront = FrontAppContext(bundleIdentifier: "com.apple.Terminal", appName: "Terminal", url: nil, windowTitle: nil, isBrowser: false)
        #expect(controller.prompt(for: terminalFront, skills: [youtube, terminal]) == nil)
        #expect(prompt.examplePrompts.first == "How do I change the speed / quality?")

        // A site the skills do not cover never prompts.
        var elsewhere = front
        elsewhere.url = URL(string: "https://example.com")
        #expect(controller.prompt(for: elsewhere, skills: [youtube]) == nil)

        // "Yes" without Composio turns into the notice; with Composio it hands the agent the connection task.
        controller.isComposioConfigured = { false }
        #expect(controller.apply(.yes, to: prompt)?.stage == .composioMissing)
        var submittedTask: String?
        controller.isComposioConfigured = { true }
        controller.submitToAgent = { submittedTask = $0 }
        #expect(controller.apply(.yes, to: prompt) == nil)
        #expect(submittedTask?.contains("toolkit \"youtube\"") == true)
        #expect(submittedTask?.contains("YouTube") == true)

        // "No" is remembered across launches and keeps the skill out of the talk prompts.
        controller.apply(.no, to: prompt)
        #expect(controller.declinedSkillIds == ["youtube"])
        #expect(controller.prompt(for: front, skills: [youtube]) == nil)

        let relaunched = AppConnectPromptController(userDefaults: userDefaults)
        #expect(relaunched.declinedSkillIds == ["youtube"])
        #expect(relaunched.prompt(for: front, skills: [youtube]) == nil)
    }
}
