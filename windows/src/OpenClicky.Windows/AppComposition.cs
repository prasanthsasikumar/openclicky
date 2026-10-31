// The composition root (CompanionAppDelegate.applicationDidFinishLaunching): every OS service, the
// configuration watcher and the sign-in, the local voices, the floating UI, CompanionManager, the
// one window, the tray icon, the updater and the login item, wired together once.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Shortcuts;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Speech;
using OpenClicky.Windows.AppWindow;
using OpenClicky.Windows.Companion;
using OpenClicky.Windows.Dictation;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Platform;
using OpenClicky.Windows.Plugins.Teacher;
using OpenClicky.Windows.Tray;

namespace OpenClicky.Windows;

public sealed class AppComposition : ICompanionHost, IDisposable
{
    private readonly Application application;
    private readonly ISingleInstance singleInstance;
    private readonly Dispatcher dispatcher;
    private readonly PlatformServices platform;
    private readonly Preferences preferences;
    private readonly DictationSettings dictationSettings;
    private readonly DictationSpaceStore spaceStore;
    private readonly TakeStore? takeStore;
    private readonly CompanionUi companionUi;
    private readonly GlobalShortcutMonitor shortcuts;
    private readonly GuidedStepInputSource guidedStepInput;
    private readonly VelopackAppUpdater updater;
    private readonly TrayIcon trayIcon;
    private readonly DispatcherTimer memoryWatch;
    private readonly nint lowMemoryNotification;
    private DictationWindow? dictationWindow;
    private MainWindowServices? mainWindowServices;
    private bool isDisposed;

    public CompanionManager Companion { get; }

