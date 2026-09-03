//
//  NotchHUDPanels.swift
//  OpenClicky
//
//  The full notch app: tab bar + Home / Agents / Settings, the agent thread store that feeds the
//  Agents tab, and the floating result card (top-right) with "Follow up with agent".
//

import AppKit
import Combine
import SwiftUI

// MARK: - Full panel

struct NotchFullPanelView: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager
    @StateObject private var threadStore = AgentThreadStore()

    var body: some View {
        VStack(spacing: 0) {
            NotchTabBar(model: model, companionManager: companionManager, threadStore: threadStore)
                .padding(.horizontal, 22)
                .padding(.top, 14)
                .padding(.bottom, 10)

            Group {
                switch model.activeTab {
                case .home:
                    NotchHomeView(companionManager: companionManager, model: model)
                case .agents:
                    NotchAgentsView(companionManager: companionManager, threadStore: threadStore)
                case .settings:
                    NotchSettingsView(companionManager: companionManager)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        }
        .onChange(of: model.activeTab) { tab in
            if tab == .agents { threadStore.refresh() }
        }
        .onChange(of: model.expansion) { expansion in
            if expansion == .full && model.activeTab == .agents { threadStore.refresh() }
        }
    }
}

// MARK: - Tab bar

struct NotchTabBar: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var threadStore: AgentThreadStore

    var body: some View {
        HStack(spacing: 8) {
            tabPill(title: "Home", systemImage: "house", tab: .home)
            tabPill(title: "Agents", systemImage: "sparkles", tab: .agents)

            Spacer()

            backendStatusPill

            if model.activeTab == .agents {
                iconButton(systemImage: "arrow.clockwise", help: "Refresh agents") { threadStore.refresh() }
            }
            iconButton(systemImage: "gearshape.fill", help: "Settings") { model.select(.settings) }
                .background(
                    Circle().fill(model.activeTab == .settings ? Color.white.opacity(0.16) : Color.clear)
                )
        }
    }

    private func tabPill(title: String, systemImage: String, tab: NotchHUDTab) -> some View {
        let isSelected = model.activeTab == tab
        return Button(action: { model.select(tab) }) {
            HStack(spacing: 6) {
                Image(systemName: systemImage).font(.system(size: 11, weight: .semibold))
                Text(title).font(.system(size: 12, weight: .semibold))
            }
            .foregroundColor(isSelected ? .white : Color.white.opacity(0.6))
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .background(Capsule().fill(isSelected ? Color.white.opacity(0.14) : Color.clear))
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }

    private var backendStatusPill: some View {
        let isConfigured = OpenClickyConfiguration.isConfigured
        return HStack(spacing: 6) {
            Circle().fill(isConfigured ? DS.Colors.success : DS.Colors.overlayCursorColor).frame(width: 6, height: 6)
            Text(isConfigured ? OpenClickyConfiguration.backendHostDescription : "Set up backend")
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(isConfigured ? Color.white.opacity(0.7) : .white)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(Capsule().fill(isConfigured ? Color.white.opacity(0.08) : DS.Colors.overlayCursorColor.opacity(0.35)))
        .onTapGesture { OpenClickyConfiguration.revealSettingsFile() }
        .pointerCursor()
    }

    private func iconButton(systemImage: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 12, weight: .semibold))
                .foregroundColor(Color.white.opacity(0.75))
                .frame(width: 26, height: 26)
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help(help)
    }
}

// MARK: - Home

