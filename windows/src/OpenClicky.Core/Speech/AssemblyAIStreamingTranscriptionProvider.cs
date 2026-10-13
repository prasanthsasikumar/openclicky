// Ports AssemblyAIStreamingTranscriptionProvider.swift: streaming transcription on AssemblyAI's v3
// websocket. A short-lived token is minted by the OpenClicky backend (POST /transcribe-token, on
// OpenClicky's own AssemblyAI key, account only), PCM16 16 kHz is streamed while the key is held,
// turn-based transcripts are composed into one take, and the final text is delivered on key-up.

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

public sealed class AssemblyAIStreamingTranscriptionProvider : IBuddyTranscriptionProvider
{
    /// <summary>The backend endpoint that mints a short-lived AssemblyAI streaming token. That key never leaves the server.</summary>
    private static string TokenProxyUrl => $"{OpenClickyConfiguration.BackendBaseUrl}/transcribe-token";

    public string DisplayName => "AssemblyAI";
    public bool RequiresSpeechRecognitionPermission => false;

    /// <summary>The token is minted on OpenClicky's own AssemblyAI key, so this needs an account; an own key does not cover it.</summary>
    public bool IsConfigured => UnavailableExplanation is null;

    public string? UnavailableExplanation =>
        ProviderRouter.Route(ProviderCapability.AssemblyAIStreaming, ProviderRouter.CurrentCredentials()).UnavailableSentence;

    /// <summary>
    /// The language dictation was asked to hear (a bare code; null = auto). Not sent: the streaming
    /// model detects and code-switches by itself, and the one steering parameter AssemblyAI
    /// documents (`language_codes`) is not confirmed for `u3-rt-pro` — an unknown parameter could
    /// make every session refuse. It is checked against the languages that model hears, so a
    /// language it cannot hear is at least written to the log.
    /// </summary>
    private readonly Func<string?> language;
    private readonly Func<ISpeechWebSocket> makeSocket;

    /// <summary>AssemblyAI's documented streaming languages (`language_codes`, read 2026-10-11).</summary>
    public static readonly IReadOnlySet<string> StreamingLanguageCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "af", "ar", "yue", "ca", "da", "nl", "en", "et", "fi", "fr", "gl", "de", "he", "hi", "it", "ja", "ko", "zh",
        "mr", "no", "nn", "fa", "pt", "ro", "ru", "es", "sv", "tr", "ur", "vi", "xh", "zu",
    };

    public AssemblyAIStreamingTranscriptionProvider(Func<string?>? language = null, Func<ISpeechWebSocket>? makeSocket = null)
    {
        this.language = language ?? (() => null);
        this.makeSocket = makeSocket ?? (() => new ClientSpeechWebSocket());
    }

    /// <summary>True when <paramref name="code"/> (bare) is a language the streaming model is documented to hear; null = auto.</summary>
    public static bool Hears(string? code) => string.IsNullOrEmpty(code) || StreamingLanguageCodes.Contains(code);

    public async Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        if (language() is { } requestedLanguage && !Hears(requestedLanguage))
            AppLog.Write($"transcription (assemblyai): {requestedLanguage} is not a language its streaming model hears; it will guess");
        string temporaryToken;
        try
        {
            temporaryToken = await FetchTemporaryTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AppLog.Write($"transcription (assemblyai): token request failed: {error.Message}");
            throw;
        }
        // Only that a token was obtained, never any part of its value: app.log is not a safe place
        // for even a credential prefix.
        AppLog.Write($"transcription (assemblyai): fetched temporary token ({temporaryToken.Length} chars)");

        var session = new AssemblyAIStreamingTranscriptionSession(
            temporaryToken, makeSocket(), keyterms, onTranscriptUpdate, onFinalTranscriptReady, onError);
        await session.OpenAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    /// <summary>Asks the backend (`POST /transcribe-token`, account only) for a short-lived AssemblyAI token.</summary>
    private static async Task<string> FetchTemporaryTokenAsync(CancellationToken cancellationToken)
    {
        if (ProviderRouter.Current(ProviderCapability.AssemblyAIStreaming).UnavailableSentence is { } sentence)
            throw new TranscriptionProviderException(sentence);
        // The URL is built from the user-configurable backend URL, so a malformed value must throw
        // instead of crashing.
        if (!Uri.TryCreate(TokenProxyUrl, UriKind.Absolute, out var tokenProxyUri))
            throw new TranscriptionProviderException($"OpenClicky backend URL is invalid: {TokenProxyUrl}");
        var request = new HttpRequestMessage(HttpMethod.Post, tokenProxyUri);
        OpenClickyConfiguration.Authorize(request);
        using var response = await BackendSignIn.SendAsync(request, sent => HttpClients.Shared.SendAsync(sent, cancellationToken)).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        if (statusCode is < 200 or > 299)
        {
            var body = System.Text.Encoding.UTF8.GetString(data);
            throw new TranscriptionProviderException($"Failed to fetch AssemblyAI token (HTTP {statusCode}): {body}");
        }
        return ParseToken(data) ?? throw new TranscriptionProviderException("Invalid token response from proxy.");
    }

    /// <summary>The `token` in the proxy's answer.</summary>
    public static string? ParseToken(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) is JsonObject json && json["token"] is JsonValue value && value.TryGetValue<string>(out var token) ? token : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public const string WebsocketBaseUrl = "wss://streaming.assemblyai.com/v3/ws";

    /// <summary>The v3 socket URL: PCM16 at 16 kHz, formatted turns, the u3-rt-pro model, the keyterms as a JSON array, the token.</summary>
    public static Uri MakeWebsocketUrl(string temporaryToken, IReadOnlyList<string> keyterms)
    {
        var queryItems = new List<(string Name, string Value)>
        {
            ("sample_rate", "16000"),
            ("encoding", "pcm_s16le"),
            ("format_turns", "true"),
            ("speech_model", "u3-rt-pro"),
        };
        var normalizedKeyterms = keyterms.Select(keyterm => keyterm.Trim()).Where(keyterm => keyterm.Length > 0).ToList();
        if (normalizedKeyterms.Count > 0)
            queryItems.Add(("keyterms_prompt", JsonSerializer.Serialize(normalizedKeyterms, SpeechJson.CompactOptions)));
        queryItems.Add(("token", temporaryToken));
        var query = string.Join("&", queryItems.Select(item => $"{item.Name}={SpeechJson.QueryEscape(item.Value)}"));
        return new Uri($"{WebsocketBaseUrl}?{query}");
    }
}

