//
//  HistoryPageView.swift
//  OpenClicky
//
//  Every take, newest first, grouped by day, searchable; a row opens the take with its raw words,
//  the written text (editable), where it went, and copy / paste / delete.
//

import AppKit
import Combine
import SwiftUI

struct HistoryPageView: View {
    @ObservedObject var model: DictationWindowModel
    let companionManager: CompanionManager
    @ObservedObject private var controller: DictationTakeController
    @ObservedObject private var settings: DictationSettings
    @State private var query = ""
    @State private var takes: [TakeRecord] = []
    @State private var modeFilter: TakeRecord.Mode?
    @State private var appFilter: String?
    @State private var appsUsed: [(bundleID: String, name: String?, count: Int)] = []
    @State private var selected: TakeRecord?
    @State private var answer: String?
    @State private var isAsking = false

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.controller = companionManager.dictationTakeController
        self.settings = companionManager.dictationSettings
    }

    var body: some View {
        PageScaffold(title: "history") {
            HStack(spacing: 10) {
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass").font(.system(size: 12)).foregroundStyle(Paper.inkTertiary)
                    TextField("search your words — try: the offsite flights", text: $query)
                        .textFieldStyle(.plain).font(Paper.body(13)).foregroundStyle(Paper.ink)
                        .onSubmit(ask)
                    Text("press enter to ask").font(Paper.mono(9)).foregroundStyle(Paper.inkTertiary)
                    if !query.isEmpty {
                        Button(action: { query = ""; reload() }) { Image(systemName: "xmark.circle.fill").foregroundStyle(Paper.inkTertiary) }.buttonStyle(.plain)
                    }
                }
                .padding(.horizontal, 14).padding(.vertical, 10)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Paper.cardRaised))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Paper.hairline))
                Menu {
                    Button("everything") { modeFilter = nil; appFilter = nil; reload() }
                    Button("dictations") { modeFilter = .dictate; reload() }
                    Button("hey clicky edits") { modeFilter = .edit; reload() }
                    if settings.clipboardHistoryEnabled { Button("clipboard") { modeFilter = .clipboard; reload() } }
                    if !appsUsed.isEmpty {
                        Divider()
                        Menu("by app") {
                            Button("any app") { appFilter = nil; reload() }
                            ForEach(appsUsed, id: \.bundleID) { app in
                                Button(app.name ?? app.bundleID) { appFilter = app.bundleID; reload() }
                            }
                        }
                    }
                } label: {
                    HStack(spacing: 6) {
                        Image(systemName: "line.3.horizontal.decrease").font(.system(size: 11))
                        Text(filterLabel).font(Paper.body(12, weight: .medium))
                    }
                    .foregroundStyle(Paper.ink)
                }
                .menuStyle(.borderlessButton).fixedSize()
                .padding(.horizontal, 10).padding(.vertical, 7)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Paper.cardRaised))
                .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Paper.hairline))
            }
            .onChange(of: query) { _, _ in reload() }

            if isAsking || answer != nil {
                PaperCard {
                    HStack(alignment: .top, spacing: 12) {
                        Image(systemName: "sparkles").foregroundStyle(Paper.accent).padding(.top, 2)
                        if isAsking {
                            Text("asking across your takes…").font(Paper.body(13)).foregroundStyle(Paper.inkSecondary)
                        } else if let answer {
                            Text(answer).font(Paper.body(13)).foregroundStyle(Paper.ink).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer()
                        Button(action: { self.answer = nil }) { Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).foregroundStyle(Paper.inkTertiary) }.buttonStyle(.plain)
                    }
                    .padding(16)
                }
            }

            if takes.isEmpty {
                VStack(spacing: 10) {
                    OrbMarkShape().fill(Paper.hairline).frame(width: 48, height: 48)
                    Text(query.isEmpty ? "your dictations will land here." : "no matching history").font(Paper.body(13)).foregroundStyle(Paper.inkTertiary)
                }
                .frame(maxWidth: .infinity).padding(.top, 80)
            }

            ForEach(groupedByDay, id: \.title) { group in
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 10) {
                        Text(group.title).font(Paper.body(12, weight: .medium)).foregroundStyle(Paper.ink)
                        Rectangle().fill(Paper.success.opacity(0.5)).frame(height: 1)
                        Text("\(group.takes.count)").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                    }
                    .padding(.bottom, 6)
                    ForEach(group.takes) { take in
                        HistoryRow(take: take, canRetry: controller.canRetry(takeID: take.id), onOpen: { selected = take }, onCopy: { copy(take) },
                                   onRetry: { Task { await controller.retry(takeID: take.id) } })
                    }
                }
            }
        }
        .sheet(item: $selected) { take in
            TakeInspectorView(take: take, companionManager: companionManager, onClose: { selected = nil; reload() })
        }
        .onAppear {
            if let pending = model.pendingHistorySearch { query = pending; model.pendingHistorySearch = nil }
            reload()
        }
        .onReceive(controller.$historyVersion) { _ in reload() }
    }

    private struct DayGroup { let title: String; let takes: [TakeRecord] }

    private var groupedByDay: [DayGroup] {
        let calendar = Calendar.current
        let formatter = DateFormatter()
        formatter.dateFormat = "EEEE, d MMM"
        var groups: [DayGroup] = []
        var byDay: [Date: [TakeRecord]] = [:]
        for take in takes { byDay[calendar.startOfDay(for: take.createdAt), default: []].append(take) }
        for day in byDay.keys.sorted(by: >) {
            let title = calendar.isDateInToday(day) ? "Today" : calendar.isDateInYesterday(day) ? "Yesterday" : formatter.string(from: day)
            groups.append(DayGroup(title: title, takes: byDay[day] ?? []))
        }
        return groups
    }

    private var filterLabel: String {
        var parts: [String] = []
        if let modeFilter { parts.append(filterName(modeFilter)) }
        if let appFilter { parts.append(appsUsed.first { $0.bundleID == appFilter }?.name ?? appFilter) }
        return parts.isEmpty ? "filter" : parts.joined(separator: " · ")
    }

    private func filterName(_ mode: TakeRecord.Mode) -> String {
        switch mode {
        case .dictate: return "dictations"
        case .edit: return "hey clicky edits"
        case .clipboard: return "clipboard"
        }
    }

    /// Enter in the search box: a question goes to the model over the matching takes.
    private func ask() {
        let question = query.trimmingCharacters(in: .whitespaces)
        guard !question.isEmpty else { return }
        isAsking = true
        Task {
            let reply = await companionManager.dictationTakeController.askHistory(question)
            answer = reply
            isAsking = false
        }
    }

    private func reload() {
        guard let store = companionManager.dictationTakeStore else { return }
        appsUsed = (try? store.appsUsed()) ?? []
        var rows = (try? store.recent(limit: 500, query: query.isEmpty ? nil : query, mode: modeFilter, appBundleID: appFilter)) ?? []
        if !settings.clipboardHistoryEnabled, modeFilter == nil { rows = rows.filter { $0.mode != .clipboard } }
        takes = rows
    }

    private func copy(_ take: TakeRecord) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(take.displayText, forType: .string)
    }
}

