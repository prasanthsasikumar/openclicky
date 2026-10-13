// Ports ProviderRoute.swift. Own keys are the primary route (issue #4). A capability the person
// has a key for goes straight to that vendor with their key; the OpenClicky backend is only for a
// capability they have no key for, on their account's token, and then without any own key: a key
// never leaves this PC except to its own vendor. Every caller asks `ProviderRouter` the same
// question, so there is one place that decides, and one log line per change of route.
//
// The direct requests are the ones the backend's bring-your-own-key path sends upstream
// (backend/src/proxy.ts, keys.ts, skillsCreate.ts), so a request means the same thing either way.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Providers;

/// <summary>What a call is for, by the key that can pay for it.</summary>
public enum ProviderCapability
{
    /// <summary>Claude: the ask lane (/chat) and screen grounding. Anthropic key.</summary>
    Claude,
    /// <summary>OpenAI chat completions: the take clean-up on an own key. OpenAI key.</summary>
    OpenAIChat,
    /// <summary>OpenAI speech on an own key, else the account's /tts. Kept because the route table is tested per key.</summary>
    Speech,
    /// <summary>Upload speech-to-text (/agent/transcribe). OpenAI key.</summary>
    Transcription,
    /// <summary>Realtime voice: the client secret (/agent/realtime/session). OpenAI key.</summary>
    Realtime,
    /// <summary>AssemblyAI streaming (/transcribe-token): minted on OpenClicky's AssemblyAI key, account only.</summary>
    AssemblyAIStreaming,
}

public enum ProviderRouteKind
{
    OpenAIDirect,
    AnthropicDirect,
    /// <summary>Questions answered by another source (an OpenAI-compatible key, Gemini, or a plan). Only for Claude.</summary>
    AnswerProvider,
    /// <summary>The OpenClicky backend, on the account's token (no own keys sent).</summary>
    Backend,
    /// <summary>No key for this and no account: the sentence says what is missing.</summary>
    Unavailable,
}

/// <summary>Where one call goes. Compare with ==; build with the static members.</summary>
public sealed record ProviderRoute
{
    public ProviderRouteKind Kind { get; }
    /// <summary>The source for an AnswerProvider route.</summary>
    public AnswerSource? Source { get; }
    /// <summary>The sentence for an Unavailable route.</summary>
    public string? Sentence { get; }

    private ProviderRoute(ProviderRouteKind kind, AnswerSource? source = null, string? sentence = null)
    {
        Kind = kind;
        Source = source;
        Sentence = sentence;
    }

    public static ProviderRoute OpenAIDirect { get; } = new(ProviderRouteKind.OpenAIDirect);
    public static ProviderRoute AnthropicDirect { get; } = new(ProviderRouteKind.AnthropicDirect);
    public static ProviderRoute Backend { get; } = new(ProviderRouteKind.Backend);
    public static ProviderRoute AnswerProvider(AnswerSource source) => new(ProviderRouteKind.AnswerProvider, source: source);
    public static ProviderRoute Unavailable(string sentence) => new(ProviderRouteKind.Unavailable, sentence: sentence);

    public string LogName => Kind switch
    {
        ProviderRouteKind.OpenAIDirect => "openai, own key",
        ProviderRouteKind.AnthropicDirect => "anthropic, own key",
        ProviderRouteKind.AnswerProvider => Source!.Value.DisplayName(),
        ProviderRouteKind.Backend => "openclicky backend, account",
        _ => "unavailable",
    };

    /// <summary>The source a Claude route answers with, for saying who answered; null when nothing can.</summary>
    public AnswerSource? AnsweringSource => Kind switch
    {
        ProviderRouteKind.AnthropicDirect => Providers.AnswerSource.Anthropic,
        ProviderRouteKind.AnswerProvider => Source,
        ProviderRouteKind.Backend => Providers.AnswerSource.OpenClickyAccount,
        _ => null,
    };

    public string? UnavailableSentence => Kind == ProviderRouteKind.Unavailable ? Sentence : null;

    public override string ToString() => Kind switch
    {
        ProviderRouteKind.AnswerProvider => $"AnswerProvider({Source})",
        ProviderRouteKind.Unavailable => $"Unavailable({Sentence})",
        _ => Kind.ToString(),
    };
}

