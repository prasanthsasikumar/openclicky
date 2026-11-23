//
//  IslandReplyTests.swift
//  OpenClickyTests
//
//  The reply card: the next questions read out of a model's reply and when they are asked for,
//  a job's steps, what an answer shows as, the card's height, and where it ranks on the island.
//

import CoreGraphics
import Foundation
import Testing
@testable import OpenClicky

struct NextQuestionsTests {
    @Test func readsUpToThreeShortLines() {
        let reply = """
        1. Why is the sky red at sunset?
        - "Is it the same on Mars?"
        • What about the sea?
        A fourth question nobody sees?
        """
        #expect(NextQuestions.parse(reply) == ["Why is the sky red at sunset?", "Is it the same on Mars?", "What about the sea?"])
    }

    @Test func leavesOutRepeatsLongLinesAndNone() {
        let long = String(repeating: "very ", count: 20) + "long?"
        #expect(NextQuestions.parse("Why?\nwhy?\n\(long)\n2) How so?") == ["Why?", "How so?"])
        #expect(NextQuestions.parse("NONE").isEmpty)
        #expect(NextQuestions.parse("Why?\nNONE").isEmpty)
        #expect(NextQuestions.parse("  \n").isEmpty)
        #expect(NextQuestions.parse("آسمان نیلا کیوں ہے؟") == ["آسمان نیلا کیوں ہے؟"])
    }

    @Test func notForSmallTalkOrShortAnswers() {
        let answer = String(repeating: "The sky scatters blue light more than red. ", count: 3)
        #expect(NextQuestions.isWorthOffering(question: "why is the sky blue", answer: answer))
        #expect(!NextQuestions.isWorthOffering(question: "why is the sky blue", answer: "done."))
        #expect(!NextQuestions.isWorthOffering(question: "Thank you!", answer: answer))
        #expect(!NextQuestions.isWorthOffering(question: "ok", answer: answer))
    }

    @Test func onlyOnTheirOwnKeyOrPlan() {
        #expect(NextQuestions.mayAsk(route: .anthropicDirect))
        #expect(NextQuestions.mayAsk(route: .answerProvider(.claudeCodePlan)))
        #expect(NextQuestions.mayAsk(route: .answerProvider(.gemini)))
        #expect(!NextQuestions.mayAsk(route: .answerProvider(.openClickyAccount)))
        #expect(!NextQuestions.mayAsk(route: .backend))
        #expect(!NextQuestions.mayAsk(route: .unavailable("no key")))
    }
}

struct IslandReplyStateTests {
    @Test func aNewMilestoneTicksTheOneBeforeIt() {
        var state = IslandReplyState(question: "compare tools", showsAnswer: true)
        state.addStep("starting agent…")
        state.addStep("starting agent…")           // the same line twice is one step
        state.addStep(" reading the pages ")
        #expect(state.steps.map(\.text) == ["starting agent…", "reading the pages"])
        #expect(state.steps.map(\.isDone) == [true, false])
        for index in 0..<6 { state.addStep("step \(index)") }
        #expect(state.visibleSteps.count == IslandReplyLayout.maximumSteps)
        #expect(state.visibleSteps.last?.text == "step 5")
        state.isFinished = true
        #expect(state.visibleSteps.isEmpty)          // the steps fold away when the answer is in
    }

    @MainActor @Test func anAnswerIsShownWithoutItsTagsAndNotAtFullLength() {
        #expect(IslandReplyController.shown("Click the gear. [POINT:120,44:gear:screen1] Then pick General. [NEXT]") == "Click the gear.  Then pick General.")
        #expect(IslandReplyController.shown("Click the gear. [POI") == "Click the gear.")
        #expect(IslandReplyController.shown("Click the gear. [POINT:120,4") == "Click the gear.")
        #expect(IslandReplyController.shown("Prices [in dollars] rose.") == "Prices [in dollars] rose.")
        let long = IslandReplyController.shown(String(repeating: "word ", count: 400))
        #expect(long.count <= 901 && long.hasSuffix("…"))
    }

