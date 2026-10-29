// Ports AnswerVoicePlayer.swift: fetches the audio for one piece of a spoken answer and plays it.
// The voice is the one picked in Settings → Clicky → Voice (SpeechVoiceChoice): a key's voice
// straight from its vendor, Kokoro or this PC through CloudVoiceSpeaker, and only the account's free
// voice from the OpenClicky backend's /tts (ElevenLabs). Each piece is fetched whole, not streamed;
// SpeechQueue overlaps fetching the next piece with playing the current one, which is what makes
// the voice start early.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

public sealed class AnswerVoicePlayer(IAudioPlayer audioPlayer, Func<string> currentProxyUrl, SpeechVoiceServices? services = null)
{
    /// <summary>The URLSession timeouts the Mac set: a request may take 30 s, the whole download 60 s.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ResourceTimeout = TimeSpan.FromSeconds(60);

    private readonly object voiceLogLock = new();
    /// <summary>The voice last logged, so app.log says when it changes rather than for every sentence.</summary>
    private SpeechVoiceChoice? lastLoggedVoice;

    /// <summary>The /tts URL, read for every request so a backend changed in shell.json while the app runs is used by the next answer.</summary>
    public Uri ProxyUrl => ResolvedProxyUrl(currentProxyUrl());

    /// <summary>The configured /tts URL, or the hosted backend's own when it is malformed.</summary>
    public static Uri ResolvedProxyUrl(string proxyUrl) =>
        Uri.TryCreate(proxyUrl, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url
            : new Uri($"{OpenClickyConfiguration.HostedBackendUrl}/tts");

    /// <summary>The voice that speaks now; injectable for tests.</summary>
    public Func<SpeechVoiceChoice> CurrentVoice { get; init; } = () => SpeechVoiceChoices.Current(services);

    /// <summary>
    /// The voice for one answer, in the voice <see cref="CurrentVoice"/> names when the answer starts. The account's free
    /// voice comes from the backend's /tts behind AnswerVoiceSession's deadlines: a late or failed piece, and the rest of
    /// that answer, are spoken by the fastest local voice (#23). Its AudioAsync is SpeechQueue's fetch step.
    /// </summary>
    public AnswerVoiceSession MakeAnswerSession()
    {
        var voice = CurrentVoice();
        lock (voiceLogLock)
        {
            if (voice != lastLoggedVoice)
            {
                AppLog.Write($"voice: {voice.RawValue()}");
                lastLoggedVoice = voice;
            }
        }
        var speaksWithAccountVoice = voice == SpeechVoiceChoice.Account;
        var fallbackVoice = speaksWithAccountVoice
            ? SpeechVoiceChoices.FastestLocalVoice((services ?? SpeechVoiceServices.Current)?.KokoroIsInstalled() ?? false, ReplyLanguage.CurrentCode)
            : voice;
        return new AnswerVoiceSession(
            speaksWithAccountVoice,
            FetchAccountAudioAsync,
            (pieceText, cancellationToken) => CloudVoiceSpeaker.AudioAsync(pieceText, fallbackVoice, services: services, cancellationToken: cancellationToken),
            onFirstAccountAudio: waited => AppLog.Write($"voice: account first sound in {waited.TotalSeconds:0.0} s"),
            onAccountTrouble: trouble =>
            {
                switch (trouble)
                {
                    case AnswerVoiceSession.AccountTrouble.TooSlow tooSlow:
                        AppLog.Write($"voice: account piece {tooSlow.PieceNumber + 1} not back in {tooSlow.Waited.TotalSeconds:0.0} s; {fallbackVoice.RawValue()} speaks the rest");
                        break;
                    case AnswerVoiceSession.AccountTrouble.Failed failed:
                        // 402 is the account's voice allowance (tts_budget): the plan, not a failure.
                        var isVoiceAllowanceSpent = failed.Error is BackendHttpException { Service: BackendHttpException.ServiceKind.Voice, Status: 402 };
                        if (!isVoiceAllowanceSpent) ClickyAnalytics.TrackTTSError(failed.Error.Message);
                        AppLog.Write($"voice: account piece {failed.PieceNumber + 1} failed ({failed.Error.GetType().Name}); {fallbackVoice.RawValue()} speaks the rest");
                        break;
                }
            },
            onAnswerOnTime: AccountVoiceRest.RecordAnswerOnTime,
            onTroubledAnswer: AccountVoiceRest.RecordTroubledAnswer);
    }

    /// <summary>
    /// The audio for <paramref name="text"/> as a one-piece answer, without playing it, in the voice <see cref="CurrentVoice"/>
    /// names (a pointing caption, the voice preview).
    /// </summary>
    public Task<byte[]> FetchAudioAsync(string text, CancellationToken cancellationToken = default) =>
        MakeAnswerSession().AudioAsync(text, cancellationToken);

    /// <summary>One piece from the backend's /tts, the account's free ElevenLabs voice.</summary>
    private async Task<byte[]> FetchAccountAudioAsync(string text, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResourceTimeout);
        var request = new HttpRequestMessage(HttpMethod.Post, ProxyUrl);
        request.SetHeader("Accept", "audio/mpeg");
        OpenClickyConfiguration.Authorize(request);
        // The backend relays this body to ElevenLabs as it is, the same body an own ElevenLabs key sends.
        request.Content = DirectProviderRequests.JsonContent(CloudVoiceRequests.ElevenLabsSpeechBody(text));
        using var response = await BackendSignIn.SendAsync(request, sent => HttpClients.Shared.SendAsync(sent, timeout.Token)).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300) throw new BackendHttpException(BackendHttpException.ServiceKind.Voice, status, data);
        return data;
    }

    /// <summary>Plays <paramref name="audio"/> and returns when it has finished (or was stopped). Cancelling stops the audio.</summary>
    public async Task PlayToCompletionAsync(byte[] audio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await audioPlayer.PlayToCompletionAsync(audio, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Whether answer audio is playing.</summary>
    public bool IsPlaying => audioPlayer.IsPlaying;

    /// <summary>Stops any playback at once.</summary>
    public void StopPlayback() => audioPlayer.StopPlayback();
}
