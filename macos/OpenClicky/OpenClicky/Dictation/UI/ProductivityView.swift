//
//  ProductivityView.swift
//  OpenClicky
//
//  One practical home for the local task, meeting, reminder, workflow and automation features.
//

import AppKit
import SwiftUI

@MainActor
struct ProductivityView: View {
    @ObservedObject var model: DictationWindowModel
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject private var store = ProductivityStore.shared
    @ObservedObject private var meeting = MeetingTranscriptionController.shared
    @ObservedObject private var skills: SkillLibraryStore

    @State private var reminderTitle = ""
    @State private var reminderDate = Date().addingTimeInterval(3_600)
    @State private var isAddingReminder = false
    @State private var expandedMeetingID: UUID?

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.skills = companionManager.skillLibraryStore
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 26) {
                header
                Divider()
                dailyTasks
                meetingTranscription
                reminders
                workflows
                automation
            }
            .padding(.horizontal, 30)
            .padding(.vertical, 26)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text("Productivity")
                .font(.system(size: 24, weight: .semibold))
                .tracking(-0.35)
            Text("Tasks, meetings, reminders and reusable workflows stay on this Mac.")
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
        }
    }

    private var dailyTasks: some View {
        featureSection("Daily task manager", symbol: "checklist") {
            HStack(alignment: .center, spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(taskSummary).font(.system(size: 13, weight: .semibold))
                    Text("ATMIYA AI can propose small jobs from your recent local work. You approve each job before it runs.")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 12)
                Button("Show today’s tasks") { companionManager.plateController.showOnRequest() }
                    .buttonStyle(.borderedProminent)
                    .tint(SettingsLook.accent)
                    .pointerCursor()
            }
        }
    }

    private var taskSummary: String {
        let count = companionManager.plateController.waitingCount
        return count == 0 ? "Ready to plan today" : "\(count) task\(count == 1 ? "" : "s") waiting for your decision"
    }

    private var meetingTranscription: some View {
        featureSection("Meeting transcription", symbol: "waveform.badge.mic") {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 10) {
                    Picker("Language", selection: $meeting.languageCode) {
                        Text("English").tag("en-IN")
                        Text("हिन्दी").tag("hi-IN")
                        Text("ગુજરાતી").tag("gu-IN")
                    }
                    .pickerStyle(.menu)
                    .frame(width: 150)
                    .disabled(meeting.isRecording || meeting.isBusy)

                    Button(meeting.isRecording ? "Stop and save" : "Start transcription") {
                        meeting.isRecording ? meeting.stop() : meeting.start()
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(meeting.isRecording ? SettingsLook.danger : SettingsLook.accent)
                    .disabled(meeting.isBusy)
                    .pointerCursor(isEnabled: !meeting.isBusy)

                    if meeting.isBusy { ProgressView().controlSize(.small) }
                    Spacer()
                    Text(meetingStatus)
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(meetingStatusColor)
                }

                if meeting.isRecording || !meeting.transcript.isEmpty {
                    Text(meeting.transcript.isEmpty ? "Listening…" : meeting.transcript)
                        .font(.system(size: 12))
                        .foregroundStyle(meeting.transcript.isEmpty ? .secondary : .primary)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, minHeight: 54, alignment: .topLeading)
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
                }

                if !store.meetings.isEmpty {
                    Divider()
                    Text("Saved locally").font(.system(size: 12, weight: .semibold))
                    ForEach(store.meetings.prefix(5)) { record in
                        Button {
                            expandedMeetingID = expandedMeetingID == record.id ? nil : record.id
                        } label: {
                            VStack(alignment: .leading, spacing: 4) {
                                HStack {
                                    Text(record.title).font(.system(size: 12, weight: .medium))
                                    Spacer()
                                    Text(Locale.current.localizedString(forIdentifier: record.languageCode) ?? record.languageCode)
                                        .font(.system(size: 10)).foregroundStyle(.secondary)
                                    Image(systemName: expandedMeetingID == record.id ? "chevron.up" : "chevron.down")
                                        .font(.system(size: 9, weight: .semibold)).foregroundStyle(.secondary)
                                }
                                if expandedMeetingID == record.id {
                                    Text(record.transcript).font(.system(size: 12)).foregroundStyle(.secondary)
                                        .textSelection(.enabled)
                                }
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .pointerCursor()
                    }
                }
            }
        }
    }

    private var meetingStatus: String {
        switch meeting.state {
        case .idle: return store.meetings.isEmpty ? "Local Apple Speech" : "Saved on this Mac"
        case .requestingPermission: return "Checking permission"
        case .recording: return "Recording"
        case .finishing: return "Saving"
        case .failed(let message): return message
        }
    }

    private var meetingStatusColor: Color {
        switch meeting.state {
        case .failed: return SettingsLook.danger
        case .recording: return SettingsLook.danger
        default: return .secondary
        }
    }

    private var reminders: some View {
        featureSection("Reminders", symbol: "bell.badge.fill") {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 8) {
                    TextField("What should I remind you about?", text: $reminderTitle)
                        .textFieldStyle(.roundedBorder)
                    DatePicker("", selection: $reminderDate, in: Date()..., displayedComponents: [.date, .hourAndMinute])
                        .labelsHidden()
                    Button("Add") { addReminder() }
                        .buttonStyle(.borderedProminent)
                        .tint(SettingsLook.accent)
                        .disabled(reminderTitle.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || isAddingReminder)
                        .pointerCursor(isEnabled: !reminderTitle.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !isAddingReminder)
                }

                if let error = store.lastError {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.system(size: 11)).foregroundStyle(SettingsLook.warning)
                }

                if store.pendingReminders.isEmpty {
                    Text("No pending reminders.").font(.system(size: 12)).foregroundStyle(.secondary)
                } else {
                    ForEach(store.pendingReminders) { reminder in
                        HStack(spacing: 9) {
                            Button { store.setReminderCompleted(reminder.id, true) } label: {
                                Image(systemName: "circle").foregroundStyle(.secondary)
                            }
                            .buttonStyle(.plain).help("Mark completed").pointerCursor()
                            VStack(alignment: .leading, spacing: 1) {
                                Text(reminder.title).font(.system(size: 12, weight: .medium))
                                Text(reminder.fireDate.formatted(date: .abbreviated, time: .shortened))
                                    .font(.system(size: 10)).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button(role: .destructive) { store.removeReminder(reminder.id) } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.plain).help("Delete reminder").pointerCursor()
                        }
                    }
                }
            }
        }
    }

    private var workflows: some View {
        featureSection("Recipes and reusable workflows", symbol: "arrow.triangle.branch") {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("\(skills.activeIds.count) active · \(skills.librarySkills.count) available")
                        .font(.system(size: 13, weight: .semibold))
                    Spacer()
                    if !skills.hasStarterWorkflows {
                        Button("Add starter recipes") { skills.installStarterWorkflows() }
                            .buttonStyle(.borderedProminent)
                            .tint(SettingsLook.accent)
                            .pointerCursor()
                    }
                    Button("Open workflow folder") { NSWorkspace.shared.open(skills.libraryDirectory) }
                        .buttonStyle(.bordered).pointerCursor()
                }
                Text("Save a useful process once, then run it again as a fresh agent task.")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
                if skills.librarySkills.isEmpty {
                    Label("Add the starter recipes or place a SKILL.md workflow in the folder.", systemImage: "square.stack.3d.up")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                } else {
                    ForEach(skills.librarySkills.prefix(8), id: \.id) { skill in
                        HStack(alignment: .center, spacing: 12) {
                            Toggle(isOn: Binding(
                                get: { skills.activeIds.contains(skill.id) },
                                set: { skills.setActive(skill.id, $0) }
                            )) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(skill.name).font(.system(size: 12, weight: .medium))
                                    Text(skill.description).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(2)
                                }
                            }
                            .toggleStyle(.switch)
                            Button("Run") { runWorkflow(skill) }
                                .buttonStyle(.bordered)
                                .help("Run this workflow as a new agent task")
                                .pointerCursor()
                        }
                    }
                }
                if let error = skills.lastError {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.system(size: 11)).foregroundStyle(SettingsLook.warning)
                }
            }
        }
    }

    private func runWorkflow(_ skill: SkillFile) {
        if !skills.activeIds.contains(skill.id) { skills.setActive(skill.id, true) }
        let request = """
        Run my \"\(skill.name)\" reusable workflow now. Follow its active SKILL.md instructions. Ask for any missing input. Use only local or connected sources I have already granted access to, and require my approval before external writes or sensitive actions.
        """
        companionManager.submitTextToAgent(request, startsNewThread: true, attachesScreen: false)
    }

    private var automation: some View {
        featureSection("Browser and computer automation", symbol: "macwindow.badge.plus") {
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 5) {
                    readinessLine("Accessibility control", ready: companionManager.hasAccessibilityPermission)
                    readinessLine("Screen understanding", ready: companionManager.hasScreenRecordingPermission && companionManager.hasScreenContentPermission)
                    Text("ATMIYA AI uses the Accessibility tree for precise controls and asks for approval before sensitive actions.")
                        .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 14)
                Button("Manage permissions") { model.select(.settings(.privacy)) }
                    .buttonStyle(.bordered).pointerCursor()
            }
        }
    }

    private func readinessLine(_ title: String, ready: Bool) -> some View {
        Label(title, systemImage: ready ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(ready ? SettingsLook.success : SettingsLook.warning)
    }

    private func featureSection<Content: View>(
        _ title: String,
        symbol: String,
        @ViewBuilder content: () -> Content
    ) -> some View {
        VStack(alignment: .leading, spacing: 11) {
            Label(title, systemImage: symbol)
                .font(.system(size: 17, weight: .semibold))
                .foregroundStyle(.primary)
            content()
                .padding(14)
                .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
        }
    }

    private func addReminder() {
        isAddingReminder = true
        Task {
            let added = await store.addReminder(title: reminderTitle, fireDate: reminderDate)
            if added {
                reminderTitle = ""
                reminderDate = Date().addingTimeInterval(3_600)
            }
            isAddingReminder = false
        }
    }
}
