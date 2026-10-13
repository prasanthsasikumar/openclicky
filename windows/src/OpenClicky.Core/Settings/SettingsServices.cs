// The seams between the window and the parts of OpenClicky being ported alongside it. The Mac's
// pages call CompanionManager, DictationTakeController, AnswerSourceCheck, SpeechVoiceChoice,
// KokoroVoice, ParakeetDictation, PlanCLIs and AppUpdater directly; on Windows the window gets
// these interfaces, the integration step implements them over the real objects, and the
// Detached* classes below stand in until then (and for the window demo). Each member names the
// Swift call it replaces.

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Settings;

/// <summary>
/// The companion's own settings the window shows and changes (CompanionManager's @Published values
/// and their setters). Raises PropertyChanged with the property names below.
/// Wired (CompanionManager, DictationHistoryActions, VelopackAppUpdater, LiveSettingsServices): implement over the Companion port's CompanionManager.
/// </summary>
public interface ICompanionSettings : INotifyPropertyChanged
{
    /// <summary>isClickyCursorEnabled / setClickyCursorEnabled.</summary>
    bool IsClickyCursorEnabled { get; }
    void SetClickyCursorEnabled(bool isEnabled);

    /// <summary>pointerPresence / setPointerPresence.</summary>
    PointerPresence PointerPresence { get; }
    void SetPointerPresence(PointerPresence presence);

    /// <summary>isCursorDocked / setCursorDocked.</summary>
    bool IsCursorDocked { get; }
    void SetCursorDocked(bool isDocked);

    /// <summary>isAgentModeEnabled / setAgentModeEnabled.</summary>
    bool IsAgentModeEnabled { get; }
    void SetAgentModeEnabled(bool isEnabled);

    /// <summary>selectedModel ("claude-sonnet-4-6" or "claude-opus-4-6") / setSelectedModel.</summary>
    string SelectedModel { get; }
    void SetSelectedModel(string model);

    /// <summary>isRealtimeVoiceEnabled / setRealtimeVoiceEnabled.</summary>
    bool IsRealtimeVoiceEnabled { get; }
    void SetRealtimeVoiceEnabled(bool isEnabled);

    /// <summary>isAlwaysListening / setAlwaysListening.</summary>
    bool IsAlwaysListening { get; }
    void SetAlwaysListening(bool isOn);

    /// <summary>hudPlacement / setHUDPlacement.</summary>
    HUDPlacement HudPlacement { get; }
    void SetHudPlacement(HUDPlacement placement);

    /// <summary>isMenuBarIconVisible / setMenuBarIconVisible: the tray icon.</summary>
    bool IsTrayIconVisible { get; }
    void SetTrayIconVisible(bool isVisible);

    /// <summary>firstRunTour.replay().</summary>
    void ReplayWelcomeTour();

    /// <summary>Whether the welcome tour is done (the Settings tour starts on its own only after it).</summary>
    bool IsWelcomeTourDone { get; }
}

/// <summary>
/// What the Your words page asks of the dictation controller (DictationTakeController).
/// Wired (CompanionManager, DictationHistoryActions, VelopackAppUpdater, LiveSettingsServices): implement over the Dictation port's DictationTakeController.
/// </summary>
public interface IDictationHistoryActions
{
    /// <summary>$historyVersion: a take was saved, changed or deleted.</summary>
    event Action? HistoryChanged;

    /// <summary>$lastTake: a take finished (raised on the UI thread). The page's "try it here" box catches one that landed nowhere.</summary>
    event Action<TakeRecord>? TakeFinished;

    /// <summary>canRetry(takeID:): a failed take whose audio was kept.</summary>
    bool CanRetry(Guid takeId);

    /// <summary>retry(takeID:): hears the saved recording again.</summary>
    Task RetryAsync(Guid takeId);

    /// <summary>historyMatches(for:): the takes an "ask" draws from.</summary>
    IReadOnlyList<TakeRecord> HistoryMatches(string question);

    /// <summary>askHistory(_:): a model's answer across the takes.</summary>
    Task<string> AskHistoryAsync(string question);

    /// <summary>pasteLast(): the last take at the cursor again.</summary>
    void PasteLast();

