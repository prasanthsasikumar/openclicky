//
//  PlateWriter.swift
//  OpenClicky
//
//  Writes the day's cards: the digest goes to whoever answers the person's questions (their own
//  key, or a Claude Code or Codex plan) and the reply is read by `PlatePrompt.parse`.
//

import Foundation

enum PlateWriter {
    static let maximumOutputTokens = 4096
    static let timeoutSeconds: TimeInterval = 150

    enum Failure: Error, Equatable {
        /// Nothing on this Mac to write from.
        case nothingToGoOn
        /// No answer source that can also run the tasks.
        case noAgent
        case noReply(String)
        /// The model answered and offered nothing (or nothing readable).
        case nothingOffered
    }

    @MainActor static func write(context: PlateContext, previous: PlateBatch?, now: Date = Date()) async -> Result<[PlateSuggestion], Failure> {
        guard !context.isEmpty else { return .failure(.nothingToGoOn) }
        guard OpenClickyConfiguration.isConfigured, AccountCapabilities.current().usesAgent else { return .failure(.noAgent) }
        let startedAt = Date()
        let answers = ClaudeAPI(currentProxyURL: { "\(OpenClickyConfiguration.backendBaseURL)/chat" })
        do {
            let reply = try await answers.analyzeImageStreaming(
                images: [], systemPrompt: PlatePrompt.system,
                userPrompt: PlatePrompt.user(digest: context.digest(now: now), previous: previous),
                options: AnswerRequestOptions(maxTokens: maximumOutputTokens, thinkingOff: true, timeoutSeconds: timeoutSeconds),
                onTextChunk: { _ in })
            let suggestions = PlatePrompt.parse(reply.text)
            AppLog.append("plate: \(suggestions.count) cards written from \(context.sessions.count) sessions, \(context.events.count) events, \(context.notes.count) note chars in \(String(format: "%.1f", Date().timeIntervalSince(startedAt))) s (\(reply.text.count) chars)")
            return suggestions.isEmpty ? .failure(.nothingOffered) : .success(suggestions)
        } catch {
            AppLog.append("plate: no cards, the answer source failed: \(error.localizedDescription)")
            return .failure(.noReply(error.localizedDescription))
        }
    }
}
