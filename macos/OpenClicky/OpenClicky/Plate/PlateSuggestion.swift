//
//  PlateSuggestion.swift
//  OpenClicky
//
//  The day's offers: once a day Clicky offers up to six jobs it could take on by itself, each
//  as a card with a plan. This file is the pure part: the suggestion, what the
//  model is asked, how its reply is read, when a day's cards are due, and what a card's
//  "do it" hands to the agent. Nothing here touches the network, the disk or the screen.
//

import Foundation

struct PlateSuggestion: Codable, Equatable, Identifiable {
    /// What the card's doodle shows.
    enum Kind: String, Codable, CaseIterable {
        case draft, research, watch, list, numbers, message, schedule, code
    }

    /// Where a card stands. Only waiting cards are dealt.
    enum Status: String, Codable {
        case waiting, taken, skipped
    }

    var id: String
    /// One sentence in Clicky's voice, starting "I'll".
    var title: String
    /// Why this, and what the plan is: the lines a hovered card opens to show.
    var detail: String
    /// What the agent is asked to do.
    var prompt: String
    var kind: Kind
    var status: Status = .waiting
    /// What the person asked to be different, typed on the card before saying yes.
    var adjustment: String?
    /// The plan in up to four short steps, shown on the slip under a hovered card.
    var steps: [String]?
    /// Roughly how long the job takes.
    var minutes: Int?
    /// What comes back: "a ranked table", "a draft email".
    var result: String?
}

/// How the day's cards went down, asked once after the deck closes.
enum PlateRating: String, Codable, CaseIterable {
    case liked, okay, terrible

    var buttonTitle: String {
        switch self {
        case .liked: return "Yes"
        case .okay: return "Some were"
        case .terrible: return "Not really"
        }
    }
}

/// One day's cards and what became of them. Yesterday's is read back to the model.
struct PlateBatch: Codable, Equatable {
    /// "2026-11-22" in the person's calendar.
    var dayKey: String
    var suggestions: [PlateSuggestion]
    var rating: PlateRating?
    /// What the person said would have been better, typed after rating.
    var comment: String?
    /// The island has announced these cards once; it does not announce them again.
    var wasAnnounced = false

    var waiting: [PlateSuggestion] { suggestions.filter { $0.status == .waiting } }
    var hasDecision: Bool { suggestions.contains { $0.status != .waiting } }
}

// MARK: - When

enum PlateSchedule {
    /// Cards are for the working day: nothing is offered before this hour.
    static let earliestHour = 6
    static let maximumCards = 6

    static func dayKey(for date: Date, calendar: Calendar = .current) -> String {
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", parts.year ?? 0, parts.month ?? 0, parts.day ?? 0)
    }

    /// True when today's cards have not been written yet and the day has begun.
    static func isDue(now: Date, lastBatchDayKey: String?, calendar: Calendar = .current) -> Bool {
        guard calendar.component(.hour, from: now) >= earliestHour else { return false }
        return lastBatchDayKey != dayKey(for: now, calendar: calendar)
    }

    /// "Sunday evening", over the announcement.
    static func dayPart(for date: Date, calendar: Calendar = .current, locale: Locale = .current) -> String {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.locale = locale
        formatter.dateFormat = "EEEE"
        let hour = calendar.component(.hour, from: date)
        let part = hour < 12 ? "morning" : hour < 18 ? "afternoon" : "evening"
        return "\(formatter.string(from: date)) \(part)"
    }

    static func headline(count: Int) -> String {
        count == 1 ? "1 job I could take on today" : "\(count) jobs I could take on today"
    }