/// <summary>One AssemblyAI streaming take. Turn messages are kept by their order and composed into the take.</summary>
internal sealed class AssemblyAIStreamingTranscriptionSession : IBuddyStreamingTranscriptionSession
{
    private const int TargetSampleRate = 16_000;
    private static readonly TimeSpan ExplicitFinalTranscriptGracePeriod = TimeSpan.FromSeconds(1.4);

    public TimeSpan FinalTranscriptFallbackDelay => TimeSpan.FromSeconds(2.8);

    private readonly string temporaryToken;
    private readonly ISpeechWebSocket socket;
    private readonly IReadOnlyList<string> keyterms;
    private readonly Action<string> onTranscriptUpdate;
    private readonly Action<string> onFinalTranscriptReady;
    private readonly Action<Exception> onError;
    private readonly BuddyPCM16AudioConverter audioPCM16Converter = new(TargetSampleRate);
    private readonly AssemblyAITurnTranscript turns = new();
    private readonly object stateLock = new();
    private readonly TaskCompletionSource readySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    /// <summary>Sends go out in order, one at a time.</summary>
    private readonly OrderedSocketSender sender;

    private bool hasDeliveredFinalTranscript;
    private bool isAwaitingExplicitFinalTranscript;
    private CancellationTokenSource? explicitFinalTranscriptDeadline;

    public AssemblyAIStreamingTranscriptionSession(
        string temporaryToken, ISpeechWebSocket socket, IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate, Action<string> onFinalTranscriptReady, Action<Exception> onError)
    {
        this.temporaryToken = temporaryToken;
        this.socket = socket;
        this.keyterms = keyterms;
        this.onTranscriptUpdate = onTranscriptUpdate;
        this.onFinalTranscriptReady = onFinalTranscriptReady;
        this.onError = onError;
        sender = new OrderedSocketSender(socket, error => { if (!lifetime.IsCancellationRequested) FailSession(error); }, lifetime.Token);
    }

    /// <summary>Connects and waits for AssemblyAI's "Begin" (or the first failure).</summary>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        var websocketUrl = AssemblyAIStreamingTranscriptionProvider.MakeWebsocketUrl(temporaryToken, keyterms);
        try
        {
            await socket.ConnectAsync(websocketUrl, new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            socket.Dispose();
            throw new TranscriptionProviderException(error.Message, error);
        }
        _ = Task.Run(ReceiveLoopAsync);
        using var registration = cancellationToken.Register(() => readySource.TrySetCanceled(cancellationToken));
        await readySource.Task.ConfigureAwait(false);
    }

