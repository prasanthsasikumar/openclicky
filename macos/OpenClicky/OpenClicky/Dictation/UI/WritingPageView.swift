//
//  WritingPageView.swift
//  OpenClicky
//
//  "How it writes": the dictionary, the snippets (spoken shortcuts) and the styles on one page,
//  drawn as a grouped form like the Settings pages so the window reads as one native app. The
//  language, the script and the AI clean-up switch are on the Dictation page; Styles says whether
//  clean-up is on, since only the clean-up reads the style rules. Anything too big for a row (a term,
//  a snippet, a style's name and switches, picking an app) opens a native sheet.
//

import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers

struct WritingPageView: View {
    let companionManager: CompanionManager
    @ObservedObject private var settings: DictationSettings
    @ObservedObject private var spaceStore: DictationSpaceStore

    @State private var isAddingTerm = false
    @State private var editedTerm: DictionaryTerm?
    @State private var isAddingSnippet = false
    @State private var editedSnippet: SpokenShortcut?
    @State private var editedStyle: DictationStyle?
    @State private var expandedStyleID: String?
    @State private var usedApps: [(bundleID: String, name: String?, count: Int)] = []

    init(companionManager: CompanionManager) {
        self.companionManager = companionManager
        self.settings = companionManager.dictationSettings
        self.spaceStore = companionManager.dictationSpaceStore
    }

    var body: some View {
        Form {
            dictionarySection
            snippetsSection
            stylesSection
        }
        .formStyle(.grouped)
        .onAppear { usedApps = (try? companionManager.dictationTakeStore?.appsUsed()) ?? [] }
        .sheet(isPresented: $isAddingTerm) {
            WritingTermEditorSheet(term: nil) { newTerm in spaceStore.update { $0.dictionary.append(newTerm) } }
        }
        .sheet(item: $editedTerm) { term in
            WritingTermEditorSheet(term: term) { changedTerm in
                spaceStore.update { space in
                    if let index = space.dictionary.firstIndex(where: { $0.id == changedTerm.id }) { space.dictionary[index] = changedTerm }
                }
            }
        }
        .sheet(isPresented: $isAddingSnippet) {
            WritingSnippetEditorSheet(snippet: nil, existingSnippets: spaceStore.space.shortcuts) { newSnippet in
                spaceStore.update { $0.shortcuts.append(newSnippet) }
            }
        }
        .sheet(item: $editedSnippet) { snippet in
            WritingSnippetEditorSheet(snippet: snippet, existingSnippets: spaceStore.space.shortcuts) { changedSnippet in
                spaceStore.update { space in
                    if let index = space.shortcuts.firstIndex(where: { $0.id == changedSnippet.id }) { space.shortcuts[index] = changedSnippet }
                }
            }
        }
        .sheet(item: $editedStyle) { style in
            WritingStyleEditorSheet(style: style, spaceStore: spaceStore)
        }
    }

    // MARK: - Dictionary

    /// A name the engines get wrong, from where the person is.
    private var dictionaryExample: String {
        settings.region.offersIndianOptions
            ? "You say “aditya shatriya”, OpenClicky writes Aaditya Kshatriya."
            : "You say “shivon”, OpenClicky writes Siobhán."
    }

    private var sortedTerms: [DictionaryTerm] {
        spaceStore.space.dictionary.sorted { $0.written.lowercased() < $1.written.lowercased() }
    }

