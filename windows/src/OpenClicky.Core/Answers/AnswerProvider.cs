// Port of macos/OpenClicky/OpenClicky/AnswerProvider.swift
//
// Who can answer a question besides Anthropic and the OpenClicky backend. The ask lane, screen
// grounding and the tour's demo all go through `ClaudeAPI.AnalyzeImageStreamingAsync`; when
// ProviderRouter picks one of these sources, ClaudeAPI hands the same request to an
// `IAnswerProvider`:
//   - OpenAI-compatible chat (OpenAIChatAnswerProvider): OpenAI, OpenRouter, Groq, Mistral, xAI
//   - Google Gemini (GeminiAnswerProvider)
//   - the Claude Code CLI on the person's Claude plan (ClaudeCodeAnswerProvider)
//   - Codex on the person's ChatGPT plan (CodexAnswerProvider)
// Keys go straight to their vendor; nothing here talks to the OpenClicky backend.
//
// The AnswerSource enum itself lives in OpenClicky.Core.Providers (the router decides on it).

using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

/// <summary>
/// One question: the labelled screenshots, the prompts, the earlier turns, and the quick-action
/// tools (Anthropic's shape: name, description, input_schema as JSON).
/// </summary>
public sealed class AnswerRequest
{
    /// <param name="MediaType">"image/jpeg" or "image/png".</param>
    public sealed record Image(byte[] Data, string Label, string MediaType)
    {
        public bool Equals(Image? other) =>
            other is not null && Label == other.Label && MediaType == other.MediaType && Data.AsSpan().SequenceEqual(other.Data);

        public override int GetHashCode() => HashCode.Combine(Label, MediaType, Data.Length);
    }

    public sealed record Turn(string User, string Assistant);

    /// <param name="InputSchemaJson">The JSON Schema of the arguments, serialized (Anthropic's `input_schema`).</param>
    public sealed record Tool(string Name, string Description, string InputSchemaJson);

    public List<Image> Images { get; set; }
    public string SystemPrompt { get; set; }
    public List<Turn> History { get; set; }
    public string UserPrompt { get; set; }
    public List<Tool> Tools { get; set; }
    public int MaxTokens { get; set; }

    public AnswerRequest(IEnumerable<Image> images, string systemPrompt, IEnumerable<Turn> history, string userPrompt,
                         IEnumerable<Tool> tools, int maxTokens = 1024)
    {
        Images = images.ToList();
        SystemPrompt = systemPrompt;
        History = history.ToList();
        UserPrompt = userPrompt;
        Tools = tools.ToList();
        MaxTokens = maxTokens;
    }

    /// <summary>A copy that can be changed without touching this one (Swift's value semantics).</summary>
    public AnswerRequest Copy() => new(Images, SystemPrompt, History, UserPrompt, Tools, MaxTokens);
}

/// <summary>A tool call read from an answer: Anthropic's tool_use, an OpenAI function call, a Gemini functionCall, or a plan's [ACTION: …] line.</summary>
public sealed record ClaudeToolCall(string Id, string Name, IReadOnlyDictionary<string, string> Arguments)
{
    public bool Equals(ClaudeToolCall? other) =>
        other is not null && Id == other.Id && Name == other.Name
        && Arguments.Count == other.Arguments.Count
        && Arguments.All(pair => other.Arguments.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Arguments.Count);

    public override string ToString() =>
        $"ClaudeToolCall({Id}, {Name}, {{{string.Join(", ", Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {pair.Value}"))}}})";
}

public sealed record AnswerResult(string Text, IReadOnlyList<ClaudeToolCall> ToolCalls)
{
    public bool Equals(AnswerResult? other) =>
        other is not null && Text == other.Text && ToolCalls.SequenceEqual(other.ToolCalls);

    public override int GetHashCode() => HashCode.Combine(Text, ToolCalls.Count);

    public override string ToString() => $"AnswerResult({Text}, [{string.Join(", ", ToolCalls)}])";
}

/// <summary>
/// A provider that answers. `onTextChunk` gets each piece of text as it arrives (a provider that
/// cannot stream calls it once with the whole answer). It may be called from a background thread.
/// </summary>
public interface IAnswerProvider
{
    Task<AnswerResult> AnswerAsync(AnswerRequest request, Action<string> onTextChunk, CancellationToken cancellationToken = default);
}

/// <summary>
/// A refusal or failure from a provider, with the HTTP status (0 for a CLI) and the vendor's own
/// message, so the ask lane can say something better than "something went wrong".
/// Swift `AnswerProviderError`; `VendorMessage` is its `message`, and `Message` its `errorDescription`.
/// </summary>
public sealed class AnswerProviderException(AnswerSource source, int status, string vendorMessage)
    : Exception($"{source.DisplayName()}: {vendorMessage}")
{
    /// <summary>Who refused (Swift `source`; `Exception.Source` is .NET's own).</summary>
    public AnswerSource AnswerSource { get; } = source;
    public int Status { get; } = status;
    public string VendorMessage { get; } = vendorMessage;

    public override bool Equals(object? obj) =>
        obj is AnswerProviderException other && other.AnswerSource == AnswerSource && other.Status == Status && other.VendorMessage == VendorMessage;

    public override int GetHashCode() => HashCode.Combine(AnswerSource, Status, VendorMessage);
}
