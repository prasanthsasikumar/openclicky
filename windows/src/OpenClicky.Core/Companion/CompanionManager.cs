//
//  CompanionManager.cs
//  OpenClicky (Windows port of CompanionManager.swift)
//
//  Central state manager for the companion. Owns the push-to-talk pipeline (dictation manager +
//  global shortcut monitor + overlay), the answer client, the voices, the Realtime session, the
//  agent lane, the skills, the permission cards and the welcome tour, and mirrors its state into
//  the floating UI's models (OverlayModel, IslandCompanionModel, OrbModel, AgentResultCardModel).
//  The Mac's one 2,700-line file is split by concern into partial files:
//
//    CompanionManager.cs             state, settings (ICompanionSettings), start/stop, permissions
//    CompanionManager.Voice.cs       the shortcuts, push-to-talk into the ask lane, hands-free, typed requests
//    CompanionManager.AskLane.cs     screenshot → Claude → spoken answer, guided steps, notices
//    CompanionManager.AgentLane.cs   the openclicky CLI's Codex threads and the result card
//    CompanionManager.Realtime.cs    the OpenAI Realtime session's wiring
//    CompanionManager.Pointing.cs    pointAt, the [POINT] tag, the demos, the Settings tour's pointer
//    CompanionManager.Captions.cs    captions, notices, (i), pointer presence, quitting
//    CompanionManager.Onboarding.cs  the welcome tour's hooks, model downloads, uses
//    CompanionManager.Skills.cs      skills in the talk lanes, the connect card, skill drafting
//    CompanionManager.Dictation.cs   the dictation take controller's wiring
//    CompanionManager.Teacher.cs     the Teacher plugin's hooks (labels, clearing the board, typed questions)
//
//  Threading: as the Mac's @MainActor class, everything here runs on the UI thread (IMainThread);
//  callbacks from elsewhere are posted to it.
//

using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Agent;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Skills;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

/// <summary>Snapshot of a finished agent turn, shown in the result card (OpenClickyAgentResultSummary).</summary>
public sealed record OpenClickyAgentResultSummary(string ThreadId, string Title, string Text, IReadOnlyList<string> Artifacts, string Status, DateTimeOffset FinishedAt);

public sealed partial class CompanionManager : ObservableObject, ICompanionSettings, IDisposable
{
    // MARK: - Preference keys (the Mac's UserDefaults names)

    public const string AgentModeEnabledKey = "isOpenClickyAgentModeEnabled";
    public const string RealtimeVoiceEnabledKey = "isOpenClickyRealtimeVoiceEnabled";
    public const string AlwaysListeningKey = "isOpenClickyAlwaysListening";
    public const string TrayIconShownOnceKey = "dictation.menuBarIconShownOnce";
    public const string TrayIconVisibleKey = "isOpenClickyMenuBarIconVisible";
    public const string SelectedModelKey = "selectedClaudeModel";
    public const string PointerPresenceKey = "openClickyPointerPresence";
    public const string HasCompletedOnboardingKey = "hasCompletedOnboarding";

    private readonly CompanionServices services;
    private readonly ICompanionHost host;
    private readonly Preferences preferences;

    public OverlayModel Overlay { get; }
    public IslandCompanionModel Island { get; }
    public OrbModel Orb { get; }
    public AgentResultCardModel AgentCard { get; }
    public DictationSettings DictationSettings { get; }

    // MARK: - Published state

    private CompanionVoiceState voiceState = CompanionVoiceState.Idle;

    /// <summary>idle → listening → processing → responding. Mirrored into the overlay, the island and the agent card.</summary>
    public CompanionVoiceState VoiceState
    {
        get => voiceState;
        private set
        {
            if (voiceState == value) return;
            voiceState = value;
            Overlay.VoiceState = value;
            Island.VoiceState = value;
            AgentCard.IsWorking = value == CompanionVoiceState.Processing;
            AgentCard.CanFollowUp = value == CompanionVoiceState.Idle;
            OnPropertyChanged();
            VoiceStateChanged?.Invoke(value);
        }
    }

