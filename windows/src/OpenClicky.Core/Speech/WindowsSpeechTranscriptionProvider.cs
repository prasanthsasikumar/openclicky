// Ports AppleSpeechTranscriptionProvider.swift: the offline engine. On the Mac it is Apple's
// recogniser (SpeechAnalyzer on macOS 26, SFSpeechRecognizer before); here it is the Windows
// speech recogniser (Windows.Media.SpeechRecognition continuous dictation, IOfflineSpeechRecognizer).
// It listens in the dictation language when one is pinned, else the reply language, else en-US.
//
// The Windows recogniser listens to the default microphone itself and cannot be fed our audio,
// so AppendAudio is ignored (the manager still captures for the level meter and the retained
// take). Like SFSpeechRecognizer it closes a stretch at each pause and starts the next from
// nothing; the platform session joins the stretches with SpokenStretches' rule.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class WindowsSpeechTranscriptionProvider(IOfflineSpeechRecognizer recognizer, Func<string?>? preferredLanguageTag = null) : IBuddyTranscriptionProvider
{
    public string DisplayName => "Windows Speech";
    public bool RequiresSpeechRecognitionPermission => true;
    public bool IsConfigured => true;
    public string? UnavailableExplanation => null;

    /// <summary>Dictation's pinned language (Settings → engine) when one is chosen; else the reply language.</summary>
    private readonly Func<string?> preferredLanguageTag = preferredLanguageTag ?? (() => null);

    /// <summary>The languages to try, in order: the pinned one, the reply language in this PC's region, the bare reply language, en-US.</summary>
    public static IReadOnlyList<string> LanguageTagsToTry(string? preferredLanguageTag, string recognitionLocale, string replyLanguageCode)
    {
        var tags = new List<string>();
        foreach (var tag in new[] { preferredLanguageTag, recognitionLocale, replyLanguageCode, "en-US" })
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        }
        return tags;
    }

    public async Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        var tags = LanguageTagsToTry(preferredLanguageTag(), ReplyLanguage.RecognitionLocale, ReplyLanguage.CurrentCode);
        IOfflineSpeechSession speechSession;
        try
        {
            speechSession = await recognizer.StartAsync(new OfflineSpeechOptions
            {
                LanguageTag = tags[0],
                FallbackLanguageTags = tags.Skip(1).ToList(),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OfflineSpeechException error)
        {
            AppLog.Write($"transcription (this pc): could not start ({error.Failure})");
            throw new TranscriptionProviderException(error.UserFacingSentence, error);
        }
        return new WindowsSpeechTranscriptionSession(speechSession, onTranscriptUpdate, onFinalTranscriptReady, onError);
    }
}

internal sealed class WindowsSpeechTranscriptionSession : IBuddyStreamingTranscriptionSession
{
    /// <summary>The Mac's SpeechAnalyzer waited 2.5 s (SFSpeechRecognizer 1.8 s); the Windows recogniser
    /// takes about a second to stop continuous dictation, so it gets the longer of the two.</summary>
    public TimeSpan FinalTranscriptFallbackDelay => TimeSpan.FromSeconds(2.5);

    private readonly IOfflineSpeechSession speechSession;
    private readonly Action<string> onTranscriptUpdate;
    private readonly Action<string> onFinalTranscriptReady;
    private readonly Action<Exception> onError;
    private readonly object stateLock = new();
    private string latestRecognizedText = "";
    private bool hasRequestedFinalTranscript;
    private bool hasDeliveredFinalTranscript;
    private bool isCancelled;

    public WindowsSpeechTranscriptionSession(IOfflineSpeechSession speechSession, Action<string> onTranscriptUpdate,
                                             Action<string> onFinalTranscriptReady, Action<Exception> onError)
    {
        this.speechSession = speechSession;
        this.onTranscriptUpdate = onTranscriptUpdate;
        this.onFinalTranscriptReady = onFinalTranscriptReady;
        this.onError = onError;
        speechSession.TranscriptUpdated += HandleTranscriptUpdated;
        speechSession.Error += HandleError;
    }

    private void HandleTranscriptUpdated(string text)
    {
        lock (stateLock)
        {
            if (isCancelled) return;
            latestRecognizedText = text;
        }
        onTranscriptUpdate(text);
    }

    private void HandleError(Exception error)
    {
        bool deliver;
        string text;
        lock (stateLock)
        {
            if (isCancelled || hasDeliveredFinalTranscript) return;
            text = latestRecognizedText;
            deliver = hasRequestedFinalTranscript && text.Trim().Length > 0;
        }
        if (deliver) DeliverFinalTranscriptIfNeeded(text);
        else onError(error);
    }

    public void AppendAudio(AudioFrame frame)
    {
        // The Windows recogniser hears the default microphone itself.
    }

    public void RequestFinalTranscript()
    {
        lock (stateLock)
        {
            if (hasRequestedFinalTranscript || isCancelled) return;
            hasRequestedFinalTranscript = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var finalText = await speechSession.StopAsync().ConfigureAwait(false);
                string best;
                lock (stateLock) best = finalText.Trim().Length > 0 ? finalText : latestRecognizedText;
                DeliverFinalTranscriptIfNeeded(best);
            }
            catch (Exception error)
            {
                HandleError(error);
            }
        });
    }

    public void Cancel()
    {
        lock (stateLock)
        {
            if (isCancelled) return;
            isCancelled = true;
        }
        speechSession.TranscriptUpdated -= HandleTranscriptUpdated;
        speechSession.Error -= HandleError;
        try { speechSession.Cancel(); }
        catch (Exception error) { AppLog.Write($"transcription (this pc): cancel failed: {error.Message}"); }
        _ = speechSession.DisposeAsync().AsTask();
    }

    private void DeliverFinalTranscriptIfNeeded(string transcriptText)
    {
        lock (stateLock)
        {
            if (hasDeliveredFinalTranscript || isCancelled) return;
            hasDeliveredFinalTranscript = true;
        }
        onFinalTranscriptReady(transcriptText.Trim());
    }
}

/// <summary>Joining the recogniser's stretches into one take.</summary>
public static class SpokenStretches
{
    private const string SentenceEndings = ".!?,;:…";

    /// <summary>
    /// <paramref name="finished"/> then <paramref name="current"/>. A stretch ends on a pause, and the
    /// recogniser often leaves its full stop off; one goes in when the next stretch starts with a
    /// capital, as a new sentence.
    /// </summary>
    public static string Joined(string finished, string current)
    {
        var finishedTrimmed = finished.Trim();
        var currentTrimmed = current.Trim();
        if (finishedTrimmed.Length == 0) return currentTrimmed;
        if (currentTrimmed.Length == 0) return finishedTrimmed;
        var finishedEndsASentence = SentenceEndings.Contains(finishedTrimmed[^1]);
        var currentStartsASentence = char.IsUpper(currentTrimmed[0]);
        var separator = !finishedEndsASentence && currentStartsASentence ? ". " : " ";
        return finishedTrimmed + separator + currentTrimmed;
    }
}
