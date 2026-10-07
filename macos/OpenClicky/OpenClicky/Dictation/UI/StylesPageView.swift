//
//  StylesPageView.swift
//  OpenClicky
//
//  "how you sound, app by app": the language and script, whether a model polishes the words (the
//  model reads the style rules, so its switches live here), then the styles. A style opens in place
//  to show the rules the model reads and the apps written in it, each one movable to another style.
//

import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers

struct StylesPageView: View {
    let companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var spaceStore: DictationSpaceStore
    @State private var editedStyle: DictationStyle?
    @State private var expandedStyleID: String?
    @State private var usedApps: [(bundleID: String, name: String?, count: Int)] = []
    /// The language last pinned, so "pin one" comes back to it after a turn on auto-detect.
    @AppStorage("dictation.lastPinnedLanguage") private var lastPinnedLanguageCode = "en-IN"

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
        self.spaceStore = companionManager.dictationSpaceStore
    }

    var body: some View {
        PageScaffold(title: "how you sound, app by app") {
            HStack(alignment: .top, spacing: 16) {
                languageCard
                scriptCard
            }
            .fixedSize(horizontal: false, vertical: true)

            polishCard

            HStack(alignment: .firstTextBaseline) {
                Text("your styles").font(Paper.heading(20)).foregroundStyle(Paper.ink)
                Spacer()
                Text("new apps start in “\(fallbackStyle.name)”").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            }
            .padding(.top, 4)

            VStack(spacing: 10) {
                ForEach(spaceStore.space.styles) { style in
                    StyleCard(
                        style: style,
                        apps: apps(in: style),
                        allStyles: spaceStore.space.styles,
                        isExpanded: expandedStyleID == style.id,
                        onToggle: { withAnimation(.easeOut(duration: 0.18)) { expandedStyleID = expandedStyleID == style.id ? nil : style.id } },
                        onSaveRules: { rules in spaceStore.update { space in
                            if let index = space.styles.firstIndex(where: { $0.id == style.id }) { space.styles[index].rules = rules }
                        } },
                        onMoveApp: move,
                        onAddApp: { bundleID in move(appBundleID: bundleID, toStyleID: style.id) },
                        onEdit: { editedStyle = style })
                }
            }
        }
        .onAppear { usedApps = (try? companionManager.dictationTakeStore?.appsUsed()) ?? [] }
        .sheet(item: $editedStyle) { style in
            StyleDetailSheet(style: style, spaceStore: spaceStore)
        }
    }

    // MARK: language and script

    private var languageCard: some View {
        PaperCard {
            VStack(alignment: .leading, spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("language").font(Paper.rowTitle).foregroundStyle(Paper.ink)
                    Text("pins every engine to one language, or lets it detect.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                }
                HStack(spacing: 8) {
                    PaperSegments(
                        options: [(true, "auto-detect"), (false, "pin one")],
                        selection: Binding(
                            get: { settings.language == .auto },
                            set: { isAuto in
                                if isAuto {
                                    settings.languageCode = DictationLanguage.auto.code
                                } else if settings.language == .auto {
                                    settings.languageCode = DictationLanguage.named(lastPinnedLanguageCode) == .auto ? "en-IN" : lastPinnedLanguageCode
                                }
                            }))
                    if settings.language != .auto {
                        Picker("", selection: Binding(
                            get: { settings.languageCode },
                            set: { code in settings.languageCode = code; lastPinnedLanguageCode = code })) {
                            ForEach(DictationLanguage.choices.filter { $0 != .auto }) { language in
                                Text("\(language.name.lowercased()) · \(language.nativeName)").tag(language.code)
                            }
                        }
                        .labelsHidden().frame(maxWidth: 180)
                        .accessibilityLabel("pinned language")
                    }
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }

    private var scriptCard: some View {
        PaperCard {
            VStack(alignment: .leading, spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("script for indian languages").font(Paper.rowTitle).foregroundStyle(Paper.ink)
                    Text("same words, written two ways.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                }
                HStack(spacing: 8) {
                    scriptChoice(.native, label: "native", example: "नमस्ते, आप कैसे हैं?")
                    scriptChoice(.roman, label: "roman", example: "namaste, aap kaise hain?")
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }

    private func scriptChoice(_ script: DictationScript, label: String, example: String) -> some View {
        let isChosen = settings.script == script
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        return Button(action: { settings.script = script }) {
            VStack(alignment: .leading, spacing: 2) {
                Text((isChosen ? "● " : "○ ") + label)
                    .font(Paper.body(12, weight: isChosen ? .semibold : .regular))
                    .foregroundStyle(isChosen ? Paper.success : Paper.inkSecondary)
                Text(example).font(Paper.body(13)).foregroundStyle(Paper.ink).lineLimit(1)
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(shape.fill(Paper.cardRaised))
            .overlay(shape.strokeBorder(isChosen ? Paper.success : Paper.hairline, lineWidth: isChosen ? 1.5 : 1))
            .contentShape(shape)
        }
        .buttonStyle(.plain).pointerCursor()
        .accessibilityLabel("\(label) script, \(example)")
        .accessibilityAddTraits(isChosen ? .isSelected : [])
    }

    // MARK: polish

    /// The two polish switches (moved here from settings → voice): one for the engines that send
    /// audio out anyway, one for takes heard on this Mac, whose words would then leave it.
    private var polishCard: some View {
        let hasModel = DictationEngineResolver.makePolisher() != nil
        let needsModelNote = hasModel ? "" : " needs a sarvam key or an account (settings → voice, account)."
        return PaperCard {
            VStack(spacing: 0) {
                PaperRow(title: "polish with a model", detail: "a model applies punctuation, numbers and the rules below. off: simple local rules only." + needsModelNote) {
                    PaperToggle(isOn: $settings.polishWithModel)
                }
                PaperDivider()
                HStack(alignment: .center, spacing: 16) {
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 6) {
                            Text("also polish takes heard on this mac").font(Paper.rowTitle).foregroundStyle(Paper.ink)
                            Text("sends text out")
                                .font(Paper.body(11, weight: .semibold)).foregroundStyle(Paper.danger)
                                .padding(.horizontal, 5).padding(.vertical, 1)
                                .background(RoundedRectangle(cornerRadius: 4).fill(Paper.danger.opacity(0.13)))
                        }
                        Text("your audio stays here; only the words go to the model for cleanup.").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                    }
                    Spacer(minLength: 12)
                    PaperToggle(isOn: $settings.polishOfflineTakes)
                }
                .padding(.horizontal, Paper.Metric.rowHorizontal)
                .padding(.vertical, Paper.Metric.rowVertical)
                .accessibilityElement(children: .combine)
            }
        }
    }

    // MARK: apps

    /// The style that writes wherever no other style claims the app.
    private var fallbackStyle: DictationStyle { spaceStore.space.style(forAppBundleID: nil) }

    /// The apps a style writes for: those it names that are on this Mac, and the apps takes went
    /// into that it is the style for (every unclaimed one, for the fallback style), most used first.
    private func apps(in style: DictationStyle) -> [StyleApp] {
        var bundleIDs = style.appBundleIDs.filter { AppIconCache.shared.icon(for: $0) != nil }
        for used in usedApps where spaceStore.space.style(forAppBundleID: used.bundleID).id == style.id && !bundleIDs.contains(used.bundleID) {
            bundleIDs.append(used.bundleID)
        }
        let takeCounts = Dictionary(usedApps.map { ($0.bundleID, $0.count) }, uniquingKeysWith: { first, _ in first })
        let recordedNames = Dictionary(usedApps.map { ($0.bundleID, $0.name) }, uniquingKeysWith: { first, _ in first })
        return bundleIDs
            .map { bundleID in
                StyleApp(
                    bundleID: bundleID,
                    name: AppIconCache.shared.name(for: bundleID) ?? recordedNames[bundleID].flatMap { $0 } ?? bundleID,
                    takeCount: takeCounts[bundleID] ?? 0)
            }
            .sorted { ($0.takeCount, $1.name.lowercased()) > ($1.takeCount, $0.name.lowercased()) }
    }

    /// Moves an app to a style. The fallback style holds no apps of its own (that is how it is
    /// found), so moving there means taking the app away from every other style.
    private func move(appBundleID: String, toStyleID styleID: String) {
        let fallbackStyleID = fallbackStyle.id
        spaceStore.update { space in
            if styleID == fallbackStyleID {
                for index in space.styles.indices { space.styles[index].appBundleIDs.removeAll { $0 == appBundleID } }
            } else {
                space.assign(appBundleID: appBundleID, toStyleID: styleID)
            }
        }
    }
}

struct StyleApp: Identifiable {
    let bundleID: String
    let name: String
    let takeCount: Int
    var id: String { bundleID }
}

/// One style: name and tagline, the apps it writes for; opened, the rules the model reads and the
/// apps, each with "move ⌄" to another style.
private struct StyleCard: View {
    let style: DictationStyle
    let apps: [StyleApp]
    let allStyles: [DictationStyle]
    let isExpanded: Bool
    let onToggle: () -> Void
    let onSaveRules: (String) -> Void
    let onMoveApp: (String, String) -> Void
    let onAddApp: (String) -> Void
    let onEdit: () -> Void
    @State private var rulesDraft = ""
    @State private var isPickingApp = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Paper.Metric.cardRadius, style: .continuous)
        VStack(alignment: .leading, spacing: 0) {
            Button(action: onToggle) {
                HStack(spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(style.name).font(Paper.cardTitle).foregroundStyle(Paper.ink)
                        Text(style.tagline).font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                    }
                    Spacer()
                    if isExpanded {
                        Text("⌃ collapse").font(Paper.body(13)).foregroundStyle(Paper.inkSecondary)
                    } else {
                        Text(appSummary).font(Paper.caption).foregroundStyle(Paper.inkSecondary).lineLimit(1)
                        Text("›").font(Paper.body(15)).foregroundStyle(Paper.inkSecondary)
                    }
                }
                .padding(.horizontal, 16).padding(.vertical, 14)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain).pointerCursor()
            .accessibilityLabel("\(style.name), \(style.tagline)")
            .accessibilityHint(isExpanded ? "collapse" : "show its rules and apps")

            if isExpanded {
                HStack(alignment: .top, spacing: 16) {
                    rulesEditor
                    appsList.frame(width: 300)
                }
                .padding(.horizontal, 16).padding(.bottom, 16)
            }
        }
        .background(shape.fill(Paper.card))
        .overlay(shape.strokeBorder(isExpanded ? Paper.accent : Paper.hairline, lineWidth: isExpanded ? 1.5 : 1))
        .onAppear { rulesDraft = style.rules }
        .onChange(of: style.rules) { _, newRules in rulesDraft = newRules }
        .sheet(isPresented: $isPickingApp) {
            RunningAppPicker { bundleID in onAddApp(bundleID) }
        }
    }

    /// "slack, teams, discord +1", or what the style does when it names no app on this Mac.
    private var appSummary: String {
        if apps.isEmpty { return style.appBundleIDs.isEmpty ? "writes wherever no style claims the app" : "none of its apps are on this mac" }
        let names = apps.prefix(3).map { $0.name.lowercased() }.joined(separator: ", ")
        return apps.count > 3 ? "\(names) +\(apps.count - 3)" : names
    }

    private var rulesEditor: some View {
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        return VStack(alignment: .leading, spacing: 6) {
            Text("rules the model reads").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            TextEditor(text: $rulesDraft)
                .font(Paper.caption).foregroundStyle(Paper.ink)
                .scrollContentBackground(.hidden)
                .padding(.horizontal, 7).padding(.vertical, 8)
                .frame(height: 116)
                .background(shape.fill(Paper.cardRaised))
                .overlay(shape.strokeBorder(Paper.hairline))
                .accessibilityLabel("rules the model reads")
            HStack(spacing: 8) {
                Button("name, cleanup and apps…", action: onEdit).buttonStyle(PaperPillButtonStyle(quiet: true))
                    .help("rename the style, its local cleanup switches and its apps")
                Spacer()
                if rulesDraft != style.rules {
                    Button("revert") { rulesDraft = style.rules }.buttonStyle(PaperPillButtonStyle(quiet: true))
                    Button("save rules") { onSaveRules(rulesDraft) }.buttonStyle(PaperPillButtonStyle(prominent: true))
                }
            }
        }
    }

    private var appsList: some View {
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        return VStack(alignment: .leading, spacing: 6) {
            Text("apps in this style").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
            VStack(spacing: 0) {
                if apps.isEmpty {
                    Text(style.appBundleIDs.isEmpty ? "every app no other style claims." : "none of its apps are on this mac.")
                        .font(Paper.caption).foregroundStyle(Paper.inkTertiary)
                        .padding(.horizontal, 10)
                        .frame(maxWidth: .infinity, minHeight: 32, alignment: .leading)
                }
                ForEach(Array(apps.enumerated()), id: \.element.id) { index, app in
                    HStack(spacing: 8) {
                        AppIconView(bundleID: app.bundleID, size: 16)
                        Text(app.name).font(Paper.body(13)).foregroundStyle(Paper.ink).lineLimit(1)
                        Spacer(minLength: 4)
                        if app.takeCount > 0 {
                            Text(app.takeCount == 1 ? "1 take" : "\(app.takeCount) takes").font(Paper.micro).foregroundStyle(Paper.inkTertiary)
                        }
                        Menu {
                            ForEach(allStyles.filter { $0.id != style.id }) { otherStyle in
                                Button(otherStyle.name) { onMoveApp(app.bundleID, otherStyle.id) }
                            }
                        } label: {
                            Text("move ⌄").font(Paper.caption).foregroundStyle(Paper.inkSecondary)
                        }
                        .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                        .pointerCursor()
                        .help("write \(app.name) in another style")
                    }
                    .padding(.horizontal, 10)
                    .frame(height: 32)
                    .overlay(alignment: .top) { if index > 0 { Rectangle().fill(Paper.lineSoft).frame(height: 1) } }
                }
                Button(action: { isPickingApp = true }) {
                    HStack(spacing: 6) {
                        Image(systemName: "plus").font(.system(size: 10, weight: .semibold))
                        Text("add an app…").font(Paper.caption)
                        Spacer()
                    }
                    .foregroundStyle(Paper.inkSecondary)
                    .padding(.horizontal, 10)
                    .frame(height: 32)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain).pointerCursor()
                .overlay(alignment: .top) { Rectangle().fill(Paper.lineSoft).frame(height: 1) }
            }
            .background(shape.fill(Paper.cardRaised))
            .overlay(shape.strokeBorder(Paper.hairline))
            .clipShape(shape)
        }
    }
}

/// A style, opened: its rules, the switches, and the apps written in it.
struct StyleDetailSheet: View {
    @Environment(\.dismiss) private var dismiss
    let style: DictationStyle
    @ObservedObject var spaceStore: DictationSpaceStore
    @State private var draft: DictationStyle
    @State private var isPickingApp = false

    init(style: DictationStyle, spaceStore: DictationSpaceStore) {
        self.style = style
        self.spaceStore = spaceStore
        _draft = State(initialValue: style)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    TextField("name", text: $draft.name).textFieldStyle(.plain).font(Paper.title(24)).foregroundStyle(Paper.ink)
                    TextField("tagline", text: $draft.tagline).textFieldStyle(.plain).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                }
                Spacer()
                Button(action: { dismiss() }) { Image(systemName: "xmark").font(.system(size: 12, weight: .semibold)).foregroundStyle(Paper.inkSecondary) }.buttonStyle(.plain).pointerCursor()
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("how this style writes (the model reads this)").font(Paper.label(11)).foregroundStyle(Paper.inkSecondary)
                TextEditor(text: $draft.rules).font(Paper.body(13)).frame(height: 90).overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Paper.hairline))
            }
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "sentence case", detail: "capital letters and full stops") { PaperToggle(isOn: $draft.sentenceCase) }
                    PaperDivider()
                    PaperRow(title: "remove fillers", detail: "um, uh, and friends") { PaperToggle(isOn: $draft.removeFillers) }
                    PaperDivider()
                    PaperRow(title: "polish with a model", detail: "when a sarvam key or an account is set") { PaperToggle(isOn: $draft.polishWithModel) }
                }
            }
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("apps written in this style").font(Paper.label(11)).foregroundStyle(Paper.inkSecondary)
                    Spacer()
                    Button("add an app…") { isPickingApp = true }.buttonStyle(PaperPillButtonStyle())
                }
                if draft.appBundleIDs.isEmpty {
                    Text("none — this style writes wherever no app has its own.").font(Paper.body(12)).foregroundStyle(Paper.inkTertiary)
                }
                FlowingAppList(bundleIDs: draft.appBundleIDs) { bundleID in draft.appBundleIDs.removeAll { $0 == bundleID } }
            }
            HStack {
                Spacer()
                Button("cancel") { dismiss() }.buttonStyle(PaperPillButtonStyle())
                Button("save") {
                    spaceStore.update { space in
                        if let index = space.styles.firstIndex(where: { $0.id == draft.id }) {
                            space.styles[index] = draft
                            // An app belongs to one style: take it away from the others.
                            for other in space.styles.indices where other != index {
                                space.styles[other].appBundleIDs.removeAll { draft.appBundleIDs.contains($0) }
                            }
                        }
                    }
                    dismiss()
                }
                .buttonStyle(PaperPillButtonStyle(prominent: true))
            }
        }
        .padding(24)
        .frame(width: 560)
        .background(Paper.background)
        .sheet(isPresented: $isPickingApp) {
            RunningAppPicker { bundleID in if !draft.appBundleIDs.contains(bundleID) { draft.appBundleIDs.append(bundleID) } }
        }
    }
}