struct NotchHomeView: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var model: NotchHUDModel

    var body: some View {
        HStack(alignment: .top, spacing: 28) {
            VStack(alignment: .leading, spacing: 10) {
                Text("Talk to your Mac")
                    .font(.system(size: 17, weight: .bold))
                    .foregroundColor(.white)
                Text("Hold the shortcut and say what you need. Questions get a spoken answer and a pointer; work goes to an agent.")
                    .font(.system(size: 11))
                    .foregroundColor(Color.white.opacity(0.55))
                    .fixedSize(horizontal: false, vertical: true)

                HStack(spacing: 10) {
                    skillBadge(systemImage: "text.viewfinder", label: "Screen")
                    skillBadge(systemImage: "doc.text", label: "Docs")
                    skillBadge(systemImage: "chevron.left.forwardslash.chevron.right", label: "Code")
                    skillBadge(systemImage: "magnifyingglass", label: "Research")
                    addBadge(help: "Skills live in the repository's skills/ folder") { OpenClickyConfiguration.revealSettingsFile() }
                }
                .padding(.top, 4)

                Text("Active integrations")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(Color.white.opacity(0.7))
                    .padding(.top, 8)
                HStack(spacing: 8) {
                    integrationBadge(title: "Composio", isOn: OpenClickyConfiguration.settings.composioMcpUrl != nil)
                    integrationBadge(title: "Computer Use", isOn: OpenClickyConfiguration.settings.cuaDriverBin != nil)
                    addBadge(help: "Set COMPOSIO_MCP_URL / CUA_DRIVER_BIN in shell.json") { OpenClickyConfiguration.revealSettingsFile() }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            VStack(alignment: .leading, spacing: 10) {
                Label("Shortcuts", systemImage: "command")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(Color.white.opacity(0.7))
                shortcutRow(title: "Talk", keys: ["⌃ control", "⌥ option"])
                shortcutRow(title: "Open this panel", keys: ["hover the notch"])
                shortcutRow(title: "Dock cursor", keys: ["button below"])

                Spacer(minLength: 8)

                HStack(spacing: 8) {
                    Button(action: { companionManager.setCursorDocked(!companionManager.isCursorDocked) }) {
                        HStack(spacing: 6) {
                            Triangle()
                                .fill(DS.Colors.overlayCursorColor)
                                .frame(width: 10, height: 10)
                                .rotationEffect(.degrees(-35))
                            Text(companionManager.isCursorDocked ? "Release Cursor" : "Dock Cursor")
                                .font(.system(size: 12, weight: .semibold))
                        }
                        .foregroundColor(.white)
                        .padding(.horizontal, 14)
                        .padding(.vertical, 8)
                        .background(Capsule().fill(Color.white.opacity(0.14)))
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()
                    .disabled(companionManager.voiceState != .idle)

                    Button(action: { model.select(.settings) }) {
                        Image(systemName: "info")
                            .font(.system(size: 11, weight: .bold))
                            .foregroundColor(.white)
                            .frame(width: 30, height: 30)
                            .background(Circle().fill(Color.white.opacity(0.14)))
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()
                }
            }
            .frame(width: 250, alignment: .leading)
        }
        .padding(.horizontal, 26)
        .padding(.bottom, 20)
    }

    private func skillBadge(systemImage: String, label: String) -> some View {
        VStack(spacing: 4) {
            Image(systemName: systemImage)
                .font(.system(size: 16, weight: .medium))
                .foregroundColor(.white)
                .frame(width: 44, height: 44)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.white.opacity(0.10)))
            Text(label).font(.system(size: 9)).foregroundColor(Color.white.opacity(0.5))
        }
        .help(label)
    }

    private func addBadge(help: String, action: @escaping () -> Void) -> some View {
        VStack(spacing: 4) {
            Button(action: action) {
                Image(systemName: "plus")
                    .font(.system(size: 16, weight: .medium))
                    .foregroundColor(Color.white.opacity(0.8))
                    .frame(width: 44, height: 44)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Color.white.opacity(0.18), lineWidth: 1))
            }
            .buttonStyle(.plain)
            .pointerCursor()
            .help(help)
            Text(" ").font(.system(size: 9))
        }
    }

    private func integrationBadge(title: String, isOn: Bool) -> some View {
        HStack(spacing: 6) {
            Circle().fill(isOn ? DS.Colors.success : Color.white.opacity(0.25)).frame(width: 6, height: 6)
            Text(title).font(.system(size: 11, weight: .medium)).foregroundColor(Color.white.opacity(isOn ? 0.9 : 0.5))
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(Capsule().fill(Color.white.opacity(0.08)))
    }

    private func shortcutRow(title: String, keys: [String]) -> some View {
        HStack {
            Text(title).font(.system(size: 12)).foregroundColor(Color.white.opacity(0.85))
            Spacer()
            HStack(spacing: 4) {
                ForEach(keys, id: \.self) { key in
                    Text(key)
                        .font(.system(size: 10, weight: .medium, design: .monospaced))
                        .foregroundColor(Color.white.opacity(0.8))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 3)
                        .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(Color.white.opacity(0.12)))
                }
            }
        }
    }
}