    /// <summary>historyDidChange(): after the window changed the history itself.</summary>
    void HistoryDidChange();
}

/// <summary>
/// Software updates (Sparkle's AppUpdater on the Mac). Wired (CompanionManager, DictationHistoryActions, VelopackAppUpdater, LiveSettingsServices): Velopack UpdateManager.
/// </summary>
public interface IAppUpdater : INotifyPropertyChanged
{
    /// <summary>False for a development build with no update feed.</summary>
    bool IsConfigured { get; }

    bool AutomaticallyChecks { get; set; }

    /// <summary>"last checked today at 9:41", lower case; the row capitalises it.</summary>
    string LastCheckDescription { get; }

    void CheckNow();
}

public enum PlanCliStatus
{
    NotInstalled,
    InstalledSignedOut,
    Ready,
}

/// <summary>Where a model download stands (the Speech port's ModelInstallState).</summary>
public sealed record ModelDownloadState
{
    public enum Stage { NotInstalled, Downloading, Unpacking, Failed, Installed }

    public required Stage Kind { get; init; }
    /// <summary>0…1 while downloading.</summary>
    public double Fraction { get; init; }
    /// <summary>What went wrong, for Failed.</summary>
    public string? Message { get; init; }

    public bool IsBusy => Kind is Stage.Downloading or Stage.Unpacking;

    public static ModelDownloadState NotInstalled { get; } = new() { Kind = Stage.NotInstalled };
    public static ModelDownloadState Installed { get; } = new() { Kind = Stage.Installed };
    public static ModelDownloadState Unpacking { get; } = new() { Kind = Stage.Unpacking };
    public static ModelDownloadState Downloading(double fraction) => new() { Kind = Stage.Downloading, Fraction = fraction };
    public static ModelDownloadState Failed(string message) => new() { Kind = Stage.Failed, Message = message };
}

/// <summary>
/// One downloadable model's row (Kokoro, Parakeet): its state, raised as PropertyChanged("State").
/// Wired (CompanionManager, DictationHistoryActions, VelopackAppUpdater, LiveSettingsServices): KokoroVoice.shared / ParakeetDictation.shared from the Speech port.
/// </summary>
public interface IModelDownload : INotifyPropertyChanged
{
    ModelDownloadState State { get; }

    /// <summary>"350 MB", "465 MB".</summary>
    string DownloadSizeText { get; }

    void Download();

    void CancelDownload();

    void Remove();
}

/// <summary>An entry of a picker: its stored id and what it says.</summary>
public sealed record SettingsOption(string Id, string Title);

/// <summary>What Settings → Voice shows for the voice picker.</summary>
public sealed record VoicePickerState
{
    /// <summary>The voices offered, by SpeechVoiceChoice raw value ("automatic", "account", "openai", …, "kokoro", "mac").</summary>
    public required IReadOnlyList<SettingsOption> Offered { get; init; }
    public required string PickedId { get; init; }
    /// <summary>"Now: …" under the picker.</summary>
    public required string NowSpeakingSentence { get; init; }
    /// <summary>Kokoro's rows show (picked, or downloading on its own).</summary>
    public bool ShowsKokoroRows { get; init; }
    /// <summary>This PC's voice row shows (speaking now, or picked).</summary>
    public bool ShowsSystemVoiceRow { get; init; }
    /// <summary>Fewer voices are offered than exist: "More voices" points at Keys & Plans.</summary>
    public bool HasMoreVoices { get; init; }
}

/// <summary>
/// The answers and voices the Settings pages test and pick.
/// Wired (CompanionManager, DictationHistoryActions, VelopackAppUpdater, LiveSettingsServices): implement over Core/Answers (AnswerSourceCheck, PlanCLIs) and Core/Speech
/// (SpeechVoiceChoice, CloudVoiceSpeaker, KokoroVoice, ParakeetDictation).
/// </summary>
public interface ISettingsServices
{
    /// <summary>AnswerSourceCheck.run(source…).sentence: "✓ Works. It answered in 3.8 s." or what failed.</summary>
    Task<string> TestAnswerSourceAsync(AnswerSource source, CancellationToken cancellationToken = default);

