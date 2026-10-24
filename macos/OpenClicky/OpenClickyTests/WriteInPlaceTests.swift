//
//  WriteInPlaceTests.swift
//  OpenClickyTests
//
//  "Rephrase this": which text a rewrite replaces, that it is never pasted once focus has moved,
//  and what the model is told.
//

import AppKit
import Testing
@testable import OpenClicky

struct WriteInPlaceTests {
    private func field(value: String? = nil, selected: String? = nil, editable: Bool = true, secure: Bool = false,
                       app: String = "com.google.Chrome") -> FocusedFieldSnapshot {
        FocusedFieldSnapshot(appBundleID: app, appName: "Google Chrome", role: "AXTextArea", isEditable: editable,
                             isSecure: secure, value: value, selectedText: selected)
    }

    @Test func aHighlightIsTheTarget() {
        let target = WritingTarget.target(in: field(value: "Hi Ravi, see you at 5. Amma", selected: "see you at 5."), ownBundleID: "org.openclicky.app")
        #expect(target?.scope == .selection)
        #expect(target?.text == "see you at 5.")
    }

    @Test func withoutAHighlightTheWholeBoxIsTheTarget() {
        let target = WritingTarget.target(in: field(value: "Hi Ravi,\nsee you at 5.\nAmma"), ownBundleID: "org.openclicky.app")
        #expect(target?.scope == .wholeField)
        #expect(target?.text == "Hi Ravi,\nsee you at 5.\nAmma")
    }

    @Test func nothingToRewriteMeansNoTarget() {
        #expect(WritingTarget.target(in: field(value: "   "), ownBundleID: "x") == nil)
        #expect(WritingTarget.target(in: field(value: "secret", secure: true), ownBundleID: "x") == nil)
        #expect(WritingTarget.target(in: field(value: "a page", editable: false), ownBundleID: "x") == nil)
        #expect(WritingTarget.target(in: field(value: "my own window", app: "org.openclicky.app"), ownBundleID: "org.openclicky.app") == nil)
        #expect(WritingTarget.target(in: field(value: String(repeating: "a", count: WritingTarget.maximumCharacters + 1)), ownBundleID: "x") == nil)
    }

    @Test func itIsOnlyPastedIntoTheSameTextStill() {
        let target = WritingTarget(scope: .wholeField, text: "Hi Ravi", appBundleID: "com.google.Chrome", appName: "Google Chrome")
        #expect(target.stillMatches(field(value: "Hi Ravi")))
        #expect(!target.stillMatches(field(value: "Hi Ravi, also")))
        #expect(!target.stillMatches(field(value: "Hi Ravi", app: "com.apple.mail")))
        #expect(!target.stillMatches(field(value: "Hi Ravi", editable: false)))
        let highlighted = WritingTarget(scope: .selection, text: "at 5", appBundleID: "com.google.Chrome", appName: nil)
        #expect(highlighted.stillMatches(field(value: "see you at 5", selected: "at 5")))
        #expect(!highlighted.stillMatches(field(value: "see you at 5", selected: nil)))
    }

    @Test @MainActor func theQuestionCarriesTheExactWords() {
        let target = WritingTarget(scope: .wholeField, text: "Hi Ravi,\nsee you at 5.", appBundleID: "com.google.Chrome", appName: "Google Chrome")
        let prompt = CompanionManager.askUserPrompt(transcript: "can you rephrase this email", writingTarget: target)
        #expect(prompt.hasPrefix("can you rephrase this email"))
        #expect(prompt.contains("Hi Ravi,\nsee you at 5."))
        #expect(prompt.contains("in Google Chrome"))
        #expect(prompt.contains(WriteInPlace.toolName))
    }

    @Test func theToolAsksForTheWholeNewText() {
        let definition = WriteInPlace.anthropicToolDefinition()
        #expect(definition["name"] as? String == "write_in_place")
        let schema = definition["input_schema"] as? [String: Any]
        #expect(schema?["required"] as? [String] == ["text"])
    }

    @Test func theRewriteKeepsTheSpacingAroundTheOldText() {
        #expect(WriteInPlace.keepingSurroundingSpace(of: "sorry it took long.\n", around: "Sorry for the delay.") == "Sorry for the delay.\n")
        #expect(WriteInPlace.keepingSurroundingSpace(of: " at 5", around: "\nat five\n") == " at five")
        #expect(WriteInPlace.keepingSurroundingSpace(of: "hi ravi", around: "Hi Ravi,") == "Hi Ravi,")
    }