private struct HistoryRow: View {
    let take: TakeRecord
    var canRetry = false
    let onOpen: () -> Void
    let onCopy: () -> Void
    var onRetry: () -> Void = {}

    private static let time: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "h:mm a"
        return formatter
    }()

    var body: some View {
        HStack(spacing: 12) {
            if take.mode == .clipboard {
                Image(systemName: "doc.on.clipboard").font(.system(size: 12)).foregroundStyle(Paper.inkTertiary).frame(width: 18)
            } else {
                AppIconView(bundleID: take.appBundleID, size: 18)
            }
            Button(action: onOpen) {
                Text(take.displayText.replacingOccurrences(of: "\n", with: " "))
                    .font(Paper.body(14)).foregroundStyle(Paper.ink).lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain).pointerCursor()
            .help("open \(take.mode == .edit ? "hey clicky edit" : "dictation") from \(take.appName ?? "unknown app")")
            if take.pinned { Image(systemName: "pin.fill").font(.system(size: 9)).foregroundStyle(Paper.accent) }
            if take.status == .failed { Text("couldn't finish").font(Paper.mono(10)).foregroundStyle(Paper.danger) }
            if take.status == .failed, canRetry {
                Button("retry", action: onRetry).buttonStyle(PaperPillButtonStyle()).help("hear the saved recording again")
            }
            if take.mode == .edit { Text("edit").font(Paper.mono(10)).foregroundStyle(Paper.inkTertiary) }
            Text(Self.time.string(from: take.createdAt).lowercased()).font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
            Button(action: onCopy) { Image(systemName: "doc.on.doc").font(.system(size: 11)).foregroundStyle(Paper.inkTertiary) }
                .buttonStyle(.plain).pointerCursor().help("copy take")
        }
        .padding(.vertical, 12)
        .paperRowRule()
    }
}