/// <summary>The keys and token a route is decided on. Values only: no key ever goes into a log.</summary>
public sealed record ProviderCredentials
{
    public string? OpenAIKey { get; init; }
    public string? AnthropicKey { get; init; }
    public string? AccountToken { get; init; }
    /// <summary>The other keys that can answer questions, and the plans the person switched on.</summary>
    public string? OpenRouterKey { get; init; }
    public string? GeminiKey { get; init; }
    public string? GroqKey { get; init; }
    public string? MistralKey { get; init; }
    public string? XaiKey { get; init; }
    public bool ClaudeCodePlanOn { get; init; }
    public bool CodexPlanOn { get; init; }
    /// <summary>Who should answer, when the person picked one (null: automatic).</summary>
    public AnswerSource? PreferredAnswerSource { get; init; }

    /// <summary>Blank values count as missing; every value is trimmed.</summary>
    public ProviderCredentials(string? openAIKey, string? anthropicKey, string? accountToken,
                               string? openRouterKey = null, string? geminiKey = null, string? groqKey = null,
                               string? mistralKey = null, string? xaiKey = null,
                               bool claudeCodePlanOn = false, bool codexPlanOn = false, AnswerSource? preferredAnswerSource = null)
    {
        OpenAIKey = OpenClickyConfiguration.Cleaned(openAIKey);
        AnthropicKey = OpenClickyConfiguration.Cleaned(anthropicKey);
        AccountToken = OpenClickyConfiguration.Cleaned(accountToken);
        OpenRouterKey = OpenClickyConfiguration.Cleaned(openRouterKey);
        GeminiKey = OpenClickyConfiguration.Cleaned(geminiKey);
        GroqKey = OpenClickyConfiguration.Cleaned(groqKey);
        MistralKey = OpenClickyConfiguration.Cleaned(mistralKey);
        XaiKey = OpenClickyConfiguration.Cleaned(xaiKey);
        ClaudeCodePlanOn = claudeCodePlanOn;
        CodexPlanOn = codexPlanOn;
        PreferredAnswerSource = preferredAnswerSource;
    }

    public static ProviderCredentials FromSettings(OpenClickyShellSettings settings) =>
        new(settings.OpenaiApiKey, settings.AnthropicApiKey, settings.Token,
            openRouterKey: settings.OpenRouterApiKey, geminiKey: settings.GeminiApiKey, groqKey: settings.GroqApiKey,
            mistralKey: settings.MistralApiKey, xaiKey: settings.XaiApiKey,
            claudeCodePlanOn: settings.UseClaudeCodePlan == true, codexPlanOn: settings.UseCodexPlan == true,
            preferredAnswerSource: AnswerSources.FromRawValue(settings.AnswerSource));

    /// <summary>The keys and token in shell.json now.</summary>
    public static ProviderCredentials Current() => FromSettings(OpenClickyConfiguration.Settings);

    /// <summary>The key for an API source, null for the plans and the account.</summary>
    public string? Key(AnswerSource source) => source switch
    {
        AnswerSource.Anthropic => AnthropicKey,
        AnswerSource.OpenAI => OpenAIKey,
        AnswerSource.OpenRouter => OpenRouterKey,
        AnswerSource.Gemini => GeminiKey,
        AnswerSource.Groq => GroqKey,
        AnswerSource.Mistral => MistralKey,
        AnswerSource.Xai => XaiKey,
        _ => null,
    };

    /// <summary>Whether `source` can answer with what is set up: its key, its plan switched on, or an account.</summary>
    public bool CanAnswer(AnswerSource source) => source switch
    {
        AnswerSource.ClaudeCodePlan => ClaudeCodePlanOn,
        AnswerSource.CodexPlan => CodexPlanOn,
        AnswerSource.OpenClickyAccount => AccountToken is not null,
        _ => Key(source) is not null,
    };

    private static readonly AnswerSource[] AutomaticOrder =
    [
        AnswerSource.Anthropic, AnswerSource.OpenRouter, AnswerSource.OpenAI, AnswerSource.Gemini, AnswerSource.Xai,
        AnswerSource.Mistral, AnswerSource.Groq, AnswerSource.ClaudeCodePlan, AnswerSource.CodexPlan, AnswerSource.OpenClickyAccount,
    ];

    /// <summary>
    /// The sources that can answer, in the order "automatic" tries them: own keys (Anthropic first,
    /// then OpenRouter, which reaches the most models), then the plans switched on, then the account.
    /// </summary>
    public IReadOnlyList<AnswerSource> AnswerSourcesInOrder => AutomaticOrder.Where(CanAnswer).ToList();
}

public static class ProviderRouter
{
    /// <summary>No key and no account: the free account is the way in for most people, so it comes first.</summary>
    public const string NeedsOpenAIKeySentence = "i can do that once you add your email for a free account, in settings, account. your own openai key works too.";
    public const string NeedsAnthropicKeySentence = "i can answer that once you add your email for a free account, in settings, account. a key from openai, anthropic, openrouter, gemini and others works too.";
    public const string AssemblyAINeedsAccountSentence = "assemblyai runs on openclicky's own key, so it needs an openclicky account. pick openai or offline dictation instead.";