    private var dictionarySection: some View {
        Section {
            if sortedTerms.isEmpty {
                Text("Nothing here yet. Teach OpenClicky its first term.")
                    .font(SettingsLook.rowTitle).foregroundStyle(.secondary)
            }
            ForEach(sortedTerms) { term in
                SettingsRow(title: term.written, detail: heardAsDetail(for: term)) {
                    WritingRowActions(
                        itemName: term.written,
                        onEdit: { editedTerm = term },
                        onDelete: { deleteTerm(term) })
                }
                .contextMenu {
                    Button("Edit…") { editedTerm = term }
                    Button("Delete", role: .destructive) { deleteTerm(term) }
                }
            }
            WritingAddRow(title: "Add a Term…") { isAddingTerm = true }
        } header: {
            WritingSectionHeader(title: "Dictionary", count: spaceStore.space.dictionary.count, unit: "term")
        } footer: {
            SettingsFootnote(text: "Names and terms written exactly as you expect. \(dictionaryExample) The written form is always matched too, in any casing.")
        }
    }

    private func heardAsDetail(for term: DictionaryTerm) -> String {
        if term.heardAs.isEmpty { return "" }
        return "Heard as " + term.heardAs.map { "“\($0)”" }.joined(separator: ", ")
    }

    private func deleteTerm(_ term: DictionaryTerm) {
        spaceStore.update { $0.dictionary.removeAll { $0.id == term.id } }
    }

    // MARK: - Snippets

    private var sortedSnippets: [SpokenShortcut] {
        spaceStore.space.shortcuts.sorted { $0.trigger.lowercased() < $1.trigger.lowercased() }
    }

