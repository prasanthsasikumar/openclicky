//
//  HistoryPageView.swift
//  OpenClicky
//
//  Every take, newest first, grouped by day. Typing filters the takes; enter asks a model across
//  them, and the answer names the takes it was drawn from. A row selects with a click (edit · copy),
//  shows "edited" once its text was changed, and a take that failed offers retry · delete. Edit
//  opens the take with its raw words, the written text, where it went, and copy / paste / delete.
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
    @State private var revisedTakeIDs: Set<UUID> = []
    @State private var selectedTakeID: UUID?
    @State private var inspectedTake: TakeRecord?
    @State private var answer: String?
    @State private var answerSources: [TakeRecord] = []
    @State private var isAsking = false
    @FocusState private var isQueryFocused: Bool

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.controller = companionManager.dictationTakeController
        self.settings = companionManager.dictationSettings
    }

    var body: some View {
        PageScaffold(title: "history") {
            HStack(spacing: 10) {
                askField
                filterMenu
            }
            .onChange(of: query) { _, _ in reload() }

            if isAsking || answer != nil { answerCard }

            if takes.isEmpty {
                VStack(spacing: 10) {
                    OrbMarkShape().fill(Paper.hairline).frame(width: 48, height: 48)
                    Text(query.isEmpty ? "your dictations will land here." : "no matching takes — press enter to ask anyway.").font(Paper.body(13)).foregroundStyle(Paper.inkTertiary)
                }
                .frame(maxWidth: .infinity).padding(.top, 80)
            }

            ForEach(groupedByDay, id: \.title) { group in
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 12) {
                        Text(group.title).font(Paper.body(13, weight: .semibold)).foregroundStyle(Paper.ink)
                        Rectangle().fill(Paper.hairline).frame(height: 1)
                        Text(group.takes.count == 1 ? "1 take" : "\(group.takes.count) takes").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                    }
                    .padding(.vertical, 6)
                    ForEach(group.takes) { take in
                        HistoryRow(
                            take: take,
                            isSelected: selectedTakeID == take.id,
                            wasEdited: revisedTakeIDs.contains(take.id),
                            canRetry: controller.canRetry(takeID: take.id),
                            onSelect: { selectedTakeID = selectedTakeID == take.id ? nil : take.id },
                            onOpen: { inspectedTake = take },
                            onCopy: { copy(take) },
                            onRetry: { Task { await controller.retry(takeID: take.id) } },
                            onDelete: { delete(take) })
                    }
                }
            }
        }
        .sheet(item: $inspectedTake) { take in
            TakeInspectorView(take: take, companionManager: companionManager, onClose: { inspectedTake = nil; reload() })
        }
        .onAppear {
            if let pending = model.pendingHistorySearch { query = pending; model.pendingHistorySearch = nil }
            reload()
        }
        .onReceive(controller.$historyVersion) { _ in reload() }
    }

    // MARK: asking

    /// One field for both: typing filters the takes below, enter asks across them.
    private var askField: some View {
        let shape = RoundedRectangle(cornerRadius: 9, style: .continuous)
        return HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").font(.system(size: 12)).foregroundStyle(Paper.inkTertiary)
            TextField("what did i say about…?", text: $query)
                .textFieldStyle(.plain).font(Paper.body(14)).foregroundStyle(Paper.ink)
                .focused($isQueryFocused)
                .onSubmit(ask)
            if !query.isEmpty {
                Button(action: { query = ""; answer = nil; answerSources = []; reload() }) {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(Paper.inkTertiary)
                }
                .buttonStyle(.plain).pointerCursor().help("clear")
            }
            Button(action: ask) {
                Text("↵ ask").font(Paper.body(12, weight: .semibold))
                    .foregroundStyle(query.isEmpty ? Paper.inkTertiary : Color.white)
                    .padding(.horizontal, 8).padding(.vertical, 3)
                    .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(query.isEmpty ? Paper.lineSoft : Paper.accentFill))
            }
            .buttonStyle(.plain).pointerCursor()
            .disabled(query.trimmingCharacters(in: .whitespaces).isEmpty || isAsking)
            .help("ask a model across your takes")
        }
        .padding(.horizontal, 12)
        .frame(height: 38)
        .background(shape.fill(Paper.cardRaised))
        .overlay(shape.strokeBorder(isQueryFocused ? Paper.accent : Paper.hairline, lineWidth: isQueryFocused ? 1.5 : 1))
    }

    private var filterMenu: some View {
        Menu {
            Button("all apps") { appFilter = nil; reload() }
            ForEach(appsUsed, id: \.bundleID) { app in
                Button(appName(app)) { appFilter = app.bundleID; reload() }
            }
            Divider()
            Button("every kind of take") { modeFilter = nil; reload() }
            Button("dictations") { modeFilter = .dictate; reload() }
            Button("hey clicky edits") { modeFilter = .edit; reload() }
            if settings.clipboardHistoryEnabled { Button("clipboard") { modeFilter = .clipboard; reload() } }
        } label: {
            Text(filterLabel + " ⌄").font(Paper.body(13)).foregroundStyle(Paper.ink)
        }
        .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
        .padding(.horizontal, 14)
        .frame(height: 38)
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Paper.cardRaised))
        .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).strokeBorder(Paper.hairline))
        .pointerCursor()
    }

    private var answerCard: some View {
        PaperCard {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text(answerCaption).font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                    Spacer()
                    Button(action: { answer = nil; answerSources = [] }) {
                        Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).foregroundStyle(Paper.inkTertiary)
                    }
                    .buttonStyle(.plain).pointerCursor().help("close the answer")
                }
                if isAsking {
                    Text("asking across your takes…").font(Paper.body(15)).foregroundStyle(Paper.inkSecondary)
                } else if let answer {
                    Text(answer).font(Paper.body(15)).foregroundStyle(Paper.ink).lineSpacing(3)
                        .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: 720, alignment: .leading)
                }
                if !answerSources.isEmpty && !isAsking {
                    FlowLayout(spacing: 6) {
                        ForEach(answerSources) { source in
                            Button(action: { reveal(source) }) {
                                Text(sourceChipLabel(source)).font(Paper.caption).foregroundStyle(Paper.ink)
                                    .padding(.horizontal, 8).padding(.vertical, 3)
                                    .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Paper.lineSoft))
                            }
                            .buttonStyle(.plain).pointerCursor()
                            .help(source.displayText)
                        }
                    }
                }
            }
            .padding(.horizontal, 18).padding(.vertical, 16)
        }
    }

    private var answerCaption: String {
        if isAsking { return "asking a model" }
        if answerSources.isEmpty { return "answer from your latest takes" }
        return "answer from \(answerSources.count) \(answerSources.count == 1 ? "take" : "takes") that match"
    }

    private func sourceChipLabel(_ take: TakeRecord) -> String {
        let calendar = Calendar.current
        let when = calendar.isDateInToday(take.createdAt) || calendar.isDateInYesterday(take.createdAt)
            ? HistoryRow.time.string(from: take.createdAt)
            : take.createdAt.formatted(.dateTime.day().month(.abbreviated)) + " " + HistoryRow.time.string(from: take.createdAt)
        let app = take.appName ?? take.appBundleID.flatMap { AppIconCache.shared.name(for: $0) }
        return ([when] + [app].compactMap { $0 }).joined(separator: " · ").lowercased()
    }

    /// A source chip selects its take in the list (clearing the filters that would hide it).
    private func reveal(_ take: TakeRecord) {
        if !takes.contains(where: { $0.id == take.id }) {
            modeFilter = nil
            appFilter = nil
            query = ""
            reload()
        }
        selectedTakeID = take.id
    }

    /// Enter in the field: a question goes to the model over the matching takes.
    private func ask() {
        let question = query.trimmingCharacters(in: .whitespaces)
        guard !question.isEmpty, !isAsking else { return }
        isAsking = true
        answerSources = Array(controller.historyMatches(for: question).prefix(8))
        Task {
            let reply = await controller.askHistory(question)
            answer = reply
            isAsking = false
        }
    }

    // MARK: the list

    private struct DayGroup { let title: String; let takes: [TakeRecord] }

    private var groupedByDay: [DayGroup] {
        let calendar = Calendar.current
        let formatter = DateFormatter()
        formatter.dateFormat = "EEEE, d MMM"
        var groups: [DayGroup] = []
        var byDay: [Date: [TakeRecord]] = [:]
        for take in takes { byDay[calendar.startOfDay(for: take.createdAt), default: []].append(take) }
        for day in byDay.keys.sorted(by: >) {
            let title = calendar.isDateInToday(day) ? "today" : calendar.isDateInYesterday(day) ? "yesterday" : formatter.string(from: day).lowercased()
            groups.append(DayGroup(title: title, takes: byDay[day] ?? []))
        }
        return groups
    }

    private var filterLabel: String {
        var parts: [String] = []
        if let appFilter { parts.append(appsUsed.first { $0.bundleID == appFilter }.map(appName) ?? appFilter) }
        if let modeFilter { parts.append(filterName(modeFilter)) }
        return parts.isEmpty ? "all apps" : parts.joined(separator: " · ")
    }

    private func appName(_ app: (bundleID: String, name: String?, count: Int)) -> String {
        (app.name ?? AppIconCache.shared.name(for: app.bundleID) ?? app.bundleID).lowercased()
    }

    private func filterName(_ mode: TakeRecord.Mode) -> String {
        switch mode {
        case .dictate: return "dictations"
        case .edit: return "hey clicky edits"
        case .clipboard: return "clipboard"
        }
    }

    private func reload() {
        guard let store = companionManager.dictationTakeStore else { return }
        appsUsed = (try? store.appsUsed()) ?? []
        revisedTakeIDs = (try? store.revisedTakeIDs()) ?? []
        var rows = (try? store.recent(limit: 500, query: query.isEmpty ? nil : query, mode: modeFilter, appBundleID: appFilter)) ?? []
        if !settings.clipboardHistoryEnabled, modeFilter == nil { rows = rows.filter { $0.mode != .clipboard } }
        takes = rows
    }

    private func copy(_ take: TakeRecord) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(take.displayText, forType: .string)
    }

    private func delete(_ take: TakeRecord) {
        try? companionManager.dictationTakeStore?.delete(takeID: take.id)
        if selectedTakeID == take.id { selectedTakeID = nil }
        controller.historyDidChange()
    }
}