    public void AppendAudio(AudioFrame frame)
    {
        var audioPCM16Data = audioPCM16Converter.ConvertToPcm16(frame);
        if (audioPCM16Data is not { Length: > 0 }) return;
        sender.SendBinary(audioPCM16Data);
    }

    public void RequestFinalTranscript()
    {
        lock (stateLock)
        {
            if (!hasDeliveredFinalTranscript)
            {
                isAwaitingExplicitFinalTranscript = true;
                ScheduleExplicitFinalTranscriptDeadline();
            }
        }
        SendJsonMessage("""{"type":"ForceEndpoint"}""");
    }

    public void Cancel()
    {
        lock (stateLock)
        {
            explicitFinalTranscriptDeadline?.Cancel();
            explicitFinalTranscriptDeadline = null;
        }
        SendJsonMessage("""{"type":"Terminate"}""");
        _ = Task.Run(async () =>
        {
            await Task.Delay(100).ConfigureAwait(false);
            lifetime.Cancel();
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable).ConfigureAwait(false);
        });
    }

    private async Task ReceiveLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            SpeechWebSocketMessage message;
            try
            {
                message = await socket.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (!lifetime.IsCancellationRequested) FailSession(error);
                return;
            }
            if (message.Kind == SpeechWebSocketMessageKind.Close)
            {
                if (!lifetime.IsCancellationRequested)
                    FailSession(new TranscriptionProviderException($"AssemblyAI closed the connection ({message.CloseStatus}) {message.CloseReason}".TrimEnd()));
                return;
            }
            if (message.AsText is { } text) HandleIncomingTextMessage(text);
        }
    }

    private void HandleIncomingTextMessage(string text)
    {
        JsonObject? envelope;
        try
        {
            envelope = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException error)
        {
            FailSession(error);
            return;
        }
        var type = SpeechJson.String(envelope?["type"])?.ToLowerInvariant();
        if (envelope is null || type is null)
        {
            FailSession(new TranscriptionProviderException("AssemblyAI sent a message without a type."));
            return;
        }
        switch (type)
        {
            case "begin":
                readySource.TrySetResult();
                break;
            case "turn":
                HandleTurnMessage(envelope);
                break;
            case "termination":
                readySource.TrySetResult();
                lock (stateLock)
                {
                    if (isAwaitingExplicitFinalTranscript && !hasDeliveredFinalTranscript)
                        DeliverFinalTranscriptIfNeeded(turns.BestAvailableTranscriptText());
                }
                break;
            case "error":
                var messageText = SpeechJson.String(envelope["error"]) ?? SpeechJson.String(envelope["message"]) ?? "AssemblyAI returned an error.";
                FailSession(new TranscriptionProviderException(messageText));
                break;
        }
    }

    private void HandleTurnMessage(JsonObject turnMessage)
    {
        var transcriptText = (SpeechJson.String(turnMessage["transcript"]) ?? "").Trim();
        var turnOrder = SpeechJson.Int(turnMessage["turn_order"]);
        var endOfTurn = SpeechJson.Bool(turnMessage["end_of_turn"]) == true;
        var isFormatted = SpeechJson.Bool(turnMessage["turn_is_formatted"]) == true;
        string fullTranscriptText;
        bool deliver;
        lock (stateLock)
        {
            fullTranscriptText = turns.Receive(transcriptText, turnOrder, endOfTurn, isFormatted);
            deliver = isAwaitingExplicitFinalTranscript && (endOfTurn || isFormatted);
        }
        if (fullTranscriptText.Length > 0) onTranscriptUpdate(fullTranscriptText);
        if (!deliver) return;
        lock (stateLock)
        {
            explicitFinalTranscriptDeadline?.Cancel();
            explicitFinalTranscriptDeadline = null;
            DeliverFinalTranscriptIfNeeded(turns.BestAvailableTranscriptText());
        }
    }

    /// <summary>Called with stateLock held.</summary>
    private void ScheduleExplicitFinalTranscriptDeadline()
    {
        explicitFinalTranscriptDeadline?.Cancel();
        var deadline = new CancellationTokenSource();
        explicitFinalTranscriptDeadline = deadline;
        _ = Task.Delay(ExplicitFinalTranscriptGracePeriod, deadline.Token).ContinueWith(delay =>
        {
            if (delay.IsCanceled) return;
            lock (stateLock) DeliverFinalTranscriptIfNeeded(turns.BestAvailableTranscriptText());
        }, TaskScheduler.Default);
    }

    /// <summary>Called with stateLock held.</summary>
    private void DeliverFinalTranscriptIfNeeded(string transcriptText)
    {
        if (hasDeliveredFinalTranscript) return;
        hasDeliveredFinalTranscript = true;
        explicitFinalTranscriptDeadline?.Cancel();
        explicitFinalTranscriptDeadline = null;
        // Raised off the lock so a callback can call back in.
        _ = Task.Run(() => onFinalTranscriptReady(transcriptText));
        SendJsonMessage("""{"type":"Terminate"}""");
    }

    private void SendJsonMessage(string json) => sender.SendText(json);

    private void FailSession(Exception error)
    {
        readySource.TrySetException(error);
        string latestTranscriptText;
        lock (stateLock)
        {
            if (hasDeliveredFinalTranscript) return;
            latestTranscriptText = turns.BestAvailableTranscriptText();
            if (isAwaitingExplicitFinalTranscript && latestTranscriptText.Length > 0)
            {
                AppLog.Write($"transcription (assemblyai): socket error, delivering the partial transcript: {error.Message}");
                DeliverFinalTranscriptIfNeeded(latestTranscriptText);
                return;
            }
        }
        AppLog.Write($"transcription (assemblyai): session failed: {error.Message}");
        onError(error);
    }
}

