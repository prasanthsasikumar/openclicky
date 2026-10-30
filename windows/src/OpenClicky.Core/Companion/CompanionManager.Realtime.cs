//
//  CompanionManager.Realtime.cs
//  OpenClicky (Windows port of CompanionManager.swift: the Realtime voice lane)
//
//  Speech-to-speech over OpenAI Realtime or Gemini Live: the session is kept warm so a press only starts audio;
//  point_at goes through the pointing chain (browser tab → OCR snap → UI Automation → Claude) and
//  ends in pointAt; click goes through the same chain and ends in a real click; send_to_agent runs the agent lane; the fast tools run the desktop actions; the
//  instructions carry the skills for the app in front.
//

using System.Text.Json.Nodes;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Shortcuts;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    private bool didGreetRealtime;
    private bool didConfigureRealtimeCallbacks;

    /// <summary>Realtime answers: switched on, something to mint a session with, and an account kind that has it.</summary>
    public bool UsesRealtimeVoice => IsRealtimeVoiceEnabled && OpenClickyConfiguration.IsConfigured && AccountCapabilities.Current().UsesRealtime;

    /// <summary>Settings → Voice picked another fast voice vendor: the next connection is made with it.</summary>
    public void RealtimeProviderChanged() => WarmUpRealtimeVoice();

    /// <summary>Opens the Realtime session now so the first key press only starts audio.</summary>
    private void WarmUpRealtimeVoice()
    {
        if (!UsesRealtimeVoice) return;
        ConfigureRealtimeCallbacksIfNeeded();
        var mode = IsAlwaysListening ? RealtimeTurnMode.AlwaysOn : RealtimeTurnMode.PushToTalk;
        RealtimeVoiceClient.Disconnect(null);
        RealtimeVoiceClient.KeepWarm(mode);
        if (IsAlwaysListening) _ = StartAlwaysListeningAsync();
    }

    private async Task StartAlwaysListeningAsync()
    {
        try
        {
            await RealtimeVoiceClient.ConnectIfNeededAsync(RealtimeTurnMode.AlwaysOn);
            RealtimeVoiceClient.StartListeningContinuously();
            if (!didGreetRealtime)
            {
                didGreetRealtime = true;
                var languageName = ReplyLanguage.EnglishName(ReplyLanguage.CurrentCode);
                RealtimeVoiceClient.RequestResponse($"Greet the user in {languageName} in one short sentence as OpenClicky.");
            }
        }
        catch (Exception error)
        {
            AppLog.Write($"realtime: always-on couldn't start ({error.Message})");
        }
    }

    private void ConfigureRealtimeCallbacksIfNeeded()
    {
        if (didConfigureRealtimeCallbacks) return;
        didConfigureRealtimeCallbacks = true;
        var client = RealtimeVoiceClient;
        client.OnTranscript = (role, text) =>
        {
            if (role == RealtimeTranscriptRole.User)
            {
                LastTranscript = text;
                AppLog.Write($"talk: you said ({text.Length} chars)");
                TeacherPlugin.StartLesson(text, client.LastScreenCapture);
            }
            else
            {
                AppLog.Write($"talk: openclicky said ({text.Length} chars)");
                TeacherPlugin.WriteAnswerIfTheyCannotHear(text, lastQuestionWasTyped, client.LastScreenCapture);
                RememberExchange(LastTranscript ?? "", text);
            }
        };
        client.OnResponseStarted = () =>
        {
            if (!UsesRealtimeVoice) return;
            if (VoiceState != CompanionVoiceState.Listening) VoiceState = CompanionVoiceState.Responding;
        };
        client.OnResponseFinished = () =>
        {
            if (!UsesRealtimeVoice || VoiceState == CompanionVoiceState.Listening) return;
            VoiceState = CompanionVoiceState.Idle;
            Overlay.ScheduleTransientHideIfNeeded();
        };
        client.OnEvent = line => AppLog.Write($"realtime: {line}");
        client.OnStopListeningRequested = EndHandsFreeBySignOff;
        // A socket that died under a turn used to leave the state on processing for good, which also
        // refused dictation. Unstick it and say why; the next press reconnects.
        client.OnTurnHeardNothing = () =>
        {
            if (!UsesRealtimeVoice || VoiceState != CompanionVoiceState.Processing) return;
            VoiceState = CompanionVoiceState.Idle;
            Overlay.StreamCursorCaption($"Didn't catch that. Hold {TalkKeysCaption} while you talk.", holdSeconds: 5);
            Overlay.ScheduleTransientHideIfNeeded();
        };
        client.OnConnectionLost = reason =>
        {
            if (!UsesRealtimeVoice) return;
            AppLog.Write($"realtime connection lost: {reason}");
            if (VoiceState == CompanionVoiceState.Idle) return;
            VoiceState = CompanionVoiceState.Idle;
            Overlay.StreamCursorCaption($"Lost the connection to {client.ProviderName}. Press again to reconnect.", holdSeconds: 6);
            Overlay.ScheduleTransientHideIfNeeded();
        };
        client.ScreenContextProvider = CaptureCursorScreenContextAsync;
        client.SelectedTextProvider = ReadSelectedTextAsync;
        // "rephrase this" on fast voice: the turn quotes the highlight or the text box, and write_in_place
        // writes over it, as on the ask lane.
        client.WritingTargetProvider = () => WritingTarget.CaptureFromFrontAppAsync(services.WriteInPlace, AppLog.Write);
        client.WriteInPlaceHost = services.WriteInPlace;
        client.OnWroteInPlace = () => LastSuccessfulQuickActionAt = DateTimeOffset.Now;
        client.InstructionsProvider = () =>
            AskLanePrompts.ComposeTalkInstructions(AskLanePrompts.WithReplyLanguage(client.BaseInstructions + PluginPromptLines()), TalkSkillsBlock());
        client.PluginToolDefinitions = () => TeacherPlugin.IsEnabled ? TeacherPlugin.RealtimeToolDefinitions() : [];
        client.OnPluginToolCall = (toolName, arguments, capture) =>
            Task.FromResult(TeacherPlugin.Perform(toolName, TeacherPlugin.StringArguments(arguments), capture));
        client.OnPointAt = (screenshotPoint, label, capture) => PointAt(screenshotPoint, label, capture);
        client.OnClick = async (point, inNewTab) =>
        {
            var result = await services.DesktopActions.ClickAsync(point, inNewTab);
            if (result is ScreenClickResult.Clicked or ScreenClickResult.FollowedLink or ScreenClickResult.OpenedLinkInNewTab) LastSuccessfulQuickActionAt = DateTimeOffset.Now;
            return result;
        };
        client.OnAgentTask = async task =>
        {
            AgentActivityText = "starting agent…";
            var screenCaptures = await CaptureScreensQuietlyAsync(CancellationToken.None);
            try
            {
                return await PerformAgentTaskAsync(task, screenCaptures);
            }
            catch (Exception error)
            {
                AgentActivityText = null;
                return $"The agent could not run: {error.Message}";
            }
        };
        client.DesktopActionToolDefinitions = DesktopAction.RealtimeToolDefinitions;
        client.OnDesktopAction = async (toolName, arguments) =>
        {
            var outcome = await desktopActionRunner.PerformToolCallAsync(toolName, arguments);
            if (outcome is null) return null;
            if (outcome.DidSomething) LastSuccessfulQuickActionAt = DateTimeOffset.Now;
            return new RealtimeToolResult(outcome.SpokenSentence, WasRejected: outcome is DesktopActionOutcome.InvalidName or DesktopActionOutcome.InvalidAppName
                or DesktopActionOutcome.UnknownLocation or DesktopActionOutcome.InvalidUrl or DesktopActionOutcome.InvalidQuery or DesktopActionOutcome.InvalidRecipient,
                ModelOutput: outcome.ToolOutput, ThenLooksAtScreen: outcome.ThenLooksAtScreen, LogDescription: outcome.LogDescription,
                LooksWithoutWaitingForAPage: outcome.LooksWithoutWaitingForAPage);
        };
        client.FrontAppProvider = () => services.FrontmostApp.CurrentWithoutUrl()?.ProcessId;
        client.Pointing = MakePointingServices();
        client.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName != nameof(RealtimeVoiceClient.InputLevel)) return;
            if (!UsesRealtimeVoice || VoiceState != CompanionVoiceState.Listening) return;
            CurrentAudioPowerLevel = client.InputLevel;
        };
    }

    /// <summary>
    /// Whether esc goes to the fast voice lane (the Mac's EscapeTarget.realtime): never while a dictation
    /// take is in progress (esc cancels the take), and only while the lane has something to stop. Esc
    /// used to know only the dictation take: pressed while Gmail loaded, it did nothing and Clicky read
    /// the inbox (the Mac, 2026-10-28).
    /// </summary>
    public static bool EscStopsFastVoice(bool dictationTakeInProgress, bool usesRealtimeVoice, bool realtimeIsBusy) =>
        !dictationTakeInProgress && usesRealtimeVoice && realtimeIsBusy;

    /// <summary>The fast voice lane stops: the reply ends, and an action still working says nothing when it
    /// finishes (its output stays in the conversation for the next turn).</summary>
    private void StopFastVoiceOnEscape()
    {
        AppLog.Write("realtime: stopped (esc)");
        RealtimeVoiceClient.StopSpeaking();
        VoiceState = CompanionVoiceState.Idle;
        ShowAskCaption("stopped", 1.5);
        Overlay.ScheduleTransientHideIfNeeded();
    }

    /// <summary>"that's all, thanks" while hands-free is on: the Realtime model called `stop_listening` and
    /// its goodbye has played. Ends hands-free exactly as the shortcut does.</summary>
    private void EndHandsFreeBySignOff()
    {
        if (!IsAlwaysListening) return;
        SetAlwaysListening(false);
        AnnounceHandsFreeChange("by voice");
    }

    /// <summary>The cursor's screen, with a caption that says where the pointer is in it.</summary>
    private async Task<RealtimeScreenContext?> CaptureCursorScreenContextAsync()
    {
        try
        {
            var capture = await services.ScreenCapture.CaptureCursorScreenAsync();
            if (capture is null) return null;
            var (pointerX, pointerY) = capture.ScreenshotPoint(services.Screens.CursorPosition());
            var caption = $"Screenshot of the user's screen ({capture.ScreenshotWidthInPixels}×{capture.ScreenshotHeightInPixels} px). The pointer is at ({(int)pointerX}, {(int)pointerY}) from the top-left.";
            return new RealtimeScreenContext(capture.ImageData, caption, capture);
        }
        catch (Exception error)
        {
            AppLog.Write($"realtime: no screen attached ({error.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// The point_at chain's steps after the OCR snap: a window's caption buttons and browser tabs,
    /// UI Automation, then Claude. The caption buttons ride in the browser-tab step (they run first,
    /// as on the Mac) so RealtimeVoiceClient's chain is left as it is; its log then says
    /// "browser tab \"close button of …\"".
    /// </summary>
    private RealtimePointingServices MakePointingServices() => new()
    {
        LocateBrowserTab = (label, text, userRequest, capture) => Task.Run(() =>
        {
            // A window's close / minimize / maximize buttons: the front window's own frame says
            // exactly where they are.
            var windowControl = WindowControlLocator.Locate(label, text, userRequest, GeometryOf(capture), FrontWindowCaptionButtons, AppLog.Write);
            if (windowControl is not null)
            {
                return ((ScreenPoint Point, string Title)?)(windowControl.Point, $"{WindowControlLocator.Name(windowControl.Control)} button of {windowControl.AppName}");
            }
            var located = BrowserTabLocator.Locate(label, text, userRequest, GeometryOf(capture), services.BrowserTabs,
                services.WindowList.OwnProcessId, AppLog.Write);
            return located is null ? ((ScreenPoint Point, string Title)?)null : (located.Point, located.Tab.Title);
        }),
        LocateAccessibleElement = async (label, text, userRequest, guess, capture, maxDistance, ambiguityMargin) =>
        {
            var controls = await services.FocusedElements.CaptionedControlsInForegroundWindowAsync();
            var candidates = new List<AccessibleElementCandidate>();
            foreach (var control in controls)
            {
                var (centerX, centerY) = capture.ScreenshotPoint(control.Bounds.Center);
                if (!capture.ContainsScreenshotPoint(centerX, centerY)) continue;
                candidates.Add(new AccessibleElementCandidate(control.Role, control.Title, control.ContainerTitles, new ScreenPoint(centerX, centerY)));
            }
            return AccessibleElementLocator.BestMatch(AccessibleElementLocator.CaptionHint(label, text), userRequest, guess, candidates, maxDistance, ambiguityMargin);
        },
        GroundWithClaude = (label, text, userRequest, capture) =>
            ScreenElementGrounding.LocateAsync(label, text, userRequest, capture.ImageData, capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels),
    };

    /// <summary>The caption buttons of the app in front (never OpenClicky's own window).</summary>
    private CaptionButtonLayout? FrontWindowCaptionButtons()
    {
        if (services.FrontmostApp.CurrentWithoutUrl() is not { WindowHandle: not 0 } front) return null;
        return services.WindowList.CaptionButtonLayout(front.WindowHandle, front.DisplayName ?? front.ProcessName ?? "the front app");
    }

    private static ScreenCaptureGeometry GeometryOf(CompanionScreenCapture capture) =>
        new(new ScreenRect(capture.DisplayFrame.Left, capture.DisplayFrame.Top, capture.DisplayFrame.Width, capture.DisplayFrame.Height),
            capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels);

    // MARK: - Realtime push-to-talk

    private void HandleRealtimeShortcutTransition(BuddyPushToTalkShortcut.ShortcutTransition transition)
    {
        switch (transition)
        {
            case BuddyPushToTalkShortcut.ShortcutTransition.Pressed:
                lastQuestionWasTyped = false;
                if (IsDictatingToFrontApp) return;
                LastQuestionFailed = false;
                Overlay.DismissCursorCaption();
                Overlay.CancelTransientHide();
                if (!IsClickyCursorEnabled && !Overlay.IsOverlayVisible) Overlay.ShowOverlayOnAllScreens();
                InterruptCurrentAnswer(includingAgentWork: true);
                Overlay.ClearDetectedElementLocation();
                TeacherPlugin.ClearBoard("a new question");
                VoiceState = CompanionVoiceState.Listening;
                _ = BeginRealtimePushToTalkAsync();
                break;
            case BuddyPushToTalkShortcut.ShortcutTransition.Released:
                if (VoiceState != CompanionVoiceState.Listening) return;
                VoiceState = CompanionVoiceState.Processing;
                RealtimeVoiceClient.EndPushToTalk();
                break;
        }
    }

    private async Task BeginRealtimePushToTalkAsync()
    {
        try
        {
            ConfigureRealtimeCallbacksIfNeeded();
            await RealtimeVoiceClient.ConnectIfNeededAsync(IsAlwaysListening ? RealtimeTurnMode.AlwaysOn : RealtimeTurnMode.PushToTalk);
            if (VoiceState != CompanionVoiceState.Listening) return;
            RealtimeVoiceClient.BeginPushToTalk();
        }
        catch (Exception error)
        {
            AppLog.Write($"realtime unavailable: {error.Message}");
            VoiceState = CompanionVoiceState.Idle;
            LastQuestionFailed = true;
            SpeakWithSystemVoice("Realtime voice is unavailable right now.");
            // Why, on screen: "check the backend" sent people looking at the network when the answer was a rejected key.
            Overlay.StreamCursorCaption(error.Message, holdSeconds: 12);
        }
    }
}
