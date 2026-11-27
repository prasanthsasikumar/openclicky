//
//  ProductivityStoreTests.swift
//  OpenClickyTests
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct ProductivityStoreTests {
    @Test func meetingsAndRemindersRoundTripLocally() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let file = folder.appendingPathComponent("productivity.json")
        defer { try? FileManager.default.removeItem(at: folder) }

        let store = ProductivityStore(fileURL: file, notificationCenter: nil)
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        store.addMeeting(MeetingTranscriptRecord(
            title: "Gujarati planning call",
            transcript: "આપણે આવતીકાલે ફરી મળીશું.",
            startedAt: start,
            endedAt: start.addingTimeInterval(60),
            languageCode: "gu-IN"
        ))
        #expect(await store.addReminder(title: "Review meeting notes", fireDate: Date().addingTimeInterval(3_600)))

        let reloaded = ProductivityStore(fileURL: file, notificationCenter: nil)
        #expect(reloaded.meetings.count == 1)
        #expect(reloaded.meetings.first?.languageCode == "gu-IN")
        #expect(reloaded.pendingReminders.map(\.title) == ["Review meeting notes"])
    }

    @Test func completedRemindersLeaveThePendingList() async {
        let file = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathComponent("productivity.json")
        let store = ProductivityStore(fileURL: file, notificationCenter: nil)
        #expect(await store.addReminder(title: "A task", fireDate: Date().addingTimeInterval(3_600)))
        let id = store.pendingReminders[0].id
        store.setReminderCompleted(id, true)
        #expect(store.pendingReminders.isEmpty)
        try? FileManager.default.removeItem(at: file.deletingLastPathComponent())
    }

    @Test func invalidRemindersAreRejected() async {
        let file = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathComponent("productivity.json")
        let store = ProductivityStore(fileURL: file, notificationCenter: nil)
        #expect(!(await store.addReminder(title: "   ", fireDate: Date().addingTimeInterval(60))))
        #expect(!(await store.addReminder(title: "Too late", fireDate: Date().addingTimeInterval(-60))))
        #expect(store.reminders.isEmpty)
    }
}