// MARK: - Agents

/// Loads recent Codex threads through the CLI for the Agents tab.
@MainActor
final class AgentThreadStore: ObservableObject {
    @Published private(set) var threads: [OpenClickyThreadSummary] = []
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?
    private let agentClient = OpenClickyAgentClient()
    private var lastRefresh: Date?

    func refresh(force: Bool = false) {
        if !force, let lastRefresh, Date().timeIntervalSince(lastRefresh) < 10, !threads.isEmpty { return }
        guard !isLoading, OpenClickyConfiguration.isConfigured else { return }
        isLoading = true
        errorMessage = nil
        Task {
            do {
                threads = try await agentClient.listThreads(limit: 30)
                lastRefresh = Date()
            } catch {
                errorMessage = error.localizedDescription
            }
            isLoading = false
        }
    }

    /// Threads grouped by day, newest first.
    var sections: [(title: String, threads: [OpenClickyThreadSummary])] {
        let calendar = Calendar.current
        var groups: [(String, [OpenClickyThreadSummary])] = []
        for thread in threads.sorted(by: { $0.updatedAt > $1.updatedAt }) {
            let title: String
            if calendar.isDateInToday(thread.updatedDate) { title = "TODAY" }
            else if calendar.isDateInYesterday(thread.updatedDate) { title = "YESTERDAY" }
            else { title = thread.updatedDate.formatted(.dateTime.month(.abbreviated).day()).uppercased() }
            if let index = groups.firstIndex(where: { $0.0 == title }) { groups[index].1.append(thread) }
            else { groups.append((title, [thread])) }
        }
        return groups.map { (title: $0.0, threads: $0.1) }
    }
}

struct NotchAgentsView: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var threadStore: AgentThreadStore

    private let columns = [GridItem(.flexible(), spacing: 14), GridItem(.flexible(), spacing: 14)]

    var body: some View {
        ScrollView(showsIndicators: false) {
            VStack(alignment: .leading, spacing: 14) {
                if !OpenClickyConfiguration.isConfigured {
                    emptyState("Connect a backend to see your agents.", detail: "Add a token to ~/.openclicky/shell.json.")
                } else if threadStore.threads.isEmpty && threadStore.isLoading {
                    emptyState("Loading agents…", detail: nil)
                } else if let errorMessage = threadStore.errorMessage, threadStore.threads.isEmpty {
                    emptyState("Couldn't load agents", detail: errorMessage)
                } else if threadStore.threads.isEmpty {
                    emptyState("No agents yet", detail: "Hold ⌃⌥ and ask OpenClicky to do something.")
                } else {
                    ForEach(threadStore.sections, id: \.title) { section in
                        Text(section.title)
                            .font(.system(size: 10, weight: .semibold))
                            .foregroundColor(Color.white.opacity(0.45))
                            .padding(.top, 2)
                        LazyVGrid(columns: columns, spacing: 14) {
                            ForEach(section.threads) { thread in
                                AgentCardView(thread: thread) {
                                    companionManager.openAgentResultCard(threadId: thread.id)
                                }
                            }
                        }
                    }
                }
            }
            .padding(.horizontal, 26)
            .padding(.bottom, 22)
        }
        .onAppear { threadStore.refresh() }
    }

    private func emptyState(_ title: String, detail: String?) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 13, weight: .semibold)).foregroundColor(.white)
            if let detail { Text(detail).font(.system(size: 11)).foregroundColor(Color.white.opacity(0.55)) }
        }
        .padding(.top, 8)
    }
}

/// One agent thread, tinted by a stable per-thread hue like HeyClicky's agent cards.
struct AgentCardView: View {
    let thread: OpenClickyThreadSummary
    let onOpen: () -> Void

    private var hue: Double {
        let hash = thread.id.unicodeScalars.reduce(0) { ($0 &* 31 &+ Int($1.value)) & 0xFFFF }
        return Double(hash % 360) / 360.0
    }