    /// "about 20 min · you get a ranked table", for the slip under a card.
    static func footnote(minutes: Int?, result: String?) -> String? {
        var parts: [String] = []
        if let minutes, minutes > 0 { parts.append("about \(minutes) min") }
        if let result = result?.trimmingCharacters(in: .whitespacesAndNewlines), !result.isEmpty { parts.append("you get \(result)") }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

/// The menu bar item's words: today's waiting cards by number, or the question that writes them.
enum PlateMenu {
    static func title(waiting: Int) -> String {
        switch waiting {
        case 0: return "What Could You Take On Today?"
        case 1: return "Today's Offer (1)…"
        default: return "Today's Offers (\(waiting))…"
        }
    }
}

// MARK: - What the model is asked

enum PlatePrompt {
    static let system = """
    You are Clicky, an assistant living on someone's Mac. Once a day you offer a few jobs you could take on for them and do on your own with an agent that can browse the web, read and write files in a work folder, and run commands.

    You are given what this Mac knows about their recent work. Offer only tasks that follow from it: name the real project, event or note. Each task must be something you can finish alone in under an hour and hand back as a draft, a report or a list. Never offer to send, post, publish, buy, delete or change anything of theirs; you prepare, they decide. Prefer tasks with a number in them ("read 20 launch posts", "compare 5 tools"). No two tasks about the same thing. If the context is too thin for a task that is truly theirs, offer fewer, or none.

    Reply with only JSON, no code fences:
    {"suggestions":[{"title":"I'll …","detail":"…","steps":["…","…","…"],"minutes":20,"result":"…","prompt":"…","kind":"draft"}]}

    title: one sentence starting with "I'll", at most 95 characters, plain words.
    detail: one sentence saying what you noticed that makes this worth doing. At most 120 characters.
    steps: the plan in three or four steps of at most 7 words each, each starting with a verb.
    minutes: your honest estimate of how long it takes you, a whole number from 5 to 60.
    result: what they get back, as a noun phrase of at most 6 words starting with "a" or "an" ("a ranked table of 6 options").
    prompt: the full brief for the agent: the goal, the context it needs (it sees none of this conversation), the steps, and what to hand back. End with the limits above.
    kind: what the result mostly is: "draft" (something written), "research" (reading and comparing), "watch" (checking on something), "list", "numbers" (a table or figures), "message" (an email or post to send themselves), "schedule" (dates and times), "code".
    """

    /// The user turn: the digest, then what yesterday's cards taught.
    static func user(digest: String, previous: PlateBatch?, maximum: Int = PlateSchedule.maximumCards) -> String {
        var parts = ["Offer up to \(maximum) tasks for today.", "What this Mac knows:\n\(digest)"]
        if let previous, !previous.suggestions.isEmpty {
            var lines: [String] = []
            for suggestion in previous.suggestions {
                let verdict: String
                switch suggestion.status {
                case .taken: verdict = "they said go ahead"
                case .skipped: verdict = "they skipped it"
                case .waiting: verdict = "left unanswered"
                }
                lines.append("- \(suggestion.title) (\(verdict))")
            }
            var learned = "Last time you offered:\n" + lines.joined(separator: "\n")
            if let rating = previous.rating {
                switch rating {
                case .liked: learned += "\nThey liked that set. More in that spirit, nothing repeated."
                case .okay: learned += "\nThey found that set only okay. Be more specific to their work, nothing repeated."
                case .terrible: learned += "\nThey found that set not useful. Change direction: smaller, more concrete, closer to what they did yesterday."
                }
            } else {
                learned += "\nDo not repeat these."
            }
            if let comment = previous.comment?.trimmingCharacters(in: .whitespacesAndNewlines), !comment.isEmpty {
                learned += "\nAsked what would have been better, they wrote: \(comment)"
            }
            parts.append(learned)
        }
        return parts.joined(separator: "\n\n")
    }

    /// Reads the model's reply. Fences and words around the JSON are tolerated; a suggestion
    /// without a title or a brief is dropped, as is a second one with the same title.
    static func parse(_ reply: String, makeID: () -> String = { UUID().uuidString }) -> [PlateSuggestion] {
        guard let start = reply.firstIndex(of: "{"), let end = reply.lastIndex(of: "}"), start < end,
              let data = String(reply[start...end]).data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let items = root["suggestions"] as? [[String: Any]] else { return [] }
        var seen = Set<String>()
        var suggestions: [PlateSuggestion] = []
        for item in items {
            let title = oneLine(item["title"] as? String)
            let prompt = (item["prompt"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !title.isEmpty, !prompt.isEmpty, seen.insert(title.lowercased()).inserted else { continue }
            let kind = (item["kind"] as? String).flatMap { PlateSuggestion.Kind(rawValue: $0.lowercased()) } ?? .draft
            var suggestion = PlateSuggestion(id: makeID(), title: title, detail: oneLine(item["detail"] as? String), prompt: prompt, kind: kind)
            let steps = (item["steps"] as? [Any] ?? []).compactMap { $0 as? String }.map { oneLine($0) }.filter { !$0.isEmpty }
            suggestion.steps = steps.isEmpty ? nil : Array(steps.prefix(4))
            if let minutes = (item["minutes"] as? NSNumber)?.intValue, minutes > 0 { suggestion.minutes = min(minutes, 180) }
            let result = oneLine(item["result"] as? String)
            suggestion.result = result.isEmpty ? nil : result
            suggestions.append(suggestion)
            if suggestions.count == PlateSchedule.maximumCards { break }
        }
        return suggestions
    }

    private static func oneLine(_ text: String?) -> String {
        (text ?? "").split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.joined(separator: " ")
    }

    /// What saying yes hands the agent: the brief, what the person asked to be different, and the
    /// limits again, because the brief was written by a model.
    static func agentBrief(for suggestion: PlateSuggestion) -> String {
        var brief = suggestion.prompt
        if let adjustment = suggestion.adjustment?.trimmingCharacters(in: .whitespacesAndNewlines), !adjustment.isEmpty {
            brief += "\n\nThe person asked for this change, and it overrides the brief where they differ: \(adjustment)"
        }
        brief += "\n\nLimits: prepare, do not act for them. Do not send, post, publish, buy, delete or change anything of theirs. Save what you make as files and end with a two sentence summary of what is ready."
        return brief
    }
}