    private var snippetsSection: some View {
        Section {
            if sortedSnippets.isEmpty {
                Text("Nothing here yet. Teach OpenClicky its first snippet.")
                    .font(SettingsLook.rowTitle).foregroundStyle(.secondary)
            }
            ForEach(sortedSnippets) { snippet in
                HStack(alignment: .top, spacing: 16) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("“\(snippet.trigger)”").font(SettingsLook.rowTitle)
                        Text(snippet.replacement)
                            .font(SettingsLook.caption).foregroundStyle(.secondary)
                            .lineLimit(3)
                    }
                    Spacer(minLength: 16)
                    WritingRowActions(
                        itemName: snippet.trigger,
                        onEdit: { editedSnippet = snippet },
                        onDelete: { deleteSnippet(snippet) })
                }
                .padding(.vertical, SettingsLook.Metric.rowVertical)
                .contextMenu {
                    Button("Edit…") { editedSnippet = snippet }
                    Button("Delete", role: .destructive) { deleteSnippet(snippet) }
                }
            }
            WritingAddRow(title: "Add a Snippet…") { isAddingSnippet = true }
        } header: {
            WritingSectionHeader(title: "Snippets", count: spaceStore.space.shortcuts.count, unit: "snippet")
        } footer: {
            SettingsFootnote(text: "Say the trigger on its own and the saved text is written instead: a sign-off, an address, anything you type a hundred times. A snippet expands only on its exact trigger; a dictionary term is replaced inside a longer sentence.")
        }
    }

    private func deleteSnippet(_ snippet: SpokenShortcut) {
        spaceStore.update { $0.shortcuts.removeAll { $0.id == snippet.id } }
    }

    // MARK: - Styles

    private var stylesSection: some View {
        Section {
            cleanUpStatusRow
            ForEach(spaceStore.space.styles) { style in
                WritingStyleRow(
                    style: style,
                    apps: apps(in: style),
                    allStyles: spaceStore.space.styles,
                    isExpanded: Binding(
                        get: { expandedStyleID == style.id },
                        set: { isExpanded in expandedStyleID = isExpanded ? style.id : nil }),
                    onSaveRules: { rules in
                        spaceStore.update { space in
                            if let index = space.styles.firstIndex(where: { $0.id == style.id }) { space.styles[index].rules = rules }
                        }
                    },
                    onMoveApp: move,
                    onAddApp: { bundleID in move(appBundleID: bundleID, toStyleID: style.id) },
                    onEdit: { editedStyle = style })
            }
        } header: {
            WritingSectionHeader(title: "Styles", count: spaceStore.space.styles.count, unit: "style")
        } footer: {
            SettingsFootnote(text: "How you sound, app by app. Each app is written in one style, and the model reads that style’s rules when it cleans up. New apps start in “\(fallbackStyle.name)”.")
        }
    }

    /// Whether the AI clean-up that reads these rules is on. The switch itself, the language and
    /// the script are on the Dictation page, where people look for them.
    private var cleanUpStatusRow: some View {
        let isOn = DictationCleanUpRow.cleanUpSwitchShowsOn(settings: settings)
        return SettingsRow(
            title: isOn ? "AI clean-up is on" : "AI clean-up is off",
            detail: isOn ? "It reads the rules of the app’s style below." : "Styles’ rules are read only while clean-up is on.") {
            Button("Language and Clean-up…") { companionManager.showSettings(.dictation) }
                .pointerCursor()
        }
    }

    /// The style that writes wherever no other style claims the app.
    private var fallbackStyle: DictationStyle { spaceStore.space.style(forAppBundleID: nil) }

    /// The apps a style writes for: those it names that are on this Mac, and the apps takes went
    /// into that it is the style for (every unclaimed one, for the fallback style), most used first.
    private func apps(in style: DictationStyle) -> [WritingStyleApp] {
        var bundleIDs = style.appBundleIDs.filter { AppIconCache.shared.icon(for: $0) != nil }
        for usedApp in usedApps where spaceStore.space.style(forAppBundleID: usedApp.bundleID).id == style.id && !bundleIDs.contains(usedApp.bundleID) {
            bundleIDs.append(usedApp.bundleID)
        }
        let takeCounts = Dictionary(usedApps.map { ($0.bundleID, $0.count) }, uniquingKeysWith: { first, _ in first })
        let recordedNames = Dictionary(usedApps.map { ($0.bundleID, $0.name) }, uniquingKeysWith: { first, _ in first })
        return bundleIDs
            .map { bundleID in
                WritingStyleApp(
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

// MARK: - Small rows

/// "Dictionary" with how many there are beside it, so the count the sidebar badges is also here.
private struct WritingSectionHeader: View {
    let title: String
    let count: Int
    let unit: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(title)
            Text(count == 1 ? "1 \(unit)" : "\(count) \(unit)s")
                .font(SettingsLook.caption).fontWeight(.regular).foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
    }
}

/// Edit and delete for one row of a list, as small native buttons.
private struct WritingRowActions: View {
    let itemName: String
    let onEdit: () -> Void
    let onDelete: () -> Void

    var body: some View {
        HStack(spacing: 6) {
            Button("Edit…", action: onEdit)
                .controlSize(.small)
                .pointerCursor()
                .accessibilityLabel("Edit \(itemName)")
            Button(action: onDelete) {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .controlSize(.small)
            .foregroundStyle(.secondary)
            .help("Delete")
            .pointerCursor()
            .accessibilityLabel("Delete \(itemName)")
        }
    }
}

/// The last row of a list: a plus and what it adds, in the accent like a link.
private struct WritingAddRow: View {
    let title: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: "plus.circle.fill")
                .font(SettingsLook.rowTitle)
                .foregroundStyle(SettingsLook.accent)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }
}

/// An app a style writes for, with how many takes went into it.
private struct WritingStyleApp: Identifiable {
    let bundleID: String
    let name: String
    let takeCount: Int
    var id: String { bundleID }
}

// MARK: - A style

/// One style as a disclosure row: name, tagline and the apps it writes for; opened, the rules the
/// model reads (edited in place, saved on demand) and the apps, each movable to another style.
private struct WritingStyleRow: View {
    let style: DictationStyle
    let apps: [WritingStyleApp]
    let allStyles: [DictationStyle]
    @Binding var isExpanded: Bool
    let onSaveRules: (String) -> Void
    let onMoveApp: (String, String) -> Void
    let onAddApp: (String) -> Void
    let onEdit: () -> Void
    @State private var rulesDraft = ""
    @State private var isPickingApp = false

    var body: some View {
        DisclosureGroup(isExpanded: $isExpanded) {
            VStack(alignment: .leading, spacing: 14) {
                rulesEditor
                appsList
            }
            .padding(.top, 8)
            .padding(.bottom, 4)
        } label: {
            HStack(spacing: 16) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(style.name.capitalizedFirstLetter).font(SettingsLook.rowTitle)
                    Text(style.tagline.capitalizedFirstLetter).font(SettingsLook.caption).foregroundStyle(.secondary)
                }
                Spacer(minLength: 16)
                if !isExpanded {
                    Text(appSummary).font(SettingsLook.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            .padding(.vertical, SettingsLook.Metric.rowVertical)
            .contentShape(Rectangle())
            // The whole label opens the row, not only the chevron, as the old card did.
            .onTapGesture { withAnimation(Motion.swap) { isExpanded.toggle() } }
            .pointerCursor()
            .accessibilityLabel("\(style.name), \(style.tagline)")
            .accessibilityHint(isExpanded ? "Collapse" : "Show its rules and apps")
        }
        .onAppear { rulesDraft = style.rules }
        .onChange(of: style.rules) { _, newRules in rulesDraft = newRules }
        .sheet(isPresented: $isPickingApp) {
            WritingAppPickerSheet { bundleID in onAddApp(bundleID) }
        }
    }

    /// "Slack, Teams, Discord +1", or what the style does when it names no app on this Mac.
    private var appSummary: String {
        if apps.isEmpty { return style.appBundleIDs.isEmpty ? "Writes wherever no style claims the app" : "None of its apps are on this Mac" }
        let names = apps.prefix(3).map(\.name).joined(separator: ", ")
        return apps.count > 3 ? "\(names) +\(apps.count - 3)" : names
    }

    private var rulesEditor: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Rules the model reads").font(SettingsLook.caption).foregroundStyle(.secondary)
            TextEditor(text: $rulesDraft)
                .font(SettingsLook.rowTitle)
                .scrollContentBackground(.hidden)
                .padding(.horizontal, 4).padding(.vertical, 6)
                .frame(height: 96)
                .background(RoundedRectangle(cornerRadius: SettingsLook.Metric.controlRadius, style: .continuous).fill(Color(nsColor: .textBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: SettingsLook.Metric.controlRadius, style: .continuous).strokeBorder(SettingsLook.hairline))
                .accessibilityLabel("Rules the model reads")
            HStack(spacing: 8) {
                Button("Name, Clean-up and Apps…", action: onEdit)
                    .controlSize(.small)
                    .help("Rename the style, its local clean-up switches and its apps")
                    .pointerCursor()
                Spacer()
                if rulesDraft != style.rules {
                    Button("Revert") { rulesDraft = style.rules }
                        .controlSize(.small)
                        .pointerCursor()
                    Button("Save Rules") { onSaveRules(rulesDraft) }
                        .controlSize(.small)
                        .buttonStyle(.borderedProminent)
                        .tint(SettingsLook.accent)
                        .pointerCursor()
                }
            }
        }
    }

    private var appsList: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Apps in this style").font(SettingsLook.caption).foregroundStyle(.secondary)
            VStack(spacing: 0) {
                if apps.isEmpty {
                    Text(style.appBundleIDs.isEmpty ? "Every app no other style claims." : "None of its apps are on this Mac.")
                        .font(SettingsLook.caption).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, minHeight: 28, alignment: .leading)
                }
                ForEach(Array(apps.enumerated()), id: \.element.id) { index, app in
                    if index > 0 { Divider() }
                    HStack(spacing: 8) {
                        AppIconView(bundleID: app.bundleID, size: 16).accessibilityHidden(true)
                        Text(app.name).font(SettingsLook.rowTitle).lineLimit(1)
                        Spacer(minLength: 4)
                        if app.takeCount > 0 {
                            Text(app.takeCount == 1 ? "1 take" : "\(app.takeCount) takes")
                                .font(SettingsLook.caption).foregroundStyle(.secondary)
                        }
                        Menu("Move to") {
                            ForEach(allStyles.filter { $0.id != style.id }) { otherStyle in
                                Button(otherStyle.name.capitalizedFirstLetter) { onMoveApp(app.bundleID, otherStyle.id) }
                            }
                        }
                        .menuStyle(.borderlessButton).controlSize(.small).fixedSize()
                        .pointerCursor()
                        .help("Write \(app.name) in another style")
                    }
                    .frame(minHeight: 28)
                }
            }
            Button(action: { isPickingApp = true }) {
                Label("Add an App…", systemImage: "plus")
            }
            .controlSize(.small)
            .pointerCursor()
        }
    }
}

