//
//  AccessibleElementLocator.swift
//  OpenClicky
//
//  `point_at` for captions that appear more than once ("Edit" on every row of a list). OCR reads
//  pixels, so every one of those captions looks identical and only distance separates them, which
//  the model's own 30–100 px error is enough to get wrong. Accessibility knows more than the pixels
//  do: each control carries a role, and it sits inside a row or group that has its own title, so
//  "the Edit button on the openclicky row" is answerable from the request itself.
//

import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

/// One control from the Accessibility tree, already mapped into the screenshot the model saw.
nonisolated struct AccessibleElementCandidate: Equatable {
    /// AX role, e.g. `AXButton`, `AXLink`, `AXStaticText`.
    let role: String
    /// The control's own caption (AXTitle, or AXDescription/AXValue when it has no title).
    let title: String
    /// Titles of the groups the control sits in, nearest ancestor first. This is where a list row's
    /// name lives, and it is what tells two identical buttons apart.
    let containerTitles: [String]
    /// Centre of the control in screenshot pixels, so it is comparable with the model's guess.
    let center: CGPoint
}

nonisolated enum AccessibleElementLocator {
    /// A candidate with the two numbers it is ranked by: how well the request describes it, and how
    /// far it is from the model's guess.
    private struct ScoredCandidate {
        let candidate: AccessibleElementCandidate
        let score: Int
        let distance: CGFloat
    }

    /// Roles a user can actually point at and click. A static label with the same caption as a
    /// button is nearly always the button's own text, not the thing being asked about.
    static let actionableRoles: Set<String> = [
        "AXButton", "AXLink", "AXMenuItem", "AXMenuButton", "AXCheckBox", "AXRadioButton",
        "AXPopUpButton", "AXTextField", "AXTextArea", "AXDisclosureTriangle", "AXTab",
    ]

    /// The control called `hint` that the request is actually about.
    ///
    /// Candidates are the controls carrying that caption within `maxDistance` of the model's guess.
    /// They are ranked by what the request says rather than by distance alone: a word of the request
    /// appearing in a candidate's container titles ("openclicky" naming the row) is strong evidence,
    /// and an actionable role beats a static label repeating the same caption. Distance only breaks
    /// ties. When the two best candidates score the same and are closer together than
    /// `ambiguityMargin`, nothing here can tell them apart either, so nil is returned and the caller
    /// falls through to the Claude grounding pass.
    static func bestMatch(
        hint: String,
        userRequest: String?,
        near guess: CGPoint,
        in candidates: [AccessibleElementCandidate],
        maxDistance: CGFloat,
        ambiguityMargin: CGFloat
    ) -> AccessibleElementCandidate? {
        let needle = normalize(hint)
        guard needle.count >= 3, !ScreenTextLocator.genericWords.contains(needle) else { return nil }

        // Words of the request that could name a row or section: long enough to be distinctive, not
        // a generic UI noun, and not the caption itself (every candidate carries that).
        let distinguishingRequestWords = Set(words(in: userRequest ?? ""))
            .filter { $0.count >= 3 && !ScreenTextLocator.genericWords.contains($0) && $0 != needle }

        var scoredCandidates: [ScoredCandidate] = []
        for candidate in candidates where candidateCarriesCaption(candidate, needle: needle) {
            let distance = hypot(candidate.center.x - guess.x, candidate.center.y - guess.y)
            guard distance <= maxDistance else { continue }
            let containerWords = Set(candidate.containerTitles.flatMap { words(in: $0) })
            let matchingRequestWordCount = distinguishingRequestWords.filter { containerWords.contains($0) }.count
            let roleScore = actionableRoles.contains(candidate.role) ? 2 : 0
            scoredCandidates.append(
                ScoredCandidate(candidate: candidate, score: matchingRequestWordCount * 3 + roleScore, distance: distance)
            )
        }
        scoredCandidates.sort { first, second in
            first.score == second.score ? first.distance < second.distance : first.score > second.score
        }

        guard let bestCandidate = scoredCandidates.first else { return nil }
        if let runnerUpCandidate = scoredCandidates.dropFirst().first,
           runnerUpCandidate.score == bestCandidate.score,
           runnerUpCandidate.distance - bestCandidate.distance < ambiguityMargin {
            return nil
        }
        return bestCandidate.candidate
    }

    /// Whether the control's own caption is the one being asked for: the whole caption, or the
    /// caption as one of its words ("Edit profile" answers to "edit").
    private static func candidateCarriesCaption(_ candidate: AccessibleElementCandidate, needle: String) -> Bool {
        let title = normalize(candidate.title)
        return title == needle || words(in: title).contains(needle)
    }

    private static func normalize(_ text: String) -> String {
        words(in: text).joined(separator: " ")
    }

    private static func words(in text: String) -> [String] {
        text.lowercased()
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
    }

    // MARK: - Accessibility access

    /// The control the request is about, read from the front app's Accessibility tree and mapped
    /// into the captured screenshot. Nil without Accessibility permission, when the front app
    /// exposes no usable tree (many Electron and game windows do not), or when the candidates
    /// cannot be told apart, in which case the caller falls through to the Claude grounding pass.
    ///
    /// Only the front app's focused window is read. A control in a window behind it is not a
    /// candidate, which is the same limitation the screenshot has for anything that is covered.
    @MainActor
    static func locate(
        label: String,
        text: String,
        userRequest: String?,
        near guess: CGPoint,
        in capture: CompanionScreenCapture,
        maxDistance: CGFloat,
        ambiguityMargin: CGFloat
    ) -> AccessibleElementCandidate? {
        guard AXIsProcessTrusted() else { return nil }
        let candidates = candidatesInFrontWindow(capture: capture)
        guard !candidates.isEmpty else { return nil }
        // The caption the model gave is the better hint; its own description is the fallback.
        let captionHint = text.isEmpty ? label : text
        return bestMatch(
            hint: captionHint, userRequest: userRequest, near: guess,
            in: candidates, maxDistance: maxDistance, ambiguityMargin: ambiguityMargin
        )
    }

    /// Every captioned control in the front app's focused window, in screenshot pixels. The walk is
    /// capped the same way the tab-strip walk is: a deep tree costs more than the pass is worth.
    @MainActor
    private static func candidatesInFrontWindow(capture: CompanionScreenCapture) -> [AccessibleElementCandidate] {
        guard let frontApplication = NSWorkspace.shared.frontmostApplication else { return [] }
        let application = AXUIElementCreateApplication(frontApplication.processIdentifier)
        AXUIElementSetMessagingTimeout(application, 0.25)
        guard let window = copyElement(application, kAXFocusedWindowAttribute) ?? copyElement(application, kAXMainWindowAttribute) else { return [] }

        let primaryDisplayHeight = NSScreen.screens.first?.frame.height ?? capture.displayFrame.height
        var candidates: [AccessibleElementCandidate] = []
        var queue: [(element: AXUIElement, depth: Int, containerTitles: [String])] = [(window, 0, [])]
        var visitedCount = 0

        while !queue.isEmpty, visitedCount < 600 {
            let (element, depth, containerTitles) = queue.removeFirst()
            visitedCount += 1
            let role = copyString(element, kAXRoleAttribute) ?? ""
            let caption = copyString(element, kAXTitleAttribute)
                ?? copyString(element, kAXDescriptionAttribute)
                ?? copyString(element, kAXValueAttribute)
                ?? ""

            if !caption.isEmpty, let frame = copyFrame(element) {
                let globalCenter = CGPoint(x: frame.midX, y: frame.midY)
                let screenshotCenter = capture.screenshotPoint(forGlobalPoint: globalCenter, primaryDisplayHeight: primaryDisplayHeight)
                if capture.containsScreenshotPoint(screenshotCenter) {
                    candidates.append(AccessibleElementCandidate(
                        role: role, title: caption, containerTitles: containerTitles, center: screenshotCenter
                    ))
                }
            }

            guard depth < 12 else { continue }
            // A captioned group is the row or section its children sit in; the three nearest are
            // enough context to tell two identical buttons apart.
            let childContainerTitles = caption.isEmpty || actionableRoles.contains(role)
                ? containerTitles
                : Array(([caption] + containerTitles).prefix(3))
            for child in copyChildren(element) {
                queue.append((child, depth + 1, childContainerTitles))
            }
        }
        return candidates
    }

    private static func copyValue(_ element: AXUIElement, _ attribute: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value
    }

    private static func copyElement(_ element: AXUIElement, _ attribute: String) -> AXUIElement? {
        guard let value = copyValue(element, attribute), CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }

    private static func copyString(_ element: AXUIElement, _ attribute: String) -> String? {
        guard let string = copyValue(element, attribute) as? String, !string.isEmpty else { return nil }
        return string
    }

    private static func copyChildren(_ element: AXUIElement) -> [AXUIElement] {
        (copyValue(element, kAXChildrenAttribute) as? [AXUIElement]) ?? []
    }

    private static func copyFrame(_ element: AXUIElement) -> CGRect? {
        guard let positionValue = copyValue(element, kAXPositionAttribute), let sizeValue = copyValue(element, kAXSizeAttribute) else { return nil }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(positionValue as! AXValue, .cgPoint, &position),
              AXValueGetValue(sizeValue as! AXValue, .cgSize, &size) else { return nil }
        return CGRect(origin: position, size: size)
    }
}
