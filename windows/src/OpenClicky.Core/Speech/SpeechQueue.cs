// Ports SpeechQueue.swift: speaks an answer piece by piece while the rest is still being written.
// A piece's audio is requested up to two pieces ahead of the one playing, so the next piece is
// usually ready by the time the current one finishes, and an interrupted answer has bought at most
// two pieces nobody hears; playback always follows the order of the text. The fetch and play steps
// are injected, so the ordering, the first-audio signal and the failure hand-off are tested
// without the network or a speaker.
//
// The Swift class lives on the main actor; this one is safe to call from any thread: its state
// sits behind a lock, and the run loop waits on a TaskCompletionSource that enqueue/finish/cancel
// complete.

using OpenClicky.Core.Answers;

namespace OpenClicky.Core.Speech;

/// <summary>A piece's audio could not be fetched or played. <see cref="UnspokenText"/> is that piece and every one
/// after it (including pieces enqueued later), so the caller can say them another way.</summary>
public sealed class SpeechQueueFailure(IReadOnlyList<string> unspokenText, Exception underlyingError)
    : Exception($"the voice failed: {underlyingError.Message}", underlyingError)
{
    public IReadOnlyList<string> UnspokenText { get; } = unspokenText;
    public Exception UnderlyingError { get; } = underlyingError;
}

public sealed class SpeechQueue
{
    private const int PiecesFetchedAhead = 2;

    private readonly Func<string, CancellationToken, Task<byte[]>> fetchAudio;
    private readonly Func<byte[], CancellationToken, Task> playAudio;
    private readonly object stateLock = new();

    private readonly List<string> pieceTexts = [];
    /// <summary>The fetches started so far, in piece order (a prefix of <see cref="pieceTexts"/>).</summary>
    private readonly List<Task<byte[]>> pieceFetches = [];
    private readonly CancellationTokenSource fetchCancellation = new();
    /// <summary>The piece the run loop is waiting for or playing; fetches reach <see cref="PiecesFetchedAhead"/> beyond it.</summary>
    private int currentPieceIndex;
    private bool isFinished;
    /// <summary>Set by <see cref="Cancel"/>: nothing more is played, even when the current piece ends normally
    /// (a stopped player finishes its piece as if it had played to the end).</summary>
    private bool isCancelled;
    /// <summary>After a failure, later pieces are only collected (for the caller's fallback), never fetched.</summary>
    private bool hasFailed;
    /// <summary>Completed when a piece is enqueued or the queue is finished, so the run loop can stop waiting.</summary>
    private TaskCompletionSource? waitingForMorePieces;

    public SpeechQueue(Func<string, CancellationToken, Task<byte[]>> fetchAudio, Func<byte[], CancellationToken, Task> playAudio)
    {
        this.fetchAudio = fetchAudio;
        this.playAudio = playAudio;
    }

    public void Enqueue(string pieceText)
    {
        lock (stateLock)
        {
            if (isFinished) return;
            pieceTexts.Add(pieceText);
            StartFetchesWithinReach();
        }
        WakeRunLoop();
    }

    /// <summary>No more pieces will come: <see cref="RunAsync"/> returns once the last one has played.</summary>
    public void Finish()
    {
        lock (stateLock) isFinished = true;
        WakeRunLoop();
    }

