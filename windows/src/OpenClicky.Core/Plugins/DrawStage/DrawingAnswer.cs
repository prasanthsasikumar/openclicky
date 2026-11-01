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
//  A reply with no program, or a stage that never started, comes back as not drawn while nothing
//  has been said, so the caller can answer another way. A voice that fails mid-answer comes back
//  with what it did not say; the stage draws the rest straight away.
//

using OpenClicky.Core.Answers;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <param name="Drew">False when nothing was drawn or said: the question is the caller's to answer.</param>
/// <param name="SpokenLines">Every say() line of the program, in order.</param>
/// <param name="UnspokenAfterVoiceFailure">The lines the voice could not say, when it failed.</param>
/// <param name="ProgramCharacters">The length of the program the reply held.</param>
public sealed record DrawingAnswerResult(bool Drew, IReadOnlyList<string> SpokenLines, IReadOnlyList<string>? UnspokenAfterVoiceFailure, int ProgramCharacters);

public static class DrawingAnswer
{
    /// <summary>The longest a drawing may take once its program is written, voice included, before the stage is let go.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Draws the answer <paramref name="writeReply"/> streams (it calls its argument with the whole reply so far and returns the
    /// whole reply) on <paramref name="capture"/>'s screen, speaking it through <paramref name="speech"/>. Stage calls go
    /// through <paramref name="onMainThread"/> in order. Cancelling <paramref name="cancellationToken"/> stops the reply and the
    /// voice; it is thrown as OperationCanceledException.
    /// </summary>
    public static async Task<DrawingAnswerResult> RunAsync(
        DrawStageController stage,
        CompanionScreenCapture capture,
        Func<Action<string>, CancellationToken, Task<string>> writeReply,
        SpeechQueue speech,
        Action<Action> onMainThread,
        Action onFirstAudio,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var spokenLines = new List<string>();
        var drawingFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageFailed = false;
        void LineAdded(int k, string text)
        {
            lock (spokenLines) spokenLines.Add(text);
            speech.Enqueue(text);
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
        speech.PieceStarted += k => onMainThread(() => stage.LineStarted(k));
        speech.PieceEnded += k => onMainThread(() => stage.LineEnded(k));
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
        try
        {
            onMainThread(() => stage.Begin(capture.DisplayFrame, capture.DisplayScale, capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels,
                DrawStagePace.Speech));
            var playback = speech.RunAsync(onFirstAudio, playbackCancellation.Token);
            // Not drawn, the stopped playback is never awaited: its exception is not an unobserved one.
            _ = playback.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

            string reply;
            try
            {
                reply = await writeReply(replySoFar =>
                {
                    var program = DrawStageLesson.ProgramIn(replySoFar);
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
                unspoken = failure.UnspokenText;
            }
            catch (OperationCanceledException)
            {
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (stageFailed && LinesSoFar() == 0) return NotDrawn("the stage did not start");
            List<string> lines;
            lock (spokenLines) lines = [.. spokenLines];
            return new DrawingAnswerResult(true, lines, unspoken, program.Length);
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
