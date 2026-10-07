//
//  RecordPageView.swift
//  OpenClicky
//
//  The home page is about the takes that left, not the box: a greeting for the time of day, who
//  hears you, how to talk, a small "try it here" strip, the recent takes with the app each one
//  landed in, and on the right this week's count and the pointer.
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
            VStack(alignment: .leading, spacing: 32) {
                VStack(alignment: .leading, spacing: 14) {
                    HStack(alignment: .firstTextBaseline, spacing: 10) {
                        Text(greeting).font(Paper.display).foregroundStyle(Paper.ink)
                        Button(action: { greetingIndex += 1 }) {
                            Image(systemName: "arrow.2.squarepath").font(.system(size: 11)).foregroundStyle(Paper.inkTertiary)
                        }
                        .buttonStyle(.plain).pointerCursor().help("another greeting")
                    }
                    engineBanner
                }

                HStack(alignment: .top, spacing: 32) {
                    VStack(alignment: .leading, spacing: 24) {
                        instructionCard
                        tryItHereStrip
                        recentTakes
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    VStack(alignment: .leading, spacing: 16) {
                        thisWeekCard
                        PointerCard(companionManager: companionManager)
                    }
                    .frame(width: 300)
                }
            }
            .padding(.top, Paper.Metric.windowTop)
            .padding(.horizontal, Paper.Metric.windowSides)
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

    // MARK: who hears you

    /// Where the voice goes and where history stays, with "change engine" → settings → voice.
    private var engineBanner: some View {
        let unavailableReason = DictationEngineResolver.unavailableReason(for: settings.engine)
        return HStack(spacing: 10) {
            HStack(spacing: 6) {
                Circle().fill(unavailableReason == nil ? Paper.success : Paper.danger).frame(width: 6, height: 6)
                Text(engineBadge).font(Paper.body(12, weight: .semibold))
            }
            .foregroundStyle(unavailableReason == nil ? Paper.success : Paper.danger)
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill((unavailableReason == nil ? Paper.success : Paper.danger).opacity(0.13)))
            Text(unavailableReason?.lowercased() ?? engineSentence)
                .font(Paper.body(13)).foregroundStyle(unavailableReason == nil ? Paper.ink : Paper.danger)
                .lineLimit(1)
            Button("change engine") { model.section = .settings; model.settingsPage = .voice }
                .buttonStyle(.plain).font(Paper.body(13)).foregroundStyle(Paper.accentFill).pointerCursor()
        }
    }

    private var engineBadge: String {
        switch settings.engine {
        case .offline: return "on this mac"
        case .sarvam: return "sarvam hears you"
        case .openclicky: return "openai, via openclicky"
        case .assemblyai: return "assemblyai, via openclicky"
        }
    }

    private var engineSentence: String {
        let history = settings.incognito ? "incognito: nothing is saved." : "your history stays on this mac."
        switch settings.engine {
        case .offline:
            if settings.polishOfflineTakes && DictationEngineResolver.makePolisher() != nil {
                return "your voice stays on this mac; the words go to a model for polish. " + history
            }
            return settings.incognito ? "your voice stays on this mac, and nothing is saved." : "your voice and your history stay on this mac."
        case .sarvam: return "audio goes to sarvam with your key. " + history
        case .openclicky: return "audio goes to openai through the openclicky backend. " + history
        case .assemblyai: return "audio goes to assemblyai through the openclicky backend. " + history
        }
    }

    // MARK: how to talk

    private var instructionCard: some View {
        let keyLabel = settings.dictationKey.keycapLabel
        return PaperCard {
            HStack(spacing: 18) {
                Text(keyLabel)
                    .font(Paper.body(15))
                    .foregroundStyle(Paper.ink)
                    .frame(minWidth: 52, minHeight: 44)
                    .padding(.horizontal, keyLabel.count > 2 ? 6 : 0)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Paper.cardRaised))
                    .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Paper.hairline))
                    .overlay(alignment: .bottom) {
                        // The keycap's thicker bottom edge.
                        RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Paper.hairline).frame(height: 3).padding(.horizontal, 2)
                    }
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text("hold \(keyLabel) in any app, speak, let go.").font(Paper.body(15, weight: .semibold)).foregroundStyle(Paper.ink)
                    Text("words are tidied for the app in front and pasted at the cursor. tap to start, tap to finish · esc discards.")
                        .font(Paper.body(13)).foregroundStyle(Paper.inkSecondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 18).padding(.vertical, 16)
        }
    }

    /// The 96 pt box a take lands in when this window is in front and no field took it.
    private var tryItHereStrip: some View {
        let shape = RoundedRectangle(cornerRadius: Paper.Metric.cardRadius, style: .continuous)
        let wordCount = boxText.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).count
        return VStack(alignment: .leading, spacing: 4) {
            TextEditor(text: $boxText)
                .font(Paper.body(13))
                .foregroundStyle(Paper.ink)
                .scrollContentBackground(.hidden)
                .padding(.horizontal, -5)
                .overlay(alignment: .topLeading) {
                    if boxText.isEmpty {
                        Text("try it here — with this window in front, words land in this box.")
                            .font(Paper.body(13)).foregroundStyle(Paper.inkTertiary)
                            .allowsHitTesting(false)
                    }
                }
            HStack(spacing: 10) {
                Text(wordCount == 1 ? "1 word" : "\(wordCount) words").font(Paper.caption).foregroundStyle(Paper.inkTertiary)
                Spacer()
                Button("copy") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(boxText, forType: .string)
                }
                .buttonStyle(.plain).pointerCursor().disabled(boxText.isEmpty)
                Text("·").accessibilityHidden(true)
                Button("clear") { boxText = "" }
                    .buttonStyle(.plain).pointerCursor().disabled(boxText.isEmpty)
            }
            .font(Paper.caption)
            .foregroundStyle(boxText.isEmpty ? Paper.inkTertiary : Paper.inkSecondary)
        }
        .padding(.horizontal, 18).padding(.vertical, 14)
        .frame(height: 96)
        .background(shape.fill(Paper.card))
        .overlay(shape.strokeBorder(Paper.hairline, style: StrokeStyle(lineWidth: 1, dash: [4, 3])))
    }

    // MARK: recent takes

    private var recentTakes: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                (Text("recent takes").font(Paper.body(13, weight: .semibold)).foregroundColor(Paper.ink)
                    + Text(recentDayLabel.map { " · \($0)" } ?? "").font(Paper.body(13)).foregroundColor(Paper.inkTertiary))
                Spacer()
                Button(stats.allTimeTakes > 0 ? "all \(stats.allTimeTakes) takes →" : "history →") { model.section = .history }
                    .buttonStyle(.plain).font(Paper.body(13)).foregroundStyle(Paper.accentFill).pointerCursor()
            }
            .padding(.bottom, 8)

            if recent.isEmpty {
                Text("your takes will collect here, with the app each one landed in.")
                    .font(Paper.body(13)).foregroundStyle(Paper.inkTertiary)
                    .frame(height: 40, alignment: .leading)
                    .overlay(alignment: .top) { Rectangle().fill(Paper.hairline).frame(height: 1) }
            }
            ForEach(recent) { take in
                HStack(spacing: 12) {
                    AppIconView(bundleID: take.appBundleID, size: 18)
                    Text(take.displayText.replacingOccurrences(of: "\n", with: " "))
                        .font(Paper.body(13)).foregroundStyle(Paper.ink).lineLimit(1)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    if take.status == .failed {
                        Text("couldn't finish · \(Self.time.string(from: take.createdAt).lowercased())").font(Paper.caption).foregroundStyle(Paper.danger)
                    } else {
                        Text("→ \(landingPlace(of: take)) · \(Self.time.string(from: take.createdAt).lowercased())")
                            .font(Paper.caption).foregroundStyle(Paper.inkTertiary).lineLimit(1)
                    }
                    Button("copy") { copy(take) }
                        .buttonStyle(PaperPillButtonStyle(compact: true))
                        .help("copy take")
                        .disabled(take.displayText.isEmpty)
                }
                .frame(height: 40)
                .overlay(alignment: .top) { Rectangle().fill(Paper.hairline).frame(height: 1) }
            }
        }
    }

    /// Where a take went: the app it pasted into, or where it was left when no field took it.
    private func landingPlace(of take: TakeRecord) -> String {
        switch take.pasteOutcome {
        case .leftInOrb: return "the orb"
        case .leftOnPasteboard: return "the clipboard"
        case .verified, .posted, .none:
            return (take.appName ?? take.appBundleID.flatMap { AppIconCache.shared.name(for: $0) } ?? "no app").lowercased()
        }
    }

    /// "today", "yesterday" or the date of the newest take: the list shows that day's takes.
    private var recentDayLabel: String? {
        guard let newest = recent.first?.createdAt else { return nil }
        let calendar = Calendar.current
        if calendar.isDateInToday(newest) { return "today" }
        if calendar.isDateInYesterday(newest) { return "yesterday" }
        return Self.day.string(from: newest).lowercased()
    }

    // MARK: this week

    private var thisWeekCard: some View {
        PaperCard {
            VStack(alignment: .leading, spacing: 0) {
                Text("this week").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(stats.wordsThisWeek.formatted()).font(Paper.heading(44)).foregroundStyle(Paper.ink)
                    Text("words · \(stats.takesThisWeek) \(stats.takesThisWeek == 1 ? "take" : "takes")").font(Paper.body(13)).foregroundStyle(Paper.inkSecondary)
                }
                .padding(.top, 4)
                weekBars.padding(.top, 18)
                HStack {
                    Text("today \(stats.takesToday) \(stats.takesToday == 1 ? "take" : "takes")")
                    Spacer()
                    Text("all time \(stats.allTimeTakes)")
                }
                .font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                .padding(.top, 12)
                .overlay(alignment: .top) { Rectangle().fill(Paper.hairline).frame(height: 1) }
                .padding(.top, 14)
            }
            .padding(18)
        }
        .accessibilityElement(children: .combine)
    }

    /// Seven bars, today on the right in the accent, scaled to the busiest day.
    private var weekBars: some View {
        let peak = max(1, wordsPerDay.map(\.words).max() ?? 1)
        return HStack(alignment: .bottom, spacing: 6) {
            ForEach(Array(wordsPerDay.enumerated()), id: \.offset) { index, entry in
                VStack(spacing: 4) {
                    RoundedRectangle(cornerRadius: 3)
                        .fill(index == wordsPerDay.count - 1 ? Paper.accent : Paper.success.opacity(0.5))
                        .frame(height: max(3, 44 * CGFloat(entry.words) / CGFloat(peak)))
                        .frame(maxWidth: .infinity)
                        .help("\(entry.words) words")
                    Text(Self.weekdayInitial.string(from: entry.day).lowercased()).font(Paper.micro).foregroundStyle(Paper.inkTertiary)
                }
            }
        }
        .frame(height: 64, alignment: .bottom)
    }

    // MARK: data

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

    private static let time: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "h:mm a"
        return formatter
    }()

    private static let day: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "EEEE, d MMM"
        return formatter
    }()

    private static let weekdayInitial: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "EEEEE"
        return formatter
    }()

    private func reload() {
        guard let store = companionManager.dictationTakeStore else { return }
        // The newest day's takes, up to six: the header names that day.
        let latest = (try? store.recent(limit: 6, mode: .dictate)) ?? []
        if let newestDay = latest.first.map({ Calendar.current.startOfDay(for: $0.createdAt) }) {
            recent = latest.filter { Calendar.current.startOfDay(for: $0.createdAt) == newestDay }
        } else {
            recent = []
        }
        stats = (try? store.stats()) ?? stats
        wordsPerDay = (try? store.wordsPerDay()) ?? []
    }

    private func copy(_ take: TakeRecord) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(take.displayText, forType: .string)
    }
}

