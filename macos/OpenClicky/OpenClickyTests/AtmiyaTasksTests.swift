import Foundation
import Testing
@testable import OpenClicky

struct AtmiyaTasksTests {
    @Test func askingForTheListIsNotATask() {
        #expect(AtmiyaTasks.asksForTheList("what can you do"))
        #expect(AtmiyaTasks.asksForTheList("give me the prompts"))
        #expect(AtmiyaTasks.asksForTheList("show me the keyboard tasks"))
        #expect(!AtmiyaTasks.asksForTheList("open chrome"))
        #expect(!AtmiyaTasks.asksForTheList("search YouTube for lo-fi"))
    }

    @Test func everyImmediateTaskHasAPromptTheAppCanRun() {
        let immediate = AtmiyaTasks.all.filter(\.runsImmediately)
        #expect(!immediate.isEmpty)
        for task in immediate {
            #expect(!task.prompt.trimmingCharacters(in: .whitespaces).isEmpty)
            #expect(!AtmiyaTasks.asksForTheList(task.prompt))
        }
        #expect(AtmiyaTasks.spokenList.contains("Open Chrome"))
    }

    @Test func theMasterPromptPastesAndRunsWithoutStoringItself() {
        #expect(AtmiyaTasks.asksToPastePrompts(AtmiyaTasks.masterPrompt))
        #expect(AtmiyaTasks.asksToRunTasks(AtmiyaTasks.masterPrompt))
        var saved: [String] = []
        saved = AtmiyaPromptLog.remember("Open Chrome", in: saved)
        saved = AtmiyaPromptLog.remember("Open Chrome", in: saved)
        saved = AtmiyaPromptLog.remember(AtmiyaTasks.masterPrompt, in: saved)
        saved = AtmiyaPromptLog.remember("Delete my downloads", in: saved)
        #expect(saved == ["Open Chrome", "Delete my downloads"])
        #expect(AtmiyaPromptLog.isSafeToRun("Open Chrome"))
        #expect(!AtmiyaPromptLog.isSafeToRun("Delete my downloads"))
        #expect(AtmiyaPromptLog.taskInstruction(for: saved).contains("Open Chrome"))
        #expect(!AtmiyaPromptLog.taskInstruction(for: saved).contains("Delete"))
    }

    @Test func onlyHeyRisWakesTheAssistant() {
        #expect(RisWakePhrase.command(in: "Hey Ris") == "")
        #expect(RisWakePhrase.command(in: "hey riz, open Chrome") == "open chrome")
        #expect(RisWakePhrase.command(in: "Hey Riss do my tasks") == "do my tasks")
        #expect(RisWakePhrase.command(in: "um hey ris") == "")
        #expect(RisWakePhrase.command(in: "Hey Atmiya") == nil)
        #expect(RisWakePhrase.command(in: "Atmiya") == nil)
        #expect(RisWakePhrase.command(in: "Atmiya AI open Chrome") == nil)
        #expect(RisWakePhrase.command(in: "open Chrome") == nil)
        #expect(RisWakePhrase.command(in: "please hey ris open Chrome") == nil)
    }
}
