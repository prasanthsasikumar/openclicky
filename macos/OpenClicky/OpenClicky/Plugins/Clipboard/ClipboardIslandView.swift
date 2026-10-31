//
//  ClipboardIslandView.swift
//  OpenClicky
//
//  The Clipboard component: the last things copied, newest first. A click puts one back on the
//  clipboard; Clear forgets them all.
//

import SwiftUI

/// A plugin: what you copied lately, a click away from being copied again.
struct ClipboardIslandComponent: IslandComponent {
    let id = IslandPluginRegistry.clipboardID
    let title = "Clipboard"
    let symbolName = "list.clipboard"
    let preferredHeight: CGFloat = 130
    let isPlugin = true
    let settingsDetail: String? = "Keeps the last 30 things you copied while OpenClicky runs, in memory only. Copies password managers mark as private are skipped."

    @MainActor init() {
        // The list should hold what was copied since launch, not since it was first opened; the
        // island builds its components at launch, so this is where the store starts following
        // its switch.
        ClipboardStore.shared.followSettings()
    }

    func isAvailable(in context: IslandContext) -> Bool { true }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(ClipboardIslandView(store: ClipboardStore.shared))
    }
}

struct ClipboardIslandView: View {
    @ObservedObject var store: ClipboardStore
    @State private var hoveredID: UUID?
    @State private var copiedID: UUID?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(store.history.entries.isEmpty ? "Clipboard" : "Copied lately")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(IslandDash.secondaryText)
                Spacer()
                if !store.history.entries.isEmpty {
                    Button("Clear", action: store.clear)
                        .buttonStyle(IslandPillButtonStyle())
                        .help("Forget everything in this list")
                }
            }
            if store.history.entries.isEmpty {
                Text("Copy something and it shows up here.")
                    .font(.system(size: 12))
                    .foregroundColor(.white.opacity(0.45))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView(.vertical, showsIndicators: false) {
                    LazyVStack(spacing: 4) {
                        ForEach(store.history.entries) { entry in
                            row(for: entry)
                        }
                    }
                    .padding(.bottom, 8)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .animation(.easeInOut(duration: 0.15), value: store.history.entries.map(\.id))
    }

    private func row(for entry: ClipboardEntry) -> some View {
        let isCopied = copiedID == entry.id
        return Button(action: { copy(entry) }) {
            HStack(spacing: 8) {
                Image(systemName: isCopied ? "checkmark" : entry.content.symbolName)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.white.opacity(0.7))
                    .frame(width: 16)
                Text(isCopied ? "Copied" : entry.content.label)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.white.opacity(isCopied ? 1 : 0.9))
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .background(RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Color.white.opacity(hoveredID == entry.id ? 0.12 : 0.07)))
            .contentShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .onHover { hovering in
            if hovering { hoveredID = entry.id } else if hoveredID == entry.id { hoveredID = nil }
        }
        .help(helpText(for: entry))
        .accessibilityLabel(entry.content.label)
        .accessibilityHint("Copies it again")
    }

    private func helpText(for entry: ClipboardEntry) -> String {
        if case .text(let text) = entry.content { return String(text.prefix(500)) }
        return entry.content.label
    }

    private func copy(_ entry: ClipboardEntry) {
        store.copyBack(entry)
        copiedID = entry.id
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 1_200_000_000)
            if copiedID == entry.id { copiedID = nil }
        }
    }
}
