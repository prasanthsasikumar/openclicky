// Ports Dictation/SarvamSpeechClient.swift: Sarvam's speech and chat APIs with the user's own key.
// Saaras hears a take (REST, one WAV), the streaming sockets hear it as it is spoken, a chat
// completion polishes the words, and Bulbul speaks answers. Written from docs.sarvam.ai as read on
// 2026-10-06. Request building and decoding are pure public statics so they can be tested without
// a network; the websocket sessions (Speech port) open `StreamingRequest` / `RealtimeRequest` and
// read frames with `DecodeStreamingMessage` / `DecodeRealtimeMessage`.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Dictation;

public enum SarvamSpeechErrorKind { NoKey, KeyRefused, OutOfCredits, Busy, Refused, Unreadable, Unreachable }

/// <summary>Why Sarvam did not hear, speak or polish; the message is the sentence to show.</summary>
public sealed class SarvamSpeechException : Exception
{
    public SarvamSpeechErrorKind Kind { get; }
    /// <summary>The HTTP status of a `Refused`.</summary>
    public int Status { get; }
    /// <summary>The refusal's message, what could not be read, or why it could not be reached.</summary>
    public string Detail { get; }

    public SarvamSpeechException(SarvamSpeechErrorKind kind, int status = 0, string detail = "") : base(Describe(kind, status, detail))
    {
        Kind = kind; Status = status; Detail = detail;
    }

    private static string Describe(SarvamSpeechErrorKind kind, int status, string detail) => kind switch
    {
        SarvamSpeechErrorKind.NoKey => "sarvam needs your key. paste it under settings → account.",
        SarvamSpeechErrorKind.KeyRefused => "sarvam did not accept the key. check it under settings → account.",
        SarvamSpeechErrorKind.OutOfCredits => "sarvam says this key is out of credits (dashboard.sarvam.ai).",
        SarvamSpeechErrorKind.Busy => "sarvam is busy. try again in a moment.",
        SarvamSpeechErrorKind.Refused => $"sarvam refused ({status}): {detail}",
        SarvamSpeechErrorKind.Unreadable => $"sarvam's answer could not be read: {detail}",
        _ => $"couldn't reach sarvam: {detail}",
    };

    /// <summary>
    /// Sarvam answers a bad key with 403 (sometimes 401), a spent account and a rate limit both with
    /// 429: only `error.code` tells the last two apart.
    /// </summary>
    public static SarvamSpeechException Refusal(int status, byte[] body)
    {
        JsonObject? stated = null;
        try { stated = (JsonNode.Parse(body) as JsonObject)?["error"] as JsonObject; }
        catch (JsonException) { }
        var code = StringValue(stated?["code"]) ?? "";
        var message = StringValue(stated?["message"]) ?? Encoding.UTF8.GetString(body, 0, Math.Min(300, body.Length));
        if (code == "insufficient_quota_error") return new(SarvamSpeechErrorKind.OutOfCredits);
        return status switch
        {
            401 => new(SarvamSpeechErrorKind.KeyRefused),
            403 when code.Length == 0 || code is "invalid_api_key_error" or "authentication_error" => new(SarvamSpeechErrorKind.KeyRefused),
            429 or 503 => new(SarvamSpeechErrorKind.Busy),
            _ => new(SarvamSpeechErrorKind.Refused, status, message),
        };
    }

