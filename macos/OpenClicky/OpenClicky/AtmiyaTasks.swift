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
}
