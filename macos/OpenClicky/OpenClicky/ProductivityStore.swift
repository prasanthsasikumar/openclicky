//
//  ProductivityStore.swift
//  OpenClicky
//
//  ATMIYA AI's local meeting transcripts and reminders. The JSON file is deliberately small and
//  readable, lives only in Application Support, and contains no provider credentials.
//

import Combine
import Foundation
import UserNotifications

struct MeetingTranscriptRecord: Codable, Equatable, Identifiable {
    var id: UUID = UUID()
    var title: String
    var transcript: String
    var startedAt: Date
    var endedAt: Date
    var languageCode: String
}

struct AtmiyaReminder: Codable, Equatable, Identifiable {
    var id: UUID = UUID()
    var title: String
    var fireDate: Date
    var isCompleted = false
}

private struct ProductivityArchive: Codable {
    var meetings: [MeetingTranscriptRecord] = []
    var reminders: [AtmiyaReminder] = []
}

@MainActor
final class ProductivityStore: ObservableObject {
    static let shared = ProductivityStore()

    @Published private(set) var meetings: [MeetingTranscriptRecord] = []
    @Published private(set) var reminders: [AtmiyaReminder] = []
    @Published private(set) var lastError: String?

    let fileURL: URL
    private let notificationCenter: UNUserNotificationCenter?

    init(
        fileURL: URL? = nil,
        notificationCenter: UNUserNotificationCenter? = .current()
    ) {
        self.fileURL = fileURL ?? Self.defaultFileURL
        self.notificationCenter = notificationCenter
        load()
    }

    nonisolated static var defaultFileURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return base
            .appendingPathComponent("ATMIYA AI", isDirectory: true)
            .appendingPathComponent("Productivity", isDirectory: true)
            .appendingPathComponent("productivity.json")
    }

    var pendingReminders: [AtmiyaReminder] {
        reminders.filter { !$0.isCompleted }.sorted { $0.fireDate < $1.fireDate }
    }

    func addMeeting(_ meeting: MeetingTranscriptRecord) {
        meetings.insert(meeting, at: 0)
        meetings = Array(meetings.prefix(100))
        save()
    }

    func addReminder(title: String, fireDate: Date) async -> Bool {
        let title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty, fireDate > Date() else {
            lastError = "Choose a future time and enter what you want to remember."
            return false
        }

        let reminder = AtmiyaReminder(title: title, fireDate: fireDate)
        guard await scheduleNotification(for: reminder) else { return false }
        reminders.append(reminder)
        reminders.sort { $0.fireDate < $1.fireDate }
        save()
        return true
    }

    func setReminderCompleted(_ id: UUID, _ completed: Bool) {
        guard let index = reminders.firstIndex(where: { $0.id == id }) else { return }
        reminders[index].isCompleted = completed
        if completed {
            notificationCenter?.removePendingNotificationRequests(withIdentifiers: [Self.notificationIdentifier(id)])
        }
        save()
    }

    func removeReminder(_ id: UUID) {
        reminders.removeAll { $0.id == id }
        notificationCenter?.removePendingNotificationRequests(withIdentifiers: [Self.notificationIdentifier(id)])
        save()
    }

    func reload() { load() }

    private func load() {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return }
        do {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            let archive = try decoder.decode(ProductivityArchive.self, from: Data(contentsOf: fileURL))
            meetings = archive.meetings.sorted { $0.startedAt > $1.startedAt }
            reminders = archive.reminders.sorted { $0.fireDate < $1.fireDate }
            lastError = nil
        } catch {
            lastError = "Could not read local productivity data: \(error.localizedDescription)"
        }
    }

    private func save() {
        do {
            try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            encoder.dateEncodingStrategy = .iso8601
            // Decode older and current archives consistently by using ISO-8601 on both sides.
            let archive = ProductivityArchive(meetings: meetings, reminders: reminders)
            let data = try encoder.encode(archive)
            try data.write(to: fileURL, options: .atomic)
            lastError = nil
        } catch {
            lastError = "Could not save local productivity data: \(error.localizedDescription)"
        }
    }

    private func scheduleNotification(for reminder: AtmiyaReminder) async -> Bool {
        guard let notificationCenter else { return true }
        do {
            let settings = await notificationCenter.notificationSettings()
            if settings.authorizationStatus == .notDetermined {
                guard try await notificationCenter.requestAuthorization(options: [.alert, .sound]) else {
                    lastError = "Notifications are off. Allow them in System Settings to create reminders."
                    return false
                }
            } else if settings.authorizationStatus != .authorized && settings.authorizationStatus != .provisional {
                lastError = "Notifications are off. Allow them in System Settings to create reminders."
                return false
            }

            let content = UNMutableNotificationContent()
            content.title = "ATMIYA AI reminder"
            content.body = reminder.title
            content.sound = .default
            let components = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute], from: reminder.fireDate)
            let request = UNNotificationRequest(
                identifier: Self.notificationIdentifier(reminder.id),
                content: content,
                trigger: UNCalendarNotificationTrigger(dateMatching: components, repeats: false)
            )
            try await notificationCenter.add(request)
            return true
        } catch {
            lastError = "Could not schedule the reminder: \(error.localizedDescription)"
            return false
        }
    }

    private static func notificationIdentifier(_ id: UUID) -> String {
        "atmiya.reminder.\(id.uuidString)"
    }
}