    private var title: String {
        let firstLine = thread.preview.split(separator: "\n").first.map(String.init) ?? thread.preview
        let trimmed = firstLine.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "Untitled agent" : trimmed.prefix(1).uppercased() + trimmed.dropFirst()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.system(size: 13, weight: .bold))
                .foregroundColor(.white)
                .lineLimit(1)
            Text(thread.cwd.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                .font(.system(size: 10))
                .foregroundColor(Color.white.opacity(0.55))
                .lineLimit(1)
                .truncationMode(.middle)

            Spacer(minLength: 6)

            HStack {
                Button(action: onOpen) {
                    HStack(spacing: 5) {
                        Image(systemName: "arrow.up.forward.square").font(.system(size: 10, weight: .semibold))
                        Text("Open Agent").font(.system(size: 11, weight: .semibold))
                    }
                    .foregroundColor(.white)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Color.black.opacity(0.35)))
                }
                .buttonStyle(.plain)
                .pointerCursor()
                Spacer()
                Text(thread.updatedDate.formatted(date: .omitted, time: .shortened))
                    .font(.system(size: 10))
                    .foregroundColor(Color.white.opacity(0.5))
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, minHeight: 118, alignment: .topLeading)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(LinearGradient(
                    colors: [Color(hue: hue, saturation: 0.55, brightness: 0.34), Color(hue: hue, saturation: 0.6, brightness: 0.18)],
                    startPoint: .topLeading, endPoint: .bottomTrailing
                ))
        )
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.white.opacity(0.10), lineWidth: 1))
    }
}

// MARK: - Settings

struct NotchSettingsView: View {
    @ObservedObject var companionManager: CompanionManager

    var body: some View {
        ScrollView(showsIndicators: false) {
            VStack(alignment: .leading, spacing: 18) {
                section("BACKEND") {
                    settingRow(systemImage: "server.rack", title: "Backend", value: OpenClickyConfiguration.backendHostDescription)
                    settingRow(systemImage: "key", title: "Token", value: OpenClickyConfiguration.isConfigured ? "configured" : "missing")
                    actionRow(systemImage: "doc.text", title: "Open settings file", detail: OpenClickyConfiguration.settingsFileURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) {
                        OpenClickyConfiguration.revealSettingsFile()
                    }
                }
                section("AGENT") {
                    toggleRow(systemImage: "gearshape.2", title: "Agent mode", detail: "Work requests go to a Codex thread", isOn: Binding(
                        get: { companionManager.isAgentModeEnabled },
                        set: { companionManager.setAgentModeEnabled($0) }
                    ))
                    settingRow(systemImage: "folder", title: "Workspace", value: OpenClickyConfiguration.workspacePath.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                    settingRow(systemImage: "cpu", title: "Model", value: OpenClickyConfiguration.agentModelOverride ?? "backend default")
                }
                section("VOICE") {
                    settingRow(systemImage: "mic.badge.waveform", title: "Speech to text", value: companionManager.buddyDictationManager.transcriptionProviderDisplayName)
                    settingRow(systemImage: "keyboard", title: "Talk shortcut", value: "⌃ control + ⌥ option")
                }
                section("CURSOR") {
                    toggleRow(systemImage: "arrow.up.to.line.compact", title: "Dock cursor in the notch", detail: "The buddy lives in the HUD", isOn: Binding(
                        get: { companionManager.isCursorDocked },
                        set: { companionManager.setCursorDocked($0) }
                    ))
                    toggleRow(systemImage: "cursorarrow", title: "Show cursor", detail: "Hide it and it appears only while you talk", isOn: Binding(
                        get: { companionManager.isClickyCursorEnabled },
                        set: { companionManager.setClickyCursorEnabled($0) }
                    ))
                }
                section("SUPPORT") {
                    actionRow(systemImage: "arrow.triangle.2.circlepath", title: "Check for updates", detail: "Not configured in this build") {}
                    actionRow(systemImage: "ladybug", title: "Report a bug", detail: "Opens the project's issue tracker") {
                        if let url = URL(string: "https://github.com/prasanthsasikumar/openclicky/issues") { NSWorkspace.shared.open(url) }
                    }
                    actionRow(systemImage: "power", title: "Quit OpenClicky", detail: nil) { NSApp.terminate(nil) }
                }
            }
            .padding(.horizontal, 26)
            .padding(.bottom, 22)
        }
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 10, weight: .semibold)).foregroundColor(Color.white.opacity(0.45))
            VStack(spacing: 1) { content() }
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.white.opacity(0.07)))
                .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        }
    }

    private func settingRow(systemImage: String, title: String, value: String) -> some View {
        HStack {
            Image(systemName: systemImage).font(.system(size: 12)).foregroundColor(Color.white.opacity(0.6)).frame(width: 18)
            Text(title).font(.system(size: 12, weight: .medium)).foregroundColor(.white)
            Spacer()
            Text(value).font(.system(size: 11)).foregroundColor(Color.white.opacity(0.55)).lineLimit(1).truncationMode(.middle)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    private func toggleRow(systemImage: String, title: String, detail: String, isOn: Binding<Bool>) -> some View {
        HStack {
            Image(systemName: systemImage).font(.system(size: 12)).foregroundColor(Color.white.opacity(0.6)).frame(width: 18)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.system(size: 12, weight: .medium)).foregroundColor(.white)
                Text(detail).font(.system(size: 10)).foregroundColor(Color.white.opacity(0.5))
            }
            Spacer()
            Toggle("", isOn: isOn).toggleStyle(.switch).labelsHidden().tint(DS.Colors.accent).scaleEffect(0.8)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
    }

    private func actionRow(systemImage: String, title: String, detail: String?, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Image(systemName: systemImage).font(.system(size: 12)).foregroundColor(Color.white.opacity(0.6)).frame(width: 18)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).font(.system(size: 12, weight: .medium)).foregroundColor(.white)
                    if let detail { Text(detail).font(.system(size: 10)).foregroundColor(Color.white.opacity(0.5)).lineLimit(1).truncationMode(.middle) }
                }
                Spacer()
                Image(systemName: "chevron.right").font(.system(size: 10, weight: .semibold)).foregroundColor(Color.white.opacity(0.35))
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }
}

