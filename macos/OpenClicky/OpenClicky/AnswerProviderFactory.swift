//
//  AnswerProviderFactory.swift
//  OpenClicky
//
//  The provider for a source ProviderRouter picked: the key it needs from the credentials, the
//  model (shell.json `answerModels`, else the source's default), and for the two plans the CLI's
//  path on this Mac.
//

import Foundation

nonisolated enum AnswerProviderFactory {
    static func make(for source: AnswerSource, credentials: ProviderCredentials,
                     modelOverrides: [String: String]) async throws -> AnswerProvider {
        let model = modelOverrides[source.rawValue].flatMap { $0.isEmpty ? nil : $0 } ?? source.defaultModel
        switch source {
        case .openAI, .openRouter, .groq, .mistral, .xai, .openCode:
            guard let key = credentials.key(for: source), let model else { throw missing(source, "add its key in settings, account.") }
            return OpenAIChatAnswerProvider(source: source, apiKey: key, model: model)
        case .gemini:
            guard let key = credentials.geminiKey, let model else { throw missing(source, "add its key in settings, account.") }
            return GeminiAnswerProvider(apiKey: key, model: model)
        case .claudeCodePlan:
            guard let path = await PlanCLIs.claudeCodePath() else {
                throw missing(source, "the claude code app (claude) isn't installed on this mac.")
            }
            return ClaudeCodeAnswerProvider(executablePath: path, model: model)
        case .codexPlan:
            guard let path = await PlanCLIs.codexPath() else {
                throw missing(source, "codex isn't installed on this mac.")
            }
            return CodexAnswerProvider(executablePath: path, model: model)
        case .anthropic, .openClickyAccount:
            // ClaudeAPI answers these itself; the router never hands them over.
            throw missing(source, "answers through openclicky's own path.")
        }
    }

    private static func missing(_ source: AnswerSource, _ sentence: String) -> AnswerProviderError {
        AnswerProviderError(source: source, status: 0, message: sentence)
    }
}
