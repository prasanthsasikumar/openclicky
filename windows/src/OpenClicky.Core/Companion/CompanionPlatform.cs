//
//  CompanionPlatform.cs
//  OpenClicky (Windows)
//
//  What CompanionManager needs from outside Core: the OS services (Core/Platform interfaces the
//  Windows exe implements), the shortcut source (GlobalPushToTalkShortcutMonitor's publishers),
//  and the few things only the app's windows can do (open the main window, find the tour card's
//  button on screen, quit). On the Mac CompanionManager reached all of these directly.
//

using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Companion;

/// <summary>
/// The global shortcuts as GlobalPushToTalkShortcutMonitor publishes them: talk (Ctrl + Win)
/// press/release, and every other companion shortcut. Events are raised on the UI thread.
/// </summary>
public interface ICompanionShortcutSource
{
    /// <summary>shortcutTransitionPublisher: talk pressed / released.</summary>
    event Action<BuddyPushToTalkShortcut.ShortcutTransition>? ShortcutTransition;

    /// <summary>companionShortcutPublisher: dictation, escape, the composer, hands-free.</summary>
    event Action<CompanionShortcutEvent>? CompanionShortcut;

    /// <summary>The key that dictates (Settings → Shortcuts). Takes effect on the next press.</summary>
    DictationHotkey DictationKey { get; set; }

    bool IsRunning { get; }

    /// <summary>Starts listening; idempotent (a running monitor keeps its press in progress).</summary>
    void Start();

    void Stop();
}

/// <summary>What only the app's windows can do for the companion.</summary>
public interface ICompanionHost
{
    /// <summary>OpenClicky's one window at a section or a settings page (a settings page wins).</summary>
    void ShowDictationWindow(DictationSection? section, DictationSettingsPage? settingsPage);

    /// <summary>Opens the window at General and plays the Settings tour.</summary>
    void StartSettingsTour();

    void StopSettingsTour();

    /// <summary>Where the tour card's main button is ("let’s go"), physical pixels, with its monitor; null when no card shows.</summary>
    (ScreenPoint Location, ScreenRect DisplayFrame)? TourPrimaryButtonLocation();

    /// <summary>The tour's "change microphone": the microphone menu at the pointer; <paramref name="chosen"/> runs after a pick.</summary>
    void PresentMicrophoneMenu(Action chosen);

    /// <summary>Shows shell.json in Explorer.</summary>
    void RevealSettingsFile();

    /// <summary>Ends the app (the farewell has been shown).</summary>
    void Quit();
}

/// <summary>Every OS service the companion uses, built once by the composition root.</summary>
public sealed record CompanionServices
{
    public required IMainThread MainThread { get; init; }
    public required Preferences Preferences { get; init; }
    public required DictationSettings DictationSettings { get; init; }
    public required IScreens Screens { get; init; }
    public required IScreenCapture ScreenCapture { get; init; }
    public required IScreenTextRecognizer TextRecognizer { get; init; }
    public required IFocusedElementReader FocusedElements { get; init; }
    public required IFrontmostAppObserver FrontmostApp { get; init; }
    public required IWindowList WindowList { get; init; }
    public required IBrowserTabSource BrowserTabs { get; init; }
    public required IWriteInPlaceHost WriteInPlace { get; init; }
    public required IDesktopActionExecutor DesktopActions { get; init; }
    public required IProcessRunner Processes { get; init; }
    public required IMicrophonePermission MicrophonePermission { get; init; }
    public required IMicrophoneDevices Microphones { get; init; }
    public required IOfflineSpeechRecognizer OfflineSpeech { get; init; }
    public required ISystemVoice SystemVoice { get; init; }
    public required IEarconPlayer Earcons { get; init; }
    public required ILoginItem LoginItem { get; init; }
    public required ICompanionShortcutSource Shortcuts { get; init; }
    public required IGuidedStepInputSource GuidedStepInput { get; init; }
    public required Func<IAudioCapture> CreateAudioCapture { get; init; }
    /// <summary>A new player (the answers, the notices and the recordings each have their own, as on the Mac).</summary>
    public required Func<IAudioPlayer> CreateAudioPlayer { get; init; }
    public required Func<IStreamingPcmPlayer> CreateStreamingPlayer { get; init; }
    /// <summary>The system-prompt windows the pointer must stay off (consent.exe, the credential dialog…), physical pixels.</summary>
    public Func<IEnumerable<PromptWindow>>? PromptWindows { get; init; }
}

/// <summary>The floating UI's models the companion drives (CompanionUi builds them with their windows).</summary>
public sealed record CompanionModels(OverlayModel Overlay, IslandCompanionModel Island, OrbModel Orb, AgentResultCardModel AgentCard);
