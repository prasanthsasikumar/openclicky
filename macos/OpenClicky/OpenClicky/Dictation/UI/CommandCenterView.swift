//
//  CommandCenterView.swift
//  OpenClicky
//
//  ATMIYA AI's operational home: live readiness, provider, permissions, task state and the
//  few actions a person needs before asking the assistant to do work.
//

import Speech
import SwiftUI

struct CommandCenterStatus: Equatable {
    let title: String
    let detail: String
    let symbol: String

    static func current(voiceState: CompanionVoiceState, agentActivity: String?) -> CommandCenterStatus {
        if let agentActivity, !agentActivity.isEmpty {
            return CommandCenterStatus(title: "Working", detail: agentActivity, symbol: "gearshape.2.fill")
        }
        switch voiceState {
        case .idle:
            return CommandCenterStatus(title: "Ready", detail: "Waiting for your voice or a task", symbol: "checkmark.circle.fill")
        case .listening:
            return CommandCenterStatus(title: "Listening", detail: "Speak naturally", symbol: "waveform.circle.fill")
        case .processing:
            return CommandCenterStatus(title: "Thinking", detail: "Understanding your request", symbol: "brain.head.profile.fill")
        case .responding:
            return CommandCenterStatus(title: "Speaking", detail: "Answering now", symbol: "speaker.wave.2.circle.fill")
        }
    }
}

