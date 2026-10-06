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
    /// The keystroke was posted but the field could not be read back.
    case posted
    /// No editable field had focus: nothing was posted; the text is left for the orb and the pasteboard.
    case noTarget
    /// No Accessibility permission: the text is on the pasteboard only.
    case leftOnPasteboard

    /// Reads the field back, briefly after the paste, and compares it with what was there before.
    static func verify(text: String, before: FocusedFieldSnapshot, settleSeconds: TimeInterval = 0.35) async -> PasteLanding {
        try? await Task.sleep(nanoseconds: UInt64(settleSeconds * 1_000_000_000))
        let after = FocusedFieldReader.snapshot()
        guard after.appBundleID == before.appBundleID else { return .posted }
        guard let value = after.value else { return .posted }
        let needle = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let probe = String(needle.prefix(200))
        if !probe.isEmpty, value.contains(probe) { return .verified }
        if let previous = before.value, value.count >= previous.count + max(1, needle.count / 2) { return .verified }
        return .posted
    }
}