private struct FlowingAppList: View {
    let bundleIDs: [String]
    let onRemove: (String) -> Void

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 8)], alignment: .leading, spacing: 8) {
            ForEach(bundleIDs, id: \.self) { bundleID in
                HStack(spacing: 6) {
                    AppIconView(bundleID: bundleID, size: 16)
                    Text(AppIconCache.shared.name(for: bundleID) ?? bundleID).font(Paper.body(11)).foregroundStyle(Paper.ink).lineLimit(1)
                    Button(action: { onRemove(bundleID) }) { Image(systemName: "xmark").font(.system(size: 8, weight: .bold)).foregroundStyle(Paper.inkTertiary) }.buttonStyle(.plain)
                }
                .padding(.horizontal, 8).padding(.vertical, 5)
                .background(RoundedRectangle(cornerRadius: 7).fill(Paper.cardRaised))
                .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(Paper.hairline))
            }
        }
    }
}

/// The apps running now plus a Browse… for any installed app.
struct RunningAppPicker: View {
    @Environment(\.dismiss) private var dismiss
    let onPick: (String) -> Void
    @State private var query = ""

    private var runningApps: [NSRunningApplication] {
        NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && $0.bundleIdentifier != nil && $0.bundleIdentifier != Bundle.main.bundleIdentifier }
            .sorted { ($0.localizedName ?? "") < ($1.localizedName ?? "") }
            .filter { query.isEmpty || ($0.localizedName ?? "").localizedCaseInsensitiveContains(query) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("choose an app to assign to this style").font(Paper.title(18)).foregroundStyle(Paper.ink)
            TextField("search running apps", text: $query).textFieldStyle(.roundedBorder)
            ScrollView {
                VStack(spacing: 0) {
                    ForEach(runningApps, id: \.processIdentifier) { app in
                        Button(action: { if let id = app.bundleIdentifier { onPick(id) }; dismiss() }) {
                            HStack(spacing: 10) {
                                AppIconView(bundleID: app.bundleIdentifier, size: 20)
                                Text(app.localizedName ?? app.bundleIdentifier ?? "").font(Paper.body(13)).foregroundStyle(Paper.ink)
                                Spacer()
                            }
                            .padding(.vertical, 8).contentShape(Rectangle())
                        }
                        .buttonStyle(.plain).pointerCursor()
                        .paperRowRule()
                    }
                }
            }
            .frame(height: 260)
            HStack {
                Button("browse…") {
                    let panel = NSOpenPanel()
                    panel.allowedContentTypes = [.applicationBundle]
                    panel.directoryURL = URL(fileURLWithPath: "/Applications")
                    if panel.runModal() == .OK, let url = panel.url, let id = Bundle(url: url)?.bundleIdentifier { onPick(id) }
                    dismiss()
                }
                .buttonStyle(PaperPillButtonStyle())
                Spacer()
                Button("close") { dismiss() }.buttonStyle(PaperPillButtonStyle())
            }
        }
        .padding(22)
        .frame(width: 420)
        .background(Paper.background)
    }
}