    /// <summary>Raised after every voice-state change (the welcome tour and the Kokoro warm-up follow it).</summary>
    public event Action<CompanionVoiceState>? VoiceStateChanged;

    [ObservableProperty] private string? lastTranscript;
    /// <summary>Who is answering the question being spoken ("Claude Code", "free credits"), for the island.</summary>
    [ObservableProperty] private string? answeringSourceName;
    [ObservableProperty] private double currentAudioPowerLevel;
    [ObservableProperty] private bool hasMicrophonePermission;
    /// <summary>Latest milestone from a running agent turn ("ran: …"), shown in the island.</summary>
    [ObservableProperty] private string? agentActivityText;
    [ObservableProperty] private IReadOnlyList<string> lastAgentArtifacts = [];
    [ObservableProperty] private OpenClickyAgentResultSummary? lastAgentResult;
    /// <summary>True when the last question ended in a failure sentence instead of an answer (the tour must not count it).</summary>
    [ObservableProperty] private bool lastQuestionFailed;
    /// <summary>When a quick action last did what was asked; the tour's "hand me a task" step watches it.</summary>
    [ObservableProperty] private DateTimeOffset? lastSuccessfulQuickActionAt;
    /// <summary>A dictation take is listening or transcribing: the words go where the cursor is, not to OpenClicky.</summary>
    [ObservableProperty] private bool isDictatingToFrontApp;

    partial void OnLastTranscriptChanged(string? value) => Island.LastTranscript = value;
    partial void OnAnsweringSourceNameChanged(string? value) => Island.AnsweringSourceName = value;
    partial void OnCurrentAudioPowerLevelChanged(double value)
    {
        Overlay.CurrentAudioPowerLevel = value;
        Island.CurrentAudioPowerLevel = value;
    }
    partial void OnAgentActivityTextChanged(string? value)
    {
        Island.AgentActivityText = value;
        AgentCard.AgentActivityText = value;
        UpdateIslandContext();
        if (value is not null) AgentTaskStarted?.Invoke();
    }
    partial void OnIsDictatingToFrontAppChanged(bool value) => Island.IsDictatingToFrontApp = value;
    partial void OnLastSuccessfulQuickActionAtChanged(DateTimeOffset? value)
    {
        if (value is not null) AgentTaskStarted?.Invoke();
    }

    /// <summary>An agent task or a quick action got going (the tour's "hand me a task" step).</summary>
    public event Action? AgentTaskStarted;

    // MARK: - The parts

    private readonly OpenClickyAgentClient openClickyAgentClient;
    private readonly DesktopActionRunner desktopActionRunner;
    private readonly ClaudeAPI claudeAPI;
    private readonly AnswerVoicePlayer answerVoicePlayer;
    private readonly IAudioPlayer noticePlayer;
    private readonly SystemVoice systemVoice;
    public RecordedVoice RecordedVoice { get; }
    public BuddyDictationManager BuddyDictationManager { get; }
    public RealtimeVoiceClient RealtimeVoiceClient { get; }
    public SkillLibraryStore SkillLibraryStore { get; }
    public PermissionPromptController PermissionPromptController { get; }
    public AppConnectPromptController AppConnectPromptController { get; }
    public FirstRunTourController FirstRunTour { get; }
    public ICompanionShortcutSource Shortcuts => services.Shortcuts;
    public CompanionServices Services => services;

    private bool isStarted;

