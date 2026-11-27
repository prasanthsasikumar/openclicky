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
}
