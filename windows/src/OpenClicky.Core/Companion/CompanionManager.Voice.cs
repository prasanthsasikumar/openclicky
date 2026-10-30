//
//  CompanionManager.Voice.cs
//  OpenClicky (Windows port of CompanionManager.swift: the shortcuts, push-to-talk, hands-free)
//

using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Shortcuts;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>
    /// Where the current Ctrl+Win press is: held, waiting for the session's final words, or settled
    /// (a question was handed over, or "didn't catch that" / an error was shown). Only an unsettled
    /// press reacts to the dictation manager.
    /// </summary>
    public enum AskOutcome { Resolved, Listening, Transcribing }

    private AskOutcome askOutcome = AskOutcome.Resolved;
    /// <summary>The Ctrl + Win press was stopped with esc while it was still listening or transcribing:
    /// words the session hands over after all are dropped.</summary>
    private bool askPressWasStopped;
    private bool askLiveWordsShownSincePress;
    private CancellationTokenSource? pendingKeyboardShortcutStart;

    /// <summary>The talk shortcut said as Windows keys, in the captions.</summary>
    public const string TalkKeysCaption = "ctrl + win";

    // MARK: - Following the talk lane's dictation manager

    private void BindDictationManager()
    {
        BuddyDictationManager.AudioPowerLevelChanged += level => CurrentAudioPowerLevel = level;
        BuddyDictationManager.PropertyChanged += (_, eventArgs) =>
        {
            switch (eventArgs.PropertyName)
            {
                case nameof(BuddyDictationManager.IsRecordingFromKeyboardShortcut):
                case nameof(BuddyDictationManager.IsPreparingToRecord):
                    UpdateVoiceStateFromDictationManager();
                    break;
                case nameof(BuddyDictationManager.IsFinalizingTranscript):
                    UpdateVoiceStateFromDictationManager();
                    // Delivered on the next UI turn: the manager publishes "no longer finalizing"
                    // just before it hands over the final words, so the question is in by then.
                    if (!BuddyDictationManager.IsFinalizingTranscript) services.MainThread.Post(AskDictationSessionEnded);
                    break;
            }
        };
        BuddyDictationManager.ErrorMessageChanged += errorMessage =>
        {
            if (errorMessage is not null) ShowAskDictationError(errorMessage);
        };
    }

    private void UpdateVoiceStateFromDictationManager()
    {
        // The answer pipeline manages .responding itself until it finishes.
        if (VoiceState == CompanionVoiceState.Responding) return;
        if (BuddyDictationManager.IsFinalizingTranscript) VoiceState = CompanionVoiceState.Processing;
        else if (BuddyDictationManager.IsRecordingFromKeyboardShortcut) VoiceState = CompanionVoiceState.Listening;
        else if (BuddyDictationManager.IsPreparingToRecord) VoiceState = CompanionVoiceState.Processing;
        else if (!UsesRealtimeVoice || VoiceState != CompanionVoiceState.Listening)
        {
            VoiceState = CompanionVoiceState.Idle;
            // Pressed and released without a word: no response task runs, so the transient hide
            // is scheduled here (only when no response is in flight).
            if (currentResponseCancellation is null) Overlay.ScheduleTransientHideIfNeeded();
        }
    }

    // MARK: - The shortcuts

    private void HandleCompanionShortcut(CompanionShortcutEvent shortcutEvent)
    {
        switch (shortcutEvent.Kind)
        {
            case CompanionShortcutEventKind.TextComposerRequested:
                OpenTextComposer();
                break;
            case CompanionShortcutEventKind.HandsFreeToggleRequested:
                ToggleHandsFree();
                break;
            case CompanionShortcutEventKind.EscapePressed:
                // A continuous scroll by voice stops on esc whatever else is going on.
                if (services.DesktopActions.StopScrolling()) AppLog.Write("scroll: continuous scroll stopped (esc)");
                // A dictation take in progress keeps esc to itself (it cancels the take, as always);
                // otherwise esc stops whatever the ask lane is doing, and then the fast voice lane when
                // it is answering, speaking or still working on an action (the Mac's order).
                var escapeTarget = AskLaneEscape.Target(IsTakeInProgress, AskLaneCanBeStopped,
                    realtimeIsBusy: EscStopsFastVoice(IsTakeInProgress, UsesRealtimeVoice, RealtimeVoiceClient.IsBusy));
                // Whatever else it stops, esc takes a lesson off the screen too (never mid-take).
                if (TeacherPlugin.EscClearsTheBoard(escapeTarget)) TeacherPlugin.ClearBoard("esc");
                switch (escapeTarget)
                {
                    case EscapeTarget.AskLane:
                        StopAskLane("esc");
                        break;
                    case EscapeTarget.Realtime:
                        StopFastVoiceOnEscape();
                        break;
                    default:
                        HandleDictationShortcut(shortcutEvent);
                        break;
                }
                break;
            case CompanionShortcutEventKind.TalkPressed:
            case CompanionShortcutEventKind.TalkReleased:
                // Delivered through ShortcutTransition.
                break;
            case CompanionShortcutEventKind.OtherDictationKeyHeld:
                if (shortcutEvent.OtherKey is { } otherKey) OtherDictationKeyHeld(otherKey);
                break;
            case CompanionShortcutEventKind.DictationPressed:
            case CompanionShortcutEventKind.DictationEditPressed:
                NoteDictationKeyPressed();
                HandleDictationShortcut(shortcutEvent);
                break;
            default:
                HandleDictationShortcut(shortcutEvent);
                break;
        }
    }

    /// <summary>Ctrl tapped twice: the island opens a one-line text field.</summary>
    public void OpenTextComposer() => Island.OpenTextComposer();

    /// <summary>
    /// A typed request goes where a spoken one would: the Realtime session (it answers out loud,
    /// points, and can call the agent) or, without Realtime, the ask lane.
    /// </summary>
    public void SubmitTypedRequest(string text)
    {
        var trimmedText = text.Trim();
        if (trimmedText.Length == 0) return;
        Overlay.CancelTransientHide();
        if (!IsClickyCursorEnabled && !Overlay.IsOverlayVisible) Overlay.ShowOverlayOnAllScreens();
        InterruptCurrentAnswer(includingAgentWork: true);
        Overlay.ClearDetectedElementLocation();
        Overlay.DismissCursorCaption();
        TeacherPlugin.ClearBoard("a new question");
        lastQuestionWasTyped = true;
        LastTranscript = trimmedText;
        AppLog.Write($"ask: typed request ({trimmedText.Length} chars)");

        if (!UsesRealtimeVoice)
        {
            SendTranscriptToClaudeWithScreenshot(trimmedText);
            return;
        }
        VoiceState = CompanionVoiceState.Processing;
        _ = SendTypedTurnThroughRealtimeAsync(trimmedText);
    }

    private async Task SendTypedTurnThroughRealtimeAsync(string text)
    {
        try
        {
            await RealtimeVoiceClient.ConnectIfNeededAsync(IsAlwaysListening ? RealtimeTurnMode.AlwaysOn : RealtimeTurnMode.PushToTalk);
            await RealtimeVoiceClient.SendTextTurnAsync(text);
            TeacherPlugin.StartLesson(text, RealtimeVoiceClient.LastScreenCapture);
        }
        catch (Exception error)
        {
            AppLog.Write($"realtime unavailable for the typed request ({error.Message}); using the ask lane");
            SendTranscriptToClaudeWithScreenshot(text);
        }
    }

    /// <summary>Dictation key + Ctrl tapped twice: always-on listening on or off. Fast voice when it is on; otherwise
    /// the standard lane listens on this PC and asks as Ctrl + Win would.</summary>
    public void ToggleHandsFree()
    {
        if (!UsesRealtimeVoice && !OpenClickyConfiguration.IsConfigured)
        {
            // Nothing could answer: no key, no plan and no account.
            Overlay.StreamCursorCaption(ProviderRouter.NeedsAnthropicKeySentence, holdSeconds: 6);
            return;
        }
        SetAlwaysListening(!IsAlwaysListening);
        AnnounceHandsFreeChange("by shortcut");
    }

    /// <summary>Says hands-free's new state by the pointer, and logs how it changed.</summary>
    private void AnnounceHandsFreeChange(string how)
    {
        var dictationKeyLabel = DictationSettings.DictationKey.KeycapLabel();
        var feedback = IsAlwaysListening
            ? $"hands-free is on. just talk, i'm listening. tap {dictationKeyLabel} + ctrl twice to stop."
            : $"hands-free is off. hold {TalkKeysCaption} to talk.";
        Overlay.ShowCaption(feedback, 6);
        AppLog.Write($"hands-free {(IsAlwaysListening ? "on" : "off")} {how}{(UsesRealtimeVoice ? "" : " (standard lane)")}");
    }

    private void HandleShortcutTransition(BuddyPushToTalkShortcut.ShortcutTransition transition)
    {
        if (UsesRealtimeVoice)
        {
            HandleRealtimeShortcutTransition(transition);
            return;
        }
        switch (transition)
        {
            case BuddyPushToTalkShortcut.ShortcutTransition.Pressed:
                TalkPressed();
                break;
            case BuddyPushToTalkShortcut.ShortcutTransition.Released:
                TalkReleased();
                break;
        }
    }

    private void TalkPressed()
    {
        lastQuestionWasTyped = false;
        // The dictation take has its own manager: a take in progress (a tapped one keeps the
        // microphone after the key is up) is the dictation this guard means too.
        if (BuddyDictationManager.IsDictationInProgress || IsTakeInProgress)
        {
            AppLog.Write("ask: ctrl+win ignored, the microphone is still busy with dictation");
            ShowAskCaption($"you’re still dictating. tap {DictationSettings.DictationKey.KeycapLabel()} to finish, then ask again.", 4);
            return;
        }
        AppLog.Write("ask: ctrl+win pressed, listening");
        LastQuestionFailed = false;
        // A new press owns the outcome from here: whatever the last release was waiting for is over.
        askOutcome = AskOutcome.Listening;
        askPressWasStopped = false;
        askLiveWordsShownSincePress = false;
        Overlay.CancelTransientHide();
        // The cursor is hidden: bring it back for this interaction.
        if (!IsClickyCursorEnabled && !Overlay.IsOverlayVisible) Overlay.ShowOverlayOnAllScreens();
        InterruptCurrentAnswer(includingAgentWork: true);
        Overlay.ClearDetectedElementLocation();
        Overlay.DismissCursorCaption();
        // Something by the pointer while the key is held: an engine that uploads on release shows no
        // words until then, and the waveform alone did not read as "listening".
        ShowAskCaption("listening…", 60);

        pendingKeyboardShortcutStart?.Cancel();
        var cancellation = new CancellationTokenSource();
        pendingKeyboardShortcutStart = cancellation;
        _ = BuddyDictationManager.StartPushToTalkFromKeyboardShortcutAsync(
            currentDraftText: "",
            updateDraftText: partialTranscript =>
            {
                // The words appear by the pointer as they are heard.
                if (askOutcome == AskOutcome.Resolved) return;
                if (partialTranscript.Trim().Length > 0) askLiveWordsShownSincePress = true;
                Overlay.ShowLiveCaption(partialTranscript);
            },
            submitDraftText: finalTranscript =>
            {
                // Esc while it was still listening or transcribing: the words go nowhere.
                if (askPressWasStopped) return;
                LastTranscript = finalTranscript;
                askOutcome = AskOutcome.Resolved;
                var question = finalTranscript.Trim();
                AppLog.Write($"ask: heard {question.Length} chars");
                if (question.Length == 0)
                {
                    ShowAskCaption($"didn't catch that. hold {TalkKeysCaption} and ask again.", 3);
                    return;
                }
                // What was heard and that an answer is coming, until the voice starts.
                ShowAskCaption($"“{question}” · thinking…", 30);
                SendTranscriptToClaudeWithScreenshot(finalTranscript);
            },
            cancellation.Token);
    }

    private void TalkReleased()
    {
        // Cancel a start still pending, or a quick press-and-release leaves the waveform stuck.
        pendingKeyboardShortcutStart?.Cancel();
        pendingKeyboardShortcutStart = null;
        BuddyDictationManager.StopPushToTalkFromKeyboardShortcut();
        AppLog.Write("ask: ctrl+win released");
        if (askOutcome != AskOutcome.Listening) return;
        askOutcome = AskOutcome.Transcribing;
        if (BuddyDictationManager.IsFinalizingTranscript)
        {
            // The session decides what was heard; the outcome is shown when it ends.
            if (!askLiveWordsShownSincePress) ShowAskCaption("transcribing…", 30);
        }
        else
        {
            // Nothing was recording (a tap, or a start that never got going). An error from the start
            // lands first and is shown instead.
            services.MainThread.Post(AskDictationSessionEnded);
        }
    }

    private void AskDictationSessionEnded()
    {
        if (askOutcome != AskOutcome.Transcribing) return;
        askOutcome = AskOutcome.Resolved;
        AppLog.Write("ask: nothing heard");
        ShowAskCaption($"didn't catch that. hold {TalkKeysCaption} and ask again.", 3);
    }

    private void ShowAskDictationError(string errorMessage)
    {
        if (askOutcome == AskOutcome.Resolved) return;
        askOutcome = AskOutcome.Resolved;
        AppLog.Write($"ask: dictation failed: {errorMessage}");
        ShowAskCaption(errorMessage, 6);
    }

    /// <summary>A caption by the pointer for the ask lane.</summary>
    private void ShowAskCaption(string message, double holdSeconds) => Overlay.ShowCaption(message, holdSeconds);
}
