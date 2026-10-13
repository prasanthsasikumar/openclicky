// Ports Parakeet/ParakeetTranscriptionProvider.swift. Parakeet is not a streaming model, so the
// take is collected as 16 kHz PCM16 and, while the key is held, the audio so far is read again
// about once a second for the orb's live words; on release the whole take is read once more for
// the final words. Live words stop once a take passes 30 seconds, since every live read re-reads
// the whole take; the final read is unaffected.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class ParakeetTranscriptionProvider : IBuddyTranscriptionProvider
{
    private readonly Func<ParakeetDictation?> dictation;
    private readonly Func<string?> unavailableReason;

    /// <param name="unavailableReason">The resolver's rule (downloaded, and a language Parakeet hears), so the
    /// provider and the island never disagree. TODO(integration): DictationEngineResolver.ParakeetUnavailableReason(
    /// ParakeetDictation.IsInstalled, DictationSettings.StoredLanguage().BareCode).</param>
    public ParakeetTranscriptionProvider(Func<string?>? unavailableReason = null, Func<ParakeetDictation?>? dictation = null)
    {
        this.dictation = dictation ?? (() => ParakeetDictation.Shared);
        this.unavailableReason = unavailableReason ?? (() => ParakeetDictation.IsInstalled ? null : ParakeetException.NotInstalledMessage);
    }

    public string DisplayName => "Parakeet (on this PC)";
    public bool RequiresSpeechRecognitionPermission => false;
    public bool IsConfigured => UnavailableExplanation is null;
    public string? UnavailableExplanation => unavailableReason();

    public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        if (!ParakeetDictation.IsInstalled || dictation() is not { } parakeet)
            throw new ParakeetException(ParakeetException.NotInstalledMessage);
        parakeet.PrepareForTake();
        IBuddyStreamingTranscriptionSession session = new ParakeetTranscriptionSession(
            onTranscriptUpdate, onFinalTranscriptReady, onError, parakeet.TranscribeAsync);
        return Task.FromResult(session);
    }
}

/// <summary>Audio arrives on the capture thread: the converter is touched only there, everything else under the lock.</summary>
public sealed class ParakeetTranscriptionSession : IBuddyStreamingTranscriptionSession
{
    /// <summary>A cold start on an older PC loads the model (a few seconds) before it reads the take.</summary>
    public TimeSpan FinalTranscriptFallbackDelay => TimeSpan.FromSeconds(20);

    private const int SampleRate = 16_000;
    /// <summary>How often the audio so far is read again for the live words.</summary>
    private static readonly TimeSpan PartialInterval = TimeSpan.FromSeconds(1);
    /// <summary>New audio needed before another live read is worth it: half a second.</summary>
    private const int PartialMinimumNewBytes = SampleRate;
    /// <summary>Past this much audio the live words stop: each live read re-reads the whole take.</summary>
    public const int LiveWordsAudioLimitSeconds = 30;
    public const int LiveWordsAudioLimitBytes = SampleRate * 2 * LiveWordsAudioLimitSeconds;

    private readonly Action<string> onTranscriptUpdate;
    private readonly Action<string> onFinalTranscriptReady;
    private readonly Action<Exception> onError;
    private readonly Func<byte[], CancellationToken, Task<string>> transcribe;
    private readonly BuddyPCM16AudioConverter converter = new(SampleRate);
    private readonly object stateLock = new();
    private readonly MemoryStream pcm16 = new();
    private int bytesAtLastPartial;
    private bool hasRequestedFinal;
    private bool isCancelled;
    private readonly CancellationTokenSource partialLoopCancellation = new();

    public ParakeetTranscriptionSession(
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        Func<byte[], CancellationToken, Task<string>> transcribe)
    {
        this.onTranscriptUpdate = onTranscriptUpdate;
        this.onFinalTranscriptReady = onFinalTranscriptReady;
        this.onError = onError;
        this.transcribe = transcribe;
        // The loop holds the session only weakly, and strongly only for the length of one live
        // read: a session nobody uses any more goes away instead of being kept alive by its own
        // loop until a cancel that may never come.
        _ = LiveWordsLoopAsync(new WeakReference<ParakeetTranscriptionSession>(this), partialLoopCancellation.Token);
    }

