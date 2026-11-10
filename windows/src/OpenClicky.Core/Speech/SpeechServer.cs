// A speech server of the person's own, on this PC or another one on the network, through OpenAI's audio API:
// POST /v1/audio/transcriptions (a WAV in, `{ "text": … }` out) hears, POST /v1/audio/speech (text in, WAV out)
// speaks. Ollama (0.40 and later), Speaches (faster-whisper, Kokoro, Piper), LocalAI and Kokoro-FastAPI all answer
// these, so one address serves whichever the person installed; GET /v1/models lists what it has to pick from
// (LocalModels). Nothing here leaves the person's own address. docs/local-speech.md says how to set one up.

using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

/// <summary>shell.json's speech server: its address (without /v1), an optional key, and the models that hear and speak
/// (null: that half is off), with the voice the speaking model uses (null: the server's own default).</summary>
public sealed record SpeechServerSettings(string BaseUrl, string? Key, string? HearingModel, string? VoiceModel, string? Voice);

public static class SpeechServer
{
    /// <summary>The server in <paramref name="settings"/>; null when no address is set, or it isn't an http(s) one.</summary>
    public static SpeechServerSettings? From(OpenClickyShellSettings settings)
    {
        if (ProviderCredentials.BaseUrl(settings.SpeechServerUrl) is not { } baseUrl) return null;
        static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new SpeechServerSettings(baseUrl, Trimmed(settings.SpeechServerKey), Trimmed(settings.SpeechServerHearingModel),
            Trimmed(settings.SpeechServerVoiceModel), Trimmed(settings.SpeechServerVoice));
    }

    /// <summary>Whether a speech server is set up to hear (an address and a hearing model).</summary>
    public static bool Hears(OpenClickyShellSettings settings) => From(settings)?.HearingModel is not null;

    /// <summary>Whether a speech server is set up to speak (an address and a voice model).</summary>
    public static bool Speaks(OpenClickyShellSettings settings) => From(settings)?.VoiceModel is not null;

    /// <summary>The ways to set one up, for a row or the island that needs one.</summary>
    public const string NeedsServerSentence = "needs a speech server: settings → clicky → your models";

    // MARK: - Which listed models hear and which speak

    /// <summary>Names that usually mean a model hears (writes down speech).</summary>
    private static readonly string[] HearingHints =
        ["whisper", "parakeet", "canary", "moonshine", "sensevoice", "voxtral", "nemotron3", "gemma4:e2b", "gemma4:e4b", "gemma-4-e2b", "gemma-4-e4b",
         "transcri", "stt", "asr", "speech-to-text", "lfm3-omni"];

    /// <summary>Names that usually mean a model speaks.</summary>
    private static readonly string[] VoiceHints =
        ["kokoro", "piper", "phonon", "chatterbox", "tts", "orpheus", "lfm3-audio", "lfm3-omni", "xtts", "zonos", "parler", "text-to-speech"];