    @Test func theCardGrowsWithItsContentUpToALimit() {
        var state = IslandReplyState(question: "why is the sky blue", showsAnswer: true)
        let waiting = IslandReplyLayout.contentHeight(for: state)
        state.text = "Because air scatters blue light the most."
        let oneLine = IslandReplyLayout.contentHeight(for: state)
        state.text = String(repeating: "Because air scatters blue light the most. ", count: 8)
        let several = IslandReplyLayout.contentHeight(for: state)
        state.text = String(repeating: "Because air scatters blue light the most. ", count: 200)
        let capped = IslandReplyLayout.contentHeight(for: state)
        #expect(waiting > 40 && oneLine >= waiting && several > oneLine && capped > several)
        #expect(capped < IslandReplyLayout.maximumTextHeight + 80)
        state.nextQuestions = ["Why red at sunset?", "And on Mars?"]
        #expect(IslandReplyLayout.contentHeight(for: state) == capped + 8 + IslandReplyLayout.pillRowHeight)
        #expect(IslandReplyLayout.pillRows(["a", "b", "c"]) == 1)
        #expect(IslandReplyLayout.pillRows([String(repeating: "w", count: 50), String(repeating: "w", count: 50)]) == 2)
        #expect(IslandReplyState(question: "", showsAnswer: false).isEmpty)
    }
}

@MainActor
struct IslandReplyModelTests {
    private func notchModel() -> NotchHUDModel {
        let model = NotchHUDModel(geometry: NotchGeometry(screenFrame: CGRect(x: 0, y: 0, width: 1512, height: 982), hasHardwareNotch: true, notchWidth: 190, notchHeight: 33, visibleFrame: CGRect(x: 0, y: 0, width: 1512, height: 949)))
        model.opensOnRest = false
        return model
    }

    @Test func theAnswerInWordsOutranksTheBusyStrip() {
        let model = notchModel()
        model.setBusy(true)
        let typed = IslandReplyState(question: "why is the sky blue", showsAnswer: true)
        model.setReply(typed)
        #expect(model.expansion == .reply)
        #expect(model.height == model.cardTopInset + IslandReplyLayout.contentHeight(for: typed))
        model.setReply(nil)
        #expect(model.expansion != .reply)
    }

    @Test func nextQuestionsAloneWaitForTheStripAndYieldToPermissions() {
        let model = notchModel()
        model.setBusy(true)
        model.setReply(IslandReplyState(question: "q", nextQuestions: ["Why?"], showsAnswer: false))
        #expect(model.expansion != .reply)
        model.setBusy(false)
        #expect(model.expansion == .reply)
        model.setPermissionPrompt(PermissionPrompt(step: .accessibility, status: PermissionStatus()))
        #expect(model.expansion == .permission)
    }
}

@MainActor
struct IslandSettingsModelTests {
    private func notchModel() -> NotchHUDModel {
        let model = NotchHUDModel(geometry: NotchGeometry(screenFrame: CGRect(x: 0, y: 0, width: 1512, height: 982), hasHardwareNotch: true, notchWidth: 190, notchHeight: 33, visibleFrame: CGRect(x: 0, y: 0, width: 1512, height: 949)))
        model.opensOnRest = false
        return model
    }

    @Test func settingsDropDownStaysOpenUntilClosed() {
        let model = notchModel()
        model.showSettings(.clicky)
        #expect(model.expansion == .settings && model.settingsPage == .clicky)
        #expect(model.width == IslandSettingsLayout.width)
        #expect(model.height == model.cardTopInset + IslandSettingsLayout.preferredHeight)
        model.setHovering(true)
        model.setHovering(false)                 // the pointer leaving does not close them
        model.setBusy(true)                      // nor does Clicky getting busy
        #expect(model.expansion == .settings)
        model.showSettings(.island)              // turning the page keeps them open
        #expect(model.expansion == .settings && model.settingsPage == .island)
        model.islandClicked()                    // a click inside is the form's, not a toggle
        #expect(model.expansion == .settings)
        model.close()
        #expect(model.settingsPage == nil && model.expansion != .settings)
    }

    @Test func aShortScreenGetsAShorterDropDownAndPermissionsStillComeFirst() {
        #expect(IslandSettingsLayout.height(visibleHeight: 949) == IslandSettingsLayout.preferredHeight)
        #expect(IslandSettingsLayout.height(visibleHeight: 600) == 510)
        #expect(IslandSettingsLayout.height(visibleHeight: 300) == 320)
        let model = notchModel()
        model.showSettings(.general)
        model.setPermissionPrompt(PermissionPrompt(step: .accessibility, status: PermissionStatus()))
        #expect(model.expansion == .permission)
    }
}
