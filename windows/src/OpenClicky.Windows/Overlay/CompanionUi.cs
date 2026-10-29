//
//  CompanionUi.cs
//  OpenClicky (Windows)
//
//  The floating companion UI in one place for the composition root: the pointer overlay, the
//  islands, the orb and the agent result card, with their models, and the little wiring that is
//  UI-only (the
//  (i) types beside the pointer). Everything else (voice, answers, pointing, permissions, the tour)
//  is the orchestrator's: it drives the models and listens to their events.
//
//  Wired (CompanionManager + AppComposition), kept as the map: for the composition root / CompanionManager port:
//   - App startup: `--openclicky-ui-demo` → Overlay.UiDemo.Run(); merge Theme/OpenClickyTheme.xaml into
//     App.Resources if XAML views want the tokens; set <ApplicationIcon>Assets\OpenClicky.ico</ApplicationIcon>.
//   - Mirror CompanionManager state into the models: OverlayModel.VoiceState / CurrentAudioPowerLevel /
//     IsPointerAwake (PointerPresenceTracker on a 30 Hz mouse sample), IslandCompanionModel's VoiceState,
//     AgentActivityText, LastTranscript, AnsweringSourceName, IsAlwaysListening, IsDictatingToFrontApp,
//     PointerPresence, IsClickyCursorEnabled, DictationKey, DictationEngineName.
//   - IslandCompanionModel.EngineWarning / EngineBlockedPillLabel / EngineUnavailableReason from
//     DictationEngineResolver.unavailableReason (Core/Dictation), button titles from NotchVoiceDestination.
//   - OrbSettings → DictationSettings once Core/Dictation has it (OrbModel.cs).
//   - pointAt: map the screenshot point (CompanionScreenCapture.GlobalPoint), drop points on system prompts
//     (SystemPromptGuard), then OverlayModel.PointAt(pixels, capture.DisplayFrame, label).
//   - Island events: ComposerSubmitted → submitTypedRequest; ExplainRequested → (wired here) ;
//     SettingsRequested → the main window's page; QuitRequested → quitWithFarewell (IslandCompanionModel.ShowGoodbye);
//     StopSpeakingRequested → interruptCurrentAnswer; EngineSetupRequested → NotchVoiceDestination.openSetup;
//     Permission* → PermissionPromptController (Grant opens ms-settings:privacy-microphone, Later = Dismiss,
//     its CurrentPrompt → IslandCompanionModel.PermissionPrompt); ConnectAnswered → AppConnectPromptController
//     (Core/Agent) with IslandCompanionModel.ConnectPrompt; Tour* → FirstRunTourController with
//     FirstRunTourCards.Make for its cards and Island.MicrophoneMenu for "change".
//   - The Ctrl-twice shortcut → IslandCompanionModel.OpenTextComposer(); a window coming forward → DismissPanels().
//   - Transient mode: OverlayModel.ScheduleTransientHideIfNeeded() after each interaction, IsAnswerAudioPlaying
//     from the speech queue; CancelTransientHide() + ShowOverlayOnAllScreens() on the hotkey.
//   - Agent card: AgentResultCardModel.BeginLoading(threadId), then the thread read through the openclicky CLI;
//     FollowUpRequested → submitTextToAgent.
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Orb;

namespace OpenClicky.Windows.Overlay;

public sealed class CompanionUi : IDisposable
{
    public OverlayModel Overlay { get; }
    public IslandCompanionModel Island { get; }
    public OrbModel Orb { get; }
    public OrbSettings OrbSettings { get; }
    public AgentResultCardModel AgentCard { get; }

    public OverlayWindowManager OverlayWindows { get; }
    public IslandWindowManager IslandWindows { get; }
    public OrbWindow OrbWindow { get; }
    public AgentResultCardWindow AgentCardWindow { get; }

    public CompanionUi(IScreens screens, Preferences preferences)
    {
        Overlay = new OverlayModel(preferences: preferences);
        Island = new IslandCompanionModel();
        Orb = new OrbModel();
        OrbSettings = new OrbSettings(preferences);
        AgentCard = new AgentResultCardModel();

        OverlayWindows = new OverlayWindowManager(Overlay, screens);
        IslandWindows = new IslandWindowManager(Island, Overlay, screens, preferences);
        OrbWindow = new OrbWindow(Orb, OrbSettings, screens);
        AgentCardWindow = new AgentResultCardWindow(AgentCard, screens);

        // The (i): typed beside the pointer, 8 s.
        Island.ExplainRequested += () =>
        {
            Overlay.CancelTransientHide();
            Overlay.ShowCaption(IslandTexts.ExplainMessage(Island.DictationKey), 8);
            Overlay.ScheduleTransientHideIfNeeded();
        };
    }

    /// <summary>The islands and the orb come up at launch; the overlay when the orchestrator asks.</summary>
    public void Show()
    {
        IslandWindows.Show();
        OrbWindow.Start();
    }

    public void Dispose()
    {
        OverlayWindows.Dispose();
        IslandWindows.Dispose();
        OrbWindow.Stop();
        OrbWindow.Close();
        AgentCardWindow.Close();
    }
}