// MARK: - Sheets

/// The buttons at the foot of every sheet here: Cancel (esc) and the default action (return).
private struct WritingSheetButtons: View {
    let confirmTitle: String
    let isConfirmDisabled: Bool
    let onCancel: () -> Void
    let onConfirm: () -> Void

    var body: some View {
        HStack {
            Spacer()
            Button("Cancel", action: onCancel)
                .keyboardShortcut(.cancelAction)
                .pointerCursor()
            Button(confirmTitle, action: onConfirm)
                .keyboardShortcut(.defaultAction)
                .disabled(isConfirmDisabled)
                .pointerCursor(isEnabled: !isConfirmDisabled)
        }
        .padding(.horizontal, 20)
        .padding(.bottom, 16)
    }
}

/// Teach or edit a dictionary term: the written form and the ways it tends to be heard.
private struct WritingTermEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    let term: DictionaryTerm?
    let onSave: (DictionaryTerm) -> Void
    @State private var writtenForm: String
    @State private var heardAsText: String

    init(term: DictionaryTerm?, onSave: @escaping (DictionaryTerm) -> Void) {
        self.term = term
        self.onSave = onSave
        _writtenForm = State(initialValue: term?.written ?? "")
        _heardAsText = State(initialValue: term?.heardAs.joined(separator: ", ") ?? "")
    }

    private var trimmedWrittenForm: String { writtenForm.trimmingCharacters(in: .whitespaces) }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField("OpenClicky writes", text: $writtenForm, prompt: Text("Aaditya Kshatriya"))
                    TextField("You say", text: $heardAsText, prompt: Text("aditya shatriya, aditya"))
                } header: {
                    Text(term == nil ? "Teach OpenClicky a term" : "Edit term")
                } footer: {
                    SettingsFootnote(text: "“You say” is optional; separate several with commas. The written form is always matched too, in any casing.")
                }
            }
            .formStyle(.grouped)
            WritingSheetButtons(
                confirmTitle: "Save",
                isConfirmDisabled: trimmedWrittenForm.isEmpty,
                onCancel: { dismiss() },
                onConfirm: {
                    let aliases = heardAsText.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
                    onSave(DictionaryTerm(id: term?.id ?? UUID(), written: trimmedWrittenForm, heardAs: aliases, createdAt: term?.createdAt ?? Date()))
                    dismiss()
                })
        }
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// Teach or edit a snippet: the spoken trigger and the text written in its place. A trigger
/// another snippet already uses (compared the way takes are matched) cannot be saved.
private struct WritingSnippetEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    let snippet: SpokenShortcut?
    let existingSnippets: [SpokenShortcut]
    let onSave: (SpokenShortcut) -> Void
    @State private var trigger: String
    @State private var replacement: String

    init(snippet: SpokenShortcut?, existingSnippets: [SpokenShortcut], onSave: @escaping (SpokenShortcut) -> Void) {
        self.snippet = snippet
        self.existingSnippets = existingSnippets
        self.onSave = onSave
        _trigger = State(initialValue: snippet?.trigger ?? "")
        _replacement = State(initialValue: snippet?.replacement ?? "")
    }

    private var triggerIsInUse: Bool {
        let normalizedTrigger = TakeFormatter.normalizedForMatching(trigger)
        return existingSnippets.contains { $0.id != snippet?.id && TakeFormatter.normalizedForMatching($0.trigger) == normalizedTrigger }
    }

    private var isSaveDisabled: Bool {
        trigger.trimmingCharacters(in: .whitespaces).isEmpty
            || replacement.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            || triggerIsInUse
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField("You say", text: $trigger, prompt: Text("my sign-off"))
                    if triggerIsInUse {
                        Text("That spoken phrase is already in use.")
                            .font(SettingsLook.caption).foregroundStyle(SettingsLook.danger)
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        Text("OpenClicky writes").font(SettingsLook.rowTitle)
                        TextEditor(text: $replacement)
                            .font(SettingsLook.rowTitle)
                            .scrollContentBackground(.hidden)
                            .padding(.horizontal, 4).padding(.vertical, 6)
                            .frame(height: 120)
                            .background(RoundedRectangle(cornerRadius: SettingsLook.Metric.controlRadius, style: .continuous).fill(Color(nsColor: .textBackgroundColor)))
                            .overlay(RoundedRectangle(cornerRadius: SettingsLook.Metric.controlRadius, style: .continuous).strokeBorder(SettingsLook.hairline))
                            .accessibilityLabel("OpenClicky writes")
                    }
                } header: {
                    Text(snippet == nil ? "Teach OpenClicky a snippet" : "Edit snippet")
                } footer: {
                    SettingsFootnote(text: "The saved text is written only when the whole take is the trigger.")
                }
            }
            .formStyle(.grouped)
            WritingSheetButtons(
                confirmTitle: "Save",
                isConfirmDisabled: isSaveDisabled,
                onCancel: { dismiss() },
                onConfirm: {
                    onSave(SpokenShortcut(id: snippet?.id ?? UUID(), trigger: trigger.trimmingCharacters(in: .whitespaces), replacement: replacement, createdAt: snippet?.createdAt ?? Date()))
                    dismiss()
                })
        }
        .frame(width: 480)
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// A style, opened in full: its name and tagline, the rules, the local clean-up switches and the
/// apps it names. Saving takes those apps away from every other style, since an app belongs to one.
private struct WritingStyleEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var spaceStore: DictationSpaceStore
    @State private var draft: DictationStyle
    @State private var isPickingApp = false

    init(style: DictationStyle, spaceStore: DictationSpaceStore) {
        self.spaceStore = spaceStore
        _draft = State(initialValue: style)
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField("Name", text: $draft.name)
                    TextField("Tagline", text: $draft.tagline)
                }
                Section {
                    TextEditor(text: $draft.rules)
                        .font(SettingsLook.rowTitle)
                        .scrollContentBackground(.hidden)
                        .frame(height: 90)
                        .accessibilityLabel("How this style writes")
                } header: {
                    Text("How this style writes")
                } footer: {
                    SettingsFootnote(text: "The model reads this when it cleans up a take in one of these apps.")
                }
                Section {
                    SettingsRow(title: "Sentence case", detail: "Capital letters and full stops.") {
                        SettingsSwitch(isOn: $draft.sentenceCase)
                    }
                    SettingsRow(title: "Remove fillers", detail: "Um, uh, and friends.") {
                        SettingsSwitch(isOn: $draft.removeFillers)
                    }
                    SettingsRow(title: "Clean up with AI", detail: "For this style, when “Clean up my words with AI” is on.") {
                        SettingsSwitch(isOn: $draft.polishWithModel)
                    }
                }
                Section {
                    if draft.appBundleIDs.isEmpty {
                        Text("None. This style writes wherever no app has its own.")
                            .font(SettingsLook.caption).foregroundStyle(.secondary)
                    }
                    ForEach(draft.appBundleIDs, id: \.self) { bundleID in
                        HStack(spacing: 8) {
                            AppIconView(bundleID: bundleID, size: 16).accessibilityHidden(true)
                            Text(AppIconCache.shared.name(for: bundleID) ?? bundleID).font(SettingsLook.rowTitle).lineLimit(1)
                            Spacer()
                            Button(action: { draft.appBundleIDs.removeAll { $0 == bundleID } }) {
                                Image(systemName: "minus.circle")
                            }
                            .buttonStyle(.borderless)
                            .foregroundStyle(.secondary)
                            .help("Take this app out of the style")
                            .pointerCursor()
                            .accessibilityLabel("Remove \(AppIconCache.shared.name(for: bundleID) ?? bundleID)")
                        }
                    }
                    WritingAddRow(title: "Add an App…") { isPickingApp = true }
                } header: {
                    Text("Apps written in this style")
                }
            }
            .formStyle(.grouped)
            WritingSheetButtons(
                confirmTitle: "Save",
                isConfirmDisabled: false,
                onCancel: { dismiss() },
                onConfirm: {
                    spaceStore.update { space in
                        if let index = space.styles.firstIndex(where: { $0.id == draft.id }) {
                            space.styles[index] = draft
                            // An app belongs to one style: take it away from the others.
                            for otherIndex in space.styles.indices where otherIndex != index {
                                space.styles[otherIndex].appBundleIDs.removeAll { draft.appBundleIDs.contains($0) }
                            }
                        }
                    }
                    dismiss()
                })
        }
        .frame(width: 540, height: 620)
        .sheet(isPresented: $isPickingApp) {
            WritingAppPickerSheet { bundleID in
                if !draft.appBundleIDs.contains(bundleID) { draft.appBundleIDs.append(bundleID) }
            }
        }
    }
}

