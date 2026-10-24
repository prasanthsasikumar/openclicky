//
//  FocusedFieldReader.swift
//  OpenClicky
//
//  What is under the cursor in the app in front, through Accessibility: whether there is a text
//  field to paste into, what it holds (so a paste can be confirmed), the selection (what "this"
//  means to Hey Clicky), and the captions nearby (so a name on screen is spelled the way it is
//  written). Everything here is read-only and bounded; without the Accessibility permission every
//  answer is "unknown", never a guess.
//

import AppKit
import ApplicationServices
import Foundation

struct FocusedFieldSnapshot: Equatable {
    var appBundleID: String?
    var appName: String?
    var role: String?
    /// A text field, text area or editable web area has focus.
    var isEditable: Bool
    /// The field is a password field: nothing is ever pasted there.
    var isSecure: Bool
    var value: String?
    var selectedText: String?

    static let unknown = FocusedFieldSnapshot(appBundleID: nil, appName: nil, role: nil, isEditable: false, isSecure: false, value: nil, selectedText: nil)
}

/// The focused element as Accessibility reports it system-wide, for live typing: kept at press and
/// compared before every typed phrase, so keystrokes only ever go to the field the take began in.
struct FocusedTextTarget {
    /// The pid Accessibility gives the element (NSWorkspace's frontmost app lags right after ⌘-Tab).
    let processID: pid_t
    let element: AXUIElement
    let role: String?
    let isSecure: Bool
    /// The bundle id of the process that owns the element (nil when it cannot be told).
    var bundleID: String? = nil

    /// Text roles live typing types into. Not AXWebArea (a focused page, where letters are
    /// single-key shortcuts — Gmail's e, #, !) and not the editable-ancestor guess the paste path
    /// uses for Electron apps: those keep the paste path.
    static let liveTypingTextRoles: Set<String> = ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"]

    static func isLiveTypingTextRole(_ role: String?) -> Bool {
        role.map { liveTypingTextRoles.contains($0) } ?? false
    }

    /// A real text field that is not a password field.
    var acceptsLiveTyping: Bool { Self.isLiveTypingTextRole(role) && !isSecure }

    /// The same element, by Accessibility's own identity (`CFEqual`) and pid. Element identity was
    /// chosen over pid + focused window: it also catches focus moving inside one window (compose
    /// box → message list). An app that hands out a new element object for the same field would
    /// stop live typing early, which only sends the rest to the pasteboard.
    func isSameElement(as other: FocusedTextTarget) -> Bool {
        processID == other.processID && CFEqual(element, other.element)
    }
}

enum FocusedFieldReader {
    private static let editableRoles: Set<String> = ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField", "AXWebArea"]
    private static let maxValueCharacters = 20_000
    private static let messagingTimeoutSeconds: Float = 0.25

    /// The focused element of the app in front, now.
    static func snapshot() -> FocusedFieldSnapshot {
        guard let frontApp = NSWorkspace.shared.frontmostApplication else { return .unknown }
        var snapshot = FocusedFieldSnapshot.unknown
        snapshot.appBundleID = frontApp.bundleIdentifier
        snapshot.appName = frontApp.localizedName
        guard AXIsProcessTrusted() else { return snapshot }
        let application = AXUIElementCreateApplication(frontApp.processIdentifier)
        AXUIElementSetMessagingTimeout(application, messagingTimeoutSeconds)
        guard let focused = element(application, kAXFocusedUIElementAttribute) else { return snapshot }
        let role = string(focused, kAXRoleAttribute)
        let subrole = string(focused, kAXSubroleAttribute)
        snapshot.role = role
        snapshot.isSecure = subrole == "AXSecureTextField"
        var editable = role.map { editableRoles.contains($0) } ?? false
        // Electron and web apps focus a generic element with an editable ancestor or a settable value.
        if !editable, let role, role == "AXGroup" || role == "AXStaticText" || role == "AXUnknown" {
            editable = isSettable(focused, kAXValueAttribute) || hasEditableAncestor(focused)
        }
        snapshot.isEditable = editable && !snapshot.isSecure
        if snapshot.isEditable, !snapshot.isSecure {
            if let value = string(focused, kAXValueAttribute) { snapshot.value = String(value.prefix(maxValueCharacters)) }
            if let selected = string(focused, kAXSelectedTextAttribute), !selected.isEmpty { snapshot.selectedText = selected }
        }
        return snapshot
    }

    /// The focused element system-wide, with its pid, role and whether it is a password field; nil
    /// without the permission or when nothing has focus. A few AX calls under the short messaging
    /// timeout, since it runs before every phrase live typing types.
    static func focusedTextTarget() -> FocusedTextTarget? {
        guard AXIsProcessTrusted() else { return nil }
        let systemWide = AXUIElementCreateSystemWide()
        // A timeout set on the system-wide element is the default for every Accessibility call this
        // process makes, so it is put back (0 = the system default) right after this one read.
        AXUIElementSetMessagingTimeout(systemWide, messagingTimeoutSeconds)
        let focusedElement = element(systemWide, kAXFocusedUIElementAttribute)
        AXUIElementSetMessagingTimeout(systemWide, 0)
        guard let focused = focusedElement else { return nil }
        AXUIElementSetMessagingTimeout(focused, messagingTimeoutSeconds)
        var processID: pid_t = 0
        guard AXUIElementGetPid(focused, &processID) == .success else { return nil }
        return FocusedTextTarget(processID: processID, element: focused, role: string(focused, kAXRoleAttribute),
                                 isSecure: string(focused, kAXSubroleAttribute) == "AXSecureTextField",
                                 bundleID: NSRunningApplication(processIdentifier: processID)?.bundleIdentifier)
    }

