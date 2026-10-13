// Ports OpenAIAudioTranscriptionProvider.swift: upload transcription on OpenAI's audio
// transcription API. The take is buffered and sent as one WAV on release, straight to OpenAI on
// the person's own key or through the OpenClicky backend (POST /agent/transcribe) on the account.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

public sealed class OpenAIAudioTranscriptionProvider : IBuddyTranscriptionProvider
{
    // ProviderRouter decides where each upload goes: with the person's own OpenAI key, straight to
    // OpenAI's /audio/transcriptions; otherwise through the backend on the account's token.
    public string DisplayName => "OpenClicky";
    public bool RequiresSpeechRecognitionPermission => false;

    /// <summary>The language to transcribe in (a bare code such as "hi"); null lets the model detect
    /// it. Read when a take starts. Dictation passes its own language; the talk lane the reply language.</summary>
    private readonly Func<string?> language;

    public OpenAIAudioTranscriptionProvider(Func<string?>? language = null)
    {
        this.language = language ?? (() => ReplyLanguage.CurrentCode);
    }

    /// <summary>An own OpenAI key (straight to OpenAI) or an account (the backend).</summary>
    public bool IsConfigured => UnavailableExplanation is null;

    public string? UnavailableExplanation =>
        ProviderRouter.Route(ProviderCapability.Transcription, ProviderRouter.CurrentCredentials()).UnavailableSentence;

