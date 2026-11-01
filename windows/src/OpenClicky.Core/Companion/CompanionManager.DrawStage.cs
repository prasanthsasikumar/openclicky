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
    /// <summary>
    /// Answers <paramref name="transcript"/> by drawing on <paramref name="capture"/>'s screen while speaking. False when the
    /// question isn't one for a drawing or nothing could be drawn: the ask lane then answers it as usual. Cancellation (esc,
    /// a new question) is thrown as from the ask lane.
    /// </summary>
    private async Task<bool> TryDrawingAnswerAsync(string transcript, CompanionScreenCapture? capture,
        IReadOnlyList<(string UserTranscript, string AssistantResponse)> history, CancellationToken cancellationToken)
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
        try
        {
            var result = await DrawingAnswer.RunAsync(
                stage,
                capture,
                async (onReplySoFar, token) => (await claudeAPI.AnalyzeImageStreamingAsync(
                    [ImageLabeledWithItsPixelSize(capture)],
                    DrawStageLesson.SystemPrompt(capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels),
                    history,
                    DrawStageLesson.UserPrompt(transcript),
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
                answerCancellation.Token);
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