    /// Captions near the focused element: the titles and values of its window's visible controls,
    /// bounded, de-duplicated, for the formatter's spelling hints. Empty without the permission.
    static func nearbyTerms(limit: Int = 40) -> [String] {
        guard AXIsProcessTrusted(), let frontApp = NSWorkspace.shared.frontmostApplication else { return [] }
        let application = AXUIElementCreateApplication(frontApp.processIdentifier)
        AXUIElementSetMessagingTimeout(application, messagingTimeoutSeconds)
        guard let window = element(application, kAXFocusedWindowAttribute) else { return [] }
        var seen = Set<String>()
        var terms: [String] = []
        var visited = 0
        func walk(_ node: AXUIElement, depth: Int) {
            guard depth < 10, visited < 400, terms.count < limit else { return }
            visited += 1
            for attribute in [kAXTitleAttribute, kAXDescriptionAttribute] {
                guard let text = string(node, attribute)?.trimmingCharacters(in: .whitespacesAndNewlines),
                      text.count >= 2, text.count <= 60, text.contains(where: { $0.isLetter }) else { continue }
                let key = text.lowercased()
                if seen.insert(key).inserted { terms.append(text) }
            }
            guard let children = array(node, kAXChildrenAttribute) else { return }
            for child in children.prefix(60) { walk(child, depth: depth + 1) }
        }
        walk(window, depth: 0)
        return terms
    }

    // MARK: AX helpers

    private static func element(_ node: AXUIElement, _ attribute: String) -> AXUIElement? {
        var value: AnyObject?
        guard AXUIElementCopyAttributeValue(node, attribute as CFString, &value) == .success, let value else { return nil }
        // swiftlint:disable:next force_cast
        return CFGetTypeID(value) == AXUIElementGetTypeID() ? (value as! AXUIElement) : nil
    }

    private static func array(_ node: AXUIElement, _ attribute: String) -> [AXUIElement]? {
        var value: AnyObject?
        guard AXUIElementCopyAttributeValue(node, attribute as CFString, &value) == .success else { return nil }
        return value as? [AXUIElement]
    }

    private static func string(_ node: AXUIElement, _ attribute: String) -> String? {
        var value: AnyObject?
        guard AXUIElementCopyAttributeValue(node, attribute as CFString, &value) == .success else { return nil }
        return value as? String
    }

    private static func isSettable(_ node: AXUIElement, _ attribute: String) -> Bool {
        var settable: DarwinBoolean = false
        return AXUIElementIsAttributeSettable(node, attribute as CFString, &settable) == .success && settable.boolValue
    }

    private static func hasEditableAncestor(_ node: AXUIElement) -> Bool {
        var current: AXUIElement? = node
        for _ in 0..<6 {
            guard let parent = current.flatMap({ element($0, kAXParentAttribute) }) else { return false }
            if let role = string(parent, kAXRoleAttribute), editableRoles.contains(role) { return true }
            current = parent
        }
        return false
    }
}

/// Where a paste landed, decided by reading the field back.
enum PasteLanding {
    /// The field's text grew by the pasted text (or contains it): it landed.
    case verified
    /// The keystroke was posted into an editable element but the text was not seen in it: it could
    /// not be read back (a web area, an Electron view — the paste almost always landed there), the
    /// text was not found, or another app has focus now.
    case posted
    /// After the paste nothing editable has focus (no focused element, no role, not editable — a
    /// sheet that swallowed ⌘V): the paste clearly had nowhere to go, so the words are left on the
    /// pasteboard.
    case noTarget
    /// No Accessibility permission: the text is on the pasteboard only.
    case leftOnPasteboard

    /// Reads the field back, briefly after the paste, and compares it with what was there before.
    static func verify(text: String, before: FocusedFieldSnapshot, settleSeconds: TimeInterval = 0.35) async -> PasteLanding {
        try? await Task.sleep(nanoseconds: UInt64(settleSeconds * 1_000_000_000))
        let after = FocusedFieldReader.snapshot()
        if keepsPastedWordsOnPasteboard(before: before, after: after) {
            await MainActor.run { FrontAppTextInserter.copyToPasteboard(text) }
        }
        return landing(of: text, before: before, after: after)
    }

    /// An editable element whose value cannot be read back: the paste probably landed, so the orb
    /// says nothing about ⌘V (pressing it would give the words twice) — but the person's old
    /// clipboard is not put back, so the words stay recoverable with ⌘V if it did not land.
    static func keepsPastedWordsOnPasteboard(before: FocusedFieldSnapshot, after: FocusedFieldSnapshot) -> Bool {
        after.appBundleID == before.appBundleID && after.role != nil && after.isEditable && after.value == nil
    }

    /// The comparison `verify` makes, on the field as it was before the paste and as it is now.
    static func landing(of text: String, before: FocusedFieldSnapshot, after: FocusedFieldSnapshot) -> PasteLanding {
        guard after.appBundleID == before.appBundleID else { return .posted }
        guard after.role != nil, after.isEditable else { return .noTarget }
        guard let value = after.value else { return .posted }
        let needle = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let probe = String(needle.prefix(200))
        if !probe.isEmpty, value.contains(probe) { return .verified }
        if let previous = before.value, value.count >= previous.count + max(1, needle.count / 2) { return .verified }
        return .posted
    }
}