    public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new TranscriptionProviderException(UnavailableExplanation ?? "OpenClicky transcription is not configured.");
        IBuddyStreamingTranscriptionSession session = new OpenAIAudioTranscriptionSession(
            keyterms, language(), onTranscriptUpdate, onFinalTranscriptReady, onError);
        return Task.FromResult(session);
    }

    /// <summary>
    /// On near-silent audio the model sometimes returns its own prompt (verbatim or nearly). A
    /// transcript that is mostly made of the prompt's words is not something the user said.
    /// </summary>
    public static bool LooksLikeEchoedPrompt(string transcriptText, string? prompt)
    {
        if (prompt is null) return false;
        static List<string> Words(string text) =>
            System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
                .Where(word => word.Length > 2)
                .ToList();
        var transcriptWords = Words(transcriptText);
        if (transcriptWords.Count < 4) return false;
        var promptWords = Words(prompt).ToHashSet();
        var sharedWordCount = transcriptWords.Count(promptWords.Contains);
        return sharedWordCount / (double)transcriptWords.Count >= 0.8;
    }

    /// <summary>The prompt that tells the model which words to expect; null without keyterms.</summary>
    public static string? TranscriptionPromptText(IReadOnlyList<string> keyterms)
    {
        var normalizedKeyterms = keyterms.Select(keyterm => keyterm.Trim()).Where(keyterm => keyterm.Length > 0).ToList();
        if (normalizedKeyterms.Count == 0) return null;
        return "This is a short push-to-talk transcript for a coding and product app. Expect product names, technical terms, and app-specific vocabulary such as: "
            + string.Join(", ", normalizedKeyterms) + ".";
    }

    /// <summary>The backend's /agent/transcribe body: base64 WAV, mime, and the language and prompt when there are any.</summary>
    public static JsonObject BackendRequestBody(byte[] wav, string? language, string? prompt)
    {
        var body = new JsonObject
        {
            ["audio"] = Convert.ToBase64String(wav),
            ["mime"] = "audio/wav",
        };
        if (!string.IsNullOrEmpty(language)) body["language"] = language;
        if (prompt is not null) body["prompt"] = prompt;
        return body;
    }

    /// <summary>The trimmed `text` of a transcription answer; null when there is none.</summary>
    public static string? ParseTranscript(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) is JsonObject json && json["text"] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class OpenAIAudioTranscriptionSession(
    IReadOnlyList<string> keyterms,
    string? language,
    Action<string> onTranscriptUpdate,
    Action<string> onFinalTranscriptReady,
    Action<Exception> onError) : IBuddyStreamingTranscriptionSession
{
    private const int TargetSampleRate = 16_000;

    public TimeSpan FinalTranscriptFallbackDelay => TimeSpan.FromSeconds(8);

    private readonly BuddyPCM16AudioConverter audioPCM16Converter = new(TargetSampleRate);
    private readonly object stateLock = new();
    /// <summary>The URLSession's 90 s resource timeout, as a cancellation.</summary>
    private readonly CancellationTokenSource uploadCancellation = new();
    private MemoryStream bufferedPCM16AudioData = new();
    /// <summary>Loudest sample magnitude seen (0…32767): audio that never rises above the noise floor is not uploaded (PCM16SilenceGate).</summary>
    private short peakSampleMagnitude;
    private bool hasRequestedFinalTranscript;
    private bool hasDeliveredFinalTranscript;
    private bool isCancelled;

    private static string TranscriptionUrl => $"{OpenClickyConfiguration.BackendBaseUrl}/agent/transcribe";

    public void AppendAudio(AudioFrame frame)
    {
        var audioPCM16Data = audioPCM16Converter.ConvertToPcm16(frame);
        if (audioPCM16Data is not { Length: > 0 }) return;
        var bufferPeak = PCM16SilenceGate.PeakMagnitude(audioPCM16Data);
        lock (stateLock)
        {
            if (hasRequestedFinalTranscript || isCancelled) return;
            bufferedPCM16AudioData.Write(audioPCM16Data);
            peakSampleMagnitude = Math.Max(peakSampleMagnitude, bufferPeak);
        }
    }

    public void RequestFinalTranscript()
    {
        byte[] audio;
        short peak;
        lock (stateLock)
        {
            if (hasRequestedFinalTranscript || isCancelled) return;
            hasRequestedFinalTranscript = true;
            audio = bufferedPCM16AudioData.ToArray();
            peak = peakSampleMagnitude;
        }
        var recordedSeconds = audio.Length / (double)(TargetSampleRate * 2);
        System.Diagnostics.Debug.WriteLine($"[OpenAI Transcription] recorded {recordedSeconds:0.0} s, peak {peak}/32767");
        if (PCM16SilenceGate.IsSilent(peak))
        {
            AppLog.Write($"transcription (openclicky): audio is silent (peak {peak}/32767); not uploading");
            _ = Task.Run(() => DeliverFinalTranscript(""));
            return;
        }
        _ = Task.Run(() => TranscribeBufferedAudioAsync(audio));
    }

    public void Cancel()
    {
        lock (stateLock)
        {
            isCancelled = true;
            bufferedPCM16AudioData = new MemoryStream();
        }
        uploadCancellation.Cancel();
    }

    private bool IsCancelled
    {
        get { lock (stateLock) return isCancelled; }
    }

    private async Task TranscribeBufferedAudioAsync(byte[] bufferedAudio)
    {
        if (IsCancelled) return;
        if (bufferedAudio.Length == 0)
        {
            DeliverFinalTranscript("");
            return;
        }
        var wavAudioData = BuddyWAVFileBuilder.BuildWavData(bufferedAudio, TargetSampleRate);
        try
        {
            var transcriptText = await RequestTranscriptionAsync(wavAudioData).ConfigureAwait(false);
            if (IsCancelled) return;
            if (OpenAIAudioTranscriptionProvider.LooksLikeEchoedPrompt(transcriptText, OpenAIAudioTranscriptionProvider.TranscriptionPromptText(keyterms)))
            {
                AppLog.Write("transcription (openclicky): the model echoed its prompt instead of transcribing; treating as silence");
                transcriptText = "";
            }
            if (transcriptText.Length > 0) onTranscriptUpdate(transcriptText);
            DeliverFinalTranscript(transcriptText);
        }
        catch (Exception error)
        {
            if (IsCancelled) return;
            AppLog.Write($"transcription (openclicky): upload failed ({wavAudioData.Length} bytes): {error.Message}");
            onError(error);
        }
    }

    private async Task<string> RequestTranscriptionAsync(byte[] wavAudioData)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(uploadCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var prompt = OpenAIAudioTranscriptionProvider.TranscriptionPromptText(keyterms);
        HttpResponseMessage response;
        var route = ProviderRouter.Current(ProviderCapability.Transcription);
        switch (route.Kind)
        {
            case ProviderRouteKind.OpenAIDirect:
            {
                // The person's own OpenAI key: the multipart upload the backend would have built,
                // sent to OpenAI itself. The answer is the same `{ "text": … }`.
                var openAIKey = ProviderRouter.CurrentCredentials().OpenAIKey
                    ?? throw new TranscriptionProviderException(ProviderRouter.NeedsOpenAIKeySentence);
                var request = DirectProviderRequests.OpenAITranscription(openAIKey, wavAudioData, language, prompt);
                response = await HttpClients.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
                break;
            }
            case ProviderRouteKind.Unavailable:
                throw new TranscriptionProviderException(route.Sentence ?? ProviderRouter.NeedsOpenAIKeySentence);
            default:
            {
                if (!Uri.TryCreate(TranscriptionUrl, UriKind.Absolute, out var transcriptionUri))
                    throw new TranscriptionProviderException("OpenClicky backend URL is invalid.");
                var request = new HttpRequestMessage(HttpMethod.Post, transcriptionUri)
                {
                    Content = DirectProviderRequests.JsonContent(OpenAIAudioTranscriptionProvider.BackendRequestBody(wavAudioData, language, prompt)),
                };
                OpenClickyConfiguration.Authorize(request);
                response = await BackendSignIn.SendAsync(request, sent => HttpClients.Shared.SendAsync(sent, timeout.Token)).ConfigureAwait(false);
                break;
            }
        }
        using (response)
        {
            var responseData = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (statusCode is < 200 or > 299)
            {
                var responseText = Encoding.UTF8.GetString(responseData);
                throw new TranscriptionProviderException($"OpenClicky transcription failed (HTTP {statusCode}): {responseText}");
            }
            return OpenAIAudioTranscriptionProvider.ParseTranscript(responseData)
                ?? throw new TranscriptionProviderException("OpenClicky transcription returned an empty transcript.");
        }
    }

    private void DeliverFinalTranscript(string transcriptText)
    {
        lock (stateLock)
        {
            if (hasDeliveredFinalTranscript) return;
            hasDeliveredFinalTranscript = true;
        }
        onFinalTranscriptReady(transcriptText);
    }
}