/// One take, opened: the written text to edit, the raw words, and what happened to it.
struct TakeInspectorView: View {
    let take: TakeRecord
    let companionManager: CompanionManager
    let onClose: () -> Void
    @State private var text: String

    init(take: TakeRecord, companionManager: CompanionManager, onClose: @escaping () -> Void) {
        self.take = take
        self.companionManager = companionManager
        self.onClose = onClose
        _text = State(initialValue: take.displayText)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                AppIconView(bundleID: take.appBundleID, size: 22)
                VStack(alignment: .leading, spacing: 2) {
                    Text(take.appName ?? "take").font(Paper.body(13, weight: .medium)).foregroundStyle(Paper.ink)
                    Text(detailLine).font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                }
                Spacer()
                Button(action: onClose) { Image(systemName: "xmark").font(.system(size: 12, weight: .semibold)).foregroundStyle(Paper.inkSecondary) }.buttonStyle(.plain).pointerCursor()
            }
            PaperCard {
                TextEditor(text: $text)
                    .font(Paper.body(14)).foregroundStyle(Paper.ink)
                    .scrollContentBackground(.hidden)
                    .padding(10)
                    .frame(minHeight: 160)
            }
            if take.rawText != take.formattedText, !take.rawText.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("what you said").font(Paper.label(10)).foregroundStyle(Paper.inkTertiary)
                    Text(take.rawText).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary).textSelection(.enabled)
                }
            }
            if let reason = take.failureReason {
                Text(reason).font(Paper.body(12)).foregroundStyle(Paper.danger)
            }
            HStack(spacing: 8) {
                Button("copy") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(text, forType: .string)
                }.buttonStyle(PaperPillButtonStyle())
                Button(take.pinned ? "unpin" : "pin") {
                    try? companionManager.dictationTakeStore?.setPinned(!take.pinned, takeID: take.id)
                    companionManager.dictationTakeController.historyDidChange()
                    onClose()
                }.buttonStyle(PaperPillButtonStyle())
                Button("paste again") {
                    onClose()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { _ = FrontAppTextInserter.insert(text) }
                }.buttonStyle(PaperPillButtonStyle()).help("closes this window, then pastes into the app in front")
                Button("delete") {
                    try? companionManager.dictationTakeStore?.delete(takeID: take.id)
                    companionManager.dictationTakeController.historyDidChange()
                    onClose()
                }.buttonStyle(PaperPillButtonStyle(destructive: true))
                Spacer()
                if text != take.displayText {
                    Button("save edit") {
                        try? companionManager.dictationTakeStore?.revise(takeID: take.id, newText: text, editor: "history")
                        companionManager.dictationTakeController.historyDidChange()
                        onClose()
                    }.buttonStyle(PaperPillButtonStyle(prominent: true))
                }
            }
        }
        .padding(22)
        .frame(width: 560)
        .background(Paper.background)
    }

    private var detailLine: String {
        var parts: [String] = []
        parts.append(take.createdAt.formatted(date: .abbreviated, time: .shortened).lowercased())
        if take.durationSeconds > 0 { parts.append(String(format: "%.0f s", take.durationSeconds)) }
        if !take.engine.isEmpty { parts.append(take.engine) }
        switch take.pasteOutcome {
        case .verified: parts.append("pasted")
        case .posted: parts.append("pasted (unconfirmed)")
        case .leftInOrb: parts.append("left in the orb")
        case .leftOnPasteboard: parts.append("copied to the clipboard")
        case .none: break
        }
        return parts.joined(separator: " · ")
    }
}