// MARK: - Agent result card (top-right)

private final class AgentResultPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

/// The floating card HeyClicky shows top-right when an agent finishes or you open one:
/// title, status, the agent's summary, Copy, and a "Follow up with agent" field.
@MainActor
final class AgentResultPanelManager {
    private var panel: AgentResultPanel?
    private let state = AgentResultPanelState()
    private let agentClient = OpenClickyAgentClient()

    func show(threadId: String, companionManager: CompanionManager) {
        state.threadId = threadId
        state.title = "Agent"
        state.summary = ""
        state.status = "loading"
        state.isLoading = true
        presentPanel(companionManager: companionManager)
        Task {
            do {
                let detail = try await agentClient.readThread(threadId)
                state.title = detail.thread.preview.split(separator: "\n").first.map(String.init) ?? "Agent"
                state.summary = detail.lastAgentMessage ?? "(no reply yet)"
                state.status = detail.turns.last?.status ?? "done"
            } catch {
                state.summary = "Couldn't load this agent: \(error.localizedDescription)"
                state.status = "error"
            }
            state.isLoading = false
        }
    }

    func hide() {
        panel?.orderOut(nil)
    }

    private func presentPanel(companionManager: CompanionManager) {
        if panel == nil {
            let newPanel = AgentResultPanel(
                contentRect: NSRect(x: 0, y: 0, width: 400, height: 240),
                styleMask: [.borderless, .nonactivatingPanel],
                backing: .buffered,
                defer: false
            )
            newPanel.isOpaque = false
            newPanel.backgroundColor = .clear
            newPanel.hasShadow = true
            newPanel.level = .floating
            newPanel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            newPanel.isReleasedWhenClosed = false
            newPanel.hidesOnDeactivate = false
            newPanel.contentView = NSHostingView(rootView: AgentResultView(state: state, companionManager: companionManager, onClose: { [weak self] in self?.hide() }))
            panel = newPanel
        }
        guard let panel, let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let visibleFrame = screen.visibleFrame
        let origin = NSPoint(x: visibleFrame.maxX - panel.frame.width - 20, y: visibleFrame.maxY - panel.frame.height - 12)
        panel.setFrameOrigin(origin)
        panel.makeKeyAndOrderFront(nil)
    }
}