    /// <summary>
    /// Plays every piece in order until <see cref="Finish"/> and the last piece are done. <paramref name="onFirstAudio"/>
    /// runs once, right before the first piece starts playing. Throws OperationCanceledException when
    /// interrupted and <see cref="SpeechQueueFailure"/> when the voice failed.
    /// </summary>
    public async Task RunAsync(Action onFirstAudio, CancellationToken cancellationToken = default)
    {
        try
        {
            var nextPieceIndex = 0;
            while (true)
            {
                CheckNotCancelled(cancellationToken);
                Task<byte[]>? fetch = null;
                Task? wait = null;
                lock (stateLock)
                {
                    if (nextPieceIndex < pieceTexts.Count)
                    {
                        currentPieceIndex = nextPieceIndex;
                        StartFetchesWithinReach();
                        fetch = pieceFetches[nextPieceIndex];
                    }
                    else if (isFinished)
                    {
                        return;
                    }
                    else
                    {
                        waitingForMorePieces = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        wait = waitingForMorePieces.Task;
                    }
                }
                if (wait is not null)
                {
                    await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    var audioData = await fetch!.WaitAsync(cancellationToken).ConfigureAwait(false);
                    CheckNotCancelled(cancellationToken);
                    if (nextPieceIndex == 0) onFirstAudio();
                    await playAudio(audioData, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (IsInterrupted(cancellationToken))
                {
                    // A fetch cancelled by Cancel() fails with its own cancellation, not the caller's:
                    // that is an interruption, not a voice failure to fall back from.
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    // Wait for the rest of the answer, then hand back everything not yet spoken.
                    lock (stateLock) hasFailed = true;
                    CancelPendingFetches();
                    while (true)
                    {
                        Task? waitForFinish;
                        lock (stateLock)
                        {
                            if (isFinished) break;
                            waitingForMorePieces = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            waitForFinish = waitingForMorePieces.Task;
                        }
                        await waitForFinish.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    List<string> unspoken;
                    lock (stateLock) unspoken = pieceTexts.Skip(nextPieceIndex).ToList();
                    throw new SpeechQueueFailure(unspoken, error);
                }
                nextPieceIndex++;
            }
        }
        finally
        {
            // However playback ends (done, failed, interrupted), no fetch is left running.
            CancelPendingFetches();
        }
    }

    /// <summary>Stops fetching and playing: used when the person speaks again or the answer is abandoned.</summary>
    public void Cancel()
    {
        lock (stateLock)
        {
            isFinished = true;
            isCancelled = true;
        }
        CancelPendingFetches();
        WakeRunLoop();
    }

    private bool IsInterrupted(CancellationToken cancellationToken)
    {
        lock (stateLock) return isCancelled || cancellationToken.IsCancellationRequested;
    }

    private void CheckNotCancelled(CancellationToken cancellationToken)
    {
        lock (stateLock)
        {
            if (isCancelled) throw new OperationCanceledException();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Starts the fetches for every enqueued piece up to <see cref="PiecesFetchedAhead"/> beyond the current one. Under the lock.</summary>
    private void StartFetchesWithinReach()
    {
        if (hasFailed || isCancelled || fetchCancellation.IsCancellationRequested) return;
        var lastPieceIndexWithinReach = Math.Min(pieceTexts.Count - 1, currentPieceIndex + PiecesFetchedAhead);
        var token = fetchCancellation.Token;
        while (pieceFetches.Count <= lastPieceIndexWithinReach)
        {
            var pieceText = pieceTexts[pieceFetches.Count];
            // At most two requests at once (ElevenLabs caps concurrent requests on smaller plans): a
            // piece waits for the fetch two places ahead of it before starting its own.
            var fetchTwoPlacesAhead = pieceFetches.Count >= 2 ? pieceFetches[^2] : null;
            pieceFetches.Add(Task.Run(async () =>
            {
                if (fetchTwoPlacesAhead is not null)
                {
                    try { await fetchTwoPlacesAhead.ConfigureAwait(false); }
                    catch (Exception) { }
                }
                token.ThrowIfCancellationRequested();
                return await fetchAudio(pieceText, token).ConfigureAwait(false);
            }, CancellationToken.None));
        }
    }

    private void CancelPendingFetches()
    {
        try { fetchCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        lock (stateLock)
        {
            // Unobserved failures of fetches nobody will wait for are expected; observe them.
            foreach (var fetch in pieceFetches) _ = fetch.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private void WakeRunLoop()
    {
        TaskCompletionSource? waiting;
        lock (stateLock)
        {
            waiting = waitingForMorePieces;
            waitingForMorePieces = null;
        }
        waiting?.TrySetResult();
    }
}

/// <summary>
/// One ask-lane answer being spoken as it streams: the chunker cuts the text, the queue fetches and
/// plays each piece, and the pointer is moved when the voice starts (or, when the tag only shows up
/// at the end, as soon as it is known).
/// </summary>
public sealed class StreamingAnswerSpeaker
{
    private readonly object stateLock = new();
    private readonly SpokenChunker chunker = new();
    private readonly SpeechQueue speechQueue;
    private readonly Action onVoiceStarts;
    private readonly Action<string> onPointTag;
    private bool hasVoiceStarted;
    private bool hasHandledPointTag;
    private int enqueuedPieceCount;
    private bool isCancelled;

    public StreamingAnswerSpeaker(SpeechQueue speechQueue, Action onVoiceStarts, Action<string> onPointTag)
    {
        this.speechQueue = speechQueue;
        this.onVoiceStarts = onVoiceStarts;
        this.onPointTag = onPointTag;
    }

    /// <summary>A new question interrupted this answer: its voice must not start and its pointer must not fly.</summary>
    public bool IsCancelled
    {
        get { lock (stateLock) return isCancelled; }
    }

    /// <summary>Plays the pieces as they come; throws <see cref="SpeechQueueFailure"/> with what was left unsaid.</summary>
    public Task PlayAsync(CancellationToken cancellationToken = default) => speechQueue.RunAsync(VoiceDidStart, cancellationToken);

    /// <summary>The whole answer so far, from the stream.</summary>
    public void Receive(string accumulatedAnswer)
    {
        IReadOnlyList<string> pieces;
        lock (stateLock) pieces = chunker.Feed(accumulatedAnswer);
        foreach (var piece in pieces) Enqueue(piece);
    }

    /// <summary>The stream is over. <paramref name="closingSentences"/> (what a quick action did) are spoken after the answer.</summary>
    public void Finish(IEnumerable<string> closingSentences)
    {
        IReadOnlyList<string> pieces;
        lock (stateLock) pieces = chunker.Finish();
        foreach (var piece in pieces) Enqueue(piece);
        foreach (var sentence in closingSentences.Where(sentence => sentence.Length > 0)) Enqueue(sentence);
        speechQueue.Finish();
        bool handleNow;
        lock (stateLock) handleNow = hasVoiceStarted || enqueuedPieceCount == 0;
        // A trailing tag is only known now; with nothing to say there is no voice to wait for.
        if (handleNow) HandlePointTagIfKnown();
    }

    /// <summary>
    /// A reply that is already whole (the agent's result): handed over in one go and closed, so the
    /// chunker cuts it into pieces exactly as it would a streamed answer and the queue fetches each
    /// piece while the one before it plays, instead of one long request for the whole reply.
    /// </summary>
    public void ReceiveWholeReply(string replyText)
    {
        Receive(replyText);
        Finish([]);
    }

    /// <summary>The voice could not be used: the caller speaks another way and points now.</summary>
    public void VoiceFellBack() => HandlePointTagIfKnown();

    public void Cancel()
    {
        lock (stateLock) isCancelled = true;
        speechQueue.Cancel();
    }

    private void Enqueue(string pieceText)
    {
        lock (stateLock) enqueuedPieceCount++;
        speechQueue.Enqueue(pieceText);
    }

    private void VoiceDidStart()
    {
        lock (stateLock)
        {
            if (isCancelled) return;
            hasVoiceStarted = true;
        }
        onVoiceStarts();
        HandlePointTagIfKnown();
    }

    private void HandlePointTagIfKnown()
    {
        string? pointTag;
        lock (stateLock)
        {
            if (isCancelled || hasHandledPointTag || chunker.PointTag is null) return;
            hasHandledPointTag = true;
            pointTag = chunker.PointTag;
        }
        onPointTag(pointTag);
    }
}