/// The apps running now, searchable, plus Browse… for any installed app.
private struct WritingAppPickerSheet: View {
    @Environment(\.dismiss) private var dismiss
    let onPick: (String) -> Void
    @State private var searchText = ""

    private var runningApps: [NSRunningApplication] {
        NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && $0.bundleIdentifier != nil && $0.bundleIdentifier != Bundle.main.bundleIdentifier }
            .sorted { ($0.localizedName ?? "") < ($1.localizedName ?? "") }
            .filter { searchText.isEmpty || ($0.localizedName ?? "").localizedCaseInsensitiveContains(searchText) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Choose an app for this style").font(.headline)
            TextField("Search running apps", text: $searchText).textFieldStyle(.roundedBorder)
            List(runningApps, id: \.processIdentifier) { runningApp in
                Button(action: {
                    if let bundleID = runningApp.bundleIdentifier { onPick(bundleID) }
                    dismiss()
                }) {
                    HStack(spacing: 10) {
                        AppIconView(bundleID: runningApp.bundleIdentifier, size: 20).accessibilityHidden(true)
                        Text(runningApp.localizedName ?? runningApp.bundleIdentifier ?? "")
                        Spacer()
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .pointerCursor()
            }
            .listStyle(.bordered(alternatesRowBackgrounds: true))
            .frame(height: 260)
            HStack {
                Button("Browse…") {
                    let openPanel = NSOpenPanel()
                    openPanel.allowedContentTypes = [.applicationBundle]
                    openPanel.directoryURL = URL(fileURLWithPath: "/Applications")
                    if openPanel.runModal() == .OK, let url = openPanel.url, let bundleID = Bundle(url: url)?.bundleIdentifier { onPick(bundleID) }
                    dismiss()
                }
                .pointerCursor()
                Spacer()
                Button("Close") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                    .pointerCursor()
            }
        }
        .padding(20)
        .frame(width: 420)
    }
}

private extension String {
    /// Seeded style names are stored lowercase ("work messaging"); the native look writes them in
    /// sentence case without changing what is saved.
    var capitalizedFirstLetter: String {
        guard let firstCharacter = first else { return self }
        return firstCharacter.uppercased() + dropFirst()
    }
}