@MainActor
struct CommandCenterView: View {
    @ObservedObject var model: DictationWindowModel
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var shellSettingsRevision = ShellSettingsRevision.shared
    @State private var stoppedAt: Date?

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
    }

    private var status: CommandCenterStatus {
        CommandCenterStatus.current(voiceState: companionManager.voiceState, agentActivity: companionManager.agentActivityText)
    }

    private var route: ProviderRoute { ProviderRouter.current(for: .claude) }

    private var providerName: String {
        switch route {
        case .answerProvider(let source): return source.displayName
        case .anthropicDirect: return AnswerSource.anthropic.displayName
        case .openAIDirect: return AnswerSource.openAI.displayName
        case .backend: return AnswerSource.openClickyAccount.displayName
        case .unavailable: return "Not configured"
        }
    }

    private var providerDetail: String {
        if case .answerProvider(.ollama) = route {
            let model = OpenClickyConfiguration.settings.ollamaModel ?? "local model"
            return "Private · \(model)"
        }
        return route.unavailableSentence ?? "Cloud requests are sent only when you ask"
    }

    private var workspaceName: String {
        let path = OpenClickyConfiguration.workspacePath
        return path.replacingOccurrences(of: NSHomeDirectory(), with: "~")
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 26) {
                statusHeader
                Divider()
                readiness
                quickActions
                activity
            }
            .padding(.horizontal, 30)
            .padding(.vertical, 26)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .accessibilityElement(children: .contain)
    }

    private var statusHeader: some View {
        HStack(alignment: .top, spacing: 16) {
            Image(systemName: status.symbol)
                .font(.system(size: 24, weight: .semibold))
                .foregroundStyle(status.title == "Ready" ? SettingsLook.success : SettingsLook.accent)
                .frame(width: 42, height: 42)
                .background(Circle().fill((status.title == "Ready" ? SettingsLook.success : SettingsLook.accent).opacity(0.12)))
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 4) {
                Text("ATMIYA AI is \(status.title.lowercased())")
                    .font(.system(size: 24, weight: .semibold))
                    .tracking(-0.35)
                Text(status.detail)
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
            }

            Spacer(minLength: 16)

            Button(role: .destructive, action: emergencyStop) {
                Label("Emergency Stop", systemImage: "stop.fill")
            }
            .buttonStyle(.bordered)
            .tint(SettingsLook.danger)
            .keyboardShortcut(".", modifiers: [.command, .shift])
            .help("Stop voice, listening, guided actions and agent work (⇧⌘.)")
            .pointerCursor()
        }
        .overlay(alignment: .bottomLeading) {
            if stoppedAt != nil {
                Label("All ATMIYA AI activity was stopped.", systemImage: "checkmark.shield.fill")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(SettingsLook.success)
                    .offset(y: 20)
                    .transition(.opacity)
            }
        }
    }

    private var readiness: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Ready for work")
                .font(.system(size: 17, weight: .semibold))

            VStack(spacing: 0) {
                overviewRow(symbol: "cpu", title: "AI provider", value: providerName, detail: providerDetail)
                Divider().padding(.leading, 42)
                overviewRow(symbol: "folder", title: "Workspace", value: workspaceName, detail: "Agent changes stay inside approved locations")
                Divider().padding(.leading, 42)
                overviewRow(
                    symbol: "externaldrive.fill",
                    title: "Memory",
                    value: settings.keepMemoryOnThisMac ? "On this Mac" : "Local history disabled",
                    detail: settings.incognito ? "Incognito is on · new takes are not saved" : "History, dictionary and styles remain local"
                )
            }
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))

            permissions
        }
    }

    private func overviewRow(symbol: String, title: String, value: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 14, weight: .semibold))
                .foregroundStyle(SettingsLook.accent)
                .frame(width: 30, height: 30)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(SettingsLook.accent.opacity(0.1)))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
                Text(value).font(.system(size: 13, weight: .semibold)).lineLimit(1).truncationMode(.middle)
                Text(detail).font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .accessibilityElement(children: .combine)
    }

    private var permissions: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack {
                Text("Permissions").font(.system(size: 13, weight: .semibold))
                Spacer()
                Button("Manage") { model.select(.settings(.privacy)) }
                    .buttonStyle(.link)
                    .pointerCursor()
            }
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) { permissionBadges }
                VStack(alignment: .leading, spacing: 8) { permissionBadges }
            }
        }
    }

    @ViewBuilder private var permissionBadges: some View {
        permissionBadge("Microphone", granted: companionManager.hasMicrophonePermission)
        permissionBadge("Screen", granted: companionManager.hasScreenRecordingPermission && companionManager.hasScreenContentPermission)
        permissionBadge("Accessibility", granted: companionManager.hasAccessibilityPermission)
        permissionBadge("Speech", granted: SFSpeechRecognizer.authorizationStatus() == .authorized)
    }

    private func permissionBadge(_ title: String, granted: Bool) -> some View {
        Label(title, systemImage: granted ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(granted ? SettingsLook.success : SettingsLook.warning)
            .padding(.horizontal, 9)
            .padding(.vertical, 5)
            .background(Capsule().fill((granted ? SettingsLook.success : SettingsLook.warning).opacity(0.1)))
            .accessibilityLabel("\(title): \(granted ? "allowed" : "needs attention")")
    }

    private var quickActions: some View {
        VStack(alignment: .leading, spacing: 11) {
            Text("Quick actions").font(.system(size: 17, weight: .semibold))
            HStack(spacing: 10) {
                commandButton("Open Island", symbol: "rectangle.topthird.inset.filled") {
                    companionManager.notchHUDManager.openPanel()
                }
                commandButton("Your words", symbol: "text.bubble.fill") { model.push(.section(.words)) }
                commandButton("AI & voice", symbol: "waveform.and.mic") { model.select(.settings(.clicky)) }
                commandButton("Privacy", symbol: "hand.raised.fill") { model.select(.settings(.privacy)) }
            }
        }
    }

    private func commandButton(_ title: String, symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: symbol)
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(.bordered)
        .controlSize(.large)
        .pointerCursor()
    }

    private var activity: some View {
        VStack(alignment: .leading, spacing: 11) {
            Text("Agent activity").font(.system(size: 17, weight: .semibold))
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: companionManager.agentActivityText == nil ? "clock.arrow.circlepath" : "gearshape.2.fill")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(companionManager.agentActivityText == nil ? Color.secondary : SettingsLook.warning)
                    .frame(width: 30, height: 30)
                VStack(alignment: .leading, spacing: 3) {
                    Text(activityTitle).font(.system(size: 13, weight: .semibold))
                    Text(activityDetail).font(.system(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            .padding(14)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color(nsColor: .controlBackgroundColor)))
        }
    }

    private var activityTitle: String {
        if companionManager.agentActivityText != nil { return "Task in progress" }
        if let result = companionManager.lastAgentResult { return result.title }
        return "No agent task is running"
    }

    private var activityDetail: String {
        if let activity = companionManager.agentActivityText { return activity }
        if let result = companionManager.lastAgentResult {
            return "Completed \(result.finishedAt.formatted(date: .abbreviated, time: .shortened)) · \(result.artifacts.count) file\(result.artifacts.count == 1 ? "" : "s")"
        }
        return "Ask ATMIYA AI to open a project, inspect a problem, or complete an approved task."
    }

    private func emergencyStop() {
        companionManager.emergencyStop()
        withAnimation(.easeOut(duration: 0.2)) { stoppedAt = Date() }
        Task {
            try? await Task.sleep(for: .seconds(4))
            await MainActor.run { withAnimation(.easeOut(duration: 0.2)) { stoppedAt = nil } }
        }
    }
}