    public CompanionManager(CompanionServices services, CompanionModels models, ICompanionHost host)
    {
        this.services = services;
        this.host = host;
        preferences = services.Preferences;
        Overlay = models.Overlay;
        Island = models.Island;
        Orb = models.Orb;
        AgentCard = models.AgentCard;
        DictationSettings = services.DictationSettings;

        isAgentModeEnabled = preferences.GetOptionalBool(AgentModeEnabledKey) ?? true;
        isRealtimeVoiceEnabled = preferences.GetOptionalBool(RealtimeVoiceEnabledKey) ?? true;
        // Always-on is session-only: a launch always starts in push-to-talk, so a forgotten hands-free
        // toggle never greets the person out of nowhere the next day. The old persisted key is cleared.
        preferences.Remove(AlwaysListeningKey);
        isAlwaysListening = false;
        isTrayIconVisible = ReadTrayIconVisibility(preferences);
        hudPlacement = HUDPlacements.FromRawValue(preferences.GetString(HUDPlacements.DefaultsKey));
        selectedModel = preferences.GetString(SelectedModelKey) ?? ClaudeAPI.DefaultModel;
        pointerPresence = PointerPresenceExtensions.FromRawValue(preferences.GetString(PointerPresenceKey)) ?? PointerPresence.Always;
        pointerPresenceTracker = new PointerPresenceTracker(pointerPresence);

        openClickyAgentClient = new OpenClickyAgentClient(services.Processes);
        desktopActionRunner = DesktopActionRunner.Live(services.DesktopActions);
        // Both read the backend URL per request: built once from it, they kept asking the old
        // backend after shell.json changed while everything else followed the new one.
        claudeAPI = new ClaudeAPI(() => $"{OpenClickyConfiguration.BackendBaseUrl}/chat", selectedModel);
        answerVoicePlayer = new AnswerVoicePlayer(services.CreateAudioPlayer(), () => $"{OpenClickyConfiguration.BackendBaseUrl}/tts");
        noticePlayer = services.CreateAudioPlayer();
        systemVoice = new SystemVoice(services.SystemVoice);
        // The lines whose Mac recording names Mac keys are said the Windows way by this PC's voice.
        RecordedVoice = new RecordedVoice(services.CreateAudioPlayer(),
            windowsSentence: line => Speech.RecordedVoice.WindowsSentence(line,
                Speech.RecordedVoice.DictateKeySpoken(DictationSettings.DictationKey),
                global::OpenClicky.Core.Shortcuts.DictationHotkeys.TalkKeysSpoken(DictationSettings.DictationKey)),
            renderSentence: (sentence, cancellationToken) => systemVoice.AudioAsync(sentence, cancellationToken: cancellationToken),
            dictationKey: () => DictationSettings.DictationKey);
        BuddyDictationManager = new BuddyDictationManager(
            BuddyTranscriptionProviderFactory.MakeDefaultProvider(services.OfflineSpeech),
            services.MainThread, services.CreateAudioCapture, services.MicrophonePermission);
        RealtimeVoiceClient = new RealtimeVoiceClient(
            services.MainThread,
            new RealtimeAudio(services.CreateAudioCapture, services.CreateStreamingPlayer, services.Microphones),
            services.TextRecognizer,
            services.MicrophonePermission);
        SkillLibraryStore = new SkillLibraryStore(
            OpenClickyConfiguration.UserSkillsDirectory,
            OpenClickyConfiguration.AppSkillsDirectory,
            post: action => services.MainThread.Post(action));
        PermissionPromptController = new PermissionPromptController();
        AppConnectPromptController = new AppConnectPromptController(preferences, services.MainThread);
        FirstRunTour = new FirstRunTourController(this, preferences);

        Island.IsAlwaysListening = isAlwaysListening;
        Island.IsClickyCursorEnabled = Overlay.IsClickyCursorEnabled;
        Island.PointerPresence = pointerPresence;
        Island.Placement = hudPlacement;
        Island.DictationKey = DictationSettings.DictationKey;
        Overlay.IsAnswerAudioPlaying = () => answerVoicePlayer.IsPlaying || isSystemVoiceNoticeSpeaking || RecordedVoice.IsPlaying;
        Overlay.PropertyChanged += OnOverlayPropertyChanged;
        Overlay.PointerWakeRequested += () => pointerPresenceTracker.Wake(MonotonicSeconds());
        Overlay.WelcomeFinished += SetupOnboardingVideo;
        AgentCard.FollowUpRequested += (text, threadId) => SubmitTextToAgent(text, threadId: string.IsNullOrEmpty(threadId) ? null : threadId);
        WireIslandButtons();
        WireTeacher();
    }

