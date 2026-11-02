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

    /// <summary>
    /// Fast voice (Realtime): a spoken question a picture answers is drawn on the stage and narrated by the answer voice,
    /// as on the ask lane, while the fast voice's own reply is cut so two voices never talk over each other. False when
    /// the question isn't one for a drawing; when the drawing can't be made, the fast voice answers it in words.
    /// </summary>
    private bool StartRealtimeDrawing(string transcript, CompanionScreenCapture? capture)
    {
        if (capture is null || DrawStageController.Shared.Surface is null || !DrawStageLesson.IsEnabled(preferences, TeacherPlugin.IsEnabled)
            || !DrawStageLesson.WantsADrawing(transcript))
        {
            return false;
        }
        realtimeDrawing?.Cancel();
        var cancellation = new CancellationTokenSource();
        realtimeDrawing = cancellation;
        RealtimeVoiceClient.StopSpeaking();
        AppLog.Write("talk: answering by drawing; the fast voice steps aside");
        _ = RunRealtimeDrawingAsync(transcript, capture, cancellation);
        return true;
    }

    private async Task RunRealtimeDrawingAsync(string transcript, CompanionScreenCapture capture, CancellationTokenSource cancellation)
    {
        var drew = false;
        try
        {
            drew = await TryDrawingAnswerAsync(transcript, capture, conversationHistory.ToList(), cancellation.Token);
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
        (byte[] Data, string Label)? markCloseUp = null, string? markedAreaLine = null)
    {
        var stage = DrawStageController.Shared;
        if (capture is null || stage.Surface is null || !DrawStageLesson.IsEnabled(preferences, TeacherPlugin.IsEnabled)
            || !DrawStageLesson.WantsADrawing(transcript))
        {
            return false;
        }
        AppLog.Write($"draw: answering by drawing ({capture.ScreenshotWidthInPixels}×{capture.ScreenshotHeightInPixels} canvas)");
        // One lesson per question: the Teacher's own board stays away from this one.
        TeacherBoard.Shared.QuestionHasLesson = true;

        var voiceSession = answerVoicePlayer.MakeAnswerSession();
        var speech = new SpeechQueue(voiceSession.AudioAsync, (audio, token) => answerVoicePlayer.PlayToCompletionAsync(audio, token));
        // The stop button and a new question stop this answer's voice like any other (StopActiveAnswer).
        using var answerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activeAnswerSpeaker = null;
        activeAnswerPlayback = answerCancellation;
        var startedAt = DateTimeOffset.UtcNow;
        var writer = DrawStageLesson.WriterFor(OpenClickyConfiguration.Settings, claudeAPI);
        try
        {
            var result = await DrawingAnswer.RunAsync(
                stage,
                capture,
                async (onReplySoFar, token) => (await writer.AnalyzeImageStreamingAsync(
                    markCloseUp is { } closeUp ? [ImageLabeledWithItsPixelSize(capture), closeUp] : [ImageLabeledWithItsPixelSize(capture)],
                    DrawStageLesson.SystemPrompt(capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels),
                    history,
                    DrawStageLesson.UserPrompt(transcript, markedAreaLine),
                    tools: null,
                    onReplySoFar,
                    token,
                    DrawStageLesson.MaximumOutputTokens)).Text,
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
                holdLine: DrawingAnswer.HoldLine);
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
            AppLog.Write($"draw: done in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.0} s ({result.SpokenLines.Count} lines, {result.ProgramCharacters} chars)");
            return true;
        }
        finally
        {
            if (ReferenceEquals(activeAnswerPlayback, answerCancellation)) activeAnswerPlayback = null;
        }
    }
}