/// <summary>
/// The take as AssemblyAI's turns build it: finished turns by their order (a formatted turn is
/// never replaced by an unformatted one), plus the turn still being spoken. Not thread-safe.
/// </summary>
public sealed class AssemblyAITurnTranscript
{
    private readonly SortedDictionary<int, (string Text, bool IsFormatted)> storedTurnTranscriptsByOrder = [];
    private int? activeTurnOrder;
    private string activeTurnTranscriptText = "";
    private string latestTranscriptText = "";

    /// <summary>Takes one Turn message and returns the whole take so far.</summary>
    public string Receive(string transcriptText, int? turnOrder, bool endOfTurn, bool isFormatted)
    {
        var order = turnOrder ?? activeTurnOrder ?? (storedTurnTranscriptsByOrder.Count == 0 ? 0 : storedTurnTranscriptsByOrder.Keys.Max() + 1);
        if (endOfTurn || isFormatted)
        {
            activeTurnOrder = null;
            activeTurnTranscriptText = "";
            StoreTurnTranscript(transcriptText, order, isFormatted);
        }
        else
        {
            activeTurnOrder = order;
            activeTurnTranscriptText = transcriptText;
        }
        latestTranscriptText = ComposeFullTranscript();
        return latestTranscriptText;
    }

    private void StoreTurnTranscript(string transcriptText, int turnOrder, bool isFormatted)
    {
        if (transcriptText.Length == 0) return;
        if (storedTurnTranscriptsByOrder.TryGetValue(turnOrder, out var existing) && existing.IsFormatted && !isFormatted) return;
        storedTurnTranscriptsByOrder[turnOrder] = (transcriptText, isFormatted);
    }

    public string ComposeFullTranscript()
    {
        var segments = storedTurnTranscriptsByOrder.Values.Select(turn => turn.Text).Where(text => text.Length > 0).ToList();
        var trimmedActive = activeTurnTranscriptText.Trim();
        if (trimmedActive.Length > 0) segments.Add(trimmedActive);
        return string.Join(" ", segments);
    }

    public string BestAvailableTranscriptText()
    {
        var composed = ComposeFullTranscript().Trim();
        return composed.Length > 0 ? composed : latestTranscriptText.Trim();
    }
}

/// <summary>JSON helpers the speech sessions share.</summary>
internal static class SpeechJson
{
    public static readonly JsonSerializerOptions CompactOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<double>(out var real) && double.IsFinite(real)) return (int)real;
        return null;
    }

    /// <summary>A number the model sent as a number or as a numeric string.</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<string>(out var text) && double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    /// <summary>A query value escaped as URLComponents escapes it (RFC 3986 unreserved kept).</summary>
    public static string QueryEscape(string value) => Uri.EscapeDataString(value);
}
