//
//  TeacherIslandView.swift
//  OpenClicky
//
//  The Teacher's row on the island: what is on the board and a button that clears it, or, with
//  nothing up, a line saying what to ask. A switch on the right turns the Teacher plugin off (and
//  on again from Settings › Island), the same switch Settings has, so a lesson can be stopped
//  from happening without opening the window.
//

import SwiftUI

/// A built-in, always on the island: lessons drawn on the screen, behind one switch (this row's,
/// and the same one in Settings), off until it is flipped.
struct TeacherIslandComponent: IslandComponent {
    let id = TeacherPlugin.id
    let title = "Teacher"
    let symbolName = "scribble.variable"
    let preferredHeight: CGFloat = 64
    let isPlugin = false
    let settingsDetail: String? = "Ask \"teach me how Pythagoras works\" and Clicky draws it in handwriting in an empty part of your screen while it explains. What a drawing can't show comes up as a picture from Wikimedia Commons (only the search words are sent)."
    func isAvailable(in context: IslandContext) -> Bool { true }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(TeacherIslandView(board: TeacherBoard.shared))
    }
}

struct TeacherIslandView: View {
    @ObservedObject var board: TeacherBoard
    @State private var isHoveringClear = false
    @State private var isOn = true

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "scribble.variable")
                .font(.system(size: 18, weight: .semibold))
                .foregroundStyle(.white.opacity(0.85))
            VStack(alignment: .leading, spacing: 2) {
                if let lesson = board.lesson {
                    Text("on the board")
                        .font(.system(size: 11))
                        .foregroundStyle(.white.opacity(0.55))
                    Text(lesson.title)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.white)
                        .lineLimit(1)
                } else if !isOn {
                    Text("lessons are off")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.7))
                    Text("switch on and ask me to teach you something")
                        .font(.system(size: 11))
                        .foregroundStyle(.white.opacity(0.45))
                } else {
                    Text("ask me to teach you something")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.white)
                    Text("i'll draw it on your screen while i explain")
                        .font(.system(size: 11))
                        .foregroundStyle(.white.opacity(0.55))
                }
            }
            Spacer(minLength: 8)
            if board.lesson != nil {
                Button {
                    board.clear(reason: "the island")
                } label: {
                    Text("clear")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .background(Capsule().fill(.white.opacity(isHoveringClear ? 0.28 : 0.16)))
                }
                .buttonStyle(.plain)
                .onHover { isHoveringClear = $0 }
                .pointerCursor()
                .accessibilityLabel("Clear the board")
            }
            // The row's own switch pauses lessons; the row stays, so it is here to switch back on.
            // Settings › Island is where the Teacher leaves the island altogether.
            Toggle("", isOn: Binding(
                get: { isOn },
                set: { newValue in
                    isOn = newValue
                    TeacherPlugin.setLessonsOn(newValue)
                    if !newValue { board.clear(reason: "the Teacher was switched off") }
                }
            ))
            .toggleStyle(.switch)
            .controlSize(.small)
            .labelsHidden()
            .pointerCursor()
            .help(isOn ? "Lessons are on" : "Lessons are off")
            .accessibilityLabel("Teacher lessons")
        }
        .padding(.horizontal, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear { isOn = TeacherPlugin.lessonsAreOn() }
    }
}