    /// <summary>CloudVoiceSpeaker.audio(for: "ok", voice:) for a voice key's Test button: "✓ Works. It can speak your answers." or what failed.</summary>
    Task<string> TestVoiceKeyAsync(string voiceId, CancellationToken cancellationToken = default);

    /// <summary>PlanCLIs.claudeCodeStatus() / codexStatus().</summary>
    Task<PlanCliStatus> PlanStatusAsync(AnswerSource plan, CancellationToken cancellationToken = default);

    /// <summary>SpeechVoiceChoice.offered / resolve and the "Now: …" sentence, for Settings → Voice.</summary>
    VoicePickerState VoicePicker(AppRegion region);

    /// <summary>Writes speechVoice to shell.json (null for Automatic); picking Kokoro downloads it.</summary>
    void PickVoice(string voiceId);

    /// <summary>VoicePreview.play(voice): "hi, i'm clicky. this is how i sound."</summary>
    Task PreviewVoiceAsync(string voiceId);

    /// <summary>KokoroVoice.speakers for the reply language, as (speaker id, label).</summary>
    IReadOnlyList<SettingsOption> KokoroSpeakers(string languageCode);

    /// <summary>The Kokoro speaker in use for the language (kokoroSpeaker in shell.json or the language's default).</summary>
    string? CurrentKokoroSpeaker(string languageCode);

    IModelDownload Kokoro { get; }

    IModelDownload Parakeet { get; }
}

// MARK: - Stand-ins until the integration step

/// <summary>The companion's settings kept in memory (and the island placement in Preferences), for the window demo.</summary>
public sealed class DetachedCompanionSettings : ObservableObject, ICompanionSettings
{
    private readonly Preferences preferences;
    private bool isClickyCursorEnabled = true;
    private PointerPresence pointerPresence = PointerPresence.Always;
    private bool isCursorDocked;
    private bool isAgentModeEnabled;
    private string selectedModel = "claude-sonnet-4-6";
    private bool isRealtimeVoiceEnabled;
    private bool isAlwaysListening;
    private HUDPlacement hudPlacement;
    private bool isTrayIconVisible = true;

    public DetachedCompanionSettings(Preferences preferences)
    {
        this.preferences = preferences;
        hudPlacement = HUDPlacements.FromRawValue(preferences.GetString(HUDPlacements.DefaultsKey));
    }

    public bool IsClickyCursorEnabled => isClickyCursorEnabled;
    public void SetClickyCursorEnabled(bool isEnabled) => SetProperty(ref isClickyCursorEnabled, isEnabled, nameof(IsClickyCursorEnabled));

    public PointerPresence PointerPresence => pointerPresence;
    public void SetPointerPresence(PointerPresence presence) => SetProperty(ref pointerPresence, presence, nameof(PointerPresence));

    public bool IsCursorDocked => isCursorDocked;
    public void SetCursorDocked(bool isDocked) => SetProperty(ref isCursorDocked, isDocked, nameof(IsCursorDocked));

    public bool IsAgentModeEnabled => isAgentModeEnabled;
    public void SetAgentModeEnabled(bool isEnabled) => SetProperty(ref isAgentModeEnabled, isEnabled, nameof(IsAgentModeEnabled));

    public string SelectedModel => selectedModel;
    public void SetSelectedModel(string model) => SetProperty(ref selectedModel, model, nameof(SelectedModel));

    public bool IsRealtimeVoiceEnabled => isRealtimeVoiceEnabled;
    public void SetRealtimeVoiceEnabled(bool isEnabled) => SetProperty(ref isRealtimeVoiceEnabled, isEnabled, nameof(IsRealtimeVoiceEnabled));

    public bool IsAlwaysListening => isAlwaysListening;
    public void SetAlwaysListening(bool isOn) => SetProperty(ref isAlwaysListening, isOn, nameof(IsAlwaysListening));

    public HUDPlacement HudPlacement => hudPlacement;
    public void SetHudPlacement(HUDPlacement placement)
    {
        if (SetProperty(ref hudPlacement, placement, nameof(HudPlacement))) preferences.Set(HUDPlacements.DefaultsKey, placement.RawValue());
    }