@MainActor
final class AgentResultPanelState: ObservableObject {
    @Published var threadId: String = ""
    @Published var title: String = ""
    @Published var summary: String = ""
    @Published var status: String = ""
    @Published var isLoading = false
}

struct AgentResultView: View {
    @ObservedObject var state: AgentResultPanelState
    @ObservedObject var companionManager: CompanionManager
    var onClose: () -> Void
    @State private var followUpText = ""
    @State private var didCopy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Text(state.title)
                    .font(.system(size: 12, weight: .bold))
                    .foregroundColor(.white)
                    .lineLimit(1)
                Spacer()
                Text(statusLabel)
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundColor(.white)
                    .padding(.horizontal, 7)
                    .padding(.vertical, 3)
                    .background(Capsule().fill(statusColor))
                Button(action: onClose) {
                    Image(systemName: "xmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundColor(Color.white.opacity(0.7))
                        .frame(width: 18, height: 18)
                        .background(Circle().fill(Color.white.opacity(0.12)))
                }
                .buttonStyle(.plain)
                .pointerCursor()
            }

            if state.isLoading {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Loading…").font(.system(size: 11)).foregroundColor(Color.white.opacity(0.6))
                }
            } else {
                ScrollView(showsIndicators: false) {
                    Text(liveSummary)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundColor(.white)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 90)

                Button(action: copySummary) {
                    HStack(spacing: 5) {
                        Image(systemName: didCopy ? "checkmark" : "doc.on.doc").font(.system(size: 10, weight: .semibold))
                        Text(didCopy ? "Copied" : "Copy").font(.system(size: 11, weight: .semibold))
                    }
                    .foregroundColor(.white)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .background(Capsule().fill(Color.white.opacity(0.14)))
                }
                .buttonStyle(.plain)
                .pointerCursor()
            }

            HStack(spacing: 8) {
                Image(systemName: "mic.fill").font(.system(size: 11)).foregroundColor(Color.white.opacity(0.7))
                TextField("Follow up with agent", text: $followUpText)
                    .textFieldStyle(.plain)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(.white)
                    .onSubmit(sendFollowUp)
                    .disabled(companionManager.voiceState != .idle)
                Image(systemName: "keyboard").font(.system(size: 11)).foregroundColor(Color.white.opacity(0.35))
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.white.opacity(0.10)))

            if let agentActivityText = companionManager.agentActivityText, companionManager.voiceState == .processing {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.mini)
                    Text(agentActivityText).font(.system(size: 10)).foregroundColor(Color.white.opacity(0.6)).lineLimit(1)
                }
            }
        }
        .padding(16)
        .frame(width: 400)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Color(hex: "#2B2D31").opacity(0.96)))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Color.white.opacity(0.12), lineWidth: 1))
    }

    /// Prefer the freshest result for this thread over the loaded history.
    private var liveSummary: String {
        if let lastResult = companionManager.lastAgentResult, lastResult.threadId == state.threadId, lastResult.finishedAt > Date().addingTimeInterval(-3600) {
            return lastResult.text.isEmpty ? state.summary : lastResult.text
        }
        return state.summary
    }

    private var statusLabel: String {
        if companionManager.voiceState == .processing { return "Working" }
        switch state.status {
        case "completed", "done": return "Done"
        case "loading": return "…"
        case "error": return "Error"
        default: return state.status.capitalized
        }
    }

    private var statusColor: Color {
        if companionManager.voiceState == .processing { return DS.Colors.warning.opacity(0.8) }
        return state.status == "error" ? DS.Colors.overlayCursorColor.opacity(0.8) : DS.Colors.accent.opacity(0.8)
    }

    private func copySummary() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(liveSummary, forType: .string)
        didCopy = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { didCopy = false }
    }

    private func sendFollowUp() {
        let text = followUpText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        followUpText = ""
        companionManager.submitTextToAgent(text, threadId: state.threadId)
    }
}
