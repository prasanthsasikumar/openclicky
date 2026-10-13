// What OpenClicky's one window works with: the Core objects it shows (the dictation settings, the
// writing space, the take history, the account), the OS services (PlatformServices) and the seams
// to the parts being ported alongside it (OpenClicky.Core.Settings.SettingsServices). The Mac's
// pages reached all of these through CompanionManager; here the composition root hands them in.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.AppWindow;

public sealed class MainWindowServices
{
    public required DictationSettings Settings { get; init; }
    public required DictationSpaceStore SpaceStore { get; init; }
    /// <summary>Null when the history file can't be opened (takes still paste; the Your words page says so).</summary>
    public TakeStore? TakeStore { get; init; }
    public required TakeAudioStore AudioStore { get; init; }
    public required Preferences Preferences { get; init; }
    public OpenClickyAuthSession Auth { get; init; } = OpenClickyAuthSession.Shared;

    public required ICompanionSettings Companion { get; init; }
    public required IDictationHistoryActions History { get; init; }
    public required IAppUpdater Updater { get; init; }
    public required ISettingsServices Services { get; init; }
    public required ISettingsTourPointer TourPointer { get; init; }
    public required ISettingsTourNarrator TourNarrator { get; init; }

    public required ILoginItem LoginItem { get; init; }
    public required IMicrophoneDevices Microphones { get; init; }
    public required IMicrophonePermission MicrophonePermission { get; init; }
    public required Func<IAudioCapture> CreateAudioCapture { get; init; }
    public required IFrontmostAppObserver FrontmostApp { get; init; }
    public required ISystemVoice SystemVoice { get; init; }
    public required IAudioPlayer AudioPlayer { get; init; }
    public required IClipboard Clipboard { get; init; }
    public required ITextInserter TextInserter { get; init; }

    /// <summary>
    /// The window over real Core objects and this PC's services, with the stand-ins for what isn't
    /// wired yet (Detached*). Wired (AppComposition.Window()): the composition root builds this with the real
    /// CompanionManager, DictationTakeController, Velopack updater, answers/speech services, overlay
    /// pointer and RecordedVoice narrator.
    /// </summary>
    public static MainWindowServices Detached(PlatformServices platform, DictationSettings settings, DictationSpaceStore spaceStore,
        TakeStore? takeStore, TakeAudioStore audioStore, Preferences preferences) => new()
    {
        Settings = settings,
        SpaceStore = spaceStore,
        TakeStore = takeStore,
        AudioStore = audioStore,
        Preferences = preferences,
        Companion = new DetachedCompanionSettings(preferences),
        History = new DetachedDictationHistoryActions(takeStore, audioStore),
        Updater = new UnconfiguredAppUpdater(),
        Services = new DetachedSettingsServices(platform.Processes),
        TourPointer = new LoggingTourPointer(),
        TourNarrator = new SilentTourNarrator(),
        LoginItem = platform.LoginItem,
        Microphones = platform.Microphones,
        MicrophonePermission = platform.MicrophonePermission,
        CreateAudioCapture = platform.CreateAudioCapture,
        FrontmostApp = platform.FrontmostApp,
        SystemVoice = platform.SystemVoice,
        AudioPlayer = platform.AudioPlayer,
        Clipboard = platform.Clipboard,
        TextInserter = platform.TextInserter,
    };
}

/// <summary>The tour's pointer before the overlay is wired: it writes where it would fly.</summary>
public sealed class LoggingTourPointer : ISettingsTourPointer
{
    public void BeginTour() => AppLog.Write("settings tour: pointer out (no overlay in this build)");

    public Task FlyTo(Core.Pointing.ScreenRect target, string caption)
    {
        var spot = SettingsTourTargets.PointerSpot(target);
        AppLog.Write($"settings tour: point at ({spot.X:0},{spot.Y:0}) \"{caption}\"");
        return Task.CompletedTask;
    }

    public Task Click(Core.Pointing.ScreenRect target) => Task.CompletedTask;

    public void EndTour() { }
}

/// <summary>The narration before RecordedVoice is wired: nothing to play, so each step waits a moment.</summary>
public sealed class SilentTourNarrator : ISettingsTourNarrator
{
    public Task<bool> SayAsync(string line, CancellationToken cancellationToken) => Task.FromResult(false);

    public void Stop() { }
}
