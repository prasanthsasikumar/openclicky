//
//  StylesPageView.swift
//  OpenClicky
//
//  "how you sound, app by app": the language and script, then one style per group of apps with
//  the apps assigned to it, and the apps takes have gone into with the style each one gets.
//

import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers

struct StylesPageView: View {
    let companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var spaceStore: DictationSpaceStore
    @State private var openStyle: DictationStyle?
    @State private var usedApps: [(bundleID: String, name: String?, count: Int)] = []

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
        self.spaceStore = companionManager.dictationSpaceStore
    }

    var body: some View {
        PageScaffold(title: "how you sound, app by app") {
            sectionHeader("language and script")
            PaperCard {
                VStack(spacing: 0) {
                    PaperRow(title: "language", detail: "let the engine detect it, or choose what you usually speak") {
                        HStack(spacing: 8) {
                            Button("auto-detect") { settings.languageCode = DictationLanguage.auto.code }
                                .buttonStyle(PaperPillButtonStyle(prominent: settings.language == .auto))
                            Picker("", selection: $settings.languageCode) {
                                ForEach(DictationLanguage.choices.filter { $0 != .auto }) { language in
                                    Text("\(language.name) · \(language.nativeName)").tag(language.code)
                                }
                            }
                            .labelsHidden().frame(width: 170)
                        }
                    }
                    PaperDivider()
                    VStack(alignment: .leading, spacing: 10) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text("choose script").font(Paper.body(13, weight: .medium)).foregroundStyle(Paper.ink)
                            Text("use the original script, or write the same words in roman letters").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                        }
                        HStack(spacing: 10) {
                            scriptChoice(.native, label: "native", example: "नमस्ते, आप कैसे हैं?")
                            scriptChoice(.roman, label: "roman", example: "namaste, aap kaise hain?")
                        }
                    }
                    .padding(18)
                }
            }

            sectionHeader("your styles")
            VStack(spacing: 10) {
                ForEach(spaceStore.space.styles) { style in
                    Button(action: { openStyle = style }) {
                        HStack(spacing: 14) {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(style.name).font(Paper.title(22)).foregroundStyle(Paper.ink)
                                Text(style.tagline).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary)
                            }
                            Spacer()
                            HStack(spacing: -4) {
                                ForEach(style.appBundleIDs.prefix(5), id: \.self) { bundleID in
                                    AppIconView(bundleID: bundleID, size: 22)
                                }
                            }
                            if style.appBundleIDs.count > 5 { Text("+\(style.appBundleIDs.count - 5)").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary) }
                            if style.appBundleIDs.isEmpty { Text("writes wherever no app has its own voice").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary) }
                            Image(systemName: "arrow.right").font(.system(size: 12)).foregroundStyle(Paper.inkSecondary)
                        }
                        .padding(18)
                        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Paper.card))
                        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Paper.hairline))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain).pointerCursor()
                    .help("open \(style.name)")
                }
            }

            sectionHeader("your apps")
            if usedApps.isEmpty {
                Text("your writing apps will appear here.").font(Paper.body(13)).foregroundStyle(Paper.inkTertiary)
            } else {
                PaperCard {
                    VStack(spacing: 0) {
                        ForEach(usedApps, id: \.bundleID) { app in
                            let style = spaceStore.space.style(forAppBundleID: app.bundleID)
                            HStack(spacing: 12) {
                                AppIconView(bundleID: app.bundleID, size: 22)
                                Text(app.name ?? AppIconCache.shared.name(for: app.bundleID) ?? app.bundleID).font(Paper.body(13, weight: .medium)).foregroundStyle(Paper.ink)
                                Text("\(app.count) takes").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
                                Spacer()
                                Picker("", selection: Binding(
                                    get: { style.id },
                                    set: { newStyleID in spaceStore.update { $0.assign(appBundleID: app.bundleID, toStyleID: newStyleID) } }
                                )) {
                                    ForEach(spaceStore.space.styles) { candidate in Text(candidate.name).tag(candidate.id) }
                                }
                                .labelsHidden().frame(width: 170)
                            }
                            .padding(.horizontal, 18).padding(.vertical, 10)
                            .paperRowRule()
                        }
                    }
                }
            }
        }
        .onAppear { usedApps = (try? companionManager.dictationTakeStore?.appsUsed()) ?? [] }
        .sheet(item: $openStyle) { style in
            StyleDetailSheet(style: style, spaceStore: spaceStore)
        }
    }

    private func sectionHeader(_ text: String) -> some View {
        HStack(spacing: 12) {
            Text(text).font(Paper.title(19)).foregroundStyle(Paper.ink)
            Rectangle().fill(Paper.hairline).frame(height: 1)
        }
        .padding(.top, 6)
    }

    private func scriptChoice(_ script: DictationScript, label: String, example: String) -> some View {
        Button(action: { settings.script = script }) {
            VStack(spacing: 6) {
                Text(label).font(Paper.body(13, weight: .medium))
                    .foregroundStyle(settings.script == script ? Color.white : Paper.ink)
                    .frame(maxWidth: .infinity).padding(.vertical, 8)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(settings.script == script ? Paper.success : Paper.cardRaised))
                    .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Paper.hairline))
                Text(example).font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
            }
        }
        .buttonStyle(.plain).pointerCursor()
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
