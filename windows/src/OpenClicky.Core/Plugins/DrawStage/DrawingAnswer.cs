//
//  DrawingAnswer.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  One answer drawn on the stage while it is spoken (DrawStageLesson): the model's reply streams
//  in, its program is fed to the stage as it grows, each say() line goes to the voice the moment
//  the stage has run it (SpeechQueue fetches up to two lines ahead), and the voice's start and end
//  of each line tell the stage when to draw that beat. The companion runs it for a question
//  (CompanionManager.DrawStage); the smoke command runs it with a written reply.
//
//  When the first line is slow to come (a strong model thinking, a detailed figure), a short hold
//  line ("One moment, let me draw that for you.") is said first, so the wait doesn't feel broken.
//
//  A reply with no program, or a stage that never started, comes back as not drawn while nothing
//  has been said, so the caller can answer another way. A voice that fails mid-answer comes back
//  with what it did not say; the stage draws the rest straight away.
//
//  A follow-up to the drawing still on the stage waits for its program's first statement: keep()
//  runs the new beats after the drawing (it stays), anything else starts a new one.
//

using System.Diagnostics;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <param name="Drew">False when nothing was drawn or said: the question is the caller's to answer.</param>
/// <param name="SpokenLines">Every say() line of the program, in order.</param>
/// <param name="UnspokenAfterVoiceFailure">The lines the voice could not say, when it failed.</param>
/// <param name="ProgramCharacters">The length of the program the reply held.</param>
/// <param name="Program">The program the reply held.</param>
/// <param name="KeptTheDrawing">A follow-up's program ran after the drawing that was on the stage, which stayed.</param>
/// <param name="LineTimes">When each line's voice started and ended, in seconds from the start (null when it wasn't heard).</param>
public sealed record DrawingAnswerResult(bool Drew, IReadOnlyList<string> SpokenLines, IReadOnlyList<string>? UnspokenAfterVoiceFailure, int ProgramCharacters,
    string Program = "", bool KeptTheDrawing = false, IReadOnlyList<(double? Start, double? End)>? LineTimes = null)
{
    /// <summary>The lines as a drawing keeps them for replaying: each with how long it was heard (or would take to read at
    /// 2.6 words a second) and the pause before it, and its audio from <paramref name="audioOf"/>.</summary>
    public IReadOnlyList<DrawnLine> DrawnLines(Func<string, byte[]?> audioOf)
    {
        var lines = new List<DrawnLine>();
        double? previousEnd = null;
        for (var k = 0; k < SpokenLines.Count; k++)
        {
            var text = SpokenLines[k];
            var (start, end) = LineTimes is { } times && k < times.Count ? times[k] : (null, null);
            var estimate = Math.Max(0.8, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.6);
            var seconds = start is { } s && end is { } e && e > s ? e - s : estimate;
            var pause = start is { } begun && previousEnd is { } ended ? Math.Clamp(begun - ended, 0, 3) : k == 0 ? 0.6 : 0.25;
            lines.Add(new DrawnLine(text, seconds, pause, audioOf(text)));
            previousEnd = end ?? (start is { } started ? started + seconds : previousEnd + pause + seconds);
        }
        return lines;
    }
}

public static class DrawingAnswer
{
    /// <summary>The longest a drawing may take once its program is written, voice included, before the stage is let go.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>Said when the first line of the drawing hasn't come after <see cref="HoldLineAfter"/>.</summary>
    public const string HoldLine = "One moment, let me draw that for you.";

