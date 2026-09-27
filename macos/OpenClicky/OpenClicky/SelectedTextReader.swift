//
//  SelectedTextReader.swift
//  OpenClicky
//
//  The text the user has highlighted in the app in front, for the screen context of a turn.
//  "What does this mean?" with a word selected is about that word, and pixels cannot say what is
//  selected as reliably as the app can: in the sibling app Saathi, a question about a highlighted
//  word was answered about the thing under the pointer instead.
//
//  Needs Accessibility, which OpenClicky already asks for. Nil without it, in a password field, or
//  when nothing is selected.
//

import AppKit
import ApplicationServices

enum SelectedTextReader {

    /// Longer than a caption, shorter than a document: a selected paragraph is the point, a
    /// select-all of a long page is not worth the tokens.
    nonisolated static let maximumCharacters = 600

    static func currentSelection() -> String? {
        guard AXIsProcessTrusted() else { return nil }
        // The front app first: the system-wide element's focused-element query fails outright
        // (kAXErrorCannotComplete) for Terminal, which answers fine when asked itself.
        var candidateElements: [AXUIElement] = []
        if let frontmostApplication = NSWorkspace.shared.frontmostApplication,
           frontmostApplication.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            candidateElements.append(AXUIElementCreateApplication(frontmostApplication.processIdentifier))
        }
        candidateElements.append(AXUIElementCreateSystemWide())

        for candidateElement in candidateElements {
            AXUIElementSetMessagingTimeout(candidateElement, 0.25)
            var focusedValue: CFTypeRef?
            guard AXUIElementCopyAttributeValue(candidateElement, kAXFocusedUIElementAttribute as CFString, &focusedValue) == .success,
                  let focusedValue, CFGetTypeID(focusedValue) == AXUIElementGetTypeID() else { continue }
            let focusedElement = focusedValue as! AXUIElement
            AXUIElementSetMessagingTimeout(focusedElement, 0.25)

            var roleValue: CFTypeRef?
            AXUIElementCopyAttributeValue(focusedElement, kAXRoleAttribute as CFString, &roleValue)
            if (roleValue as? String) == "AXSecureTextField" { return nil }

            var selectedTextValue: CFTypeRef?
            guard AXUIElementCopyAttributeValue(focusedElement, kAXSelectedTextAttribute as CFString, &selectedTextValue) == .success,
                  let selectedText = selectedTextValue as? String else { return nil }
            return tidied(selectedText)
        }
        return nil
    }

    /// Whitespace collapsed and capped; nil when nothing is left.
    nonisolated static func tidied(_ selectedText: String) -> String? {
        let collapsed = selectedText.split(whereSeparator: { $0.isWhitespace || $0.isNewline }).joined(separator: " ")
        guard !collapsed.isEmpty else { return nil }
        guard collapsed.count > maximumCharacters else { return collapsed }
        return String(collapsed.prefix(maximumCharacters)) + "…"
    }

    /// The line added to a turn's screen context.
    nonisolated static func contextLine(for selectedText: String) -> String {
        "The user has selected (highlighted) this text on screen: «\(selectedText)». When they say \"this\", \"this line\" or \"this word\", they almost certainly mean the selection — answer about that text, not about whatever is under the pointer."
    }
}
