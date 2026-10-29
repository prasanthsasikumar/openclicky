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
        teacherSwitchObservation = preferences.Observe(IslandComponentSettings.PluginsKey, () => services.MainThread.Post(TeacherSwitchMayHaveChanged));
    }

    /// <summary>The plugin switch flipped: the lesson goes, and an idle fast-voice session reconnects so its tools follow
    /// (OpenAI takes new tools with the next instructions update, but a Gemini Live session's are fixed at setup).</summary>
    private void TeacherSwitchMayHaveChanged()
    {
        var isEnabled = TeacherPlugin.IsEnabled;
        if (isEnabled == teacherWasEnabled) return;
        teacherWasEnabled = isEnabled;
        AppLog.Write($"teacher: plugin {(isEnabled ? "on" : "off")}");
        if (!isEnabled) TeacherPlugin.ClearBoard("switched off");
        if (isStarted && UsesRealtimeVoice && VoiceState == CompanionVoiceState.Idle) WarmUpRealtimeVoice();
    }

    /// <summary>The prompt lines the switched-on plugins add to both voice lanes.</summary>
    private static string PluginPromptLines() => TeacherPlugin.PromptLines(TeacherPlugin.IsEnabled);

    /// <summary>The screenshot a lesson is placed on: the cursor's screen, else the first.</summary>
    private static CompanionScreenCapture? CursorCapture(IReadOnlyList<CompanionScreenCapture> captures) =>
        captures.FirstOrDefault(capture => capture.IsCursorScreen) ?? captures.FirstOrDefault();
}