    public static readonly TimeSpan HoldLineAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Draws the answer <paramref name="writeReply"/> streams (it calls its argument with the whole reply so far and returns the
    /// whole reply) on <paramref name="capture"/>'s screen, speaking it through <paramref name="speech"/>. Stage calls go
    /// through <paramref name="onMainThread"/> in order. Cancelling <paramref name="cancellationToken"/> stops the reply and the
    /// voice; it is thrown as OperationCanceledException. <paramref name="holdLine"/> (none when null) is said when the first
    /// line is slower than <paramref name="holdLineAfter"/> (default <see cref="HoldLineAfter"/>). With
    /// <paramref name="followUp"/> (the drawing on the stage), a program that starts with keep() is drawn after it.
    /// </summary>
    public static async Task<DrawingAnswerResult> RunAsync(
        DrawStageController stage,
        CompanionScreenCapture capture,
        Func<Action<string>, CancellationToken, Task<string>> writeReply,
        SpeechQueue speech,
        Action<Action> onMainThread,
        Action onFirstAudio,
        Action<string> log,
        CancellationToken cancellationToken,
        string? holdLine = null,
        TimeSpan? holdLineAfter = null,
        DrawnAnswer? followUp = null)
    {
        var spokenLines = new List<string>();
        var clock = Stopwatch.StartNew();
        var lineTimes = new List<(double? Start, double? End)>();
        void Timed(int k, bool started)
        {
            lock (lineTimes)
            {
                while (lineTimes.Count <= k) lineTimes.Add((null, null));
                var seconds = clock.Elapsed.TotalSeconds;
                lineTimes[k] = started ? (lineTimes[k].Start ?? seconds, null) : (lineTimes[k].Start ?? seconds, seconds);
            }
        }
        // The voice's pieces are the stage's lines, after the hold line when it was said first.
        var piecesBeforeLines = 0;
        var drawingFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageFailed = false;
        void LineAdded(int k, string text)
        {
            lock (spokenLines)
            {
                spokenLines.Add(text);
                speech.Enqueue(text);
            }
        }
        void SayHoldLine()
        {
            lock (spokenLines)
            {
                if (spokenLines.Count > 0 || piecesBeforeLines > 0 || holdLine is null) return;
                piecesBeforeLines = 1;
                speech.Enqueue(holdLine);
            }
            log("the first line is slow; saying the hold line");
        }
        void Finished() => drawingFinished.TrySetResult();
        void Logged(string line)
        {
            log($"page: {line}");
            if (!line.StartsWith("error: the stage did not start", StringComparison.Ordinal)) return;
            stageFailed = true;
            drawingFinished.TrySetResult();
        }
        // No more lines will come: the voice may end after the last one (and report a failure without waiting for more).
        void ProgramRan(int lineCount) => speech.Finish();
        int LinesSoFar() { lock (spokenLines) return spokenLines.Count; }
        int LineOf(int piece) { lock (spokenLines) return piece - piecesBeforeLines; }
        speech.PieceStarted += piece => { var k = LineOf(piece); if (k >= 0) { Timed(k, started: true); onMainThread(() => stage.LineStarted(k)); } };
        speech.PieceEnded += piece => { var k = LineOf(piece); if (k >= 0) { Timed(k, started: false); onMainThread(() => stage.LineEnded(k)); } };
        stage.LineAdded += LineAdded;
        stage.Finished += Finished;
        stage.Logged += Logged;
        stage.ProgramRan += ProgramRan;

        using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        DrawingAnswerResult NotDrawn(string reason)
        {
            log($"{reason}; not drawn");
            speech.Cancel();
            playbackCancellation.Cancel();
            onMainThread(() => stage.ClearIfShowing(reason));
            return new DrawingAnswerResult(false, [], null, 0);
        }
        // A new drawing starts now; a follow-up's waits for its first statement (keep() draws after the one on the stage).
        bool? keeps = followUp is null ? false : null;
        void Open(bool keep) => onMainThread(() =>
        {
            if (keep && followUp is not null) stage.Continue(followUp.Frame, followUp.Scale, DrawStagePace.Speech);
            else stage.Begin(capture.DisplayFrame, capture.DisplayScale, capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels, DrawStagePace.Speech);
        });
        try
        {
            if (followUp is null) Open(keep: false);
            var playback = speech.RunAsync(onFirstAudio, playbackCancellation.Token);
            // Not drawn, the stopped playback is never awaited: its exception is not an unobserved one.
            _ = playback.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            if (holdLine is not null)
            {
                _ = Task.Delay(holdLineAfter ?? HoldLineAfter, playbackCancellation.Token).ContinueWith(
                    task => { if (task.IsCompletedSuccessfully) SayHoldLine(); }, TaskScheduler.Default);
            }

            string reply;
            try
            {
                reply = await writeReply(replySoFar =>
                {
                    var program = DrawStageLesson.ProgramIn(replySoFar);
                    if (keeps is null)
                    {
                        keeps = DrawStageLesson.KeepsTheDrawing(program, done: false);
                        if (keeps is not { } keep) return;
                        log(keep ? "the follow-up keeps the drawing" : "the follow-up draws afresh");
                        Open(keep);
                    }
                    if (program.Length > 0) onMainThread(() => stage.Feed(program, done: false));
                }, cancellationToken);
            }
            catch (Exception error) when (!AskLaneFailures.IsCancellation(error) && LinesSoFar() == 0)
            {
                return NotDrawn($"the program was not written ({error.GetType().Name}: {error.Message})");
            }

            var program = DrawStageLesson.ProgramIn(reply);
            if (stageFailed) return NotDrawn("the stage did not start");
            if (program.Trim().Length == 0) return NotDrawn("the reply held no program");
            if (keeps is null)
            {
                keeps = DrawStageLesson.KeepsTheDrawing(program, done: true) ?? false;
                Open(keeps.Value);
            }
            log($"program written ({program.Length} chars)");
            onMainThread(() => stage.Feed(program, done: true));

            // The drawing ends with its last line; the voice failing or being stopped ends it sooner.
            await Task.WhenAny(drawingFinished.Task, playback, Task.Delay(Limit, cancellationToken)).ConfigureAwait(false);
            speech.Finish();
            IReadOnlyList<string>? unspoken = null;
            try
            {
                await playback.ConfigureAwait(false);
            }
            catch (SpeechQueueFailure failure)
            {
                log($"voice failed ({failure.UnderlyingError.GetType().Name}); drawing the rest now");
                onMainThread(stage.FinishNow);
                unspoken = holdLine is null ? failure.UnspokenText : [.. failure.UnspokenText.Where(text => text != holdLine)];
            }
            catch (OperationCanceledException)
            {
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (stageFailed && LinesSoFar() == 0) return NotDrawn("the stage did not start");
            List<string> lines;
            lock (spokenLines) lines = [.. spokenLines];
            List<(double? Start, double? End)> times;
            lock (lineTimes) times = [.. lineTimes];
            return new DrawingAnswerResult(true, lines, unspoken, program.Length, program, keeps == true, times);
        }
        finally
        {
            stage.LineAdded -= LineAdded;
            stage.Finished -= Finished;
            stage.Logged -= Logged;
            stage.ProgramRan -= ProgramRan;
        }
    }
}
