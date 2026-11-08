// Push-to-talk heard by Gemini on the person's own Gemini key: the take is buffered and sent as one WAV on release
// (the same session as upload transcription), and Gemini writes down what was said. Used for a language this PC's
// own engines don't hear (Urdu), on an account or with the offline engine picked (CompanionManager.ApplyHearingForAccount):
// Gemini follows Urdu with English words mixed in, keeping each word in its own script.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

public sealed class GeminiAudioTranscriptionProvider(Func<string?>? language = null, HttpClient? client = null) : IBuddyTranscriptionProvider
{
    /// <summary>The model that listens: the one that answers on a Gemini key, quick with its thinking low.</summary>
    public const string Model = "gemini-3.8-flash";

    private readonly Func<string?> language = language ?? (() => ReplyLanguage.CurrentCode);

    public string DisplayName => "Gemini";
    public bool RequiresSpeechRecognitionPermission => false;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(OpenClickyConfiguration.Settings.GeminiApiKey);
    public string? UnavailableExplanation => IsConfigured ? null : "Add a Gemini key under Keys & Account to be heard in this language.";

    public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new TranscriptionProviderException(UnavailableExplanation!);
        IBuddyStreamingTranscriptionSession session = new OpenAIAudioTranscriptionSession(
            keyterms, language(), onTranscriptUpdate, onFinalTranscriptReady, onError, TranscribeAsync);
        return Task.FromResult(session);
    }

    /// <summary>Heard instead when the main model is busy (Gemini answers 503 "high demand" at times) or too slow.</summary>
    public const string BusyModel = "gemini-3.5-flash-lite";

    private async Task<string> TranscribeAsync(byte[] wav, string? languageCode, string? prompt, CancellationToken cancellationToken)
    {
        var key = OpenClickyConfiguration.Settings.GeminiApiKey?.Trim() ?? throw new TranscriptionProviderException(UnavailableExplanation!);
        var first = await AskAsync(Model, key, wav, languageCode, prompt, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (first.Text is { } heard) return heard;
        if (!IsBusy(first.Status)) throw Failure(first.Status);
        AppLog.Write($"transcription (gemini): {Model} busy ({first.Status?.ToString() ?? "timeout"}); trying {BusyModel}");
        var second = await AskAsync(BusyModel, key, wav, languageCode, prompt, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return second.Text ?? throw Failure(second.Status);
    }

    /// <summary>Busy or slow (worth asking the other model): 429, 500, 503, or no answer in time (a null status).</summary>
    public static bool IsBusy(int? status) => status is null or 429 or 500 or 503;

    private static TranscriptionProviderException Failure(int? status) =>
        new(status is { } code ? $"Gemini couldn't hear that (HTTP {code})." : "Gemini took too long to hear that.");

    /// <summary>One model's transcript, or the HTTP status it failed with (null when it timed out).</summary>
    private async Task<(string? Text, int? Status)> AskAsync(string model, string key, byte[] wav, string? languageCode, string? prompt,
        TimeSpan limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        request.Headers.Add("x-goog-api-key", key);
        request.Content = new StringContent(RequestBody(wav, languageCode, prompt, model).ToJsonString(), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            using var response = await (client ?? HttpClients.Shared).SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Write($"transcription (gemini {model}): {(int)response.StatusCode}");
                return (null, (int)response.StatusCode);
            }
            var text = ParseTranscript(body) ?? "";
            AppLog.Write($"transcription (gemini {model}): {text.Length} chars");
            return (text, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    /// <summary>The generateContent body: the WAV inline, and what to write down (in the language, each word in its own
    /// script, nothing else; nothing at all for a take with no speech).</summary>
    public static JsonObject RequestBody(byte[] wav, string? languageCode, string? prompt, string model = Model)
    {
        var name = ReplyLanguage.EnglishName(languageCode ?? "en");
        var ask = $"Write down exactly what is said in this recording. The speaker mostly speaks {name}; write {name} in its own script, "
            + "and any English words in English letters, as they were said. Reply with the words only: no quotes, no notes, no translation. "
            + "If nobody speaks, reply with nothing.";
        if (prompt is { Length: > 0 }) ask += " " + prompt;
        var config = new JsonObject { ["temperature"] = 0 };
        if (Answers.GeminiAnswerProvider.ThinkingConfig(model) is { } thinking) config["thinkingConfig"] = thinking;
        return new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(wav) } },
                    new JsonObject { ["text"] = ask }),
            }),
            ["generationConfig"] = config,
        };
    }

    /// <summary>The transcript in Gemini's answer (its text parts, trimmed); null when there is none.</summary>
    public static string? ParseTranscript(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data)?["candidates"]?[0]?["content"]?["parts"] is not JsonArray parts) return null;
            var text = string.Concat(parts.Select(part => part?["text"]?.GetValue<string>() ?? "")).Trim().Trim('"', '“', '”');
            return text;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
