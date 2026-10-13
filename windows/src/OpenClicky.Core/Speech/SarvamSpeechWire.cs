// The Sarvam requests and decoders the speech side needs. They are ported (pure, tested) in
// Core/Dictation/SarvamSpeechClient.cs by the dictation port; the sessions here talk to them only
// through this interface, so the two halves meet in one adapter.
//
// TODO(integration): implement ISarvamSpeechWire as a thin adapter over the dictation port's
// statics and set `SarvamSpeechWire.Current` at startup:
//   RealtimeRequest  → SarvamSpeechClient.RealtimeRequest(language, keyterms, key, model)   (url + "Api-Subscription-Key" header)
//   StreamingRequest → SarvamSpeechClient.StreamingRequest(language, keyterms, key)
//   RealtimeAudioMessage / RealtimeEventMessage / StreamingAudioMessage / StreamingFlushMessage
//   DecodeRealtimeMessage / DecodeStreamingMessage
//   TranscribeAsync  → new SarvamSpeechClient(key).Transcribe(wav, language, keyterms)
//   SpeechAsync      → new SarvamSpeechClient(key).Speech(text, languageCode)  (Bulbul, bulbul:v3)
//   SpeechLanguageCode → SarvamSpeechClient.SpeechLanguageCode(forReplyLanguage:)

namespace OpenClicky.Core.Speech;

/// <summary>Sarvam's realtime socket models: Saaras v4 first, v3-realtime as the fallback should v4 refuse the session.</summary>
public enum SarvamRealtimeModel
{
    /// <summary>"saaras:v4"</summary>
    V4,
    /// <summary>"saaras:v3-realtime"</summary>
    V3,
}

/// <summary>A socket to open: the URL and the headers (the key travels in "Api-Subscription-Key").</summary>
public sealed record SarvamSocketRequest(Uri Url, IReadOnlyDictionary<string, string> Headers);

public enum SarvamRealtimeMessageKind
{
    Partial,
    Final,
    SessionBegin,
    SessionEnd,
    Error,
    Other,
}

/// <summary>What a realtime frame means (SarvamSpeechClient.RealtimeServerMessage).</summary>
public sealed record SarvamRealtimeMessage(SarvamRealtimeMessageKind Kind, string Text = "", bool IsFatal = true);

public enum SarvamStreamingMessageKind
{
    Transcript,
    Error,
    Other,
}

/// <summary>What a chunked-socket frame means (SarvamSpeechClient.StreamingServerMessage).</summary>
public sealed record SarvamStreamingMessage(SarvamStreamingMessageKind Kind, string Text = "");

/// <summary>The Sarvam wire format as the speech sessions need it. <paramref name="languageCode"/> is the
/// dictation language's code ("hi-IN", "en-IN", or "auto").</summary>
public interface ISarvamSpeechWire
{
    int SampleRate { get; }

    SarvamSocketRequest RealtimeRequest(string languageCode, IReadOnlyList<string> keyterms, string key, SarvamRealtimeModel model);

    SarvamSocketRequest StreamingRequest(string languageCode, IReadOnlyList<string> keyterms, string key);

    string RealtimeAudioMessage(byte[] pcm16);

    string RealtimeEventMessage(string eventName);

    string StreamingAudioMessage(byte[] pcm16);

    string StreamingFlushMessage { get; }

    SarvamRealtimeMessage DecodeRealtimeMessage(string text);

    SarvamStreamingMessage DecodeStreamingMessage(string text);

    /// <summary>REST transcription of one WAV (PCM16 mono 16 kHz), up to about thirty seconds.</summary>
    Task<string> TranscribeAsync(string key, byte[] wav, string languageCode, IReadOnlyList<string> keyterms, CancellationToken cancellationToken = default);

    /// <summary>Bulbul's voice for <paramref name="text"/>, as WAV bytes.</summary>
    Task<byte[]> SpeechAsync(string key, string text, string speechLanguageCode, CancellationToken cancellationToken = default);

    /// <summary>The Bulbul language code for a reply language ("hi" → "hi-IN"); null when Bulbul does not speak it.</summary>
    string? SpeechLanguageCode(string replyLanguageCode);
}

public static class SarvamSpeechWire
{
    /// <summary>The adapter the app installs at startup (see the TODO at the top of this file); null until then.</summary>
    public static ISarvamSpeechWire? Current { get; set; }

    /// <summary>
    /// The languages Bulbul speaks, by the reply language's two-letter code, as SarvamSpeechClient
    /// lists them. Used only until <see cref="Current"/> is installed, so the voice picker's rule
    /// holds in the meantime. TODO(integration): drop once SarvamSpeechClient.SpeechLanguageCode is reachable.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FallbackSpeechLanguageCodes = new Dictionary<string, string>
    {
        ["en"] = "en-IN", ["hi"] = "hi-IN", ["bn"] = "bn-IN", ["gu"] = "gu-IN", ["kn"] = "kn-IN", ["ml"] = "ml-IN",
        ["mr"] = "mr-IN", ["od"] = "od-IN", ["or"] = "od-IN", ["pa"] = "pa-IN", ["ta"] = "ta-IN", ["te"] = "te-IN",
    };

    public static string? SpeechLanguageCode(string replyLanguageCode) =>
        Current is { } wire
            ? wire.SpeechLanguageCode(replyLanguageCode)
            : FallbackSpeechLanguageCodes.TryGetValue(replyLanguageCode.ToLowerInvariant(), out var code) ? code : null;

    /// <summary>The installed wire, or an error saying it is missing.</summary>
    public static ISarvamSpeechWire Required =>
        Current ?? throw new InvalidOperationException("sarvam isn't set up in this build yet.");
}

/// <summary>Sarvam's errors as SarvamSpeechError words them (the sessions throw the ones they detect themselves).</summary>
public sealed class SarvamSpeechException(string message) : Exception(message)
{
    public static SarvamSpeechException NoKey() => new("sarvam needs your key. paste it under settings → account.");
    public static SarvamSpeechException Refused(int status, string message) => new($"sarvam refused ({status}): {message}");
    public static SarvamSpeechException Unreachable(string reason) => new($"couldn't reach sarvam: {reason}");
}
