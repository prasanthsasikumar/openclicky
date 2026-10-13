// Ports SpeechQueueTests.swift: order, the first-audio signal, the failure hand-off, cancellation,
// and the streaming speaker around the chunker, with injected fetch and play steps.

using System.Collections.Concurrent;
using System.Text;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class SpeechQueueTests
{
    private sealed class FetchFailed : Exception;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Text(byte[] data) => Encoding.UTF8.GetString(data);

    [Fact]
    public async Task PiecesPlayInOrderAndTheFirstAudioIsReportedOnce()
    {
        var played = new ConcurrentQueue<string>();
        var firstAudioCount = 0;
        var queue = new SpeechQueue(
            async (text, token) =>
            {
                // The second piece arrives first: order must still follow the text.
                if (text == "one") await Task.Delay(30, token);
                return Bytes(text);
            },
            (data, _) => { played.Enqueue(Text(data)); return Task.CompletedTask; });
        var playback = queue.RunAsync(() => Interlocked.Increment(ref firstAudioCount));
        queue.Enqueue("one");
        queue.Enqueue("two");
        queue.Finish();
        await playback;
        Assert.Equal(["one", "two"], played);
        Assert.Equal(1, firstAudioCount);
    }

    [Fact]
    public async Task RunWaitsForPiecesThatArriveLater()
    {
        var played = new ConcurrentQueue<string>();
        var queue = new SpeechQueue((text, _) => Task.FromResult(Bytes(text)), (data, _) => { played.Enqueue(Text(data)); return Task.CompletedTask; });
        var playback = queue.RunAsync(() => { });
        await Task.Delay(20);
        queue.Enqueue("late");
        queue.Finish();
        await playback;
        Assert.Equal(["late"], played);
    }

    [Fact]
    public async Task AFailedPieceReportsWhatWasNotSpoken()
    {
        var played = new ConcurrentQueue<string>();
        var queue = new SpeechQueue(
            (text, _) => text == "two" ? Task.FromException<byte[]>(new FetchFailed()) : Task.FromResult(Bytes(text)),
            (data, _) => { played.Enqueue(Text(data)); return Task.CompletedTask; });
        var playback = queue.RunAsync(() => { });
        queue.Enqueue("one");
        queue.Enqueue("two");
        queue.Enqueue("three");
        queue.Finish();
        var failure = await Assert.ThrowsAsync<SpeechQueueFailure>(() => playback);
        Assert.Equal(["two", "three"], failure.UnspokenText);
        Assert.IsType<FetchFailed>(failure.UnderlyingError);
        Assert.Equal(["one"], played);
    }

    [Fact]
    public async Task AnEmptyQueueFinishesWithoutAudio()
    {
        var firstAudioCount = 0;
        var queue = new SpeechQueue((text, _) => Task.FromResult(Bytes(text)), (_, _) => Task.CompletedTask);
        var playback = queue.RunAsync(() => firstAudioCount++);
        queue.Finish();
        await playback;
        Assert.Equal(0, firstAudioCount);
    }

    /// <summary>F-10: an interrupted answer must not keep buying audio nobody will hear.</summary>
    [Fact]
    public async Task CancellingPlaybackStopsTheRemainingFetches()
    {
        var fetched = new ConcurrentQueue<string>();
        var queue = new SpeechQueue(
            async (text, token) =>
            {
                await Task.Delay(50, token);
                fetched.Enqueue(text);
                return Bytes(text);
            },
            (_, token) => Task.Delay(300, token));
        foreach (var piece in new[] { "p1", "p2", "p3", "p4", "p5", "p6" }) queue.Enqueue(piece);
        queue.Finish();
        using var playbackCancellation = new CancellationTokenSource();
        var playback = queue.RunAsync(() => { }, playbackCancellation.Token);
        await Task.Delay(150);
        playbackCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
        await Task.Delay(1_000);
        Assert.True(fetched.Count <= 3, $"fetched {fetched.Count}");
    }

    /// <summary>F-11: a stop that ends the current piece normally must not move on to the next piece once the queue was cancelled.</summary>
    [Fact]
    public async Task ACancelledQueuePlaysNothingAfterTheCurrentPiece()
    {
        var played = new ConcurrentQueue<string>();
        var finishCurrentPiece = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPieceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new SpeechQueue(
            (text, _) => Task.FromResult(Bytes(text)),
            async (data, _) =>
            {
                var pieceText = Text(data);
                played.Enqueue(pieceText);
                // Only the first piece waits to be stopped; a second one returns at once, so this
                // test fails instead of hanging when the queue moves on.
                if (pieceText == "one")
                {
                    firstPieceStarted.TrySetResult();
                    await finishCurrentPiece.Task;
                }
            });
        queue.Enqueue("one");
        queue.Enqueue("two");
        queue.Finish();
        var playback = queue.RunAsync(() => { });
        await firstPieceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Cancel();
        finishCurrentPiece.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
        Assert.Equal(["one"], played);
    }

    [Fact]
    public async Task ACancelledSpeakerNeverStartsTheVoiceOrPoints()
    {
        var voiceStarts = 0;
        var pointTags = new ConcurrentQueue<string>();
        var speaker = new StreamingAnswerSpeaker(
            new SpeechQueue((text, _) => Task.FromResult(Bytes(text)), (_, _) => Task.CompletedTask),
            () => voiceStarts++,
            pointTags.Enqueue);
        var playback = speaker.PlayAsync();
        speaker.Cancel();
        speaker.Receive("[POINT:10,20:x] that is the save button. more");
        speaker.Finish([]);
        speaker.VoiceFellBack();
        try { await playback; } catch (OperationCanceledException) { }
        Assert.Equal(0, voiceStarts);
        Assert.Empty(pointTags);
        Assert.True(speaker.IsCancelled);
    }

    /// <summary>The agent's result arrives whole; it is cut into pieces like an answer.</summary>
    [Fact]
    public async Task AWholeAgentReplyIsSpokenInPiecesInOrder()
    {
        const string firstSentence = "done, i made the folder on your desktop and put the three reports in it.";
        var laterSentences = Enumerable.Repeat("each report is named after the month it covers so they sort in order.", 4);
        var wholeReply = string.Join(" ", new[] { firstSentence }.Concat(laterSentences)) + " i saved 3 files.";
        var fetchedPieces = new ConcurrentQueue<string>();
        var playedPieces = new ConcurrentQueue<string>();
        var voiceStarts = 0;
        var pointTags = 0;
        var speaker = new StreamingAnswerSpeaker(
            new SpeechQueue(
                (pieceText, _) => { fetchedPieces.Enqueue(pieceText); return Task.FromResult(Bytes(pieceText)); },
                (data, _) => { playedPieces.Enqueue(Text(data)); return Task.CompletedTask; }),
            () => voiceStarts++,
            _ => pointTags++);
        var playback = speaker.PlayAsync();
        speaker.ReceiveWholeReply(wholeReply);
        await playback;
        Assert.True(playedPieces.Count > 1);
        Assert.Equal(firstSentence, playedPieces.First());
        Assert.Equal(fetchedPieces.OrderBy(piece => piece), playedPieces.OrderBy(piece => piece));
        Assert.Equal(wholeReply, string.Join(" ", playedPieces));
        Assert.Equal(1, voiceStarts);
        Assert.Equal(0, pointTags);
    }

    [Fact]
    public async Task AWholeReplyWhoseVoiceFailsHandsBackWhatWasNotSaid()
    {
        const string firstPiece = "the first part was said out loud.";
        var wholeReply = firstPiece + " " + string.Concat(Enumerable.Repeat("the rest could not be fetched at all. ", 5));
        var playedPieces = new ConcurrentQueue<string>();
        var speaker = new StreamingAnswerSpeaker(
            new SpeechQueue(
                (pieceText, _) => pieceText != firstPiece ? Task.FromException<byte[]>(new FetchFailed()) : Task.FromResult(Bytes(pieceText)),
                (data, _) => { playedPieces.Enqueue(Text(data)); return Task.CompletedTask; }),
            () => { },
            _ => { });
        var playback = speaker.PlayAsync();
        speaker.ReceiveWholeReply(wholeReply);
        var failure = await Assert.ThrowsAsync<SpeechQueueFailure>(() => playback);
        Assert.Equal([firstPiece], playedPieces);
        var everythingSaidOrHandedBack = string.Join(" ", playedPieces.Concat(failure.UnspokenText));
        Assert.Equal(wholeReply.Trim(), everythingSaidOrHandedBack);
    }
}