private struct HistoryRow: View {
    let take: TakeRecord
    var isSelected = false
    var wasEdited = false
    var canRetry = false
    let onSelect: () -> Void
    let onOpen: () -> Void
    let onCopy: () -> Void
    var onRetry: () -> Void = {}
    var onDelete: () -> Void = {}
    @State private var isHovered = false

    static let time: DateFormatter = {
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
            Text(take.displayText.replacingOccurrences(of: "\n", with: " "))
                .font(Paper.body(13)).foregroundStyle(Paper.ink).lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
            if take.pinned { Image(systemName: "pin.fill").font(.system(size: 9)).foregroundStyle(Paper.accent).help("pinned") }
            if take.mode == .edit { Text("hey clicky").font(Paper.caption).foregroundStyle(Paper.inkTertiary) }
            if take.status == .failed {
                Text(failureLine).font(Paper.caption).foregroundStyle(Paper.danger).lineLimit(1)
                if canRetry {
                    Button("retry", action: onRetry).buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.inkSecondary).pointerCursor()
                        .help("hear the saved recording again")
                    Text("·").font(Paper.caption).foregroundStyle(Paper.inkSecondary).accessibilityHidden(true)
                }
                Button("delete", action: onDelete).buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.inkSecondary).pointerCursor()
                    .help("delete this take")
            } else if isSelected {
                Button("edit", action: onOpen).buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.inkSecondary).pointerCursor()
                    .help("open the take to edit, pin, paste again or delete it")
                Text("·").font(Paper.caption).foregroundStyle(Paper.inkSecondary).accessibilityHidden(true)
                Button("copy", action: onCopy).buttonStyle(.plain).font(Paper.caption).foregroundStyle(Paper.inkSecondary).pointerCursor()
                    .help("copy take")
            } else if wasEdited {
                Text("edited").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            }
            Text(Self.time.string(from: take.createdAt).lowercased())
                .font(Paper.caption).foregroundStyle(Paper.inkTertiary)
                .frame(width: 56, alignment: .trailing)
        }
        .padding(.horizontal, 10)
        .frame(height: 40)
        .background(
            RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(isSelected ? Paper.lineSoft : (isHovered ? Paper.lineSoft.opacity(0.5) : Color.clear)))
        .overlay(alignment: .bottom) { Rectangle().fill(Paper.hairline).frame(height: 1).opacity(isSelected ? 0 : 1) }
        .padding(.horizontal, -10)
        .contentShape(Rectangle())
        .onTapGesture(count: 2, perform: onOpen)
        .onTapGesture(perform: onSelect)
        .onHover { isHovered = $0 }
        .pointerCursor()
        .accessibilityElement(children: .contain)
        .accessibilityLabel(take.displayText.isEmpty ? failureLine : take.displayText)
        .accessibilityHint("\(take.appName ?? "unknown app"), \(Self.time.string(from: take.createdAt))")
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
        .help("\(take.mode == .edit ? "hey clicky edit" : "dictation") into \(take.appName ?? "unknown app") — double-click to open")
    }

    private var failureLine: String {
        guard let reason = take.failureReason?.trimmingCharacters(in: .whitespacesAndNewlines), !reason.isEmpty else { return "couldn't finish" }
        return "couldn't finish · \(reason.lowercased())"
    }
}

