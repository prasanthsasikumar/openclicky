// Ports the AnswerSource enum of AnswerProvider.swift: the services that can answer a question,
// as the router and Settings name them. It lives here because ProviderRouter decides on it; the
// providers themselves (OpenClicky.Core.Answers) use this type.

using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Providers;

public enum AnswerSource
{
    Anthropic,
    OpenAI,
    OpenRouter,
    Gemini,
    Groq,
    Mistral,
    Xai,
    ClaudeCodePlan,
    CodexPlan,
    OpenClickyAccount,
}

public static class AnswerSources
{
    public static IReadOnlyList<AnswerSource> AllCases { get; } = Enum.GetValues<AnswerSource>();

    /// <summary>shell.json's `answerSource` value: "anthropic", "openAI", … "openClickyAccount".</summary>
    public static string Id(this AnswerSource source) => source.RawValue();

    /// <summary>Swift's `AnswerSource(rawValue:)`.</summary>
    public static AnswerSource? FromRawValue(string? raw) => RawValues.FromRawValue<AnswerSource>(raw);

    /// <summary>The few words the island shows next to "speaking".</summary>
    public static string ShortName(this AnswerSource source) => source switch
    {
        AnswerSource.Anthropic => "Claude",
        AnswerSource.OpenAI => "OpenAI",
        AnswerSource.OpenRouter => "OpenRouter",
        AnswerSource.Gemini => "Gemini",
        AnswerSource.Groq => "Groq",
        AnswerSource.Mistral => "Mistral",
        AnswerSource.Xai => "Grok",
        AnswerSource.ClaudeCodePlan => "Claude Code",
        AnswerSource.CodexPlan => "Codex",
        AnswerSource.OpenClickyAccount => "free credits",
        _ => source.ToString(),
    };

    public static string DisplayName(this AnswerSource source) => source switch
    {
        AnswerSource.Anthropic => "Claude (Anthropic key)",
        AnswerSource.OpenAI => "OpenAI",
        AnswerSource.OpenRouter => "OpenRouter",
        AnswerSource.Gemini => "Google Gemini",
        AnswerSource.Groq => "Groq",
        AnswerSource.Mistral => "Mistral",
        AnswerSource.Xai => "xAI (Grok)",
        AnswerSource.ClaudeCodePlan => "Claude Code (your Claude plan)",
        AnswerSource.CodexPlan => "Codex (your ChatGPT plan)",
        AnswerSource.OpenClickyAccount => "OpenClicky free credits",
        _ => source.ToString(),
    };

    /// <summary>The model asked when shell.json's `answerModels` names none. Each one sees images.</summary>
    public static string? DefaultModel(this AnswerSource source) => source switch
    {
        AnswerSource.Anthropic => null, // ClaudeAPI's own model (Settings → Clicky → Model)
        AnswerSource.OpenAI => "gpt-4.1",
        AnswerSource.OpenRouter => "anthropic/claude-sonnet-4.6",
        AnswerSource.Gemini => "gemini-3.8-flash",
        AnswerSource.Groq => "meta-llama/llama-4-scout-17b-16e-instruct",
        AnswerSource.Mistral => "mistral-medium-latest",
        AnswerSource.Xai => "grok-4",
        _ => null, // the CLI's or backend's own
    };

    /// <summary>The OpenAI-compatible chat completions endpoint, for the sources that speak it.</summary>
    public static Uri? ChatCompletionsUrl(this AnswerSource source) => source switch
    {
        AnswerSource.OpenAI => new Uri("https://api.openai.com/v1/chat/completions"),
        AnswerSource.OpenRouter => new Uri("https://openrouter.ai/api/v1/chat/completions"),
        AnswerSource.Groq => new Uri("https://api.groq.com/openai/v1/chat/completions"),
        AnswerSource.Mistral => new Uri("https://api.mistral.ai/v1/chat/completions"),
        AnswerSource.Xai => new Uri("https://api.x.ai/v1/chat/completions"),
        _ => null,
    };
}