    @Test func eachOutcomeSaysWhatHappened() {
        #expect(WriteInPlace.Outcome.replaced.spokenSentence.contains("command z"))
        #expect(WriteInPlace.Outcome.targetGone.spokenSentence.contains("didn't change anything"))
        #expect(!WriteInPlace.Outcome.targetGone.didReplace)
        for outcome in [WriteInPlace.Outcome.replaced, .probablyReplaced, .targetGone, .leftOnClipboard, .noAccessibility, .emptyText] {
            #expect(!outcome.spokenSentence.contains("—"))
        }
    }

    // Gmail in Chrome on a tester's Mac gave Accessibility nothing at all (a second Mac on
    // 2026-10-18): the words they highlighted are then read by copying them, and a highlight read
    // that way is replaced on the same footing.
    private func nothingReadable(app: String = "com.google.Chrome") -> FocusedFieldSnapshot {
        FocusedFieldSnapshot(appBundleID: app, appName: "Google Chrome", role: nil, isEditable: false, isSecure: false, value: nil, selectedText: nil)
    }

    @Test func aCopiedHighlightIsTheTargetWhenAccessibilityReadsNothing() {
        let target = WritingTarget.target(in: nothingReadable(), copiedSelection: "see you at 5.", ownBundleID: "org.openclicky.app")
        #expect(target?.scope == .selection)
        #expect(target?.source == .clipboard)
        #expect(target?.text == "see you at 5.")
        #expect(target?.appBundleID == "com.google.Chrome")
    }

    @Test func whatAccessibilityReadsWinsOverTheCopiedHighlight() {
        let target = WritingTarget.target(in: field(value: "Hi Ravi, see you at 5.", selected: "see you at 5."), copiedSelection: "an older copy", ownBundleID: "x")
        #expect(target?.source == .accessibility)
        #expect(target?.text == "see you at 5.")
    }

    @Test func aCopiedHighlightIsNoTargetWhereAccessibilityCanSeeThereIsNoTextBox() {
        // A readable element that is not editable (words selected on a web page): nothing to write over.
        #expect(WritingTarget.target(in: field(value: "a page", editable: false), copiedSelection: "a page", ownBundleID: "x") == nil)
        #expect(WritingTarget.target(in: field(value: "secret", secure: true), copiedSelection: "secret", ownBundleID: "x") == nil)
        #expect(WritingTarget.target(in: nothingReadable(app: "org.openclicky.app"), copiedSelection: "mine", ownBundleID: "org.openclicky.app") == nil)
        #expect(WritingTarget.target(in: nothingReadable(), copiedSelection: " \n", ownBundleID: "x") == nil)
        #expect(WritingTarget.target(in: nothingReadable(), copiedSelection: nil, ownBundleID: "x") == nil)
        #expect(WritingTarget.target(in: nothingReadable(), copiedSelection: String(repeating: "a", count: WritingTarget.maximumCharacters + 1), ownBundleID: "x") == nil)
        // An editable box Accessibility can read but which holds nothing: the copy is still tried.
        #expect(WritingTarget.target(in: field(value: ""), copiedSelection: "see you", ownBundleID: "x")?.source == .clipboard)
    }

    @Test func aCopiedHighlightIsOnlyReplacedWhileTheSameWordsAreStillHighlighted() {
        let target = WritingTarget(scope: .selection, text: "at 5", appBundleID: "com.google.Chrome", appName: nil, source: .clipboard)
        #expect(target.stillMatches(copiedSelection: "at 5"))
        #expect(!target.stillMatches(copiedSelection: "at 5 pm"))
        #expect(!target.stillMatches(copiedSelection: nil))
    }

    @Test func onlyWordsCountAsACopiedHighlight() {
        // Files copied from Finder carry their names as text: never a highlight to write over.
        #expect(SelectionCopier.selectionText(string: "Report.docx", types: [.fileURL, .string]) == nil)
        #expect(SelectionCopier.selectionText(string: "see you", types: [.string]) == "see you")
        #expect(SelectionCopier.selectionText(string: " \n", types: [.string]) == nil)
        #expect(SelectionCopier.selectionText(string: nil, types: []) == nil)
    }

    @Test func withoutATargetTheModelAsksForAHighlightInsteadOfReadingARewriteOut() {
        #expect(WriteInPlace.noTargetPromptInstruction.contains("highlight"))
        #expect(!WriteInPlace.noTargetPromptInstruction.contains("—"))
    }
}