    internal static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>A websocket to open: its URL and the headers it goes with.</summary>
public sealed record SarvamSocketRequest(Uri Url, IReadOnlyDictionary<string, string> Headers)
{
    public string? HeaderValue(string name) =>
        Headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed class SarvamSpeechClient
{
    public static readonly Uri Host = new("https://api.sarvam.ai");
    public static readonly Uri StreamingHost = new("wss://api.sarvam.ai");
    /// <summary>The REST transcription model; `saaras:v4` is Sarvam's current default.</summary>
    public const string TranscriptionModel = "saaras:v4";
    /// <summary>
    /// Sarvam retired sarvam-30b ("Please use one of the available models instead: sarvam-105b,
    /// sarvam-105b-conversations"), so the 105B model it is, with the formatter given more time.
    /// </summary>
    public const string ChatModel = "sarvam-105b";
    public const int SampleRate = 16_000;
    public static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan SpeechTimeout = TimeSpan.FromSeconds(20);

    public string Key { get; }
    public Uri BaseHost { get; }
    private readonly HttpClient? httpClient;

    public SarvamSpeechClient(string key, Uri? host = null, HttpClient? httpClient = null)
    {
        // A pasted key routinely brings a newline with it, and an untrimmed one is refused in a way
        // indistinguishable from a wrong one.
        Key = key.Trim();
        BaseHost = host ?? Host;
        this.httpClient = httpClient;
    }

    private HttpClient Client => httpClient ?? HttpClients.Shared;

    /// <summary>The language Sarvam is told to expect: its BCP-47 code, or `unknown` to let it detect.</summary>
    public static string LanguageCode(DictationLanguage language) => language.Code == "auto" ? "unknown" : language.Code;

    private static Uri Append(Uri host, string path) => new(host.ToString().TrimEnd('/') + "/" + path);

    // MARK: ears (REST)

    /// <summary>What was said in `wav` (PCM16 mono 16 kHz). Up to about thirty seconds per request.</summary>
    public async Task<string> TranscribeAsync(byte[] wav, DictationLanguage language, IReadOnlyList<string>? keyterms = null,
                                              CancellationToken cancellationToken = default)
    {
        if (Key.Length == 0) throw new SarvamSpeechException(SarvamSpeechErrorKind.NoKey);
        var data = await SendAsync(TranscriptionRequest(wav, language, keyterms ?? [], Key, BaseHost), TranscriptionTimeout, cancellationToken).ConfigureAwait(false);
        return DecodeTranscription(data);
    }

    public static string DecodeTranscription(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data) is JsonObject answer && SarvamSpeechException.StringValue(answer["transcript"]) is { } transcript)
            {
                return transcript.Trim();
            }
        }
        catch (JsonException) { }
        throw new SarvamSpeechException(SarvamSpeechErrorKind.Unreadable, detail: "there was no transcript in it");
    }

    public static HttpRequestMessage TranscriptionRequest(byte[] wav, DictationLanguage language, IReadOnlyList<string> keyterms, string key, Uri host,
                                                          string? boundary = null)
    {
        boundary ??= $"openclicky-{Guid.NewGuid().ToString().ToUpperInvariant()}";
        var request = new HttpRequestMessage(HttpMethod.Post, Append(host, "speech-to-text"));
        request.SetHeader("api-subscription-key", key.Trim());
        var body = new MemoryStream();
        void Write(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            body.Write(bytes, 0, bytes.Length);
        }
        void Field(string name, string value) => Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
        Field("model", TranscriptionModel);
        Field("language_code", LanguageCode(language));
        foreach (var keyterm in keyterms.Take(50)) Field("keyterms", keyterm);
        Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"take.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
        body.Write(wav, 0, wav.Length);
        Write($"\r\n--{boundary}--\r\n");
        var content = new ByteArrayContent(body.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        request.Content = content;
        return request;
    }

    // MARK: ears (streaming)

    /// <summary>
    /// The socket a streaming session opens: `/speech-to-text/ws` with the language, the model and
    /// the sample rate in the query, the key in a header.
    /// </summary>
    public static SarvamSocketRequest StreamingRequest(DictationLanguage language, IReadOnlyList<string> keyterms, string key, Uri? host = null)
    {
        var items = new List<(string Name, string Value)>
        {
            ("language-code", LanguageCode(language)),
            ("model", TranscriptionModel),
            ("sample_rate", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("input_audio_codec", "wav"),
            ("vad_signals", "true"),
        };
        if (KeytermsQueryValue(keyterms) is { } encoded) items.Add(("keyterms", encoded));
        return SocketRequest(host ?? StreamingHost, "speech-to-text/ws", items, key);
    }

    /// <summary>
    /// The chunked socket takes `keyterms` as a JSON array of strings ("'keyterms' must be a valid
    /// JSON array of strings"), fifty at most; null when there are none.
    /// </summary>
    public static string? KeytermsQueryValue(IEnumerable<string> keyterms)
    {
        var cleaned = keyterms.Select(term => term.Trim()).Where(term => term.Length > 0).Take(50).ToList();
        if (cleaned.Count == 0) return null;
        return JsonSerializer.Serialize(cleaned, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // MARK: ears (realtime)

    /// <summary>
    /// The realtime socket's models: Saaras v4 first (keyterms, global English, the best language
    /// ID; same latency measured), v3-realtime as the fallback should v4 refuse the session.
    /// </summary>
    public enum RealtimeModel { V4, V3 }

    public static string RawValue(RealtimeModel model) => model == RealtimeModel.V4 ? "saaras:v4" : "saaras:v3-realtime";

    /// <summary>
    /// The realtime socket, with manual endpointing, so the key decides where the utterance ends and
    /// partial words arrive while it is held. `language_code` must be named (`auto` detects).
    /// </summary>
    public static SarvamSocketRequest RealtimeRequest(DictationLanguage language, IReadOnlyList<string> keyterms, string key,
                                                      RealtimeModel model = RealtimeModel.V4, Uri? host = null)
    {
        var items = new List<(string Name, string Value)>
        {
            ("language_code", language.Code == "auto" ? "auto" : language.Code),
            ("model", RawValue(model)),
            ("stream_type", "balanced"),
            ("encoding", "linear16"),
            ("sample_rate", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("endpointing", "manual"),
        };
        // v4 takes `keyterms` (a JSON array); v3-realtime refuses them ("only supported by model
        // 'saaras:v4'") but takes a `prompt`, so the dictionary's words go there instead.
        if (model == RealtimeModel.V4)
        {
            if (KeytermsQueryValue(keyterms) is { } encoded) items.Add(("keyterms", encoded));
        }
        else
        {
            var hint = string.Join(", ", keyterms.Select(term => term.Trim()).Where(term => term.Length > 0).Take(50));
            if (hint.Length > 0) items.Add(("prompt", $"Names and terms that may come up: {hint}."));
        }
        return SocketRequest(host ?? StreamingHost, "speech-to-text-realtime/ws", items, key);
    }

    public static string RealtimeAudioMessage(ReadOnlySpan<byte> pcm16) =>
        Json(new JsonObject { ["event"] = "audio_input", ["audio"] = Convert.ToBase64String(pcm16) });

    public static string RealtimeEventMessage(string eventName) => Json(new JsonObject { ["event"] = eventName });

    public enum RealtimeServerMessageKind { Partial, Final, SessionBegin, SessionEnd, Error, Other }

    /// <summary>What a realtime frame means: its kind, the text (or error message), and whether an error is fatal.</summary>
    public sealed record RealtimeServerMessage(RealtimeServerMessageKind Kind, string Text = "", bool Fatal = false)
    {
        public static RealtimeServerMessage Partial(string text) => new(RealtimeServerMessageKind.Partial, text);
        public static RealtimeServerMessage Final(string text) => new(RealtimeServerMessageKind.Final, text);
        public static RealtimeServerMessage Error(string message, bool fatal) => new(RealtimeServerMessageKind.Error, message, fatal);
        public static RealtimeServerMessage SessionBegin { get; } = new(RealtimeServerMessageKind.SessionBegin);
        public static RealtimeServerMessage SessionEnd { get; } = new(RealtimeServerMessageKind.SessionEnd);
        public static RealtimeServerMessage Other { get; } = new(RealtimeServerMessageKind.Other);
    }

    public static RealtimeServerMessage DecodeRealtimeMessage(string text)
    {
        var json = ParseObject(text);
        if (json is null || SarvamSpeechException.StringValue(json["event"]) is not { } eventName) return RealtimeServerMessage.Other;
        return eventName switch
        {
            "transcript.partial" => RealtimeServerMessage.Partial(SarvamSpeechException.StringValue(json["text"]) ?? ""),
            "transcript.final" => RealtimeServerMessage.Final(SarvamSpeechException.StringValue(json["text"]) ?? ""),
            "session.begin" => RealtimeServerMessage.SessionBegin,
            "session.end" => RealtimeServerMessage.SessionEnd,
            "error" => RealtimeServerMessage.Error(
                SarvamSpeechException.StringValue(json["message"]) ?? SarvamSpeechException.StringValue(json["code"]) ?? "unknown error",
                json["is_fatal"] is JsonValue fatalValue && fatalValue.TryGetValue<bool>(out var fatal) ? fatal : true),
            _ => RealtimeServerMessage.Other,
        };
    }

    /// <summary>One audio chunk as the chunked socket wants it: a base64 WAV at 16 kHz.</summary>
    public static string StreamingAudioMessage(ReadOnlySpan<byte> pcm16)
    {
        var wav = DictationWavFile.FromPcm16Mono(pcm16, SampleRate);
        return Json(new JsonObject
        {
            ["audio"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(wav),
                ["sample_rate"] = SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["encoding"] = "audio/wav",
            },
        });
    }

    public const string StreamingFlushMessage = "{\"type\":\"flush\"}";

    public enum StreamingServerMessageKind { Transcript, Error, Other }

    /// <summary>What a chunked-socket frame means: a piece of transcript, an error, or something to ignore.</summary>
    public sealed record StreamingServerMessage(StreamingServerMessageKind Kind, string Text = "")
    {
        public static StreamingServerMessage Transcript(string text) => new(StreamingServerMessageKind.Transcript, text);
        public static StreamingServerMessage Error(string message) => new(StreamingServerMessageKind.Error, message);
        public static StreamingServerMessage Other { get; } = new(StreamingServerMessageKind.Other);
    }

    public static StreamingServerMessage DecodeStreamingMessage(string text)
    {
        var json = ParseObject(text);
        if (json is null || SarvamSpeechException.StringValue(json["type"]) is not { } type) return StreamingServerMessage.Other;
        var payload = json["data"] as JsonObject;
        return type switch
        {
            "data" => StreamingServerMessage.Transcript(SarvamSpeechException.StringValue(payload?["transcript"]) ?? ""),
            "error" => StreamingServerMessage.Error(
                SarvamSpeechException.StringValue(payload?["error"]) ?? SarvamSpeechException.StringValue(payload?["code"]) ?? "unknown error"),
            _ => StreamingServerMessage.Other,
        };
    }

    // MARK: mind

    /// <summary>One chat completion: `system` then `user`; the assistant's text comes back.</summary>
    public async Task<string> CompleteAsync(string system, string user, int maxTokens = 1024, CancellationToken cancellationToken = default)
    {
        if (Key.Length == 0) throw new SarvamSpeechException(SarvamSpeechErrorKind.NoKey);
        var data = await SendAsync(ChatRequest(system, user, maxTokens, Key, BaseHost), ChatTimeout, cancellationToken).ConfigureAwait(false);
        return DecodeChat(data);
    }

    public static string DecodeChat(byte[] data) =>
        BackendTakePolisher.ChatCompletionContent(data)?.Trim()
        ?? throw new SarvamSpeechException(SarvamSpeechErrorKind.Unreadable, detail: "there was no message in it");

    public static HttpRequestMessage ChatRequest(string system, string user, int maxTokens, string key, Uri host)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Append(host, "v1/chat/completions"));
        request.SetHeader("Authorization", $"Bearer {key}");
        request.SetHeader("api-subscription-key", key);
        request.Content = DirectProviderRequests.JsonContent(new JsonObject
        {
            ["model"] = ChatModel,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["temperature"] = 0.2,
            ["max_tokens"] = maxTokens,
            // sarvam-105b reasons by default, and "low" still spent all 1,024 tokens thinking and
            // answered nothing after ~5 s (2026-10-11). An explicit null turns reasoning off.
            ["reasoning_effort"] = null,
        });
        return request;
    }

    // MARK: voice

    /// <summary>
    /// Sarvam's speech model; bulbul:v2 was retired ("has been deprecated. Please use 'bulbul:v3'",
    /// 2026-10-12). Answers come back as base64 WAV.
    /// </summary>
    public const string SpeechModel = "bulbul:v3";

    /// <summary>
    /// The languages Bulbul speaks, by the reply language's two-letter code. Anything else is left
    /// to the next voice in line.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SpeechLanguageCodes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["en"] = "en-IN", ["hi"] = "hi-IN", ["bn"] = "bn-IN", ["gu"] = "gu-IN", ["kn"] = "kn-IN", ["ml"] = "ml-IN",
        ["mr"] = "mr-IN", ["od"] = "od-IN", ["or"] = "od-IN", ["pa"] = "pa-IN", ["ta"] = "ta-IN", ["te"] = "te-IN",
    };

    public static string? SpeechLanguageCode(string replyLanguageCode) =>
        SpeechLanguageCodes.TryGetValue(replyLanguageCode.ToLowerInvariant(), out var code) ? code : null;

    /// <summary>`text` spoken by Bulbul, as WAV bytes.</summary>
    public async Task<byte[]> SpeechAsync(string text, string languageCode, CancellationToken cancellationToken = default)
    {
        if (Key.Length == 0) throw new SarvamSpeechException(SarvamSpeechErrorKind.NoKey);
        var data = await SendAsync(SpeechRequest(text, languageCode, Key, BaseHost), SpeechTimeout, cancellationToken).ConfigureAwait(false);
        return DecodeSpeech(data);
    }

    public static HttpRequestMessage SpeechRequest(string text, string languageCode, string key, Uri host)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Append(host, "text-to-speech"));
        request.SetHeader("api-subscription-key", key);
        request.Content = DirectProviderRequests.JsonContent(new JsonObject
        {
            ["text"] = text,
            ["target_language_code"] = languageCode,
            ["model"] = SpeechModel,
        });
        return request;
    }

    /// <summary>The first audio in a text-to-speech answer (one is returned per text sent).</summary>
    public static byte[] DecodeSpeech(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data)?["audios"] is JsonArray audios && audios.Count > 0
                && SarvamSpeechException.StringValue(audios[0]) is { } first)
            {
                var audio = Convert.FromBase64String(first);
                if (audio.Length > 0) return audio;
            }
        }
        catch (Exception error) when (error is JsonException or FormatException) { }
        throw new SarvamSpeechException(SarvamSpeechErrorKind.Unreadable, detail: "there was no audio in it");
    }

    // MARK: plumbing

    private async Task<byte[]> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        HttpResponseMessage response;
        byte[] data;
        try
        {
            response = await Client.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            data = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SarvamSpeechException(SarvamSpeechErrorKind.Unreachable, detail: error.Message);
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is < 200 or >= 300) throw SarvamSpeechException.Refusal(status, data);
            return data;
        }
    }

    private static JsonObject? ParseObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string Json(JsonObject json) =>
        json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static SarvamSocketRequest SocketRequest(Uri host, string path, IEnumerable<(string Name, string Value)> items, string key)
    {
        var query = string.Join("&", items.Select(item => $"{QueryEscape(item.Name)}={QueryEscape(item.Value)}"));
        var url = new Uri(Append(host, path) + "?" + query);
        return new SarvamSocketRequest(url, new Dictionary<string, string> { ["Api-Subscription-Key"] = key.Trim() });
    }

    /// <summary>
    /// A query value escaped the way Foundation's URLComponents escapes one: everything outside
    /// the URL-query-allowed set, plus the characters that would end a value or a name.
    /// </summary>
    public static string QueryEscape(string value)
    {
        const string allowedPunctuation = "-._~!$'()*,;:@/?";
        var builder = new StringBuilder();
        foreach (var byteValue in Encoding.UTF8.GetBytes(value))
        {
            var character = (char)byteValue;
            if (byteValue < 128 && (char.IsAsciiLetterOrDigit(character) || allowedPunctuation.Contains(character))) builder.Append(character);
            else builder.Append('%').Append(byteValue.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}

/// <summary>A PCM16 mono WAV (the 44-byte RIFF header and the samples), as the Mac's BuddyWAVFileBuilder builds it.</summary>
public static class DictationWavFile
{
    public const int HeaderLength = 44;

    public static byte[] FromPcm16Mono(ReadOnlySpan<byte> pcm16, int sampleRate)
    {
        var wav = new byte[HeaderLength + pcm16.Length];
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BitConverter.TryWriteBytes(span[4..], (uint)(36 + pcm16.Length));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BitConverter.TryWriteBytes(span[16..], 16u);
        BitConverter.TryWriteBytes(span[20..], (ushort)1); // PCM
        BitConverter.TryWriteBytes(span[22..], (ushort)1); // mono
        BitConverter.TryWriteBytes(span[24..], (uint)sampleRate);
        BitConverter.TryWriteBytes(span[28..], (uint)(sampleRate * 2));
        BitConverter.TryWriteBytes(span[32..], (ushort)2);
        BitConverter.TryWriteBytes(span[34..], (ushort)16);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BitConverter.TryWriteBytes(span[40..], (uint)pcm16.Length);
        pcm16.CopyTo(span[HeaderLength..]);
        return wav;
    }

    /// <summary>The samples of a WAV this class wrote (the `data` chunk); null when it is not one.</summary>
    public static byte[]? Pcm16(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wav, offset, 4);
            var chunkLength = (int)BitConverter.ToUInt32(wav, offset + 4);
            if (chunkId == "data") return wav.AsSpan(offset + 8, Math.Min(chunkLength, wav.Length - offset - 8)).ToArray();
            offset += 8 + chunkLength + (chunkLength % 2);
        }
        return null;
    }
}
