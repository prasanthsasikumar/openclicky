//
//  FrontAppTextInserter.swift
//  OpenClicky
//
//  Types dictated text into whatever app is in front (fn + control dictation): the text goes on
//  the pasteboard, a ⌘V keystroke is posted to the front app, and the pasteboard is restored a
//  moment later. Posting keystrokes needs the Accessibility permission OpenClicky already asks
//  for; without it the text is left on the pasteboard for a manual paste.
//

import AppKit
import ApplicationServices

enum FrontAppTextInserter {
    enum Outcome: Equatable {
        /// The keystroke was posted; the app in front received the text.
        case typed
        /// No Accessibility permission (or no event source): the text is on the pasteboard instead.
        case leftOnPasteboard
    }

    private static let virtualKeyCodeV: CGKeyCode = 9

    @MainActor
    static func insert(_ text: String) -> Outcome {
        let pasteboard = NSPasteboard.general
        let previousPasteboardItems = snapshot(of: pasteboard)
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)

        guard AXIsProcessTrusted(),
              let eventSource = CGEventSource(stateID: .combinedSessionState),
              let pasteKeyDown = CGEvent(keyboardEventSource: eventSource, virtualKey: virtualKeyCodeV, keyDown: true),
              let pasteKeyUp = CGEvent(keyboardEventSource: eventSource, virtualKey: virtualKeyCodeV, keyDown: false) else {
            return .leftOnPasteboard
        }
        pasteKeyDown.flags = .maskCommand
        pasteKeyUp.flags = .maskCommand
        pasteKeyDown.post(tap: .cghidEventTap)
        pasteKeyUp.post(tap: .cghidEventTap)

        // Give the front app time to read the pasteboard before the user's clipboard comes back.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
            guard pasteboard.string(forType: .string) == text else { return }
            pasteboard.clearContents()
            if !previousPasteboardItems.isEmpty {
                pasteboard.writeObjects(previousPasteboardItems)
            }
        }
        return .typed
    }

    /// Copies every item and type on the pasteboard so it can be put back after the paste.
    private static func snapshot(of pasteboard: NSPasteboard) -> [NSPasteboardItem] {
        (pasteboard.pasteboardItems ?? []).map { item in
            let copiedItem = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) {
                    copiedItem.setData(data, forType: type)
                }
            }
            return copiedItem
        }
    }
}
