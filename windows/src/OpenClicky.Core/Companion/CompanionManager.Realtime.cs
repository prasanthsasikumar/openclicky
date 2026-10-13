//
//  CompanionManager.Realtime.cs
//  OpenClicky (Windows port of CompanionManager.swift: the Realtime voice lane)
//
//  Speech-to-speech over OpenAI Realtime: the session is kept warm so a press only starts audio;
//  point_at goes through the pointing chain (browser tab → OCR snap → UI Automation → Claude) and
//  ends in pointAt; send_to_agent runs the agent lane; the fast tools run the desktop actions; the
//  instructions carry the skills for the app in front.
//

using System.Text.Json.Nodes;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
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
            }
            else
            {
                AppLog.Write($"talk: openclicky said ({text.Length} chars)");
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
            Overlay.StreamCursorCaption("Lost the connection to OpenAI. Press again to reconnect.", holdSeconds: 6);
            Overlay.ScheduleTransientHideIfNeeded();
        };
        client.ScreenContextProvider = CaptureCursorScreenContextAsync;
        client.SelectedTextProvider = ReadSelectedTextAsync;
        client.InstructionsProvider = () =>
            AskLanePrompts.ComposeTalkInstructions(AskLanePrompts.WithReplyLanguage(client.BaseInstructions), TalkSkillsBlock());
        client.OnPointAt = (screenshotPoint, label, capture) => PointAt(screenshotPoint, label, capture);
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
                or DesktopActionOutcome.UnknownLocation or DesktopActionOutcome.InvalidUrl);
        };
        client.Pointing = MakePointingServices();
        client.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName != nameof(RealtimeVoiceClient.InputLevel)) return;
            if (!UsesRealtimeVoice || VoiceState != CompanionVoiceState.Listening) return;
            CurrentAudioPowerLevel = client.InputLevel;
        };
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

    /// <summary>The point_at chain's steps after the OCR snap: browser tabs, UI Automation, then Claude.</summary>
    private RealtimePointingServices MakePointingServices() => new()
    {
        LocateBrowserTab = (label, text, userRequest, capture) => Task.Run(() =>
        {
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

    private static ScreenCaptureGeometry GeometryOf(CompanionScreenCapture capture) =>
        new(new ScreenRect(capture.DisplayFrame.Left, capture.DisplayFrame.Top, capture.DisplayFrame.Width, capture.DisplayFrame.Height),
            capture.ScreenshotWidthInPixels, capture.ScreenshotHeightInPixels);

    // MARK: - Realtime push-to-talk

    private void HandleRealtimeShortcutTransition(BuddyPushToTalkShortcut.ShortcutTransition transition)
    {
        switch (transition)
        {
            case BuddyPushToTalkShortcut.ShortcutTransition.Pressed:
                if (IsDictatingToFrontApp) return;
                LastQuestionFailed = false;
                Overlay.DismissCursorCaption();
                Overlay.CancelTransientHide();
                if (!IsClickyCursorEnabled && !Overlay.IsOverlayVisible && !IsCursorDocked) Overlay.ShowOverlayOnAllScreens();
                InterruptCurrentAnswer(includingAgentWork: true);
                Overlay.ClearDetectedElementLocation();
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