    public AppComposition(Application application, ISingleInstance singleInstance, IReadOnlyList<string> args)
    {
        this.application = application;
        this.singleInstance = singleInstance;
        dispatcher = application.Dispatcher;
        platform = new PlatformServices(dispatcher);
        preferences = Preferences.Shared;
        dictationSettings = DictationSettings.Shared;

        OpenClickyConfiguration.EnsureSettingsFileExists();
        OpenClickyConfiguration.StartWatchingSettingsFile(SynchronizationContext.Current);
        OpenClickyConfiguration.SettingsFileUnreadable += reason => AppLog.Write($"settings: shell.json unreadable: {reason}");
        OpenClickyAuthSession.Shared.Start(SynchronizationContext.Current);

        RegisterLocalModels(platform.Processes, platform.Network, preferences, platform.MainThread, dictationSettings);
        SpeechVoiceServices.Current = SpeechVoiceServices.ForThisPC(platform.SystemVoice);
        if (KokoroVoice.Shared is { } kokoro) kokoro.IsCurrentVoice = () => SpeechVoiceChoices.Current() == SpeechVoiceChoice.Kokoro;

        spaceStore = new DictationSpaceStore();
        try { takeStore = new TakeStore(OpenClickyPaths.DictationDatabaseFile); }
        catch (Exception error) { AppLog.Write($"take store unavailable: {error.Message}"); }

        companionUi = new CompanionUi(platform.Screens, preferences);
        shortcuts = new GlobalShortcutMonitor(platform.KeyboardMonitor, platform.MainThread);
        guidedStepInput = new GuidedStepInputSource(platform.KeyboardMonitor);
        var services = new CompanionServices
        {
            MainThread = platform.MainThread,
            Preferences = preferences,
            DictationSettings = dictationSettings,
            Screens = platform.Screens,
            ScreenCapture = platform.ScreenCapture,
            TextRecognizer = platform.TextRecognizer,
            FocusedElements = platform.FocusedElements,
            FrontmostApp = platform.FrontmostApp,
            WindowList = platform.WindowList,
            BrowserTabs = new BrowserTabSource(platform.BrowserTabs, platform.WindowList),
            WriteInPlace = new WriteInPlaceHost(platform.FocusedElements, platform.TextInserter),
            DesktopActions = platform.DesktopActions,
            Processes = platform.Processes,
            MicrophonePermission = platform.MicrophonePermission,
            Microphones = platform.Microphones,
            OfflineSpeech = platform.OfflineSpeech,
            SystemVoice = platform.SystemVoice,
            Earcons = platform.Earcons,
            LoginItem = platform.LoginItem,
            Shortcuts = shortcuts,
            GuidedStepInput = guidedStepInput,
            CreateAudioCapture = platform.CreateAudioCapture,
            CreateAudioPlayer = () => new AudioPlayer(),
            CreateStreamingPlayer = () => platform.CreateStreamingPlayer(),
            CreateDictationTakeController = orb => WindowsDictationHost.CreateTakeController(platform, dictationSettings, spaceStore, takeStore, orb),
        };
        // The Teacher plugin's board windows (its state is Core's TeacherBoard).
        TeacherBoard.Shared.Surface = new TeacherBoardWindows(platform.Screens);
        Companion = new CompanionManager(services,
            new CompanionModels(companionUi.Overlay, companionUi.Island, companionUi.Orb, companionUi.AgentCard), this);

        updater = new VelopackAppUpdater(preferences, new UpdatePromptSheets(dispatcher, dictationSettings));
        trayIcon = new TrayIcon(ReadTrayState);
        WireTray();

        singleInstance.ArgumentsReceived += _ => dispatcher.BeginInvoke(() => Companion.ShowDictationWindow());
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        lowMemoryNotification = CreateMemoryResourceNotification(LowMemoryResourceNotification);
        memoryWatch = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => CheckMemory(), dispatcher);
    }

    /// <summary>KokoroVoice.Shared, ParakeetDictation.Shared and MoonshineDictation.Shared, run by this exe's helper flags.</summary>
    public static void RegisterLocalModels(IProcessRunner processes, INetworkReachability network, Preferences preferences, IMainThread? mainThread,
                                           DictationSettings? dictationSettings = null)
    {
        var helperPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenClicky.exe");
        KokoroVoice.Shared ??= new KokoroVoice(processes, network, preferences, helperPath, mainThread);
        MoonshineDictation.Shared ??= new MoonshineDictation(processes, helperPath);
        if (ParakeetDictation.Shared is null)
        {
            var parakeet = new ParakeetDictation(processes, network, preferences, helperPath, mainThread);
            if (dictationSettings is not null) parakeet.UseDictationSettings(dictationSettings);
            ParakeetDictation.Shared = parakeet;
        }
    }

    public void Start()
    {
        Companion.Start();
        companionUi.Show();
        trayIcon.IsVisible = TrayShouldShow();
        updater.Start();
        memoryWatch.Start();
        if (Companion.DictationTakeController is { } controller) controller.HideOwnWindows = () => dictationWindow?.Hide();
        // Launching at sign-in is opt-in (`registerAsLoginItem` in shell.json), as on the Mac.
        if (OpenClickyConfiguration.Settings.RegisterAsLoginItem == true && !platform.LoginItem.IsEnabled) platform.LoginItem.SetEnabled(true);
        AppLog.Write("launch: ready");
    }

    // MARK: - The tray

    private bool TrayShouldShow() => Companion.IsTrayIconVisible || Companion.HudPlacement == HUDPlacement.Hidden;

    private TrayMenuState ReadTrayState() => new(
        TrayMenuState.StatusTextFor(Companion.VoiceState.ToString().ToLowerInvariant(), Companion.AgentActivityText is not null),
        Companion.IsAgentModeEnabled,
        dictationSettings.DictationKey.KeycapLabel());

    private void WireTray()
    {
        trayIcon.AgentModeToggled += () => Companion.SetAgentModeEnabled(!Companion.IsAgentModeEnabled);
        trayIcon.OpenWindowRequested += () => Companion.ShowDictationWindow(DictationSection.Words);
        trayIcon.OpenIslandRequested += companionUi.IslandWindows.OpenPanel;
        trayIcon.SettingsRequested += () => Companion.ShowSettings();
        trayIcon.ReplayWelcomeRequested += Companion.ReplayWelcomeTour;
        trayIcon.QuitRequested += Companion.QuitWithFarewell;
        // The app icon's colour (Settings › General): the tray and every window drawn from now on; the
        // Settings window follows it itself.
        void ApplyAppIconColor()
        {
            OpenClickyMarkDrawing.CurrentColor = dictationSettings.AppIconColor;
            trayIcon.SetColor(dictationSettings.AppIconColor);
        }
        ApplyAppIconColor();
        dictationSettings.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(DictationSettings.AppIconColor)) dispatcher.BeginInvoke(ApplyAppIconColor);
        };
        Companion.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(CompanionManager.IsTrayIconVisible) or nameof(CompanionManager.HudPlacement))
            {
                trayIcon.IsVisible = TrayShouldShow();
            }
        };
    }

    // MARK: - ICompanionHost

    private DictationWindow Window()
    {
        if (dictationWindow is not null) return dictationWindow;
        var controller = Companion.DictationTakeController;
        mainWindowServices = new MainWindowServices
        {
            Settings = dictationSettings,
            SpaceStore = spaceStore,
            TakeStore = takeStore,
            AudioStore = controller?.AudioStore ?? new TakeAudioStore(),
            Preferences = preferences,
            Companion = Companion,
            History = new DictationHistoryActions(Companion),
            Updater = updater,
            Services = new LiveSettingsServices(
                KokoroVoice.Shared is { } kokoro ? ModelDownloadRow.For(kokoro, platform.MainThread) : new DetachedModelDownload(KokoroVoice.DownloadSizeText),
                ParakeetDictation.Shared is { } parakeet ? ModelDownloadRow.For(parakeet, platform.MainThread) : new DetachedModelDownload(ParakeetDictation.DownloadSizeText),
                new AudioPlayer()),
            TourPointer = Companion.SettingsTourPointer,
            TourNarrator = new RecordedVoiceTourNarrator(Companion.RecordedVoice),
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
        dictationWindow = new DictationWindow(mainWindowServices);
        return dictationWindow;
    }

    public void ShowDictationWindow(DictationSection? section, DictationSettingsPage? settingsPage) => Window().Open(section, settingsPage);

    public void StartSettingsTour()
    {
        var window = Window();
        window.Open(settingsPage: DictationSettingsPage.General);
        window.StartSettingsTour();
    }

    public void StopSettingsTour() => dictationWindow?.Tour.Stop();

    public (ScreenPoint Location, ScreenRect DisplayFrame)? TourPrimaryButtonLocation() => companionUi.IslandWindows.TourPrimaryButtonLocation();

    public void PresentMicrophoneMenu(Action chosen) =>
        MicrophoneMenu.Present(platform.Microphones, dictationSettings.PreferredMicrophoneUID, microphoneId =>
        {
            dictationSettings.PreferredMicrophoneUID = microphoneId;
            chosen();
        });

    public void RevealSettingsFile()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{OpenClickyConfiguration.SettingsFilePath}\""); }
        catch (Exception error) { AppLog.Write($"settings: couldn't reveal shell.json ({error.Message})"); }
    }

    public void Quit()
    {
        AppLog.Write("quit");
        // Stopped now, with the windows still open: at Application.Exit they are closed already,
        // and hiding a closed window throws ("Cannot set Visibility … after a Window has closed").
        try
        {
            Dispose();
        }
        catch (Exception error)
        {
            AppLog.Write($"quit: stop failed: {error.Message}");
        }
        application.Shutdown();
    }

    // MARK: - Power and memory

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Resume) dispatcher.BeginInvoke(Companion.NoteSystemResumed);
    }

    private const int LowMemoryResourceNotification = 0;
    private bool wasLowOnMemory;

    /// <summary>Windows' low-memory signal (the Mac's memory-pressure source): the local models give their memory back.</summary>
    private void CheckMemory()
    {
        if (lowMemoryNotification == 0 || !QueryMemoryResourceNotification(lowMemoryNotification, out var isLow)) return;
        if (isLow && !wasLowOnMemory) Companion.UnloadForMemoryPressure();
        wasLowOnMemory = isLow;
    }

    [DllImport("kernel32.dll")]
    private static extern nint CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll")]
    private static extern bool QueryMemoryResourceNotification(nint resourceNotificationHandle, out bool resourceState);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        memoryWatch.Stop();
        if (lowMemoryNotification != 0) CloseHandle(lowMemoryNotification);
        try
        {
            Companion.Dispose();
        }
        catch (Exception error)
        {
            AppLog.Write($"quit: companion stop failed: {error.Message}");
        }
        trayIcon.Dispose();
        updater.Dispose();
        guidedStepInput.Dispose();
        companionUi.Dispose();
        if (dictationWindow is not null) { dictationWindow.HidesOnClose = false; dictationWindow.Close(); }
        takeStore?.Dispose();
        OpenClickyConfiguration.StopWatchingSettingsFile();
        platform.Dispose();
    }
}