/// The fast voice lane rewrites in place too. It never had the tool: with an own OpenAI key the
/// ⌃⌥ question goes to Realtime, and "rephrase this" in a Gmail draft was read out (2026-10-23).
struct RealtimeWriteInPlaceTests {
    @Test func theRealtimeSessionGetsTheTool() {
        let tool = WriteInPlace.realtimeToolDefinition()
        #expect(tool["type"] as? String == "function")
        #expect(tool["name"] as? String == WriteInPlace.toolName)
        #expect(((tool["parameters"] as? [String: Any])?["required"] as? [String]) == ["text"])
    }

    @Test func theTurnCarriesTheTextToWriteOver() {
        let target = WritingTarget(scope: .selection, text: "hey can u send it", appBundleID: "com.google.Chrome", appName: "Google Chrome")
        let caption = RealtimeVoiceClient.screenCaption("Screenshot of the screen.", writingTarget: target, selectedText: "ignored")
        #expect(caption.contains("«hey can u send it»"))
        #expect(caption.contains("write_in_place"))
        #expect(!caption.contains("ignored"))
        let plain = RealtimeVoiceClient.screenCaption("Screenshot of the screen.", writingTarget: nil, selectedText: nil)
        #expect(plain == "Screenshot of the screen.")
    }

    @Test func doingItAndExplainingHowAreToldApart() {
        let instructions = RealtimeVoiceClient.defaultInstructions
        #expect(instructions.contains("write_in_place"))
        #expect(instructions.contains("how to rephrase"))
    }
}

/// Apple Mail's message body is an editable web area that reports no text to Accessibility, so
/// "rephrase this" with the cursor in an email (nothing highlighted) found nothing to write over,
/// while the same request worked in Gmail, whose box Chrome reads out (2026-10-24).
struct UnreadableBoxWriteInPlaceTests {
    private func mailBody(value: String? = "", role: String = "AXWebArea", editable: Bool = true) -> FocusedFieldSnapshot {
        FocusedFieldSnapshot(appBundleID: "com.apple.mail", appName: "Mail", role: role, isEditable: editable,
                             isSecure: false, value: value, selectedText: nil)
    }

    @Test func anEditableWebBoxWithNoReadableTextIsTheWholeBoxReadFromTheScreen() {
        for body in [mailBody(), mailBody(value: nil), mailBody(role: "AXGroup")] {
            let target = WritingTarget.target(in: body, copiedSelection: nil, ownBundleID: "org.openclicky.app")
            #expect(target?.source == .screenOnly)
            #expect(target?.scope == .wholeField)
            #expect(target?.appBundleID == "com.apple.mail")
        }
    }

    @Test func aHighlightStillWinsAndRealTextBoxesAreNotGuessed() {
        // Words highlighted in the body are copied and used as they are.
        #expect(WritingTarget.target(in: mailBody(), copiedSelection: "tmrw", ownBundleID: "x")?.source == .clipboard)
        // An empty native text box is empty, not unreadable.
        #expect(WritingTarget.target(in: mailBody(role: "AXTextArea"), copiedSelection: nil, ownBundleID: "x") == nil)
        // A page that is not editable, or an element nobody can see, is no target.
        #expect(WritingTarget.target(in: mailBody(editable: false), copiedSelection: nil, ownBundleID: "x") == nil)
        #expect(WritingTarget.target(in: mailBody(role: "AXWebArea", editable: true), copiedSelection: nil, ownBundleID: "com.apple.mail") == nil)
    }

    @Test func theModelIsToldToReadTheBoxFromTheScreenshot() {
        let target = WritingTarget(scope: .wholeField, text: "", appBundleID: "com.apple.mail", appName: "Mail", source: .screenOnly)
        #expect(target.contextLine.contains("screenshot"))
        #expect(target.contextLine.contains("write_in_place"))
        #expect(target.kindForLog == "whole box read from the screen")
    }

    @Test func itIsOnlyReplacedWhileTheCursorIsStillInThatBox() {
        let target = WritingTarget(scope: .wholeField, text: "", appBundleID: "com.apple.mail", appName: "Mail", source: .screenOnly)
        #expect(target.cursorIsStillInTheBox(mailBody()))
        #expect(!target.cursorIsStillInTheBox(mailBody(editable: false)))
        #expect(!target.cursorIsStillInTheBox(FocusedFieldSnapshot(appBundleID: "com.google.Chrome", appName: "Chrome", role: "AXWebArea",
                                                                   isEditable: true, isSecure: false, value: "", selectedText: nil)))
    }
}
