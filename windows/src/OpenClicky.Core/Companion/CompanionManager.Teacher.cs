//
//  CompanionManager.Teacher.cs
//  OpenClicky (Windows port of CompanionManager.swift: the Teacher plugin's hooks)
//
//  What the companion does for the Teacher plugin (Core/Plugins/Teacher): the pointer flies to
//  each label as it is written, the board is cleared by esc and by every new question, a typed
//  question is remembered as typed (they may not be able to hear the answer either), and
//  switching the plugin on or off reconnects an idle fast-voice session so its tools follow (a
//  Gemini Live session's tools are fixed when it opens). The voice lanes call TeacherPlugin
//  themselves (AskLane, Realtime).
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.Teacher;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>The question in flight was typed (the composer), not spoken: they may not be able to hear either, so the
    /// Teacher writes the answer down (TeacherListening).</summary>
    private bool lastQuestionWasTyped;

    private bool teacherWasEnabled;
    private IDisposable? teacherSwitchObservation;

    private void WireTeacher()
    {
        TeacherPlugin.Preferences = () => preferences;
        var board = TeacherBoard.Shared;
        board.MainThread = services.MainThread;
        board.OnLabelWritten = (location, displayFrame) => PointAt(location, displayFrame, null);
        teacherWasEnabled = TeacherPlugin.IsEnabled;
        // Photo cards on the drawing stage use the Teacher's Wikimedia search.
        Plugins.DrawStage.DrawStageController.Shared.MainThread = services.MainThread;
        Plugins.DrawStage.DrawStageController.Shared.FindPhoto = Plugins.DrawStage.DrawStagePhotos.Finder(() => board.Surface);
        // A part of a 3D drawing clicked: a follow-up question about that part, answered by a new drawing.
        Plugins.DrawStage.DrawStageController.Shared.FigurePartClicked += part => services.MainThread.Post(() => AskAboutFigurePart(part));
        // The drawing's Save button.
        Plugins.DrawStage.DrawStageController.Shared.SaveRequested += what => services.MainThread.Post(() => SaveDrawing(what));
        // The drawing's close button.
        Plugins.DrawStage.DrawStageController.Shared.CloseRequested += () => services.MainThread.Post(CloseDrawing);
        // A quiz on the drawing: its Quiz me button (shown unless Settings turns quizzes off), and the lines a quiz says.
        Plugins.DrawStage.DrawStageController.Shared.OffersQuiz = () =>
            Plugins.DrawStage.DrawStageLesson.QuizFrom(OpenClickyConfiguration.Settings.DrawingQuiz) != Plugins.DrawStage.DrawingQuiz.Off;
        Plugins.DrawStage.DrawStageController.Shared.QuizRequested += () => services.MainThread.Post(AskForQuiz);
        Plugins.DrawStage.DrawStageController.Shared.SayRequested += line => services.MainThread.Post(() => SayQuizLine(line));
        // 3D pieces a drawing asks for (f.piece), made by the maker Settings picks, the drawing model when it fails.
        var pieceWorkshop = new Plugins.DrawStage.PieceWorkshop(() => Plugins.DrawStage.PieceMakers.For(Configuration.OpenClickyConfiguration.Settings,
            Plugins.DrawStage.DrawStageLesson.WriterFor(Configuration.OpenClickyConfiguration.Settings, claudeAPI),
            () => Providers.ProviderRouter.CurrentCredentials().OpenAIKey));
        Plugins.DrawStage.DrawStageController.Shared.MakePiece = pieceWorkshop.MakeAsync;
        teacherSwitchObservation = preferences.Observe(TeacherPlugin.LessonsKey, () => services.MainThread.Post(TeacherSwitchMayHaveChanged));
    }

    /// <summary>The Teacher's switch flipped: the lesson goes, and an idle fast-voice session reconnects so its tools follow
    /// (OpenAI takes new tools with the next instructions update, but a Gemini Live session's are fixed at setup).</summary>
    private void TeacherSwitchMayHaveChanged()
    {
        var isEnabled = TeacherPlugin.IsEnabled;
        if (isEnabled == teacherWasEnabled) return;
        teacherWasEnabled = isEnabled;
        AppLog.Write($"teacher: lessons {(isEnabled ? "on" : "off")}");
        if (!isEnabled) TeacherPlugin.ClearBoard("switched off");
        if (isStarted && UsesRealtimeVoice && VoiceState == CompanionVoiceState.Idle) WarmUpRealtimeVoice();
    }

    /// <summary>The prompt lines the switched-on plugins add to both voice lanes.</summary>
    private static string PluginPromptLines() => TeacherPlugin.PromptLines(TeacherPlugin.IsEnabled);

    /// <summary>The screenshot a lesson is placed on: the cursor's screen, else the first.</summary>
    private static CompanionScreenCapture? CursorCapture(IReadOnlyList<CompanionScreenCapture> captures) =>
        captures.FirstOrDefault(capture => capture.IsCursorScreen) ?? captures.FirstOrDefault();
}