/// Lays chips out in rows, wrapping to the next row when one does not fit.
private struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let maximumWidth = proposal.width ?? .infinity
        var rowWidth: CGFloat = 0, rowHeight: CGFloat = 0, totalHeight: CGFloat = 0, widestRow: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if rowWidth > 0 && rowWidth + spacing + size.width > maximumWidth {
                totalHeight += rowHeight + spacing
                widestRow = max(widestRow, rowWidth)
                rowWidth = 0
                rowHeight = 0
            }
            rowWidth += (rowWidth > 0 ? spacing : 0) + size.width
            rowHeight = max(rowHeight, size.height)
        }
        widestRow = max(widestRow, rowWidth)
        return CGSize(width: widestRow, height: totalHeight + rowHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var origin = bounds.origin
        var rowHeight: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if origin.x > bounds.minX && origin.x + size.width > bounds.maxX {
                origin.x = bounds.minX
                origin.y += rowHeight + spacing
                rowHeight = 0
            }
            subview.place(at: origin, proposal: ProposedViewSize(size))
            origin.x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
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
                    // The window hides so the app behind it is in front for the paste.
                    NSApp.hide(nil)
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.45) { _ = FrontAppTextInserter.insert(text) }
                }.buttonStyle(PaperPillButtonStyle()).help("hides openclicky, then pastes into the app behind it")
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
