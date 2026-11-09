//
//  CompanionManager.DrawStage.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Answering by drawing (Core/Plugins/DrawStage): a question a picture answers is sent to the
//  answer model, whichever source answers, with the drawing language as its system prompt, and
//  DrawingAnswer draws its program on the stage while the answer voice says its lines. A reply
//  with no program, or a stage that never started, hands the question back to the ask lane before
//  anything was said; a voice that fails partway hands the rest to the system voice.
//
//  A drawing that finished stays up (DrawStageController.Current). A question asked while it is up
//  may be a follow-up: the model reads the drawing's program and adds to it or draws it again, or
//  hands the question back. "Save it", or the page's Save button, saves it (IDrawingSaver).
//

using System.Collections.Concurrent;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>A part of the 3D drawing on the screen was clicked: "how does the hot end work?", about the question it answered.</summary>
    private void AskAboutFigurePart(string part)
    {
        var earlier = lastAskQuestion?.Transcript ?? LastTranscript;
        var question = earlier is { Length: > 0 }
            ? $"How does the {part} work? It is a part of the 3D drawing you made for \"{earlier}\"."
            : $"How does the {part} work?";
        AppLog.Write($"draw: a part of the figure was clicked; asking about it");
        SendTranscriptToClaudeWithScreenshot(question);
    }

    /// <summary>The fast-voice question being answered by drawing, while it is (null otherwise).</summary>
    private CancellationTokenSource? realtimeDrawing;

    /// <summary>Until then the fast voice's own replies are cut (it was asked to save the drawing, which the app does).</summary>
    private DateTimeOffset fastVoiceQuietUntil;

    /// <summary>
    /// Fast voice (Realtime): a spoken question a picture answers is drawn on the stage and narrated by the answer voice,
    /// as on the ask lane, while the fast voice's own reply is cut so two voices never talk over each other. False when
    /// the question isn't one for a drawing; when the drawing can't be made, the fast voice answers it in words.
    /// </summary>
    private bool StartRealtimeDrawing(string transcript, CompanionScreenCapture? capture, bool asked = false)
    {
        // asked: the fast voice itself asked for a drawing (the Teacher's draw tool), whatever the question's words.
        if (asked ? capture is null || !DrawingStageIsOn : !IsForTheStage(transcript, capture))
        {
            // Hands-free moved on: a drawing still being narrated stops with its stage, and the fast voice answers this one
            // (its reply would otherwise be cut for the drawing's sake).
            StopRealtimeDrawing("a question that isn't about the drawing");
            DrawStageController.Shared.ClearIfShowing("a question that isn't about the drawing");
            return false;
        }
        StopRealtimeDrawing("a new question for the stage");
        var cancellation = new CancellationTokenSource();
        realtimeDrawing = cancellation;
        RealtimeVoiceClient.StopSpeaking();
        AppLog.Write("talk: answering by drawing; the fast voice steps aside");
        _ = RunRealtimeDrawingAsync(transcript, capture, cancellation, asked);
        return true;
    }

    /// <summary>The drawing's close button: the drawing goes, and so does what it was doing (its answer still being drawn
    /// and said, on the ask lane or the fast voice, and a save), as with esc.</summary>
    private void CloseDrawing()
    {
        AppLog.Write("draw: closed with its close button");
        StopSavingTheDrawing();
        StopQuizVoice();
        StopRealtimeDrawing("the drawing was closed");
        var stage = DrawStageController.Shared;
        if (stage.IsShowing && stage.Current is null && AskLaneCanBeStopped) StopAskLane("the drawing was closed");
        TeacherPlugin.ClearBoard("its close button");
    }

    /// <summary>The fast voice called the Teacher's draw or picture tool while the drawing stage is on: the stage draws the
    /// question instead (its words weren't ones the stage knew), so the drawing can be moved, saved and closed like any
    /// other; null for a tool that isn't the Teacher's.</summary>
    private string? DrawOnTheStageInstead(string toolName, CompanionScreenCapture? capture)
    {
        if (!TeacherPlugin.ToolNames.Contains(toolName)) return null;
        const string LeaveItToTheDrawing = "the drawing is appearing on their screen and its own voice explains it; say nothing more";
        if (realtimeDrawing is not null) return LeaveItToTheDrawing;
        if (LastTranscript is not { Length: > 0 } question || !StartRealtimeDrawing(question, capture, asked: true)) return "explain it in words";
        AppLog.Write($"talk: the fast voice asked for {toolName}; the drawing stage draws it");
        return LeaveItToTheDrawing;
    }

    /// <summary>Stops the fast-voice question being answered by drawing, its narration with it.</summary>
    private void StopRealtimeDrawing(string reason)
    {
        if (realtimeDrawing is not { } drawing) return;
        realtimeDrawing = null;
        AppLog.Write($"talk: the drawing being narrated stops ({reason})");
        try { drawing.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunRealtimeDrawingAsync(string transcript, CompanionScreenCapture capture, CancellationTokenSource cancellation, bool asked)
    {
        var drew = false;
        try
        {
            drew = await TryDrawingAnswerAsync(transcript, capture, conversationHistory.ToList(), cancellation.Token, asked: asked);
        }
        catch (Exception error) when (error is OperationCanceledException || cancellation.IsCancellationRequested)
        {
            drew = true;
        }
        catch (Exception error)
        {
            AppLog.Write($"talk: the drawing failed ({error.GetType().Name}); the fast voice answers instead");
        }
        finally
        {
            if (ReferenceEquals(realtimeDrawing, cancellation)) realtimeDrawing = null;
            cancellation.Dispose();
        }
        // Nothing was drawn or said: the fast voice answers the same question in words.
        if (!drew) await RealtimeVoiceClient.SendTextTurnAsync(transcript);
    }

    /// <summary>
    /// Answers <paramref name="transcript"/> by drawing on <paramref name="capture"/>'s screen while speaking. False when the
    /// question isn't one for a drawing or nothing could be drawn: the ask lane then answers it as usual. Cancellation (esc,
    /// a new question) is thrown as from the ask lane.
    /// </summary>
    private async Task<bool> TryDrawingAnswerAsync(string transcript, CompanionScreenCapture? capture,
        IReadOnlyList<(string UserTranscript, string AssistantResponse)> history, CancellationToken cancellationToken,
        (byte[] Data, string Label)? markCloseUp = null, string? markedAreaLine = null, bool asked = false)
    {
        var stage = DrawStageController.Shared;
        // asked: the fast voice asked for a drawing (its draw tool), so the question's words aren't checked again; they may be
        // in a language the word lists don't know, or written in another script ("बारिश कैसे बरसती है" for Urdu).
        if (capture is null || !(asked ? DrawingStageIsOn : IsForTheStage(transcript, capture)))
        {
            stage.ClearIfShowing("a question that isn't about the drawing");
            return false;
        }
        // The drawing still up, when the question is asked on its screen: the question may be about it.
        var drawingUp = DrawingUpOn(capture);
        var wantsADrawing = asked || DrawStageLesson.WantsADrawing(transcript) || TeacherQuestion.IsTeachingQuestion(transcript);
        AppLog.Write($"draw: answering by drawing ({capture.ScreenshotWidthInPixels}×{capture.ScreenshotHeightInPixels} canvas"
            + (drawingUp is null ? ")" : $", a follow-up to the drawing up ({drawingUp.Lines.Count} lines))"));
        // One lesson per question: the Teacher's own board stays away from this one.
        TeacherBoard.Shared.QuestionHasLesson = true;

        var voiceSession = answerVoicePlayer.MakeAnswerSession();
        // Each line's audio is kept with the drawing, so a saved video says it in the same voice.
        var audioByLine = new ConcurrentDictionary<string, byte[]>();
        var speech = new SpeechQueue(async (text, token) => audioByLine[text] = await voiceSession.AudioAsync(text, token),
            (audio, token) => answerVoicePlayer.PlayToCompletionAsync(audio, token));
        // The stop button and a new question stop this answer's voice like any other (StopActiveAnswer).
        using var answerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activeAnswerSpeaker = null;
        activeAnswerPlayback = answerCancellation;
        var startedAt = DateTimeOffset.UtcNow;
        var settings = OpenClickyConfiguration.Settings;
        var writer = DrawStageLesson.WriterFor(settings, claudeAPI);
        var detail = DrawStageLesson.DetailFrom(settings.DrawingDetail);
        try
        {
            var result = await DrawingAnswer.RunAsync(
                stage,
                capture,
                async (onReplySoFar, token) => (await writer.AnalyzeImageStreamingAsync(
                    markCloseUp is { } closeUp ? [ImageLabeledWithItsPixelSize(capture), closeUp] : [ImageLabeledWithItsPixelSize(capture)],
                    DrawStageLesson.SystemPrompt(capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels, detail, settings.DrawingInstructions,
                        DrawStageLesson.OffersPieces(settings), ReplyLanguage.CurrentCode),
                    history,
                    drawingUp is null ? DrawStageLesson.UserPrompt(transcript, markedAreaLine) : DrawStageLesson.FollowUpPrompt(transcript, drawingUp, markedAreaLine),
                    tools: null,
                    onReplySoFar,
                    token,
                    DrawStageLesson.OutputTokens(detail),
                    forDrawing: true)).Text,
                speech,
                services.MainThread.Post,
                onFirstAudio: () => services.MainThread.Post(() =>
                {
                    VoiceState = CompanionVoiceState.Responding;
                    Overlay.DismissCursorCaption();
                    AppLog.Write($"draw: voice started after {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0} s");
                }),
                line => AppLog.Write($"draw: {line}"),
                answerCancellation.Token,
                // A question that may not be about the drawing at all says nothing until it is drawn.
                holdLine: wantsADrawing ? DrawingAnswer.HoldLine : null,
                followUp: drawingUp);
            if (!result.Drew)
            {
                TeacherBoard.Shared.QuestionHasLesson = false;
                return false;
            }
            if (result.UnspokenAfterVoiceFailure is { Count: > 0 } unspoken && !cancellationToken.IsCancellationRequested)
            {
                Overlay.DismissCursorCaption();
                SpeakWithSystemVoice(string.Join(" ", unspoken));
            }
            if (result.SpokenLines.Count > 0) RememberExchange(transcript, string.Join(" ", result.SpokenLines));
            if (!cancellationToken.IsCancellationRequested)
            {
                var lines = result.DrawnLines(text => audioByLine.TryGetValue(text, out var audio) ? audio : null);
                var drawing = result.KeptTheDrawing && drawingUp is not null
                    ? drawingUp.AddedTo(transcript, result.Program, lines)
                    : new DrawnAnswer([transcript], result.Program, capture.DisplayFrame, capture.DisplayScale, capture.ScreenshotWidthInPixels,
                        capture.ScreenshotHeightInPixels, lines);
                services.MainThread.Post(() => stage.Remember(drawing));
                // Quizzes always on (Settings → Clicky → Drawings): a new drawing is followed by a few questions on it. Not
                // a follow-up (they're about the drawing already up) and not a quiz itself.
                if (drawingUp is null && !DrawStageLesson.IsQuizRequest(transcript)
                    && DrawStageLesson.QuizFrom(settings.DrawingQuiz) == DrawingQuiz.Always)
                {
                    _ = AskForQuizSoonAsync(drawing);
                }
            }
            AppLog.Write($"draw: done in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0} s ({result.SpokenLines.Count} lines, {result.ProgramCharacters} chars)");
            return true;
        }
        finally
        {
            if (ReferenceEquals(activeAnswerPlayback, answerCancellation)) activeAnswerPlayback = null;
        }
    }

    /// <summary>The drawing stage answers what a picture answers (Teacher on, its drawing stage on); the Teacher's own board
    /// is left for when it is off, so every drawing can be moved, saved, shrunk and closed the same way.</summary>
    private bool DrawingStageIsOn =>
        DrawStageController.Shared.Surface is not null && DrawStageLesson.IsEnabled(preferences, TeacherPlugin.IsEnabled);

    /// <summary>Whether the stage answers <paramref name="transcript"/>: one a picture answers (the stage's words, or a
    /// question the Teacher would teach on its board), or one that may be about the drawing still up on
    /// <paramref name="capture"/>'s screen.</summary>
    private bool IsForTheStage(string transcript, CompanionScreenCapture? capture) =>
        capture is not null && DrawingStageIsOn
        && (DrawStageLesson.WantsADrawing(transcript) || TeacherQuestion.IsTeachingQuestion(transcript)
            || (DrawingUpOn(capture) is not null && DrawStageLesson.MayBeAFollowUp(transcript)));

    /// <summary>The finished drawing on the stage, when it is on <paramref name="capture"/>'s screen.</summary>
    private static DrawnAnswer? DrawingUpOn(CompanionScreenCapture capture) =>
        DrawStageController.Shared.Current is { } drawing && drawing.Frame == capture.DisplayFrame ? drawing : null;

    // MARK: - A quiz on the drawing

    /// <summary>The quiz line being said, while it is.</summary>
    private CancellationTokenSource? quizVoice;

    /// <summary>The Quiz me button, or a quiz after every drawing: a few questions on the drawing up, asked as a follow-up to
    /// it (the model writes them with quiz(); the page shows them and asks for each line to be said).</summary>
    private void AskForQuiz()
    {
        if (DrawStageController.Shared.Current is null) return;
        AppLog.Write("draw: a quiz on the drawing");
        SendTranscriptToClaudeWithScreenshot(DrawStageLesson.QuizQuestion);
    }

    /// <summary>A moment after <paramref name="drawing"/> is remembered, its quiz, if it is still the drawing up and nothing
    /// else has started.</summary>
    private async Task AskForQuizSoonAsync(DrawnAnswer drawing)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.2)).ConfigureAwait(false);
        services.MainThread.Post(() =>
        {
            if (ReferenceEquals(DrawStageController.Shared.Current, drawing) && VoiceState is CompanionVoiceState.Idle or CompanionVoiceState.Responding)
                AskForQuiz();
        });
    }

    /// <summary>Says a quiz's line (a question with its choices, whether the answer was right, the score) in the answer voice,
    /// this PC's voice when that fails. A new line cuts the one being said.</summary>
    private void SayQuizLine(string text)
    {
        if (DrawStageController.Shared.Current is null && !DrawStageController.Shared.IsShowing) return;
        StopQuizVoice();
        var cancellation = new CancellationTokenSource();
        quizVoice = cancellation;
        _ = SayQuizLineAsync(text, cancellation);
    }

    private async Task SayQuizLineAsync(string text, CancellationTokenSource cancellation)
    {
        try
        {
            var audio = await answerVoicePlayer.MakeAnswerSession().AudioAsync(text, cancellation.Token);
            await answerVoicePlayer.PlayToCompletionAsync(audio, cancellation.Token);
        }
        catch (Exception error) when (error is OperationCanceledException || cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            AppLog.Write($"draw: the quiz line couldn't be said in the answer voice ({error.GetType().Name}); this PC's voice says it");
            services.MainThread.Post(() => SpeakWithSystemVoice(text));
        }
        finally
        {
            if (ReferenceEquals(quizVoice, cancellation)) quizVoice = null;
            cancellation.Dispose();
        }
    }

    private void StopQuizVoice()
    {
        if (quizVoice is not { } voice) return;
        quizVoice = null;
        try { voice.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // MARK: - Saving the drawing

    /// <summary>The drawing being saved, while it is.</summary>
    private CancellationTokenSource? drawingSave;

    /// <summary>"Save it" while a drawing is up: it is saved (the picture, the video or both, as asked), and true; otherwise
    /// false (the request is answered as usual).</summary>
    private bool TrySaveDrawing(string request)
    {
        if (DrawStageLesson.SaveRequestKind(request) is not { } what || DrawStageController.Shared.Current is null) return false;
        AppLog.Write($"draw: asked to save the drawing ({what})");
        SaveDrawing(what);
        return true;
    }

    /// <summary>Saves the drawing up on the stage: a picture of it, a video of it drawn again with its voice, or both. How it
    /// goes shows beside the drawing.</summary>
    private void SaveDrawing(DrawingSaveKind what)
    {
        var stage = DrawStageController.Shared;
        if (drawingSave is not null) return;
        if (stage.Current is not { } drawing || stage.Saver is not { } saver)
        {
            ShowAskCaption("There's no finished drawing to save.", 4);
            return;
        }
        var cancellation = new CancellationTokenSource();
        drawingSave = cancellation;
        _ = SaveDrawingAsync(saver, drawing, what, cancellation);
    }

    private async Task SaveDrawingAsync(IDrawingSaver saver, DrawnAnswer drawing, DrawingSaveKind what, CancellationTokenSource cancellation)
    {
        var stage = DrawStageController.Shared;
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            stage.ShowSaving(new DrawingSaveProgress(what == DrawingSaveKind.Picture ? "Saving the picture…" : "Getting ready…"));
            var saved = await saver.SaveAsync(drawing, what, progress => services.MainThread.Post(() =>
            {
                if (!cancellation.IsCancellationRequested) stage.ShowSaving(progress);
            }), cancellation.Token);
            if (saved.Failure is { } failure)
            {
                AppLog.Write($"draw: saving failed ({failure})");
                stage.ShowSaved($"Couldn't save it: {failure}", failed: true);
                return;
            }
            AppLog.Write($"draw: saved in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0} s (picture {saved.PicturePath is not null}, video {saved.VideoPath is not null})");
            var where = saved.VideoPath ?? saved.PicturePath;
            var lostTheVideo = what != DrawingSaveKind.Picture && saved.VideoPath is null;
            stage.ShowSaved(where is null ? "Saved."
                : lostTheVideo ? $"The video couldn't be made; the picture is in {Path.GetFileName(Path.GetDirectoryName(where))}"
                : $"Saved to {Path.GetFileName(Path.GetDirectoryName(where))}: {Path.GetFileName(where)}", failed: lostTheVideo);
        }
        catch (Exception error) when (error is OperationCanceledException || cancellation.IsCancellationRequested)
        {
            AppLog.Write("draw: saving stopped");
            stage.ShowSaved("Saving stopped.", failed: true);
        }
        catch (Exception error)
        {
            AppLog.Write($"draw: saving failed ({error.GetType().Name}: {error.Message})");
            stage.ShowSaved("Couldn't save the drawing.", failed: true);
        }
        finally
        {
            if (ReferenceEquals(drawingSave, cancellation)) drawingSave = null;
            cancellation.Dispose();
        }
    }

    /// <summary>A new question or esc stops a save in progress (its replay is on the stage the next answer needs).</summary>
    private void StopSavingTheDrawing()
    {
        if (drawingSave is null) return;
        drawingSave.Cancel();
        drawingSave = null;
    }
}
