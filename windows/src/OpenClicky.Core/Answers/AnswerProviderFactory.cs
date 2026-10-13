// Port of macos/OpenClicky/OpenClicky/AnswerProviderFactory.swift
//
// The provider for a source ProviderRouter picked: the key it needs from the credentials, the
// model (shell.json `answerModels`, else the source's default), and for the two plans the CLI's
// path on this PC.

using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

public static class AnswerProviderFactory
{
    /// <param name="locateClaudeCode">Finds `claude` (PlanCLIs.ClaudeCodePathAsync by default).</param>
    /// <param name="locateCodex">Finds `codex` (PlanCLIs.CodexPathAsync by default).</param>
    public static async Task<IAnswerProvider> MakeAsync(AnswerSource source, ProviderCredentials credentials,
                                                        IReadOnlyDictionary<string, string> modelOverrides,
                                                        Func<Task<string?>>? locateClaudeCode = null,
                                                        Func<Task<string?>>? locateCodex = null,
                                                        CancellationToken cancellationToken = default)
    {
        var model = modelOverrides.TryGetValue(source.Id(), out var overriddenModel) && !string.IsNullOrEmpty(overriddenModel)
            ? overriddenModel
            : source.DefaultModel();
        switch (source)
        {
            case AnswerSource.OpenAI or AnswerSource.OpenRouter or AnswerSource.Groq or AnswerSource.Mistral or AnswerSource.Xai:
                if (credentials.Key(source) is not { } key || model is null) throw Missing(source, "add its key in settings, account.");
                return new OpenAIChatAnswerProvider(source, key, model);
            case AnswerSource.Gemini:
                if (credentials.GeminiKey is not { } geminiKey || model is null) throw Missing(source, "add its key in settings, account.");
                return new GeminiAnswerProvider(geminiKey, model);
            case AnswerSource.ClaudeCodePlan:
                var claudePath = await (locateClaudeCode ?? (() => PlanCLIs.ClaudeCodePathAsync(cancellationToken: cancellationToken)))().ConfigureAwait(false);
                if (claudePath is null) throw Missing(source, "the claude code app (claude) isn't installed on this pc.");
                return new ClaudeCodeAnswerProvider(claudePath, model);
            case AnswerSource.CodexPlan:
                var codexPath = await (locateCodex ?? PlanCLIs.CodexPathAsync)().ConfigureAwait(false);
                if (codexPath is null) throw Missing(source, "codex isn't installed on this pc.");
                return new CodexAnswerProvider(codexPath, model);
            default:
                // ClaudeAPI answers these itself (Anthropic, the account); the router never hands them over.
                throw Missing(source, "answers through openclicky's own path.");
        }
    }

    private static AnswerProviderException Missing(AnswerSource source, string sentence) => new(source, 0, sentence);
}
