//
//  DictionaryPageView.swift
//  OpenClicky
//
//  Names and terms written exactly as you expect: "you say aditya shatriya → OpenClicky writes
//  Aaditya Kshatriya". Each term has the written form and the ways it tends to be heard.
//

import SwiftUI
import Combine

struct DictionaryPageView: View {
    @ObservedObject var spaceStore: DictationSpaceStore
    @State private var isAdding = false
    @State private var editing: DictionaryTerm?

    var body: some View {
        PageScaffold(title: "dictionary") {
            PaperCard {
                HStack(spacing: 24) {
                    HStack(spacing: 6) {
                        Text("words openclicky").font(Paper.title(19)).foregroundStyle(Paper.ink)
                        Text("never misspells.").font(Paper.title(19)).foregroundStyle(Paper.ink)
                            .background(alignment: .bottom) { RoundedRectangle(cornerRadius: 3).fill(Paper.highlighter).frame(height: 8).offset(y: -2) }
                    }
                    Rectangle().fill(Paper.hairline).frame(width: 1, height: 44)
                    HStack(spacing: 18) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text("you say").font(Paper.label(10)).foregroundStyle(Paper.inkSecondary)
                            Text("“aditya shatriya”").font(.system(size: 13, design: .serif).italic()).foregroundStyle(Paper.inkSecondary)
                        }
                        Image(systemName: "arrow.right").foregroundStyle(Paper.success)
                        VStack(alignment: .leading, spacing: 3) {
                            Text("openclicky writes").font(Paper.label(10)).foregroundStyle(Paper.inkSecondary)
                            Text("Aaditya Kshatriya").font(Paper.body(15, weight: .semibold)).foregroundStyle(Paper.ink)
                        }
                    }
                    Spacer()
                }
                .padding(22)
            }

            Button(action: { isAdding = true }) {
                HStack(spacing: 10) {
                    Image(systemName: "plus.circle").font(.system(size: 14)).foregroundStyle(Paper.ink)
                    Text("teach openclicky a term").font(Paper.body(13, weight: .medium)).foregroundStyle(Paper.ink)
                    Spacer()
                    Image(systemName: "chevron.right").font(.system(size: 11, weight: .semibold)).foregroundStyle(Paper.accent)
                }
                .padding(.horizontal, 18).padding(.vertical, 14)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Paper.card))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Paper.accent.opacity(0.5)))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain).pointerCursor()

            if spaceStore.space.dictionary.isEmpty {
                VStack(spacing: 10) {
                    OrbMarkShape().fill(Paper.hairline).frame(width: 48, height: 48)
                    Text("nothing here yet — teach openclicky its first term.").font(Paper.body(13)).foregroundStyle(Paper.inkTertiary)
                }
                .frame(maxWidth: .infinity).padding(.top, 60)
            } else {
                VStack(spacing: 0) {
                    ForEach(spaceStore.space.dictionary.sorted { $0.written.lowercased() < $1.written.lowercased() }) { term in
                        HStack(spacing: 14) {
                            Text(term.written).font(Paper.body(15, weight: .medium)).foregroundStyle(Paper.ink)
                            if !term.heardAs.isEmpty {
                                Text("heard as " + term.heardAs.map { "“\($0)”" }.joined(separator: ", ")).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary).lineLimit(1)
                            }
                            Spacer()
                            Button("edit") { editing = term }.buttonStyle(.plain).font(Paper.body(12)).foregroundStyle(Paper.inkSecondary).pointerCursor()
                            Button(action: { spaceStore.update { $0.dictionary.removeAll { $0.id == term.id } } }) {
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
            TermEditorSheet(term: nil) { term in spaceStore.update { $0.dictionary.append(term) } }
        }
        .sheet(item: $editing) { term in
            TermEditorSheet(term: term) { changed in
                spaceStore.update { space in
                    if let index = space.dictionary.firstIndex(where: { $0.id == changed.id }) { space.dictionary[index] = changed }
                }
            }
        }
    }
}

struct TermEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    let term: DictionaryTerm?
    let onSave: (DictionaryTerm) -> Void
    @State private var written: String
    @State private var heardAs: String

    init(term: DictionaryTerm?, onSave: @escaping (DictionaryTerm) -> Void) {
        self.term = term
        self.onSave = onSave
        _written = State(initialValue: term?.written ?? "")
        _heardAs = State(initialValue: term?.heardAs.joined(separator: ", ") ?? "")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(term == nil ? "teach openclicky a term" : "edit term").font(Paper.title(20)).foregroundStyle(Paper.ink)
            VStack(alignment: .leading, spacing: 6) {
                Text("openclicky writes").font(Paper.label(11)).foregroundStyle(Paper.inkSecondary)
                TextField("Aaditya Kshatriya", text: $written).textFieldStyle(.roundedBorder).font(Paper.body(14))
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("you say (optional, comma-separated)").font(Paper.label(11)).foregroundStyle(Paper.inkSecondary)
                TextField("aditya shatriya, aditya", text: $heardAs).textFieldStyle(.roundedBorder).font(Paper.body(14))
                Text("the written form is always matched too, in any casing.").font(Paper.body(11)).foregroundStyle(Paper.inkTertiary)
            }
            HStack {
                Spacer()
                Button("cancel") { dismiss() }.buttonStyle(PaperPillButtonStyle())
                Button("save") {
                    let aliases = heardAs.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
                    onSave(DictionaryTerm(id: term?.id ?? UUID(), written: written.trimmingCharacters(in: .whitespaces), heardAs: aliases, createdAt: term?.createdAt ?? Date()))
                    dismiss()
                }
                .buttonStyle(PaperPillButtonStyle(prominent: true))
                .disabled(written.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(24)
        .frame(width: 460)
        .background(Paper.background)
    }
}
