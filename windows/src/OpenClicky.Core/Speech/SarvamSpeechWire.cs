// The Sarvam requests and decoders the speech side needs. They are ported (pure, tested) in
// Core/Dictation/SarvamSpeechClient.cs; the sessions here talk to them only through this interface
// (so tests can fake the wire), and `SarvamSpeechClientWire` is the adapter over those statics that
// `SarvamSpeechWire.Current` holds unless a test replaces it.

using OpenClicky.Core.Dictation;

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
    /// <summary>The wire the sessions and the voice use: SarvamSpeechClient's, unless a test sets another (null is not kept).</summary>
    public static ISarvamSpeechWire? Current
    {
        get => current;
        set => current = value ?? SarvamSpeechClientWire.Shared;
    }

    private static ISarvamSpeechWire current = SarvamSpeechClientWire.Shared;

    public static string? SpeechLanguageCode(string replyLanguageCode) => current.SpeechLanguageCode(replyLanguageCode);

    public static ISarvamSpeechWire Required => current;
}

/// <summary>ISarvamSpeechWire over Core/Dictation's SarvamSpeechClient (requests, decoders, REST calls).</summary>
public sealed class SarvamSpeechClientWire : ISarvamSpeechWire
{
    public static SarvamSpeechClientWire Shared { get; } = new();

    public int SampleRate => SarvamSpeechClient.SampleRate;

    /// <summary>The dictation language a code names ("auto" detects); a code the pickers do not list is still sent as it is.</summary>
    public static DictationLanguage Language(string languageCode) =>
        DictationLanguage.Choices.FirstOrDefault(choice => choice.Code == languageCode) ?? new DictationLanguage(languageCode, languageCode, languageCode);

    public SarvamSocketRequest RealtimeRequest(string languageCode, IReadOnlyList<string> keyterms, string key, SarvamRealtimeModel model) =>
        Socket(SarvamSpeechClient.RealtimeRequest(Language(languageCode), keyterms, key,
            model == SarvamRealtimeModel.V4 ? SarvamSpeechClient.RealtimeModel.V4 : SarvamSpeechClient.RealtimeModel.V3));

    public SarvamSocketRequest StreamingRequest(string languageCode, IReadOnlyList<string> keyterms, string key) =>
        Socket(SarvamSpeechClient.StreamingRequest(Language(languageCode), keyterms, key));

    private static SarvamSocketRequest Socket(Dictation.SarvamSocketRequest request) => new(request.Url, request.Headers);

    public string RealtimeAudioMessage(byte[] pcm16) => SarvamSpeechClient.RealtimeAudioMessage(pcm16);

    public string RealtimeEventMessage(string eventName) => SarvamSpeechClient.RealtimeEventMessage(eventName);

    public string StreamingAudioMessage(byte[] pcm16) => SarvamSpeechClient.StreamingAudioMessage(pcm16);

    public string StreamingFlushMessage => SarvamSpeechClient.StreamingFlushMessage;

    public SarvamRealtimeMessage DecodeRealtimeMessage(string text)
    {
        var decoded = SarvamSpeechClient.DecodeRealtimeMessage(text);
        var kind = decoded.Kind switch
        {
            SarvamSpeechClient.RealtimeServerMessageKind.Partial => SarvamRealtimeMessageKind.Partial,
            SarvamSpeechClient.RealtimeServerMessageKind.Final => SarvamRealtimeMessageKind.Final,
            SarvamSpeechClient.RealtimeServerMessageKind.SessionBegin => SarvamRealtimeMessageKind.SessionBegin,
            SarvamSpeechClient.RealtimeServerMessageKind.SessionEnd => SarvamRealtimeMessageKind.SessionEnd,
            SarvamSpeechClient.RealtimeServerMessageKind.Error => SarvamRealtimeMessageKind.Error,
            _ => SarvamRealtimeMessageKind.Other,
        };
        return new SarvamRealtimeMessage(kind, decoded.Text, decoded.Fatal);
    }

    public SarvamStreamingMessage DecodeStreamingMessage(string text)
    {
        var decoded = SarvamSpeechClient.DecodeStreamingMessage(text);
        var kind = decoded.Kind switch
        {
            SarvamSpeechClient.StreamingServerMessageKind.Transcript => SarvamStreamingMessageKind.Transcript,
            SarvamSpeechClient.StreamingServerMessageKind.Error => SarvamStreamingMessageKind.Error,
            _ => SarvamStreamingMessageKind.Other,
        };
        return new SarvamStreamingMessage(kind, decoded.Text);
    }

    public Task<string> TranscribeAsync(string key, byte[] wav, string languageCode, IReadOnlyList<string> keyterms, CancellationToken cancellationToken = default) =>
        new SarvamSpeechClient(key).TranscribeAsync(wav, Language(languageCode), keyterms, cancellationToken);

    public Task<byte[]> SpeechAsync(string key, string text, string speechLanguageCode, CancellationToken cancellationToken = default) =>
        new SarvamSpeechClient(key).SpeechAsync(text, speechLanguageCode, cancellationToken);

    public string? SpeechLanguageCode(string replyLanguageCode) => SarvamSpeechClient.SpeechLanguageCode(replyLanguageCode);
}

/// <summary>Sarvam's errors as SarvamSpeechError words them (the sessions throw the ones they detect themselves).</summary>
public sealed class SarvamSpeechException(string message) : Exception(message)
{
    public static SarvamSpeechException NoKey() => new("sarvam needs your key. paste it under settings → account.");
    public static SarvamSpeechException Refused(int status, string message) => new($"sarvam refused ({status}): {message}");
    public static SarvamSpeechException Unreachable(string reason) => new($"couldn't reach sarvam: {reason}");
}