/// The pointer, from the record page: what it is doing now, how to talk to it, dock it in the
/// notch or release it, and "what can it do?" (the buddy types out the answer next to itself).
/// It reads the companion's few published flags into its own state rather than observing the
/// whole `CompanionManager`, which publishes audio levels many times a second.
private struct PointerCard: View {
    let companionManager: CompanionManager
    @State private var isCursorDocked = false
    @State private var isPointerAwake = true
    @State private var pointerPresence: PointerPresence = .always
    @State private var isCursorEnabled = true
    @State private var isVoiceIdle = true

    var body: some View {
        PaperCard {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 8) {
                    OrbMarkShape()
                        .fill(Paper.accent)
                        .frame(width: 16, height: 18)
                        .shadow(color: Paper.accent.opacity(0.6), radius: 4)
                    Text("the pointer").font(Paper.body(13, weight: .semibold)).foregroundStyle(Paper.ink)
                    Text(stateLine).font(Paper.caption).foregroundStyle(Paper.inkTertiary).lineLimit(1)
                }
                (Text("hold ") + Text("⌃ ⌥").font(Paper.key) + Text(" and ask about anything on screen. it answers out loud and points."))
                    .font(Paper.body(13)).foregroundStyle(Paper.ink)
                    .lineSpacing(2)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 8) {
                    Button(isCursorDocked ? "release it" : "dock it") {
                        companionManager.setCursorDocked(!isCursorDocked)
                    }
                    .buttonStyle(PaperPillButtonStyle())
                    .disabled(!isVoiceIdle)
                    .help(isCursorDocked ? "the pointer leaves the notch and follows the mouse again" : "the pointer flies into the notch and waits there")
                    Button("what can it do?") { companionManager.explainWhatOpenClickyDoes() }
                        .buttonStyle(PaperPillButtonStyle(quiet: true))
                        .help("the pointer types out what openclicky does, next to itself")
                }
            }
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .onReceive(companionManager.$isCursorDocked) { isCursorDocked = $0 }
        .onReceive(companionManager.$isPointerAwake) { isPointerAwake = $0 }
        .onReceive(companionManager.$pointerPresence) { pointerPresence = $0 }
        .onReceive(companionManager.$isClickyCursorEnabled) { isCursorEnabled = $0 }
        .onReceive(companionManager.$voiceState.map { $0 == .idle }.removeDuplicates()) { isVoiceIdle = $0 }
    }

    private var stateLine: String {
        if isCursorDocked { return "docked in the notch" }
        if !isCursorEnabled { return "hidden until you talk" }
        if isPointerAwake { return "following you" }
        switch pointerPresence {
        case .always: return "following you"
        case .whileMoving: return "resting until the mouse moves"
        case .onShake: return "waiting for a shake"
        }
    }
}