    public bool IsTrayIconVisible => isTrayIconVisible;
    public void SetTrayIconVisible(bool isVisible) => SetProperty(ref isTrayIconVisible, isVisible, nameof(IsTrayIconVisible));

    public void ReplayWelcomeTour() => AppLog.Write("welcome tour: replay asked for (not wired in this build)");

    public bool IsWelcomeTourDone => true;
}

/// <summary>The history actions over the take store alone: no retry, no model to ask.</summary>
public sealed class DetachedDictationHistoryActions(TakeStore? takeStore, TakeAudioStore? audioStore) : IDictationHistoryActions
{
    public event Action? HistoryChanged;
#pragma warning disable CS0067 // Raised by the real controller only.
    public event Action<TakeRecord>? TakeFinished;
#pragma warning restore CS0067

    public bool CanRetry(Guid takeId) => false;

    public Task RetryAsync(Guid takeId) => Task.CompletedTask;

    public IReadOnlyList<TakeRecord> HistoryMatches(string question)
    {
        if (takeStore is null) return [];
        try { return takeStore.Recent(limit: 8, query: question); }
        catch (TakeStoreException) { return []; }
    }

    public Task<string> AskHistoryAsync(string question) =>
        Task.FromResult("Asking across your takes needs the dictation controller, which this build doesn’t have yet.");

    public void PasteLast() => AppLog.Write("dictation: paste last asked for (not wired in this build)");

    public void HistoryDidChange()
    {
        audioStore?.PruneIfNeeded();
        HistoryChanged?.Invoke();
    }
}

/// <summary>No update feed (a development build).</summary>
public sealed class UnconfiguredAppUpdater : ObservableObject, IAppUpdater
{
    private bool automaticallyChecks;

    public bool IsConfigured => false;

    public bool AutomaticallyChecks
    {
        get => automaticallyChecks;
        set => SetProperty(ref automaticallyChecks, value);
    }

    public string LastCheckDescription => "never checked";

    public void CheckNow() { }
}

/// <summary>A model that is never downloaded (the demo, and before the Speech port is wired).</summary>
public sealed class DetachedModelDownload(string downloadSizeText) : ObservableObject, IModelDownload
{
    private ModelDownloadState state = ModelDownloadState.NotInstalled;

    public ModelDownloadState State
    {
        get => state;
        set => SetProperty(ref state, value);
    }

    public string DownloadSizeText { get; } = downloadSizeText;

    public void Download() => State = ModelDownloadState.Failed("Downloads come with the speech engines, which this build doesn’t have yet.");

    public void CancelDownload() => State = ModelDownloadState.NotInstalled;

    public void Remove() => State = ModelDownloadState.NotInstalled;
}

/// <summary>
/// The answers and voices without Core/Answers and Core/Speech: the voice picker follows
/// SpeechVoices.swift's rules on the keys in shell.json; tests say they aren't wired yet.
/// </summary>
public sealed class DetachedSettingsServices(IProcessRunner? processes = null) : ISettingsServices
{
    public const string NotWiredSentence = "Testing comes with the answers module, which this build doesn’t have yet.";

    public IModelDownload Kokoro { get; } = new DetachedModelDownload("350 MB");

    public IModelDownload Parakeet { get; } = new DetachedModelDownload(ParakeetFacts.DownloadSizeText);

    public Task<string> TestAnswerSourceAsync(AnswerSource source, CancellationToken cancellationToken = default) => Task.FromResult(NotWiredSentence);

    public Task<string> TestVoiceKeyAsync(string voiceId, CancellationToken cancellationToken = default) => Task.FromResult(NotWiredSentence);

    public Task<PlanCliStatus> PlanStatusAsync(AnswerSource plan, CancellationToken cancellationToken = default)
    {
        var command = plan == AnswerSource.ClaudeCodePlan ? "claude" : "codex";
        var installed = processes?.ResolveExecutable(command) is not null;
        // Whether it is signed in is PlanCLIs' to say; until it is wired, an installed CLI asks for a sign-in.
        return Task.FromResult(installed ? PlanCliStatus.InstalledSignedOut : PlanCliStatus.NotInstalled);
    }

