//
//  NextQuestions.swift
//  OpenClicky
//
//  Up to three short questions someone might ask next, offered as pills after an answer. The
//  answer itself is left alone: once an exchange is remembered, one small text-only request
//  asks the answering model for them. Only the person's own key, model or plan is asked, never
//  the free allowance, where the extra request would use up their questions. The Windows app
//  offers the same; the rules here follow its `Answers/NextQuestions`.
//

import Foundation

enum NextQuestions {
    static let maximum = 3
    static let maximumLength = 70
    /// Answers shorter than this (a "done.", a yes) get none.
    static let minimumAnswerLength = 80

    static let systemPrompt = """
    Someone asked a question and got the answer below. Write up to three short questions they might want to ask next, each one a natural follow-up to that answer, each at most ten words, in the language of the answer. One question per line, no numbers, no bullets, no quotes, nothing else. If no follow-up is worth asking, write only NONE.
    """

    static func userPrompt(question: String, answer: String) -> String {
        "They asked: \(question.prefix(400))\n\nThe answer: \(answer.prefix(1500))"
    }

    /// The reply as up to three questions: bullets, numbers and quotes taken off, long lines and
    /// repeats left out, nothing for NONE.
    static func parse(_ reply: String) -> [String] {
        var questions: [String] = []
        var seen = Set<String>()
        for rawLine in reply.split(whereSeparator: \.isNewline) {
            var line = rawLine.trimmingCharacters(in: .whitespaces)
            // "1. ", "2) ", "- ", "• ", "* "
            if let match = line.range(of: #"^(\d+[.)]|[-•*–])\s+"#, options: .regularExpression) { line.removeSubrange(match) }
            line = line.trimmingCharacters(in: CharacterSet(charactersIn: "\"'“”‘’«» ").union(.whitespaces))
            guard !line.isEmpty else { continue }
            if line.uppercased() == "NONE" || line.uppercased() == "NONE." { return [] }
            guard line.count <= maximumLength, seen.insert(line.lowercased()).inserted else { continue }
            questions.append(line)
            if questions.count == maximum { break }
        }
        return questions
    }

    /// Not for small talk or one-word answers.
    static func isWorthOffering(question: String, answer: String) -> Bool {
        let asked = question.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard asked.count >= 8, answer.trimmingCharacters(in: .whitespacesAndNewlines).count >= minimumAnswerLength else { return false }
        let smallTalk = ["thanks", "thank you", "ok thanks", "hello", "hi there", "good morning", "good night", "bye", "goodbye"]
        return !smallTalk.contains { asked == $0 || asked == $0 + "." || asked == $0 + "!" }
    }

    /// Own key, model or plan only: the free account's allowance is never spent on these.
    static func mayAsk(route: ProviderRoute) -> Bool {
        switch route {
        case .anthropicDirect, .answerProvider: return route.answeringSource != .openClickyAccount
        case .backend, .openAIDirect, .unavailable: return false
        }
    }

    /// Nil when none may be asked for, none are worth it, or the model offered none.
    @MainActor static func make(question: String, answer: String) async -> [String]? {
        guard isWorthOffering(question: question, answer: answer),
              mayAsk(route: ProviderRouter.route(for: .claude, credentials: ProviderRouter.currentCredentials())) else { return nil }
        let answers = ClaudeAPI(currentProxyURL: { "\(OpenClickyConfiguration.backendBaseURL)/chat" })
        guard let reply = try? await answers.analyzeImage(images: [], systemPrompt: systemPrompt, userPrompt: userPrompt(question: question, answer: answer)) else { return nil }
        let questions = parse(reply.text)
        AppLog.append("next questions: \(questions.count) offered in \(String(format: "%.1f", reply.duration)) s")
        return questions.isEmpty ? nil : questions
    }
}
