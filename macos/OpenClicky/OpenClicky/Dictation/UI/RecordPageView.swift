//
//  RecordPageView.swift
//  OpenClicky
//
//  The home page: a greeting for the time of day, the box where a take lands when the window is
//  in front, the recent takes, and today's count.
//

import AppKit
import Combine
import SwiftUI

struct RecordPageView: View {
    @ObservedObject var model: DictationWindowModel
    let companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var controller: DictationTakeController
    @State private var boxText = ""
    @State private var recent: [TakeRecord] = []
    @State private var stats = TakeStats(wordsToday: 0, takesToday: 0, wordsThisWeek: 0, takesThisWeek: 0, mostUsedAppToday: nil, allTimeTakes: 0)
    @State private var greetingIndex = 0
    @State private var wordsPerDay: [(day: Date, words: Int)] = []

    init(model: DictationWindowModel, companionManager: CompanionManager) {
        self.model = model
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
        self.controller = companionManager.dictationTakeController
    }

    var body: some View {
        ScrollView {
            HStack(alignment: .top, spacing: 36) {
                VStack(alignment: .leading, spacing: 22) {
                    HStack(alignment: .firstTextBaseline, spacing: 10) {
                        Text(greeting).font(Paper.heading(34)).foregroundStyle(Paper.ink)
                        Button(action: { greetingIndex += 1 }) {
                            Image(systemName: "arrow.2.squarepath").font(.system(size: 11)).foregroundStyle(Paper.inkTertiary)
                        }
                        .buttonStyle(.plain).pointerCursor().help("another greeting")
                    }
                    .padding(.top, 44)

                    engineBanner

                    PaperCard {
                        VStack(alignment: .leading, spacing: 0) {
                            TextEditor(text: $boxText)
                                .font(Paper.body(14))
                                .foregroundStyle(Paper.ink)
                                .scrollContentBackground(.hidden)
                                .padding(12)
                                .frame(minHeight: 140)
                                .overlay(alignment: .topLeading) {
                                    if boxText.isEmpty {
                                        Text("your words land here when this window is in front.")
                                            .font(Paper.body(14)).foregroundStyle(Paper.inkTertiary)
                                            .padding(17).allowsHitTesting(false)
                                    }
                                }
                            PaperDivider()
                            HStack(spacing: 6) {
                                Spacer()
                                Text("press").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                                Keycap(text: settings.dictationKey.keycapLabel)
                                Text("anywhere to talk").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                                if !boxText.isEmpty {
                                    Button("copy") {
                                        NSPasteboard.general.clearContents()
                                        NSPasteboard.general.setString(boxText, forType: .string)
                                    }
                                    .buttonStyle(PaperPillButtonStyle()).padding(.leading, 8)
                                }
                            }
                            .padding(12)
                        }
                    }

                    HStack(alignment: .firstTextBaseline) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("recent takes").font(Paper.body(12, weight: .medium)).foregroundStyle(Paper.inkSecondary)
                            Text(recentSummary).font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
                        }
                        Spacer()
                        Button("all takes →") { model.section = .history }.buttonStyle(PaperPillButtonStyle())
                    }
                    .padding(.top, 10)

                    VStack(spacing: 0) {
                        if recent.isEmpty {
                            Text("your recent takes will collect here.").font(Paper.body(13)).foregroundStyle(Paper.inkTertiary).padding(.vertical, 18)
                        }
                        ForEach(recent.prefix(6)) { take in
                            HStack(spacing: 10) {
                                AppIconView(bundleID: take.appBundleID, size: 16)
                                Text(take.displayText.replacingOccurrences(of: "\n", with: " "))
                                    .font(Paper.body(13)).foregroundStyle(Paper.ink).lineLimit(1)
                                Spacer()
                                if take.status == .failed { Text("couldn't finish").font(Paper.mono(10)).foregroundStyle(Paper.danger) }
                                Button(action: { copy(take) }) {
                                    Image(systemName: "doc.on.doc").font(.system(size: 11)).foregroundStyle(Paper.inkTertiary)
                                }
                                .buttonStyle(.plain).pointerCursor().help("copy take")
                            }
                            .padding(.vertical, 10)
                            .paperRowRule()
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                statsPanel
                    .frame(width: 280)
                    .padding(.top, 44)
            }
            .padding(.horizontal, 44)
            .padding(.bottom, 40)
        }
        .onAppear(perform: reload)
        .onReceive(controller.$historyVersion) { _ in reload() }
        .onReceive(controller.$lastTake.dropFirst().compactMap { $0 }) { take in
            // A take while this window was in front and no field took it: it lands here. (A take
            // pasted into the box itself is already there — verified — and is not added twice.)
            if take.pasteOutcome == .leftInOrb || (take.appBundleID == Bundle.main.bundleIdentifier && take.pasteOutcome != .verified) {
                boxText = boxText.isEmpty ? take.displayText : boxText + " " + take.displayText
            }
        }
    }

    private var engineBanner: some View {
        let reason = DictationEngineResolver.unavailableReason(for: settings.engine)
        return HStack(spacing: 8) {
            Text(settings.engine == .offline ? "offline" : settings.engine.displayName.lowercased())
                .font(Paper.mono(10)).foregroundStyle(Paper.ink)
                .padding(.horizontal, 7).padding(.vertical, 2)
                .background(RoundedRectangle(cornerRadius: 5).fill(Paper.highlighter))
            Text(reason ?? (settings.engine == .offline
                ? (settings.polishOfflineTakes && DictationEngineResolver.makePolisher() != nil ? "heard on this mac · polished by a model (settings → engine)" : "your voice stays on this mac — words never leave it.")
                : "mode is active — \(settings.engine.detail)"))
                .font(Paper.body(12)).foregroundStyle(reason == nil ? Paper.inkSecondary : Paper.danger).lineLimit(1)
            Spacer()
            Button("change") { model.section = .settings; model.settingsPage = .engine }.buttonStyle(.plain).font(Paper.body(11)).foregroundStyle(Paper.inkTertiary).pointerCursor()
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Paper.card))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Paper.hairline))
    }

    private var statsPanel: some View {
        PaperCard {
            VStack(alignment: .leading, spacing: 0) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("\(stats.wordsToday)").font(.system(size: 30, weight: .medium, design: .serif)).foregroundStyle(Paper.ink)
                    Text(stats.wordsToday == 0 ? "ready when you are." : "words took flight today.").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                }
                .padding(18)
                Rectangle().fill(Paper.hairline).frame(height: 1)
                VStack(alignment: .leading, spacing: 12) {
                    statRow("takes today", "\(stats.takesToday)")
                    statRow("words this week", stats.wordsThisWeek.formatted())
                    statRow("takes this week", "\(stats.takesThisWeek)")
                    statRow("all time", "\(stats.allTimeTakes) takes")
                    statRow("engine", controller.engineDisplayName.lowercased())
                    statRow("history", settings.incognito ? "incognito" : "on this mac")
                }
                .padding(18)
                Rectangle().fill(Paper.hairline).frame(height: 1)
                VStack(alignment: .leading, spacing: 8) {
                    Text("the last seven days").font(Paper.label(10)).foregroundStyle(Paper.inkTertiary)
                    weekBars
                }
                .padding(18)
                Spacer(minLength: 0)
                ZStack {
                    RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Paper.highlighter.opacity(0.5))
                    OrbMarkShape().fill(Paper.accent).frame(width: 64, height: 64)
                }
                .frame(height: 120)
                .padding(14)
            }
        }
    }

    /// Seven bars, today on the right, scaled to the busiest day.
    private var weekBars: some View {
        let peak = max(1, wordsPerDay.map(\.words).max() ?? 1)
        let formatter = DateFormatter()
        formatter.dateFormat = "EEEEE"
        return HStack(alignment: .bottom, spacing: 6) {
            ForEach(Array(wordsPerDay.enumerated()), id: \.offset) { index, entry in
                VStack(spacing: 4) {
                    RoundedRectangle(cornerRadius: 3)
                        .fill(index == wordsPerDay.count - 1 ? Paper.accent : Paper.success.opacity(0.55))
                        .frame(height: max(3, 48 * CGFloat(entry.words) / CGFloat(peak)))
                        .frame(maxWidth: .infinity)
                        .help("\(entry.words) words")
                    Text(formatter.string(from: entry.day).lowercased()).font(Paper.mono(9)).foregroundStyle(Paper.inkTertiary)
                }
            }
        }
        .frame(height: 64, alignment: .bottom)
    }

    private func statRow(_ label: String, _ value: String) -> some View {
        HStack {
            Text(label).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
            Spacer()
            Text(value).font(Paper.body(12, weight: .medium)).foregroundStyle(Paper.ink)
        }
    }

    private static let greetings: [(hourRange: Range<Int>, lines: [String])] = [
        (5..<12, ["morning! ready when you are.", "morning's quiet. perfect.", "early bird? me too."]),
        (12..<17, ["midday. words flowing?", "afternoon — where you left off.", "it walks the talk."]),
        (17..<22, ["evening. say what's left.", "yapped all day? welcome home.", "back again. it's good here."]),
        (22..<24, ["still up? i'm listening.", "late words count double."]),
        (0..<5, ["still up? i'm listening.", "the night shift. go on."]),
    ]

    private var greeting: String {
        let hour = Calendar.current.component(.hour, from: Date())
        let lines = Self.greetings.first { $0.hourRange.contains(hour) }?.lines ?? ["ready when you are."]
        return lines[greetingIndex % lines.count]
    }

    private var recentSummary: String {
        guard stats.takesToday > 0 else { return "nothing yet today" }
        let app = stats.mostUsedAppToday.map { " · mostly \($0.lowercased())" } ?? ""
        return "\(stats.takesToday) today\(app)"
    }

    private func reload() {
        guard let store = companionManager.dictationTakeStore else { return }
        recent = (try? store.recent(limit: 8, mode: .dictate)) ?? []
        stats = (try? store.stats()) ?? stats
        wordsPerDay = (try? store.wordsPerDay()) ?? []
    }

    private func copy(_ take: TakeRecord) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(take.displayText, forType: .string)
    }
}
