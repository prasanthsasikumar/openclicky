//
//  ShortcutsPageView.swift
//  OpenClicky
//
//  Spoken shortcuts: say the trigger on its own and the saved text is what gets written — a
//  sign-off, an address, a snippet you type a hundred times.
//

import SwiftUI
import Combine

struct ShortcutsPageView: View {
    @ObservedObject var spaceStore: DictationSpaceStore
    @State private var isAdding = false
    @State private var editing: SpokenShortcut?

    var body: some View {
        PageScaffold(title: "shortcuts") {
            PaperCard {
                HStack(spacing: 28) {
                    VStack(alignment: .center, spacing: 4) {
                        Text("you say").font(Paper.label(11)).foregroundStyle(Paper.inkSecondary)
                        Text("“my sign-off”").font(.system(size: 15, design: .serif).italic()).foregroundStyle(Paper.ink)
                    }
                    Image(systemName: "arrow.turn.down.right").foregroundStyle(Paper.success)
                    VStack(alignment: .leading, spacing: 6) {
                        HStack(spacing: 4) {
                            Circle().fill(Paper.danger).frame(width: 6, height: 6)
                            Circle().fill(Color.yellow).frame(width: 6, height: 6)
                            Circle().fill(Paper.success).frame(width: 6, height: 6)
                        }
                        Text("Warm regards,\nPrasanth\nFlowsXR").font(Paper.body(11)).foregroundStyle(Paper.inkSecondary)
                    }
                    .padding(12)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Paper.cardRaised))
                    .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Paper.hairline))
                    Spacer()
                }
                .padding(22)
                .frame(maxWidth: .infinity)
            }

            Button(action: { isAdding = true }) {
                HStack(spacing: 10) {
                    Image(systemName: "plus.circle").font(.system(size: 14)).foregroundStyle(Paper.ink)
                    Text("teach openclicky a shortcut").font(Paper.body(13, weight: .medium)).foregroundStyle(Paper.ink)
                    Spacer()
                    Image(systemName: "chevron.right").font(.system(size: 11, weight: .semibold)).foregroundStyle(Paper.accent)
                }
                .padding(.horizontal, 18).padding(.vertical, 14)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Paper.card))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Paper.accent.opacity(0.5)))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain).pointerCursor()

            Text("shortcuts expand only when you say the exact trigger. a term in your dictionary is replaced inside a longer sentence instead.")
                .font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)

            if spaceStore.space.shortcuts.isEmpty {
                VStack(spacing: 10) {
                    OrbMarkShape().fill(Paper.hairline).frame(width: 48, height: 48)
                    Text("nothing here yet — teach openclicky its first shortcut.").font(Paper.body(13)).foregroundStyle(Paper.inkTertiary)
                }
                .frame(maxWidth: .infinity).padding(.top, 50)
            } else {
                VStack(spacing: 0) {
                    ForEach(spaceStore.space.shortcuts.sorted { $0.trigger.lowercased() < $1.trigger.lowercased() }) { shortcut in
                        HStack(alignment: .top, spacing: 14) {
                            Text("“\(shortcut.trigger)”").font(.system(size: 14, design: .serif).italic()).foregroundStyle(Paper.ink).frame(width: 180, alignment: .leading)
                            Text(shortcut.replacement).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary).lineLimit(3)
                            Spacer()
                            Button("edit") { editing = shortcut }.buttonStyle(.plain).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary).pointerCursor()
                            Button(action: { spaceStore.update { $0.shortcuts.removeAll { $0.id == shortcut.id } } }) {
                                Image(systemName: "trash").font(.system(size: 11)).foregroundStyle(Paper.inkTertiary)
                            }
                            .buttonStyle(.plain).pointerCursor()
                        }
                        .padding(.vertical, 14)
                        .paperRowRule()
                    }
                }
            }
        }
        .sheet(isPresented: $isAdding) {
            ShortcutEditorSheet(shortcut: nil, existing: spaceStore.space.shortcuts) { new in spaceStore.update { $0.shortcuts.append(new) } }
        }
        .sheet(item: $editing) { shortcut in
            ShortcutEditorSheet(shortcut: shortcut, existing: spaceStore.space.shortcuts) { changed in
                spaceStore.update { space in
                    if let index = space.shortcuts.firstIndex(where: { $0.id == changed.id }) { space.shortcuts[index] = changed }
                }
            }
        }
    }
}

struct ShortcutEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    let shortcut: SpokenShortcut?
    let existing: [SpokenShortcut]
    let onSave: (SpokenShortcut) -> Void
    @State private var trigger: String
    @State private var replacement: String

    init(shortcut: SpokenShortcut?, existing: [SpokenShortcut], onSave: @escaping (SpokenShortcut) -> Void) {
        self.shortcut = shortcut
        self.existing = existing
        self.onSave = onSave
        _trigger = State(initialValue: shortcut?.trigger ?? "")
        _replacement = State(initialValue: shortcut?.replacement ?? "")
    }

    private var triggerInUse: Bool {
        let normalized = TakeFormatter.normalizedForMatching(trigger)
        return existing.contains { $0.id != shortcut?.id && TakeFormatter.normalizedForMatching($0.trigger) == normalized }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(shortcut == nil ? "teach openclicky a shortcut" : "edit shortcut").font(Paper.title(20)).foregroundStyle(Paper.ink)
            VStack(alignment: .leading, spacing: 6) {
                Text("you say").font(Paper.label(11)).foregroundStyle(Paper.inkSecondary)
                TextField("my sign-off", text: $trigger).textFieldStyle(.roundedBorder).font(Paper.body(14))
                if triggerInUse { Text("that spoken phrase is already in use.").font(Paper.body(11)).foregroundStyle(Paper.danger) }
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("openclicky writes").font(Paper.label(11)).foregroundStyle(Paper.inkSecondary)
                TextEditor(text: $replacement).font(Paper.body(13)).frame(height: 120)
                    .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Paper.hairline))
            }
            HStack {
                Spacer()
                Button("cancel") { dismiss() }.buttonStyle(PaperPillButtonStyle())
                Button("save") {
                    onSave(SpokenShortcut(id: shortcut?.id ?? UUID(), trigger: trigger.trimmingCharacters(in: .whitespaces), replacement: replacement, createdAt: shortcut?.createdAt ?? Date()))
                    dismiss()
                }
                .buttonStyle(PaperPillButtonStyle(prominent: true))
                .disabled(trigger.trimmingCharacters(in: .whitespaces).isEmpty || replacement.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || triggerInUse)
            }
        }
        .padding(24)
        .frame(width: 480)
        .background(Paper.background)
    }
}