    ~ParakeetTranscriptionSession()
    {
        try { partialLoopCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static async Task LiveWordsLoopAsync(WeakReference<ParakeetTranscriptionSession> weakSession, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(PartialInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            // In its own method so the loop's state machine never keeps a strong reference between reads.
            if (await ReadIfAliveAsync(weakSession, cancellationToken).ConfigureAwait(false) != LiveReadOutcome.KeepReading) return;
        }
    }

    /// <summary>One live read on the session if it still exists; null when it is gone.</summary>
    private static async Task<LiveReadOutcome?> ReadIfAliveAsync(WeakReference<ParakeetTranscriptionSession> weakSession, CancellationToken cancellationToken)
    {
        if (!weakSession.TryGetTarget(out var session)) return null;
        return await session.ReadLiveWordsOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    public void AppendAudio(AudioFrame frame)
    {
        var converted = converter.ConvertToPcm16(frame);
        if (converted is { Length: > 0 }) AppendPcm16(converted);
    }

    /// <summary>The take's audio, already 16 kHz PCM16 (the converter's output; tests feed it directly).</summary>
    public void AppendPcm16(byte[] converted)
    {
        lock (stateLock)
        {
            if (hasRequestedFinal || isCancelled) return;
            pcm16.Write(converted, 0, converted.Length);
        }
    }

    public void RequestFinalTranscript()
    {
        byte[] audio;
        lock (stateLock)
        {
            if (hasRequestedFinal || isCancelled) return;
            hasRequestedFinal = true;
            audio = pcm16.ToArray();
        }
        partialLoopCancellation.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                var text = audio.Length == 0 ? "" : await transcribe(audio, CancellationToken.None).ConfigureAwait(false);
                if (!IsCancelled) onFinalTranscriptReady(text);
            }
            catch (Exception error)
            {
                AppLog.Write($"transcription (parakeet): {error.Message}");
                if (!IsCancelled) onError(error);
            }
        });
    }

    public void Cancel()
    {
        lock (stateLock)
        {
            isCancelled = true;
            pcm16.SetLength(0);
        }
        partialLoopCancellation.Cancel();
    }

    private bool IsCancelled
    {
        get { lock (stateLock) return isCancelled; }
    }

    private enum LiveReadOutcome { KeepReading, StopReading }

    /// <summary>One live read of the audio so far, when enough new audio arrived. A read still running when
    /// the next is due simply delays it (the helper answers one request at a time).</summary>
    private async Task<LiveReadOutcome> ReadLiveWordsOnceAsync(CancellationToken cancellationToken)
    {
        byte[] audioSoFar;
        lock (stateLock)
        {
            if (pcm16.Length > LiveWordsAudioLimitBytes)
            {
                AppLog.Write($"transcription (parakeet): live words stop past {LiveWordsAudioLimitSeconds} s; the whole take is read on release");
                return LiveReadOutcome.StopReading;
            }
            if (hasRequestedFinal || isCancelled || pcm16.Length - bytesAtLastPartial < PartialMinimumNewBytes) return LiveReadOutcome.KeepReading;
            bytesAtLastPartial = (int)pcm16.Length;
            audioSoFar = pcm16.ToArray();
        }
        string text;
        try
        {
            text = await transcribe(audioSoFar, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return LiveReadOutcome.KeepReading;
        }
        if (cancellationToken.IsCancellationRequested) return LiveReadOutcome.KeepReading;
        bool stillListening;
        lock (stateLock) stillListening = !hasRequestedFinal && !isCancelled;
        if (stillListening && text.Length > 0) onTranscriptUpdate(text);
        return LiveReadOutcome.KeepReading;
    }
}