    public static string VoiceTitle(string voiceId) => voiceId switch
    {
        "automatic" => "Automatic",
        "account" => "Free voice (ElevenLabs)",
        "openai" => "OpenAI",
        "elevenlabs" => "ElevenLabs (your key)",
        "google" => "Google Cloud",
        "gemini" => "Google Gemini",
        "polly" => "Amazon Polly",
        "sarvam" => "Sarvam",
        "kokoro" => "Kokoro (on this PC)",
        _ => "This PC",
    };

    private static readonly string[] AllVoiceIds = ["automatic", "account", "openai", "elevenlabs", "google", "gemini", "polly", "sarvam", "kokoro", "mac"];

    /// <summary>Automatic's order (SpeechVoiceChoice.automaticOrder).</summary>
    private static readonly string[] AutomaticOrder = ["openai", "elevenlabs", "google", "polly", "sarvam", "gemini", "kokoro", "account", "mac"];

    private bool CanSpeak(string voiceId, OpenClickyShellSettings shell, bool hasAccount)
    {
        static bool Has(string? key) => !string.IsNullOrWhiteSpace(key);
        return voiceId switch
        {
            "account" => hasAccount,
            "openai" => Has(shell.OpenaiApiKey),
            "elevenlabs" => Has(shell.ElevenLabsApiKey),
            "google" => Has(shell.GoogleCloudApiKey),
            "gemini" => Has(shell.GeminiApiKey),
            "polly" => (shell.AmazonPollyCredentials ?? "").Split(':').Length is 2 or 3,
            "sarvam" => Has(shell.SarvamKey),
            "kokoro" => Kokoro.State.Kind == ModelDownloadState.Stage.Installed,
            "mac" => true,
            _ => false,
        };
    }

    public VoicePickerState VoicePicker(AppRegion region)
    {
        var shell = OpenClickyConfiguration.Settings;
        var hasAccount = OpenClickyConfiguration.HasAccountToken;
        var picked = AllVoiceIds.Contains(shell.SpeechVoice) ? shell.SpeechVoice! : "automatic";
        var offered = AllVoiceIds.Where(id => id is "automatic" or "mac" or "kokoro" || id == picked || CanSpeak(id, shell, hasAccount)).ToList();
        var candidates = picked == "automatic" ? AutomaticOrder : [picked];
        var speaking = candidates.FirstOrDefault(id => CanSpeak(id, shell, hasAccount)) ?? "mac";
        string sentence;
        if (picked != "automatic" && picked != speaking)
        {
            sentence = picked == "kokoro" ? "this PC’s voice until Kokoro is downloaded." : $"this PC’s voice: {VoiceTitle(picked)} needs its key under Keys & Plans.";
        }
        else
        {
            sentence = speaking switch
            {
                "openai" => "OpenAI, on your own key.",
                "elevenlabs" => "ElevenLabs, on your own key.",
                "google" => "Google Cloud, on your own key.",
                "gemini" => "Google Gemini, on your own key.",
                "polly" => "Amazon Polly, on your own key.",
                "sarvam" => "Sarvam, on your own key.",
                "kokoro" => "Kokoro, on this PC. Free, offline, nothing counted.",
                "account" => "ElevenLabs, through OpenClicky.",
                _ => "this PC’s voice. Free, offline, nothing counted.",
            };
        }
        return new VoicePickerState
        {
            Offered = offered.Select(id => new SettingsOption(id, VoiceTitle(id))).ToList(),
            PickedId = picked,
            NowSpeakingSentence = sentence,
            ShowsKokoroRows = picked == "kokoro" || Kokoro.State.IsBusy,
            ShowsSystemVoiceRow = speaking == "mac" || picked == "mac",
            HasMoreVoices = offered.Count < AllVoiceIds.Length,
        };
    }

    public void PickVoice(string voiceId)
    {
        OpenClickyConfiguration.Update(shell => shell.SpeechVoice = voiceId == "automatic" ? null : voiceId);
        if (voiceId == "kokoro") Kokoro.Download();
    }

    public Task PreviewVoiceAsync(string voiceId) => Task.CompletedTask;

    public IReadOnlyList<SettingsOption> KokoroSpeakers(string languageCode) => [];

    public string? CurrentKokoroSpeaker(string languageCode) => null;
}