    /// <summary>
    /// The route for `capability`: own key → that vendor; no key but an account → the backend;
    /// neither → unavailable, saying which key would do.
    /// </summary>
    public static ProviderRoute Route(ProviderCapability capability, ProviderCredentials credentials)
    {
        switch (capability)
        {
            case ProviderCapability.Claude:
                // The person's pick when it can answer, else the first that can in the automatic order.
                AnswerSource? source = credentials.PreferredAnswerSource is { } preferred && credentials.CanAnswer(preferred)
                    ? preferred
                    : credentials.AnswerSourcesInOrder.Count > 0 ? credentials.AnswerSourcesInOrder[0] : null;
                return source switch
                {
                    AnswerSource.Anthropic => ProviderRoute.AnthropicDirect,
                    AnswerSource.OpenClickyAccount => ProviderRoute.Backend,
                    { } other => ProviderRoute.AnswerProvider(other),
                    null => ProviderRoute.Unavailable(NeedsAnthropicKeySentence),
                };
            case ProviderCapability.OpenAIChat or ProviderCapability.Speech or ProviderCapability.Transcription or ProviderCapability.Realtime:
                if (credentials.OpenAIKey is not null) return ProviderRoute.OpenAIDirect;
                break;
        }
        if (credentials.AccountToken is not null) return ProviderRoute.Backend;
        return capability switch
        {
            ProviderCapability.Claude => ProviderRoute.Unavailable(NeedsAnthropicKeySentence),
            ProviderCapability.AssemblyAIStreaming => ProviderRoute.Unavailable(AssemblyAINeedsAccountSentence),
            _ => ProviderRoute.Unavailable(NeedsOpenAIKeySentence),
        };
    }

    /// <summary>The keys and token in shell.json now.</summary>
    public static ProviderCredentials CurrentCredentials() => ProviderCredentials.Current();

    /// <summary>The route for `capability` with the settings in shell.json now. Logged when it changes.</summary>
    public static ProviderRoute Current(ProviderCapability capability)
    {
        var route = Route(capability, CurrentCredentials());
        NoteRoute(route, capability);
        return route;
    }

    private static readonly object LastRoutesLock = new();
    private static readonly Dictionary<ProviderCapability, ProviderRoute> LastRoutes = [];

    private static void NoteRoute(ProviderRoute route, ProviderCapability capability)
    {
        bool changed;
        lock (LastRoutesLock)
        {
            changed = !LastRoutes.TryGetValue(capability, out var last) || last != route;
            LastRoutes[capability] = route;
        }
        if (changed) AppLog.Write($"route: {capability.RawValue()} → {route.LogName}");
    }
}

/// <summary>The error a caller throws for an unavailable route; its message is the sentence to show or say.</summary>
public sealed class ProviderRouteUnavailableException(ProviderCapability capability, string sentence) : Exception(sentence)
{
    public ProviderCapability Capability { get; } = capability;
    public string Sentence { get; } = sentence;
}

/// <summary>The direct vendor requests, shaped as the backend's own-key path shapes them upstream.</summary>
public static class DirectProviderRequests
{
    public const string OpenAIBaseUrl = "https://api.openai.com/v1";
    public const string AnthropicMessagesUrl = "https://api.anthropic.com/v1/messages";
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>The backend's BYOK defaults (keys.ts, proxy.ts, backend/.dev.vars.example).</summary>
    public const string DefaultOpenAIModel = "gpt-5.5";
    public const string DefaultAnthropicModel = "claude-haiku-4-5-20251001";
    public const string RealtimeModel = "gpt-realtime";
    public const string TranscriptionModel = "gpt-4o-mini-transcribe";
    public const string SpeechModel = "gpt-4o-mini-tts";
    public const string SpeechVoice = "marin";

    /// <summary>A body with exactly `Content-Type: application/json` (no charset), as URLRequest sends it.</summary>
    public static HttpContent JsonContent(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    public static HttpContent JsonContent(JsonNode body) => JsonContent(Encoding.UTF8.GetBytes(body.ToJsonString()));

    public static HttpContent JsonContent(string body) => JsonContent(Encoding.UTF8.GetBytes(body));

    private static HttpRequestMessage OpenAIRequest(string path, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, OpenAIBaseUrl + path);
        request.SetHeader("Authorization", $"Bearer {key}");
        return request;
    }

