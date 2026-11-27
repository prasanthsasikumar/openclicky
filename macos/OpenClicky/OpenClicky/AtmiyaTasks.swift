//
//  AtmiyaTasks.swift
//  OpenClicky
//
//  The prompts a person can type or pick from the menu bar. Each one is a request the
//  existing fast tools already know how to do.
//

import Foundation

struct AtmiyaTask: Equatable, Identifiable {
    let title: String
    /// What to send. A prompt that still needs a name stays in the composer for them to finish.
    let prompt: String
    /// True when the prompt is complete and can run as soon as they choose it.
    let runsImmediately: Bool

    var id: String { title }
}

enum AtmiyaTasks {
    static let all: [AtmiyaTask] = [
        AtmiyaTask(title: "Answer a question", prompt: "", runsImmediately: false),
        AtmiyaTask(title: "Search the web", prompt: "Search the web for today's news", runsImmediately: true),
        AtmiyaTask(title: "Search YouTube and play", prompt: "Search YouTube for lo-fi and play the first video", runsImmediately: true),
        AtmiyaTask(title: "Open a video", prompt: "Open this YouTube video: ", runsImmediately: false),
        AtmiyaTask(title: "Follow a person", prompt: "Open YouTube and follow ", runsImmediately: false),
        AtmiyaTask(title: "Open Chrome", prompt: "Open Chrome", runsImmediately: true),
        AtmiyaTask(title: "Open Terminal", prompt: "Open Terminal", runsImmediately: true),
        AtmiyaTask(title: "Find a file", prompt: "Find ", runsImmediately: false),
        AtmiyaTask(title: "Open Downloads", prompt: "Open Downloads", runsImmediately: true),
        AtmiyaTask(title: "Copy a file", prompt: "Copy ", runsImmediately: false),
        AtmiyaTask(title: "Check email", prompt: "Check my email", runsImmediately: true),
        AtmiyaTask(title: "Draft an email", prompt: "Draft an email to ", runsImmediately: false),
        AtmiyaTask(title: "Set the volume", prompt: "Set the volume to 30", runsImmediately: true),
        AtmiyaTask(title: "Scroll the page", prompt: "Scroll down", runsImmediately: true),
    ]

    /// True when they asked for the list itself, not for one of the tasks.
    static func asksForTheList(_ text: String) -> Bool {
        let words = text.lowercased()
        let phrases = [
            "what can you do", "what can i do", "what can it do",
            "give me the prompts", "show me the prompts", "show the prompts",
            "keyboard tasks", "what tasks", "which tasks", "list the tasks",
        ]
        return phrases.contains { words.contains($0) }
    }

    static var spokenList: String {
        all.map { task in
            task.prompt.isEmpty ? "\(task.title). Hold Control and Option and ask." : "\(task.title). Say: \(task.prompt)"
        }.joined(separator: " ")
    }

    /// The one phrase that pastes saved prompts and runs the safe tasks.
    static let masterPrompt = "Paste my prompts and do my tasks."

    static func asksToPastePrompts(_ text: String) -> Bool {
        let words = text.lowercased()
        return words.contains("paste my prompts") || words.contains("paste all the prompts") || words.contains("paste the prompts")
    }

    static func asksToRunTasks(_ text: String) -> Bool {
        let words = text.lowercased()
        return words.contains("do my tasks") || words.contains("do all my tasks") || words.contains("do all the tasks")
    }

    static func asksToUseSavedPrompts(_ text: String) -> Bool {
        asksToPastePrompts(text) || asksToRunTasks(text)
    }
}

/// The only spoken wake phrase. "Hey Atmiya", "Atmiya", and "Atmiya AI" do not wake it.
enum RisWakePhrase {
    /// The words after "Hey Ris", or nil when that phrase was not said.
    /// An empty string means they only said the wake phrase.
    static func command(in text: String) -> String? {
        let folded = text.folding(options: .diacriticInsensitive, locale: Locale(identifier: "en_US_POSIX")).lowercased()
        let words = folded.split { !$0.isLetter }.map(String.init)
        guard let heyIndex = words.firstIndex(of: "hey"), heyIndex + 1 < words.count else { return nil }
        let name = words[heyIndex + 1]
        guard name == "ris" || name == "riz" || name == "riss" else { return nil }
        let fillers: Set<String> = ["um", "uh", "okay", "ok", "so", "well"]
        guard words[..<heyIndex].allSatisfy({ fillers.contains($0) }) else { return nil }
        return words[(heyIndex + 2)...].joined(separator: " ")
    }
}

enum AtmiyaPromptLog {
    static let maximumCount = 40

    /// Adds a prompt the person gave. The master command and an immediate repeat are not stored.
    static func remember(_ prompt: String, in prompts: [String]) -> [String] {
        let trimmed = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 500 else { return prompts }
        guard !AtmiyaTasks.asksForTheList(trimmed), !AtmiyaTasks.asksToUseSavedPrompts(trimmed) else { return prompts }
        guard prompts.last != trimmed else { return prompts }
        var updated = prompts + [trimmed]
        if updated.count > maximumCount { updated.removeFirst(updated.count - maximumCount) }
        return updated
    }

    /// Sending, paying, and deleting stay out of an automatic run. Pasting still shows them.
    static func isSafeToRun(_ prompt: String) -> Bool {
        let words = prompt.lowercased()
        let blocked = ["delete", "erase", "trash", "buy", "pay", "purchase", "password", "uninstall", "send the email", "send it"]
        return !blocked.contains { words.contains($0) }
    }

    static func pastedText(for prompts: [String]) -> String {
        prompts.enumerated().map { "\($0.offset + 1). \($0.element)" }.joined(separator: "\n")
    }

    static func taskInstruction(for prompts: [String]) -> String {
        let safe = prompts.filter(isSafeToRun)
        let lines = pastedText(for: safe)
        return "Do these tasks in order with your tools. Do not send, buy, pay, or delete. When one finishes, do the next. Stop when the list is done.\n\(lines)"
    }
}