    private void OnOverlayPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(OverlayModel.IsClickyCursorEnabled):
                Island.IsClickyCursorEnabled = Overlay.IsClickyCursorEnabled;
                OnPropertyChanged(nameof(IsClickyCursorEnabled));
                break;
            case nameof(OverlayModel.IsPointerAwake):
                Island.IsPointerAwake = Overlay.IsPointerAwake;
                break;
            case nameof(OverlayModel.DetectedElementScreenLocation):
            case nameof(OverlayModel.IsCursorCaptionVisible):
                break;
        }
    }

    /// <summary>The island's buttons (CompanionUi wires the (i) itself).</summary>
    private void WireIslandButtons()
    {
        Island.ComposerSubmitted += SubmitTypedRequest;
        ObserveIslandContext();
        Island.SettingsRequested += destination => ShowSettings(destination switch
        {
            IslandSettingsDestination.Dictation => DictationSettingsPage.Dictation,
            IslandSettingsDestination.Keys => DictationSettingsPage.Keys,
            IslandSettingsDestination.Account => DictationSettingsPage.Account,
            _ => DictationSettingsPage.General,
        });
        Island.QuitRequested += QuitWithFarewell;
        Island.StopSpeakingRequested += StopSpeaking;
        Island.StopAskRequested += () => StopAskLane("the island");
        Island.EngineSetupRequested += () => ShowSettings(IslandVoiceDestination.SetupPage(DictationSettings.Engine,
            DictationEngineResolver.UnavailableReason(DictationSettings.Engine)));
        Island.PermissionGrantRequested += PermissionPromptController.Grant;
        Island.PermissionLaterRequested += PermissionPromptController.Dismiss;
        Island.PermissionSettingsRequested += PermissionPromptController.OpenPanel;
        Island.ConnectAnswered += AnswerConnectCard;
        Island.TourPrimaryRequested += FirstRunTour.PrimaryAction;
        Island.TourSecondaryRequested += FirstRunTour.SecondaryAction;
        Island.TourChangeMicrophoneRequested += FirstRunTour.ChangeMicrophone;
        Island.TourFieldSubmitted += text =>
        {
            FirstRunTour.FieldText = text;
            FirstRunTour.SubmitField();
        };
        Island.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(IslandCompanionModel.TourFieldText)) FirstRunTour.FieldText = Island.TourFieldText;
        };
        PermissionPromptController.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(PermissionPromptController.CurrentPrompt)) Island.PermissionPrompt = PermissionPromptController.CurrentPrompt;
        };
        AppConnectPromptController.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(AppConnectPromptController.CurrentPrompt)) Island.ConnectPrompt = IslandConnectCard(AppConnectPromptController.CurrentPrompt);
        };
    }

    // MARK: - Settings (ICompanionSettings)

    private bool isAgentModeEnabled;
    /// <summary>Agent mode: a cheap gate classifies each utterance; work goes to a Codex thread through the CLI.</summary>
    public bool IsAgentModeEnabled => isAgentModeEnabled;

    public void SetAgentModeEnabled(bool isEnabled)
    {
        SetProperty(ref isAgentModeEnabled, isEnabled, nameof(IsAgentModeEnabled));
        preferences.Set(AgentModeEnabledKey, isEnabled);
    }

    private bool isRealtimeVoiceEnabled;
    /// <summary>Speech-to-speech over OpenAI Realtime instead of transcribe → Claude → voice.</summary>
    public bool IsRealtimeVoiceEnabled => isRealtimeVoiceEnabled;

    public void SetRealtimeVoiceEnabled(bool isEnabled)
    {
        SetProperty(ref isRealtimeVoiceEnabled, isEnabled, nameof(IsRealtimeVoiceEnabled));
        preferences.Set(RealtimeVoiceEnabledKey, isEnabled);
        if (isEnabled) WarmUpRealtimeVoice();
        else RealtimeVoiceClient.Disconnect("realtime voice turned off");
        // Hands-free carries on, on whichever lane is now the one.
        ApplyHandsFreeLane();
    }

    private bool isAlwaysListening;
    /// <summary>Always-on listening instead of push-to-talk: fast voice's own turn detection when it is on, else the
    /// standard lane listening on this PC (CompanionManager.StandardHandsFree.cs). Session-only.</summary>
    public bool IsAlwaysListening => isAlwaysListening;

    public void SetAlwaysListening(bool isOn)
    {
        SetProperty(ref isAlwaysListening, isOn, nameof(IsAlwaysListening));
        Island.IsAlwaysListening = isOn;
        WarmUpRealtimeVoice();
        ApplyHandsFreeLane();
    }

    private bool isTrayIconVisible;
    /// <summary>The tray icon (the Mac's menu bar icon): on by default since dictation.</summary>
    public bool IsTrayIconVisible => isTrayIconVisible;

    /// <summary>On by default, and shown once more to everyone who had it off before dictation (the Mac's rule).</summary>
    private static bool ReadTrayIconVisibility(Preferences preferences)
    {
        if (!preferences.GetBool(TrayIconShownOnceKey))
        {
            preferences.Set(TrayIconShownOnceKey, true);
            preferences.Set(TrayIconVisibleKey, true);
            return true;
        }
        return preferences.GetOptionalBool(TrayIconVisibleKey) ?? true;
    }

    public void SetTrayIconVisible(bool isVisible)
    {
        SetProperty(ref isTrayIconVisible, isVisible, nameof(IsTrayIconVisible));
        preferences.Set(TrayIconVisibleKey, isVisible);
    }

    private HUDPlacement hudPlacement;
    /// <summary>Where the island lives: the top, a side drawer, or hidden while idle.</summary>
    public HUDPlacement HudPlacement => hudPlacement;

    public void SetHudPlacement(HUDPlacement placement)
    {
        SetProperty(ref hudPlacement, placement, nameof(HudPlacement));
        preferences.Set(HUDPlacements.DefaultsKey, placement.RawValue());
        Island.SetPlacement(placement);
        // With the island hidden the tray icon is the way into the panel and the window.
        if (placement == HUDPlacement.Hidden) SetTrayIconVisible(true);
    }

    private string selectedModel;
    /// <summary>The Claude model for spoken answers.</summary>
    public string SelectedModel => selectedModel;

    public void SetSelectedModel(string model)
    {
        SetProperty(ref selectedModel, model, nameof(SelectedModel));
        preferences.Set(SelectedModelKey, model);
        claudeAPI.Model = model;
    }

    /// <summary>"Show OpenClicky": off means transient cursor mode.</summary>
    public bool IsClickyCursorEnabled => Overlay.IsClickyCursorEnabled;

    public void SetClickyCursorEnabled(bool isEnabled) => Overlay.SetClickyCursorEnabled(isEnabled);


    public void ReplayWelcomeTour() => FirstRunTour.Replay();

    public bool IsWelcomeTourDone => FirstRunTour.IsFinished;

    /// <summary>Whether the person has been through onboarding at least once.</summary>
    public bool HasCompletedOnboarding
    {
        get => preferences.GetBool(HasCompletedOnboardingKey);
        set => preferences.Set(HasCompletedOnboardingKey, value);
    }

    // MARK: - Permissions (the microphone, and online speech once Windows speech asks for it)

    /// <summary>Every permission setup needs is in (the Mac's four collapse to the microphone). Online speech is
    /// asked for only when Windows speech needs it, so it does not hold up setup.</summary>
    public bool AllPermissionsGranted => HasMicrophonePermission;

    public PermissionStatus PermissionStatus =>
        new(Microphone: HasMicrophonePermission, OnlineSpeech: !services.OfflineSpeech.OnlineSpeechWasRefused);

    private CancellationTokenSource? permissionPolling;

    public void RefreshAllPermissions()
    {
        var previouslyHadMicrophone = HasMicrophonePermission;
        // An unreadable consent store is not a refusal: the first capture tells.
        HasMicrophonePermission = services.MicrophonePermission.ReadConsent() is MicrophoneConsent.Allowed or MicrophoneConsent.Unknown;
        if (previouslyHadMicrophone != HasMicrophonePermission)
        {
            AppLog.Write($"permissions: microphone {(HasMicrophonePermission ? "allowed" : "off")}");
        }
        PermissionPromptController.Update(PermissionStatus);
    }

    /// <summary>Polls every 1.5 s so the card goes away on its own once access lands in Settings.</summary>
    private void StartPermissionPolling()
    {
        permissionPolling?.Cancel();
        var cancellation = new CancellationTokenSource();
        permissionPolling = cancellation;
        _ = PollPermissionsAsync(cancellation.Token);
    }

    private async Task PollPermissionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken); }
            catch (OperationCanceledException) { return; }
            RefreshAllPermissions();
        }
    }

    /// <summary>Wires the permission card's buttons; the microphone's grant opens Settings → Privacy → Microphone,
    /// online speech's "turn on" switches it on directly (Settings → Privacy → Speech when that fails).</summary>
    private void StartPermissionPrompts()
    {
        PermissionPromptController.PerformRequest = step =>
        {
            if (step != PermissionStep.OnlineSpeech)
            {
                services.MicrophonePermission.OpenPrivacySettings();
                return;
            }
            if (services.OfflineSpeech.AcceptOnlineSpeech())
            {
                AppLog.Write("permissions: online speech recognition switched on from the island");
                RefreshAllPermissions();
                return;
            }
            AppLog.Write("permissions: could not switch online speech recognition on; opening speech settings");
            services.OfflineSpeech.OpenSpeechPrivacySettings();
        };
        PermissionPromptController.OpenSettings = () => ShowSettings();
        PermissionPromptController.Update(PermissionStatus);
        Island.PermissionPrompt = PermissionPromptController.CurrentPrompt;
        // The welcome tour waits for the last permission, then takes the island.
        FirstRunTour.Start();
        // Kokoro comes into memory only when a question starts (the ask shortcut, or a typed
        // question); hands-free answers in OpenAI's realtime voice and never needs it.
        VoiceStateChanged += state =>
        {
            if (state is CompanionVoiceState.Listening or CompanionVoiceState.Processing && !IsAlwaysListening) KokoroVoice.Shared?.PrepareForAnswer();
            // A spoken answer counts as a use.
            if (state == CompanionVoiceState.Responding) NoteUse();
        };
        // Kokoro (and Parakeet) download on their own once setup is long done; a few seconds in, so
        // the launch itself stays quick.
        _ = StartAutomaticModelDownloadsSoonAsync();
    }

    // MARK: - Start / stop

    /// <summary>An account hears with the offline engine; own keys keep the configured provider.</summary>
    private void ApplyHearingForAccount()
    {
        BuddyDictationManager.ReplaceTranscriptionProvider(
            AccountCapabilities.Current().HearsOnDevice
                ? new OnDeviceHearingProvider(services.OfflineSpeech)
                : BuddyTranscriptionProviderFactory.MakeDefaultProvider(services.OfflineSpeech));
    }

    public void Start()
    {
        if (isStarted) return;
        isStarted = true;
        RefreshAllPermissions();
        pointerPresenceTracker = new PointerPresenceTracker(pointerPresence);
        Overlay.IsPointerAwake = pointerPresenceTracker.IsAwake;
        StartPointerPresenceMonitor();
        AppLog.Write($"companion: start, mic {HasMicrophonePermission}, onboarded {HasCompletedOnboarding}");
        StartPermissionPolling();
        BindDictationManager();
        services.Shortcuts.ShortcutTransition += HandleShortcutTransition;
        services.Shortcuts.CompanionShortcut += HandleCompanionShortcut;
        // The keyboard hook needs no permission on Windows (the Mac waited for Accessibility).
        try { services.Shortcuts.Start(); }
        catch (Exception error) { AppLog.Write($"shortcuts: couldn't start the keyboard hook: {error.Message}"); }
        ApplyHearingForAccount();
        // A key or token changed in shell.json: the open Realtime session was minted with the old one.
        OpenClickyConfiguration.CredentialsChanged += OnCredentialsChanged;
        AccountProfileStore.ProfileChanged += OnAccountProfileChanged;
        AccountProfileStore.Shared.ShowNotice = notice => services.MainThread.Post(() => ShowQuietNotice(notice));
        AccountProfileStore.Shared.Start();

        // Docking the pointer is gone: someone who had it docked gets it back under "the pointer shows".
        if (preferences.Contains(OverlayModel.RetiredDockedPreferenceKey))
        {
            preferences.Remove(OverlayModel.RetiredDockedPreferenceKey);
            AppLog.Write("pointer: the retired dock setting was cleared");
        }
        // Onboarded with every permission still granted: the pointer shows at once.
        if (HasCompletedOnboarding && AllPermissionsGranted && IsClickyCursorEnabled) Overlay.ShowOverlayOnAllScreens();

        StartDictation();
        StartPermissionPrompts();
        if (ConnectorsAreEnabled) StartAppConnectPrompts();
        // Keep the Realtime voice session warm so talking is instant.
        if (HasCompletedOnboarding && AllPermissionsGranted) WarmUpRealtimeVoice();
    }

    private void OnCredentialsChanged() => services.MainThread.Post(() =>
    {
        AppLog.Write("shell.json credentials changed; reconnecting realtime");
        ApplyHearingForAccount();
        if (UsesRealtimeVoice) WarmUpRealtimeVoice();
        // A key added or removed can move hands-free between fast voice and the standard lane.
        ApplyHandsFreeLane();
    });

    private void OnAccountProfileChanged() => services.MainThread.Post(ApplyHearingForAccount);

    public void Stop()
    {
        if (!isStarted) return;
        isStarted = false;
        StopDictation();
        PermissionPromptController.Dismiss();
        AppConnectPromptController.Stop();
        RealtimeVoiceClient.Disconnect(null);
        StopStandardHandsFree();
        services.Shortcuts.Stop();
        services.Shortcuts.ShortcutTransition -= HandleShortcutTransition;
        services.Shortcuts.CompanionShortcut -= HandleCompanionShortcut;
        BuddyDictationManager.CancelCurrentDictation();
        Overlay.HideOverlay();
        Overlay.CancelTransientHide();
        InterruptCurrentAnswer(includingAgentWork: false);
        currentResponseCancellation = null;
        Overlay.DismissCursorCaption();
        permissionPolling?.Cancel();
        pointerPresenceTimer?.Dispose();
        pointerPresenceTimer = null;
        OpenClickyConfiguration.CredentialsChanged -= OnCredentialsChanged;
        AccountProfileStore.ProfileChanged -= OnAccountProfileChanged;
    }

    /// <summary>Windows says memory is low: give the local models' memory back (the Mac's memory-pressure source).</summary>
    public void UnloadForMemoryPressure()
    {
        AppLog.Write("memory: low; unloading the local models");
        KokoroVoice.Shared?.UnloadForMemoryPressure();
        ParakeetDictation.Shared?.UnloadForMemoryPressure();
        MoonshineDictation.Shared?.UnloadForMemoryPressure();
    }

    /// <summary>The PC woke up: a socket can look open and be dead, so the next press checks it first.</summary>
    public void NoteSystemResumed()
    {
        AppLog.Write("power: resumed from sleep");
        RealtimeVoiceClient.NoteSystemResumed();
    }

    public void Dispose()
    {
        Stop();
        teacherSwitchObservation?.Dispose();
        RealtimeVoiceClient.Dispose();
        BuddyDictationManager.Dispose();
        SkillLibraryStore.Dispose();
        AppConnectPromptController.Dispose();
    }

    private static double MonotonicSeconds() => Environment.TickCount64 / 1000.0;
}
