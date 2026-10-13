// Ports SarvamTranscriptionProviders.swift: Sarvam as a transcription engine. Streaming first:
// the realtime socket hears 16 kHz audio as it is captured and answers with transcript pieces, so
// words show while the key is held and the final text is a flush away (saaras:v4, then
// saaras:v3-realtime if v4 refuses the session). Then the chunked socket. Upload last: the whole
// take as WAV after release, used when no socket opens. The wire format (URLs, messages, decoding)
// is SarvamSpeechClient's, reached through ISarvamSpeechWire.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class SarvamTranscriptionProvider : IBuddyTranscriptionProvider
{
    public string DisplayName => "Sarvam";
    public bool RequiresSpeechRecognitionPermission => false;

    /// <summary>The key from shell.json.</summary>
    private readonly Func<string?> key;
    /// <summary>The dictation language's code ("hi-IN", or "auto").</summary>
    private readonly Func<string> languageCode;
    /// <summary>The resolver's rule, so the provider and Settings never disagree about the key.
    /// TODO(integration): DictationEngineResolver.SarvamUnavailableReason(sarvamKey).</summary>
    private readonly Func<string?, string?> unavailableReason;
    private readonly Func<ISarvamSpeechWire> wire;
    private readonly Func<ISpeechWebSocket> makeSocket;

    public SarvamTranscriptionProvider(
        Func<string> languageCode,
        Func<string?>? key = null,
        Func<string?, string?>? unavailableReason = null,
        Func<ISarvamSpeechWire>? wire = null,
        Func<ISpeechWebSocket>? makeSocket = null)
    {
        this.languageCode = languageCode;
        this.key = key ?? (() => OpenClickyConfiguration.Settings.SarvamKey);
        this.unavailableReason = unavailableReason ?? (sarvamKey => string.IsNullOrWhiteSpace(sarvamKey) ? SarvamSpeechException.NoKey().Message : null);
        this.wire = wire ?? (() => SarvamSpeechWire.Required);
        this.makeSocket = makeSocket ?? (() => new ClientSpeechWebSocket());
    }

    public bool IsConfigured => UnavailableExplanation is null;

    public string? UnavailableExplanation => unavailableReason(key());

    public async Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        var trimmedKey = key()?.Trim();
        if (string.IsNullOrEmpty(trimmedKey)) throw SarvamSpeechException.NoKey();
        var sarvam = wire();
        var language = languageCode();
        // The realtime socket gives words while the key is held; the chunked socket gives them at
        // the end; the upload is the last resort. Each is tried in turn.
        foreach (var model in new[] { SarvamRealtimeModel.V4, SarvamRealtimeModel.V3 })
        {
            try
            {
                return await SarvamStreamingSession.OpenAsync(
                    sarvam, makeSocket(), trimmedKey, language, keyterms, SarvamStreamingSession.Flavour.Realtime, model,
                    onTranscriptUpdate, onFinalTranscriptReady, onError, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                AppLog.Write($"sarvam realtime socket ({(model == SarvamRealtimeModel.V4 ? "saaras:v4" : "saaras:v3-realtime")}) could not open ({error.Message}); trying the next");
            }
        }
        try
        {
            return await SarvamStreamingSession.OpenAsync(
                sarvam, makeSocket(), trimmedKey, language, keyterms, SarvamStreamingSession.Flavour.Chunked, SarvamRealtimeModel.V4,
                onTranscriptUpdate, onFinalTranscriptReady, onError, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AppLog.Write($"sarvam streaming could not open ({error.Message}); uploading the take instead");
        }
        return new SarvamUploadSession(sarvam, trimmedKey, language, keyterms, onTranscriptUpdate, onFinalTranscriptReady, onError);
    }
}

/// <summary>
/// The socket session. Audio arrives on the capture thread; the converter and the pending-chunk
/// buffer sit behind a lock, and every send goes through one ordered queue so frames keep their order.
/// </summary>
public sealed class SarvamStreamingSession : IBuddyStreamingTranscriptionSession
{
    public TimeSpan FinalTranscriptFallbackDelay => TimeSpan.FromSeconds(6);

    /// <summary>Which of Sarvam's sockets this is: the realtime one (partials, manual endpointing, raw PCM)
    /// or the chunked one (a transcript per chunk, WAV).</summary>
    public enum Flavour
    {
        Realtime,
        Chunked,
    }

    /// <summary>Frames are batched into chunks this long before they go out (a frame per message is too chatty).</summary>
    public const double ChunkDurationSeconds = 0.25;

    private readonly Flavour flavour;
    private readonly ISarvamSpeechWire sarvam;
    private readonly ISpeechWebSocket socket;
    private readonly int chunkByteCount;
    private readonly Action<string> onTranscriptUpdate;
    private readonly Action<string> onFinalTranscriptReady;
    private readonly Action<Exception> onError;
    private readonly BuddyPCM16AudioConverter converter;
    private readonly object stateLock = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly OrderedSocketSender sender;

    // Behind stateLock.
    private MemoryStream pending = new();
    private readonly List<string> pieces = [];
    /// <summary>Realtime: the words of the utterance in progress, replaced by each partial.</summary>
    private string partial = "";
    private bool hasRequestedFinal;
    private bool hasDeliveredFinal;
    private bool isClosed;
    private CancellationTokenSource? finalDelivery;

    private SarvamStreamingSession(Flavour flavour, ISarvamSpeechWire sarvam, ISpeechWebSocket socket,
                                   Action<string> onTranscriptUpdate, Action<string> onFinalTranscriptReady, Action<Exception> onError)
    {
        this.flavour = flavour;
        this.sarvam = sarvam;
        this.socket = socket;
        this.onTranscriptUpdate = onTranscriptUpdate;
        this.onFinalTranscriptReady = onFinalTranscriptReady;
        this.onError = onError;
        converter = new BuddyPCM16AudioConverter(sarvam.SampleRate);
        chunkByteCount = (int)(sarvam.SampleRate * 2 * ChunkDurationSeconds);
        sender = new OrderedSocketSender(socket, error => AppLog.Write($"sarvam stream send failed: {error.Message}"), lifetime.Token);
    }

    /// <summary>Opens the socket within 6 s or throws; the realtime socket is then told the speech started.</summary>
    public static async Task<SarvamStreamingSession> OpenAsync(
        ISarvamSpeechWire sarvam, ISpeechWebSocket socket, string key, string languageCode, IReadOnlyList<string> keyterms,
        Flavour flavour, SarvamRealtimeModel realtimeModel,
        Action<string> onTranscriptUpdate, Action<string> onFinalTranscriptReady, Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        var session = new SarvamStreamingSession(flavour, sarvam, socket, onTranscriptUpdate, onFinalTranscriptReady, onError);
        var request = flavour == Flavour.Realtime
            ? sarvam.RealtimeRequest(languageCode, keyterms, key, realtimeModel)
            : sarvam.StreamingRequest(languageCode, keyterms, key);
        using var openTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        openTimeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await socket.ConnectAsync(request.Url, request.Headers, openTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The socket may still open later; it is torn down so a late answer goes nowhere.
            session.Cancel();
            throw SarvamSpeechException.Unreachable("the streaming socket did not open in 6 s");
        }
        catch (SpeechWebSocketHandshakeException error)
        {
            session.Cancel();
            throw SarvamSpeechException.Refused(error.StatusCode ?? 0, error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            session.Cancel();
            throw SarvamSpeechException.Unreachable(error.Message);
        }
        _ = Task.Run(session.ReceiveLoopAsync);
        if (flavour == Flavour.Realtime) session.Send(sarvam.RealtimeEventMessage("speech_start"));
        return session;
    }

    private async Task ReceiveLoopAsync()
    {
        while (true)
        {
            SpeechWebSocketMessage message;
            try
            {
                message = await socket.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                bool wasClosed, hadText;
                lock (stateLock)
                {
                    wasClosed = isClosed;
                    hadText = pieces.Count > 0;
                    isClosed = true;
                }
                // After Cancel() every failure is the teardown's own; nothing is reported.
                if (wasClosed) return;
                if (hadText) DeliverFinalIfRequested();
                else onError(SarvamSpeechException.Unreachable(error.Message));
                return;
            }
            if (message.Kind == SpeechWebSocketMessageKind.Close)
            {
                bool wasClosed;
                lock (stateLock)
                {
                    wasClosed = isClosed;
                    isClosed = true;
                }
                if (wasClosed) return;
                AppLog.Write($"sarvam stream closed ({message.CloseStatus}) {message.CloseReason}");
                DeliverFinalIfRequested();
                return;
            }
            if (message.AsText is { } text) Handle(text);
            lock (stateLock)
            {
                if (isClosed) return;
            }
        }
    }

    private void Handle(string serverText)
    {
        if (flavour == Flavour.Realtime)
        {
            HandleRealtime(serverText);
            return;
        }
        var decoded = sarvam.DecodeStreamingMessage(serverText);
        switch (decoded.Kind)
        {
            case SarvamStreamingMessageKind.Transcript:
            {
                var trimmed = decoded.Text.Trim();
                if (trimmed.Length == 0) return;
                string text;
                bool requested;
                lock (stateLock)
                {
                    pieces.Add(trimmed);
                    text = string.Join(" ", pieces);
                    requested = hasRequestedFinal;
                }
                onTranscriptUpdate(text);
                // Every piece after the flush could be the last; wait briefly for a straggler.
                if (requested) ScheduleFinalDelivery(TimeSpan.FromSeconds(0.7));
                break;
            }
            case SarvamStreamingMessageKind.Error:
                AppLog.Write($"sarvam stream error: {decoded.Text}");
                onError(SarvamSpeechException.Refused(0, decoded.Text));
                break;
        }
    }

    private void HandleRealtime(string serverText)
    {
        var decoded = sarvam.DecodeRealtimeMessage(serverText);
        switch (decoded.Kind)
        {
            case SarvamRealtimeMessageKind.Partial:
            {
                string text;
                lock (stateLock)
                {
                    partial = decoded.Text;
                    text = string.Join(" ", pieces.Append(decoded.Text));
                }
                onTranscriptUpdate(text.Trim(' '));
                break;
            }
            case SarvamRealtimeMessageKind.Final:
            {
                var trimmed = decoded.Text.Trim();
                string text;
                bool requested;
                lock (stateLock)
                {
                    if (trimmed.Length > 0) pieces.Add(trimmed);
                    partial = "";
                    text = string.Join(" ", pieces);
                    requested = hasRequestedFinal;
                }
                onTranscriptUpdate(text);
                // The final after the flush is the last word; the session is ended right after.
                if (requested) ScheduleFinalDelivery(TimeSpan.FromSeconds(0.3));
                break;
            }
            case SarvamRealtimeMessageKind.SessionEnd:
                lock (stateLock) isClosed = true;
                DeliverFinalIfRequested();
                break;
            case SarvamRealtimeMessageKind.Error:
                AppLog.Write($"sarvam realtime error: {decoded.Text} (fatal: {(decoded.IsFatal ? "true" : "false")})");
                if (decoded.IsFatal) onError(SarvamSpeechException.Refused(0, decoded.Text));
                break;
        }
    }

    public void AppendAudio(AudioFrame frame)
    {
        byte[]? chunk = null;
        lock (stateLock)
        {
            if (hasRequestedFinal || isClosed) return;
            var pcm = converter.ConvertToPcm16(frame);
            if (pcm is null) return;
            pending.Write(pcm);
            if (pending.Length >= chunkByteCount)
            {
                chunk = pending.ToArray();
                pending = new MemoryStream();
            }
        }
        if (chunk is not null) Send(AudioMessage(chunk));
    }

    private string AudioMessage(byte[] pcm16) =>
        flavour == Flavour.Realtime ? sarvam.RealtimeAudioMessage(pcm16) : sarvam.StreamingAudioMessage(pcm16);

    public void RequestFinalTranscript()
    {
        byte[] remainder;
        lock (stateLock)
        {
            if (hasRequestedFinal) return;
            hasRequestedFinal = true;
            remainder = pending.ToArray();
            pending = new MemoryStream();
        }
        if (remainder.Length > 0) Send(AudioMessage(remainder));
        if (flavour == Flavour.Realtime)
        {
            Send(sarvam.RealtimeEventMessage("speech_end"));
            Send(sarvam.RealtimeEventMessage("flush"));
        }
        else
        {
            Send(sarvam.StreamingFlushMessage);
        }
        // The flushed pieces normally arrive within a second; the manager's own fallback covers more.
        ScheduleFinalDelivery(TimeSpan.FromSeconds(2.5));
    }

    public void Cancel()
    {
        lock (stateLock)
        {
            isClosed = true;
            hasDeliveredFinal = true;
            finalDelivery?.Cancel();
            finalDelivery = null;
        }
        lifetime.Cancel();
        _ = socket.CloseAsync();
    }

    private void Send(string text) => sender.SendText(text);

    private void ScheduleFinalDelivery(TimeSpan delay)
    {
        CancellationTokenSource work;
        lock (stateLock)
        {
            finalDelivery?.Cancel();
            work = new CancellationTokenSource();
            finalDelivery = work;
        }
        _ = Task.Delay(delay, work.Token).ContinueWith(delayTask =>
        {
            if (!delayTask.IsCanceled) DeliverFinalIfRequested();
        }, TaskScheduler.Default);
    }

    private void DeliverFinalIfRequested()
    {
        string text;
        lock (stateLock)
        {
            if (!hasRequestedFinal || hasDeliveredFinal) return;
            hasDeliveredFinal = true;
            // A partial that never got its final still counts: those words were heard.
            text = string.Join(" ", partial.Length == 0 ? pieces : pieces.Append(partial));
        }
        if (flavour == Flavour.Realtime) Send(sarvam.RealtimeEventMessage("end"));
        _ = Task.Run(async () =>
        {
            await Task.Delay(200).ConfigureAwait(false);
            lifetime.Cancel();
            await socket.CloseAsync().ConfigureAwait(false);
        });
        onFinalTranscriptReady(text.Trim());
    }
}

/// <summary>The whole take, uploaded once after release; nothing shows while the key is held.</summary>
public sealed class SarvamUploadSession(
    ISarvamSpeechWire sarvam,
    string key,
    string languageCode,
    IReadOnlyList<string> keyterms,
    Action<string> onTranscriptUpdate,
    Action<string> onFinalTranscriptReady,
    Action<Exception> onError) : IBuddyStreamingTranscriptionSession
{
    public TimeSpan FinalTranscriptFallbackDelay => TimeSpan.FromSeconds(20);

    /// <summary>Saaras takes about thirty seconds a request; longer takes go up in pieces, in order.</summary>
    public const double PieceSeconds = 28.0;

    private readonly object stateLock = new();
    private readonly BuddyPCM16AudioConverter converter = new(sarvam.SampleRate);
    private readonly CancellationTokenSource uploadCancellation = new();
    private MemoryStream pcm = new();
    private short peak;
    private bool hasRequestedFinal;
    private bool isCancelled;

    public void AppendAudio(AudioFrame frame)
    {
        var converted = converter.ConvertToPcm16(frame);
        if (converted is null) return;
        var framePeak = PCM16SilenceGate.PeakMagnitude(converted);
        lock (stateLock)
        {
            if (hasRequestedFinal) return;
            pcm.Write(converted);
            peak = Math.Max(peak, framePeak);
        }
    }

    public void RequestFinalTranscript()
    {
        byte[] captured;
        short capturedPeak;
        lock (stateLock)
        {
            if (hasRequestedFinal) return;
            hasRequestedFinal = true;
            captured = pcm.ToArray();
            capturedPeak = peak;
        }
        if (PCM16SilenceGate.IsSilent(capturedPeak) || captured.Length == 0)
        {
            onFinalTranscriptReady("");
            return;
        }
        var pieces = Split(captured, sarvam.SampleRate, PieceSeconds);
        var cancellationToken = uploadCancellation.Token;
        _ = Task.Run(async () =>
        {
            var texts = new List<string>();
            foreach (var piece in pieces)
            {
                if (cancellationToken.IsCancellationRequested) return;
                try
                {
                    var wav = BuddyWAVFileBuilder.BuildWavData(piece, sarvam.SampleRate);
                    var text = await sarvam.TranscribeAsync(key, wav, languageCode, keyterms, cancellationToken).ConfigureAwait(false);
                    if (text.Length > 0) texts.Add(text);
                    onTranscriptUpdate(string.Join(" ", texts));
                }
                catch (Exception error)
                {
                    if (IsCancelled) return;
                    onError(error);
                    return;
                }
            }
            if (IsCancelled) return;
            onFinalTranscriptReady(string.Join(" ", texts));
        });
    }

    private bool IsCancelled
    {
        get { lock (stateLock) return isCancelled; }
    }

    public void Cancel()
    {
        lock (stateLock) isCancelled = true;
        uploadCancellation.Cancel();
    }

    /// <summary>Whole pieces of <paramref name="pieceSeconds"/>, the last one shorter. Pure, for the test.</summary>
    public static IReadOnlyList<byte[]> Split(byte[] pcm16, int sampleRate, double pieceSeconds)
    {
        var pieceBytes = Math.Max(2, (int)(sampleRate * 2 * pieceSeconds));
        var pieces = new List<byte[]>();
        var offset = 0;
        while (offset < pcm16.Length)
        {
            var end = Math.Min(offset + pieceBytes, pcm16.Length);
            pieces.Add(pcm16[offset..end]);
            offset = end;
        }
        return pieces;
    }
}