    /// <summary>
    /// Anthropic Messages; the body is the caller's (the same one /chat relays), as JSON content —
    /// pass it here or set `Content = DirectProviderRequests.JsonContent(...)` afterwards.
    /// </summary>
    public static HttpRequestMessage AnthropicMessages(string key, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AnthropicMessagesUrl);
        request.Content = JsonContent(body ?? new JsonObject());
        request.SetHeader("x-api-key", key);
        request.SetHeader("anthropic-version", AnthropicVersion);
        return request;
    }

    /// <summary>The polish model the backend picks (modelPolicy.ts) and its output ceiling (polish.ts).</summary>
    public const string PolishModel = "claude-haiku-4-5";
    public const int PolishMaxOutputTokens = 4096;

    /// <summary>Dictation polish or a Hey Clicky edit on Claude Haiku, as /v1/polish sends it upstream.</summary>
    public static HttpRequestMessage AnthropicPolish(string key, string system, string text)
    {
        var body = new JsonObject
        {
            ["model"] = PolishModel,
            // An upper bound: some scripts take a token or more per character, and a take cut off
            // at max_tokens would be pasted half-finished.
            ["max_tokens"] = Math.Min(PolishMaxOutputTokens, text.Length * 2 + 64),
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = system,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }),
        };
        return AnthropicMessages(key, body);
    }

    /// <summary>
    /// The text of a non-streamed Messages reply. Null when there is none, or when it stopped at
    /// max_tokens: half a take must never be pasted as if it were whole.
    /// </summary>
    public static string? AnthropicReplyText(byte[] data)
    {
        JsonObject? json;
        try { json = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return null; }
        if (json?["content"] is not JsonArray content) return null;
        if (StringValue(json["stop_reason"]) == "max_tokens") return null;
        var text = string.Concat(content
            .OfType<JsonObject>()
            .Where(block => StringValue(block["type"]) == "text")
            .Select(block => StringValue(block["text"]))
            .Where(blockText => blockText is not null)).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>OpenAI Chat Completions; the body is the caller's.</summary>
    public static HttpRequestMessage OpenAIChatCompletions(string key, JsonNode? body = null)
    {
        var request = OpenAIRequest("/chat/completions", key);
        request.Content = JsonContent(body ?? new JsonObject());
        return request;
    }

    /// <summary>OpenAI speech for `text`, mp3 (synthesizeSpeech in proxy.ts).</summary>
    public static HttpRequestMessage OpenAISpeech(string key, string text)
    {
        var request = OpenAIRequest("/audio/speech", key);
        request.SetHeader("Accept", "audio/mpeg");
        request.Content = JsonContent(new JsonObject
        {
            ["model"] = SpeechModel,
            ["voice"] = SpeechVoice,
            ["input"] = text,
            ["response_format"] = "mp3",
        });
        return request;
    }

    /// <summary>The multipart upload the backend builds for /agent/transcribe (transcribeAudio in proxy.ts).</summary>
    public static HttpRequestMessage OpenAITranscription(string key, byte[] wav, string? language, string? prompt, string? boundary = null)
    {
        boundary ??= $"openclicky-{Guid.NewGuid().ToString().ToUpperInvariant()}";
        var request = OpenAIRequest("/audio/transcriptions", key);
        using var body = new MemoryStream();
        void Append(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            body.Write(bytes, 0, bytes.Length);
        }
        void Field(string name, string value) =>
            Append($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
        Append($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
        body.Write(wav, 0, wav.Length);
        Append("\r\n");
        Field("model", TranscriptionModel);
        Field("response_format", "json");
        if (!string.IsNullOrEmpty(language)) Field("language", language);
        if (!string.IsNullOrEmpty(prompt)) Field("prompt", prompt);
        Append($"--{boundary}--\r\n");
        var content = new ByteArrayContent(body.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        request.Content = content;
        return request;
    }

    /// <summary>A Realtime client secret (createRealtimeSession in proxy.ts); the answer has the backend's shape.</summary>
    public static HttpRequestMessage OpenAIRealtimeSecret(string key, string? instructions, string? voice, int expiresInSeconds = 600)
    {
        var request = OpenAIRequest("/realtime/client_secrets", key);
        var session = new JsonObject { ["type"] = "realtime", ["model"] = RealtimeModel };
        if (!string.IsNullOrEmpty(instructions)) session["instructions"] = instructions;
        if (!string.IsNullOrEmpty(voice)) session["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = voice } };
        request.Content = JsonContent(new JsonObject
        {
            ["expires_after"] = new JsonObject { ["anchor"] = "created_at", ["seconds"] = expiresInSeconds },
            ["session"] = session,
        });
        return request;
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
