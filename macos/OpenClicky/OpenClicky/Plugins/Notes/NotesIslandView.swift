//
//  NotesIslandView.swift
//  OpenClicky
//
//  The Notes component: a plain multi-line notepad on the island, saved on this Mac as you type.
//

import AppKit
import SwiftUI

/// A plugin: a scratch pad that is always one hover away.
struct NotesIslandComponent: IslandComponent {
    let id = IslandPluginRegistry.notesID
    let title = "Notes"
    let symbolName = "note.text"
    let preferredHeight: CGFloat = 120
    let isPlugin = true
    let settingsDetail: String? = "A quick notepad on the island. Click it to type; it saves on this Mac as you go and never leaves it."
    func isAvailable(in context: IslandContext) -> Bool { true }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(NotesIslandView(store: NotesStore.shared, hud: companionManager.notchHUDManager))
    }
}

struct NotesIslandView: View {
    @ObservedObject var store: NotesStore
    let hud: NotchHUDManager

    var body: some View {
        ZStack(alignment: .topLeading) {
            NotesTextEditor(
                text: store.text,
                onChange: { store.update($0) },
                onBeginTyping: { hud.beginComponentTyping(in: $0) },
                onEndTyping: { view in
                    store.saveNow()
                    hud.endComponentTyping(in: view)
                }
            )
            if store.text.isEmpty {
                // The text view's own inset puts the first line here.
                Text("Click to jot something down")
                    .font(.system(size: 13))
                    .foregroundColor(.white.opacity(0.35))
                    .padding(.horizontal, NotesTextEditor.inset.width + 5)
                    .padding(.vertical, NotesTextEditor.inset.height)
                    .allowsHitTesting(false)
            }
        }
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.white.opacity(0.07)))
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.bottom, 10)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Notes")
    }
}

/// The editor is an AppKit text view, not SwiftUI's TextEditor: the island is a non-activating
/// panel, and only an AppKit first responder in a key window receives typing there (SwiftUI focus
/// needs an active app). A click asks the HUD for the keyboard; esc or leaving the island gives
/// it back.
struct NotesTextEditor: NSViewRepresentable {
    static let inset = CGSize(width: 6, height: 7)

    let text: String
    let onChange: (String) -> Void
    let onBeginTyping: (NSView) -> Void
    let onEndTyping: (NSView) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let textView = IslandNotesTextView()
        textView.isRichText = false
        textView.importsGraphics = false
        textView.allowsUndo = true
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.drawsBackground = false
        textView.font = .systemFont(ofSize: 13)
        textView.textColor = .white
        textView.insertionPointColor = .white
        textView.typingAttributes = [.font: NSFont.systemFont(ofSize: 13), .foregroundColor: NSColor.white]
        textView.selectedTextAttributes = [.backgroundColor: NSColor.white.withAlphaComponent(0.25)]
        textView.textContainerInset = Self.inset
        textView.string = text
        textView.delegate = context.coordinator
        textView.onMouseDown = { [weak textView] in
            guard let textView else { return }
            context.coordinator.parent.onBeginTyping(textView)
        }
        textView.onEscape = { [weak textView] in
            guard let textView else { return }
            context.coordinator.parent.onEndTyping(textView)
        }

        let scrollView = NSScrollView()
        // The usual scrolling text view: as wide as the scroll view, growing downwards.
        let contentSize = scrollView.contentSize
        textView.frame = NSRect(origin: .zero, size: contentSize)
        textView.minSize = NSSize(width: 0, height: contentSize.height)
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.textContainer?.containerSize = NSSize(width: contentSize.width, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainer?.widthTracksTextView = true
        scrollView.drawsBackground = false
        scrollView.hasVerticalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.scrollerStyle = .overlay
        scrollView.scrollerKnobStyle = .light
        scrollView.borderType = .noBorder
        scrollView.documentView = textView
        context.coordinator.textView = textView
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        context.coordinator.parent = self
        // Only text from elsewhere is pushed in; echoing the user's own typing back would move
        // the caret.
        if let textView = context.coordinator.textView, textView.string != text {
            textView.string = text
        }
    }

    static func dismantleNSView(_ scrollView: NSScrollView, coordinator: Coordinator) {
        guard let textView = coordinator.textView else { return }
        coordinator.parent.onEndTyping(textView)
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: NotesTextEditor
        weak var textView: IslandNotesTextView?

        init(_ parent: NotesTextEditor) { self.parent = parent }

        func textDidChange(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            parent.onChange(textView.string)
        }
    }
}

/// A text view that reports the click that should give it the keyboard, and esc.
final class IslandNotesTextView: NSTextView {
    var onMouseDown: (() -> Void)?
    var onEscape: (() -> Void)?

    // The island's window is usually not key when the pointer reaches it: the first click must
    // place the caret, not just wake the window.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        if window?.firstResponder !== self || window?.isKeyWindow != true { onMouseDown?() }
        super.mouseDown(with: event)
    }

    // Esc arrives as cancelOperation: or, through the standard key bindings, as complete:
    // (autocompletion); either one lets go of the keyboard.
    override func cancelOperation(_ sender: Any?) {
        onEscape?()
    }

    override func complete(_ sender: Any?) {
        onEscape?()
    }
}