    public static bool LooksLikeItHears(string model) => HearingHints.Any(hint => model.Contains(hint, StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeItSpeaks(string model) => VoiceHints.Any(hint => model.Contains(hint, StringComparison.OrdinalIgnoreCase));

    /// <summary><paramref name="models"/> with the likely hearing (or speaking) ones first, each group in the server's order.
    /// Every model stays offered: a name says little, and the person knows what they installed.</summary>
    public static IReadOnlyList<string> Ordered(IReadOnlyList<string> models, bool forHearing)
    {
        Func<string, bool> likely = forHearing ? LooksLikeItHears : LooksLikeItSpeaks;
        return [.. models.Where(likely), .. models.Where(model => !likely(model))];
    }

    // MARK: - Requests

    /// <summary>POST {base}/v1/audio/transcriptions: the WAV as `file`, the model, and the language and prompt when there are any.</summary>
    public static HttpRequestMessage TranscriptionRequest(SpeechServerSettings server, string model, byte[] wav, string? language, string? prompt,
                                                          string? boundary = null)
    {
        boundary ??= $"openclicky-{Guid.NewGuid():N}";
        var request = new HttpRequestMessage(HttpMethod.Post, $"{server.BaseUrl}/v1/audio/transcriptions");
        Authorize(request, server);
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
        Field("model", model);
        Field("response_format", "json");
        if (!string.IsNullOrEmpty(language)) Field("language", language);
        if (!string.IsNullOrEmpty(prompt)) Field("prompt", prompt);
        Append($"--{boundary}--\r\n");
        var content = new ByteArrayContent(body.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        request.Content = content;
        return request;
    }

    /// <summary>POST {base}/v1/audio/speech for <paramref name="text"/> as WAV (the one format Ollama, Speaches and
    /// Kokoro-FastAPI all give); the voice only when one is set, so a server's own default speaks otherwise.</summary>
    public static HttpRequestMessage SpeechRequest(SpeechServerSettings server, string model, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{server.BaseUrl}/v1/audio/speech");
        Authorize(request, server);
        var body = new JsonObject { ["model"] = model, ["input"] = text, ["response_format"] = "wav" };
        if (server.Voice is { } voice) body["voice"] = voice;
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    private static void Authorize(HttpRequestMessage request, SpeechServerSettings server)
    {
        if (server.Key is { } key) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
    }

    // MARK: - Hearing and speaking

    /// <summary>How long a reply may take: the first one after the server starts loads its model (Ollama: several seconds).</summary>
    public static readonly TimeSpan ReplyLimit = TimeSpan.FromSeconds(60);

    /// <summary>The words in <paramref name="wav"/>, heard by the server's hearing model.</summary>
    public static async Task<string> TranscribeAsync(SpeechServerSettings server, byte[] wav, string? language, string? prompt,
                                                     CancellationToken cancellationToken = default, HttpClient? client = null)
    {
        var model = server.HearingModel ?? throw new TranscriptionProviderException(Capitalized(NeedsServerSentence) + ".");
        using var request = TranscriptionRequest(server, model, wav, language, prompt);
        var data = await SendAsync(request, server, cancellationToken, client).ConfigureAwait(false);
        return OpenAIAudioTranscriptionProvider.ParseTranscript(data)
            ?? throw new TranscriptionProviderException($"The speech server at {server.BaseUrl} answered without any words.");
    }

    /// <summary><paramref name="text"/> spoken by the server's voice model, as WAV.</summary>
    public static async Task<byte[]> SpeakAsync(SpeechServerSettings server, string text, CancellationToken cancellationToken = default,
                                                HttpClient? client = null)
    {
        var model = server.VoiceModel ?? throw CloudVoiceException.NoKey(VendorName);
        using var request = SpeechRequest(server, model, text);
        var data = await SendAsync(request, server, cancellationToken, client, forVoice: true).ConfigureAwait(false);
        if (data.Length == 0) throw CloudVoiceException.Unreadable(VendorName);
        return WithWholeSoundData(data);
    }

    /// <summary>
    /// One WAV holding all the sound that came. Speaches sends Piper's answer as one whole WAV per sentence, back to back, so a
    /// reader that believes the first header (NAudio, Whisper) plays or hears only the first sentence; those are joined into one
    /// file when they share a format. A streamed header that doesn't know its length (0 or 0xFFFFFFFF, or more than came) gets
    /// the length that is there. Anything else is returned as it came.
    /// </summary>
    public static byte[] WithWholeSoundData(byte[] wav)
    {
        if (SoundData(wav, 0) is not { } first) return wav;
        var (format, dataSizeAt, dataStart, declared) = first;
        long there = wav.Length - dataStart;
        if (declared is 0 or uint.MaxValue || declared > there)
        {
            var whole = there - there % Math.Max(1, (int)BitConverter.ToUInt16(wav, format + 12));
            return Rebuilt(wav, dataSizeAt, wav.AsSpan(dataStart, (int)whole));
        }
        var end = dataStart + declared;
        if (end >= wav.Length) return wav;
        var sound = new MemoryStream();
        sound.Write(wav, dataStart, (int)declared);
        while (end < wav.Length)
        {
            if (SoundData(wav, (int)end) is not { } next || !wav.AsSpan(next.Format, 16).SequenceEqual(wav.AsSpan(format, 16))
                || next.DataStart + next.Declared > wav.Length) return wav;
            sound.Write(wav, next.DataStart, (int)next.Declared);
            end = next.DataStart + next.Declared;
        }
        return Rebuilt(wav, dataSizeAt, sound.ToArray());
    }

    /// <summary>The WAV starting at <paramref name="at"/>: where its "fmt " body is, where its data size is written, where its sound starts, and the size it declares.</summary>
    private static (int Format, int DataSizeAt, int DataStart, long Declared)? SoundData(byte[] wav, int at)
    {
        if (at + 44 > wav.Length || !Ascii(wav, at, "RIFF") || !Ascii(wav, at + 8, "WAVE")) return null;
        var format = -1;
        var offset = at + 12;
        while (offset + 8 <= wav.Length)
        {
            var size = (long)BitConverter.ToUInt32(wav, offset + 4);
            if (Ascii(wav, offset, "fmt ") && size >= 16 && offset + 24 <= wav.Length) format = offset + 8;
            if (Ascii(wav, offset, "data")) return format < 0 ? null : (format, offset + 4, offset + 8, size);
            offset += (int)Math.Min(8 + size + (size & 1), int.MaxValue / 2);
        }
        return null;
    }

    /// <summary>The header up to the data size (fmt and any other chunks before the sound), then the sound, with both sizes right.</summary>
    private static byte[] Rebuilt(byte[] wav, int dataSizeAt, ReadOnlySpan<byte> sound)
    {
        var header = dataSizeAt + 4;
        var rebuilt = new byte[header + sound.Length];
        wav.AsSpan(0, header).CopyTo(rebuilt);
        sound.CopyTo(rebuilt.AsSpan(header));
        BitConverter.TryWriteBytes(rebuilt.AsSpan(dataSizeAt, 4), (uint)sound.Length);
        BitConverter.TryWriteBytes(rebuilt.AsSpan(4, 4), (uint)(rebuilt.Length - 8));
        return rebuilt;
    }

    private static bool Ascii(byte[] bytes, int at, string text)
    {
        if (at < 0 || at + text.Length > bytes.Length) return false;
        for (var i = 0; i < text.Length; i++) if (bytes[at + i] != text[i]) return false;
        return true;
    }

    /// <summary>The server's name in the voice's errors and the log.</summary>
    public const string VendorName = "Your speech server";

    private static async Task<byte[]> SendAsync(HttpRequestMessage request, SpeechServerSettings server, CancellationToken cancellationToken,
                                                HttpClient? client, bool forVoice = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReplyLimit);
        HttpResponseMessage response;
        try
        {
            response = await (client ?? HttpClients.Shared).SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var sentence = error is TaskCanceledException
                ? $"The speech server at {server.BaseUrl} took longer than {ReplyLimit.TotalSeconds:0} seconds."
                : $"Nothing answered at {server.BaseUrl}. Is the speech server running?";
            AppLog.Write($"speech server: {request.RequestUri?.AbsolutePath} failed ({error.GetType().Name})");
            throw forVoice ? CloudVoiceException.Refused(VendorName, 0, sentence) : new TranscriptionProviderException(sentence);
        }
        using (response)
        {
            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) return data;
            var said = Encoding.UTF8.GetString(data, 0, Math.Min(300, data.Length));
            AppLog.Write($"speech server: {request.RequestUri?.AbsolutePath} said {status}: {said}");
            throw forVoice
                ? CloudVoiceException.Refused(VendorName, status, said)
                : new TranscriptionProviderException($"The speech server said {status}: {said}");
        }
    }

    private static string Capitalized(string sentence) => sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];
}

/// <summary>Push-to-talk (and dictation, when picked there) heard by the person's speech server: the take is buffered and
/// sent as one WAV on release, the same session as upload transcription.</summary>
public sealed class SpeechServerTranscriptionProvider(Func<string?>? language = null, Func<OpenClickyShellSettings>? settings = null,
                                                       HttpClient? client = null) : IBuddyTranscriptionProvider
{
    private readonly Func<string?> language = language ?? (() => ReplyLanguage.CurrentCode);
    private readonly Func<OpenClickyShellSettings> settings = settings ?? (() => OpenClickyConfiguration.Settings);

    public string DisplayName => "your speech server";
    public bool RequiresSpeechRecognitionPermission => false;
    public bool IsConfigured => SpeechServer.Hears(settings());
    public string? UnavailableExplanation => IsConfigured ? null : "Pick a model that hears under Settings → Clicky → Your models → Speech server.";

    /// <summary>How long a take waits for its words: a server's first reply loads its model, and a take sent on without
    /// its words would ask an empty question.</summary>
    public static readonly TimeSpan FinalTranscriptWait = TimeSpan.FromSeconds(45);

    public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        if (SpeechServer.From(settings()) is not { HearingModel: not null } server) throw new TranscriptionProviderException(UnavailableExplanation!);
        IBuddyStreamingTranscriptionSession session = new OpenAIAudioTranscriptionSession(
            keyterms, language(), onTranscriptUpdate, onFinalTranscriptReady, onError,
            (wav, languageCode, prompt, token) => SpeechServer.TranscribeAsync(server, wav, languageCode, prompt, token, client),
            FinalTranscriptWait);
        return Task.FromResult(session);
    }
}
