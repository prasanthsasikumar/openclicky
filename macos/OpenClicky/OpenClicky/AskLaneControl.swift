//
//  AskLaneControl.swift
//  OpenClicky
//
//  The person stays in charge of the ask lane. Esc stops it at any time: a press still listening,
//  a question on its way, an answer being said, or a guide waiting for a click. A dictation take
//  keeps esc to itself, as it always has. The rules are pure, so they are unit-tested without
//  events; CompanionManager.stopAskLane does the stopping. Also here: the pause before the screen
//  is sent, and the [UNSURE] marker for a reply that cannot find what to point at.
//

import Foundation

/// Where one press of esc goes.
enum EscapeTarget: Equatable {
    /// A dictation take is in progress: esc cancels the take, and nothing else.
    case dictationTake
    /// The ask lane has something under way: esc stops it.
    case askLane
    case nothing
    /// The fast voice lane: stop its reply and drop what its actions were about to say.
    case realtime
}

enum AskLaneEscape {
    /// `realtimeIsBusy`: the fast voice lane is answering, speaking, or still working on an action
    /// (a page settling before the look): esc stops that too, as it stops the ask lane.
    static func target(dictationTakeInProgress: Bool, askLaneCanBeStopped: Bool, realtimeIsBusy: Bool = false) -> EscapeTarget {
        if dictationTakeInProgress { return .dictationTake }
        if askLaneCanBeStopped { return .askLane }
        return realtimeIsBusy ? .realtime : .nothing
    }

    /// Whether the ask lane has anything to stop: a ⌃⌥ press not yet settled (listening or
    /// transcribing), a question on its way or being answered, or a guide (waiting for a click, or
    /// fetching its next step).
    static func canStop(pressIsSettled: Bool, isAskInFlight: Bool, guideStep: Int?) -> Bool {
        !pressIsSettled || isAskInFlight || guideStep != nil
    }
}

/// The pause between a question and its screenshot. The question is heard, then for a moment the
/// pointer and the island say the screen is about to be sent, and esc or a click on the island
/// calls it off before anything is captured. Settings → Privacy turns it off.
enum AskSendPause {
    static let defaultSeconds: TimeInterval = 1.2
    static let caption = "sending your screen · esc to cancel"

    /// How long to wait before capturing the screen, or nil to capture at once. A guide's own
    /// "what's next?" after each click never waits: the task was already let go ahead.
    static func seconds(isEnabled: Bool, isGuidedStepContinuation: Bool, pauseSeconds: TimeInterval = defaultSeconds) -> TimeInterval? {
        guard isEnabled, !isGuidedStepContinuation, pauseSeconds > 0 else { return nil }
        return pauseSeconds
    }

    /// Waits out the pause; false when the task was cancelled meanwhile (esc, the island, a new
    /// question), in which case nothing may be captured or sent.
    static func waitOut(seconds: TimeInterval) async -> Bool {
        try? await Task.sleep(for: .seconds(seconds))
        return !Task.isCancelled
    }
}

/// The [UNSURE] marker that opens a reply when the model cannot find the control on screen. Such
/// a reply carries no point (a tag sent anyway is ignored), says in words what to look for or
/// asks what they see, and is never spoken with the marker. Guessing with the pointer would send
/// the person to the wrong place with all the confidence of a right one.
enum UnsureMarker {
    static let marker = "[UNSURE]"
    static let islandStatus = "not sure · tell me what you see"

    /// Matches the marker however a model spaces or cases it ("[unsure]", "[ UNSURE ]").
    private static let markerPattern = #"\[\s*unsure\s*\]"#

    static func containsMarker(_ text: String) -> Bool {
        text.range(of: markerPattern, options: [.regularExpression, .caseInsensitive]) != nil
    }

    /// The text without any marker, trimmed.
    static func removingMarkers(from text: String) -> String {
        text.replacingOccurrences(of: markerPattern, with: "", options: [.regularExpression, .caseInsensitive])
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The length of a marker opening `text` (after any whitespace), or nil when it does not open with one.
    static func leadingMarkerLength(in text: String) -> Int? {
        guard let range = text.range(of: "^\\s*" + markerPattern, options: [.regularExpression, .caseInsensitive]) else { return nil }
        return text.distance(from: text.startIndex, to: range.upperBound)
    }

    /// True while a streamed answer's opening ("[", "[UNS") could still turn out to be the marker.
    static func couldStillBecomeMarker(_ opening: Substring) -> Bool {
        let soFar = opening.prefix(marker.count).uppercased()
        return soFar.count < marker.count && marker.hasPrefix(soFar)
    }
}
