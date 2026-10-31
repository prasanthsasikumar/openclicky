// Ports Dictation/DictationSettings.swift (and the DictationLanguage extensions of
// Dictation/AppRegion.swift): every dictation preference, in Preferences (the UserDefaults
// replacement, same key names), as one observable object. Each property has a default that
// matches a fresh install: the orb shows at the bottom of the screen as a pill, sounds are on, the
// key is the default dictation key, nothing leaves the PC until an engine that needs the network
// is chosen.
//
// On Windows the `offline` engine is Windows speech recognition; the raw values stay the Mac's so
// a preferences file means the same thing on both. The Mac's macOS-26-only branches are gone.
//
// Windows speech recognition is not a private engine the way Apple's is: its dictation needs
// "Online speech recognition" switched on (Settings → Privacy & security → Speech), and then it can
// use Microsoft's online speech service. So a fresh install dictates with Moonshine, a small English
// model that ships inside the app and needs no permission. Parakeet is the better offline engine,
// set up as on a Mac before macOS 26: once the welcome tour is done its model downloads on its own
// (unless declined, on an unmetered network with room on the disk) and replaces `moonshine` or
// `offline` (ParakeetDictation.StartAutomaticSetup). `offline` stays a choice, and its words say
// where its audio may go.

using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Dictation;

/// <summary>Which speech engine turns a take into words.</summary>
public enum DictationEngineChoice
{
    /// <summary>Useful Sensors' Moonshine tiny (English) through sherpa-onnx, shipped inside the app.
    /// Nothing leaves the PC and nothing needs switching on: the default on a fresh install.</summary>
    Moonshine,
    /// <summary>NVIDIA's Parakeet through sherpa-onnx, downloaded once. Nothing leaves the PC: the
    /// offline default on Windows once its model is downloaded.</summary>
    Parakeet,
    /// <summary>Windows' own recogniser (Windows speech recognition). No key or account; it may use
    /// Microsoft's online speech service when Windows' online speech recognition is on.</summary>
    Offline,
    /// <summary>Sarvam's Saaras, with the user's own key (`sarvamKey` in shell.json).</summary>
    Sarvam,
    /// <summary>The OpenClicky backend (an account or the user's own OpenAI key).</summary>
    [RawValue("openclicky")] OpenClicky,
    /// <summary>AssemblyAI streaming through a backend-minted token.</summary>
    [RawValue("assemblyai")] AssemblyAI,
}

/// <summary>
/// Every per-engine rule and sentence the windows and the island show, in one place: adding an
/// engine means adding its words here, and the views read these rather than switching themselves.
/// </summary>
public static class DictationEngineChoices
{
    public static IReadOnlyList<DictationEngineChoice> AllCases { get; } = Enum.GetValues<DictationEngineChoice>();

    public static string Id(this DictationEngineChoice choice) => choice.RawValue();

    public static DictationEngineChoice? FromRawValue(string? raw) => RawValues.FromRawValue<DictationEngineChoice>(raw);

    public static string DisplayName(this DictationEngineChoice choice) => choice switch
    {
        DictationEngineChoice.Moonshine => "Moonshine",
        DictationEngineChoice.Offline => "Windows speech",
        DictationEngineChoice.Parakeet => "Parakeet",
        DictationEngineChoice.Sarvam => "Sarvam",
        DictationEngineChoice.OpenClicky => "OpenClicky",
        _ => "AssemblyAI",
    };

    /// <summary>
    /// The engines that run on this PC's own recogniser, with no key, account or credits. Every rule
    /// the Mac draws from "an engine on this machine" reads this one property instead of naming the
    /// engines again: no model clean-up unless it was allowed by name, no failed take's audio kept
    /// for a retry (a local engine that failed has no key, credits or network to wait for).
    /// Whether the voice itself stays on the PC is <see cref="KeepsVoiceOnThisPC"/>.
    /// </summary>
    public static bool IsOnDevice(this DictationEngineChoice choice) =>
        choice is DictationEngineChoice.Moonshine or DictationEngineChoice.Offline or DictationEngineChoice.Parakeet;

    /// <summary>
    /// The engines whose audio never leaves this PC: Moonshine and Parakeet. Windows speech recognition
    /// may use Microsoft's online speech service (when Windows' online speech recognition is on), so it
    /// is never called private. The "private · offline" tag reads this.
    /// </summary>
    public static bool KeepsVoiceOnThisPC(this DictationEngineChoice choice) =>
        choice is DictationEngineChoice.Moonshine or DictationEngineChoice.Parakeet;

    /// <summary>Who hears the voice, lower case, for the island ("this pc", "sarvam").</summary>
    public static string ListenerName(this DictationEngineChoice choice) => choice switch
    {
        DictationEngineChoice.Offline => "windows",
        DictationEngineChoice.Moonshine or DictationEngineChoice.Parakeet => "this pc",
        DictationEngineChoice.Sarvam => "sarvam",
        DictationEngineChoice.OpenClicky => "openai",
        _ => "assemblyai",
    };

    /// <summary>The engine's name in Settings → Dictation's "Who hears you".</summary>
    public static string SettingsTitle(this DictationEngineChoice choice) => choice switch
    {
        DictationEngineChoice.Moonshine => "Moonshine, built in",
        DictationEngineChoice.Offline => "Windows speech recognition",
        DictationEngineChoice.Parakeet => "Parakeet, on this PC",
        DictationEngineChoice.Sarvam => "Sarvam",
        DictationEngineChoice.OpenClicky => "OpenAI, via OpenClicky",
        _ => "AssemblyAI, via OpenClicky",
    };

    /// <summary>The title of the "how to dictate" row on the Your words page.</summary>
    public static string HowToDictateTitle(this DictationEngineChoice choice) => choice switch
    {
        DictationEngineChoice.Offline => "Windows speech recognition",
        DictationEngineChoice.Sarvam => "Sarvam hears you",
        _ => choice.SettingsTitle(),
    };

    /// <summary>The tag beside the engine's name in Settings: where the audio goes.</summary>
    public static string AudioDestinationTag(this DictationEngineChoice choice) => choice switch
    {
        DictationEngineChoice.Offline => "May use Microsoft online",
        DictationEngineChoice.Moonshine or DictationEngineChoice.Parakeet => "Private · offline",
        DictationEngineChoice.Sarvam => "Audio goes to Sarvam",
        DictationEngineChoice.OpenClicky => "Audio goes to OpenAI",
        _ => "Audio goes to AssemblyAI",
    };

    /// <summary>Where a take's audio goes, as one sentence for the Your words page.</summary>
    public static string AudioDestinationSentence(this DictationEngineChoice choice) => choice switch
    {
        DictationEngineChoice.Offline => "Windows speech recognition hears you. With Windows’ online speech recognition on, it may use Microsoft’s online speech service.",
        DictationEngineChoice.Moonshine or DictationEngineChoice.Parakeet => "Your voice stays on this PC.",
        DictationEngineChoice.Sarvam => "Audio goes to Sarvam with your key.",
        DictationEngineChoice.OpenClicky => "Audio goes to OpenAI through the OpenClicky backend.",
        _ => "Audio goes to AssemblyAI through the OpenClicky backend.",
    };

    /// <summary>What the engine does and what it needs, under its name in Settings.</summary>
    public static string SettingsDetail(this DictationEngineChoice choice) => choice switch
    {
        DictationEngineChoice.Moonshine => "A small model that comes with OpenClicky. Works the moment it’s installed, and nothing leaves this PC. English only.",
        DictationEngineChoice.Offline => "Windows’ own recogniser. No key, no account. When online speech recognition is on in Windows settings (privacy & security, speech), it may use Microsoft’s online speech service.",
        DictationEngineChoice.Parakeet => ParakeetFacts.IsRecommendedForThisPC
            ? $"Far more accurate than Windows speech recognition, and nothing leaves this PC. English and 24 European languages. A {ParakeetFacts.DownloadSizeText} download."
            : $"NVIDIA’s open model, offline. English and 24 European languages. A {ParakeetFacts.DownloadSizeText} download.",
        DictationEngineChoice.Sarvam => "Saaras hears eleven Indian languages. Billed by Sarvam to your key.",
        DictationEngineChoice.OpenClicky => "Needs an OpenClicky account or your own OpenAI key.",
        _ => "Words appear as you speak. Needs the OpenClicky backend.",
    };

    /// <summary>The short note on the right of the engine in Settings while it can't be used yet.</summary>
    public static string UnavailableBadge(this DictationEngineChoice choice) => choice switch
    {
        DictationEngineChoice.Moonshine => "Not in this build",
        DictationEngineChoice.Offline => "",
        DictationEngineChoice.Parakeet => "Not downloaded",
        DictationEngineChoice.Sarvam => "Needs a key",
        DictationEngineChoice.OpenClicky => "Needs account",
        _ => "Needs backend",
    };

    /// <summary>The words the settings search finds the engine by, besides its title and detail.</summary>
    public static IReadOnlyList<string> SearchKeywords(this DictationEngineChoice choice)
    {
        string[] common = ["engine", "provider", "transcription", "speech", "who hears you", choice.AudioDestinationTag()];
        string[] onDeviceKeywords = choice.IsOnDevice() ? ["offline", "local", "private", "on device"] : ["cloud"];
        string[] own = choice switch
        {
            // Not "private" or "offline": it may use Microsoft's online speech service.
            DictationEngineChoice.Offline => ["local", "on device", "windows", "microsoft", "speech recognition", "online speech recognition"],
            DictationEngineChoice.Moonshine => [.. onDeviceKeywords, "moonshine", "built in", "bundled", "model", "english"],
            DictationEngineChoice.Parakeet => [.. onDeviceKeywords, "parakeet", "nvidia", "download", "model"],
            DictationEngineChoice.Sarvam => ["sarvam", "saaras", "key", "api key", "indian", "hindi"],
            DictationEngineChoice.OpenClicky => [.. onDeviceKeywords, "openai", "openclicky", "account", "backend"],
            _ => [.. onDeviceKeywords, "assemblyai", "streaming", "backend"],
        };
        return [.. common, .. own];
    }
}

/// <summary>
/// The one source of truth about Parakeet's model: its size, its languages, its folder and whether
/// it is installed. Speech's ParakeetDictation (download, helper process) and the resolver both read
/// these, so Settings, the island and the provider never disagree.
/// </summary>
public static class ParakeetFacts
{
    /// <summary>What Settings says before the download, rounded.</summary>
    public const string DownloadSizeText = "465 MB";

    /// <summary>
    /// Whether this PC should be offered Parakeet as the better offline engine (the Mac says so
    /// before macOS 26, where its own recogniser is the old one; Windows speech recognition is
    /// behind it too).
    /// </summary>
    public const bool IsRecommendedForThisPC = true;

    /// <summary>The model's folder name, as sherpa-onnx's release tarball unpacks it.</summary>
    public const string ModelFolderName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

    /// <summary>The languages Parakeet v3 hears (ISO 639-1).</summary>
    public static IReadOnlySet<string> LanguageCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt",
        "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk",
    };

    /// <summary>Whether it hears `bareLanguageCode` (null: auto-detect, which Parakeet does itself).</summary>
    public static bool Hears(string? bareLanguageCode) => bareLanguageCode is null || LanguageCodes.Contains(bareLanguageCode);

    /// <summary>%LOCALAPPDATA%\OpenClicky\Models\Parakeet\&lt;model&gt;.</summary>
    public static string ModelFolder => Path.Combine(Folder, ModelFolderName);

    /// <summary>%LOCALAPPDATA%\OpenClicky\Models\Parakeet (what a download unpacks into and a removal deletes).</summary>
    public static string Folder => Path.Combine(OpenClickyPaths.ModelsDirectory, "Parakeet");

    /// <summary>The files a working model folder has.</summary>
    public static IReadOnlyList<string> ModelFileNames { get; } = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];

    /// <summary>The sherpa-onnx engine can be loaded (shared with Kokoro) and the model's files are in <paramref name="modelFolder"/>.</summary>
    public static bool IsInstalledIn(string modelFolder) =>
        Speech.KokoroVoice.EngineIsAvailable() && ModelFileNames.All(name => File.Exists(Path.Combine(modelFolder, name)));

    /// <summary>Whether the model is installed. Replaceable (tests).</summary>
    public static Func<bool> IsInstalledCheck { get; set; } = () => IsInstalledIn(ModelFolder);

    public static bool IsInstalled => IsInstalledCheck();
}

/// <summary>
/// The model that ships inside the app: Moonshine tiny (English, int8) from sherpa-onnx's asr-models
/// release. windows/scripts/fetch-speech-model.ps1 puts it in Assets\Models before a release build
/// (its hash pinned there); a build without it (a plain dev build) has no Moonshine and keeps
/// Windows speech as the default.
/// </summary>
public static class MoonshineFacts
{
    /// <summary>The model's folder name, as sherpa-onnx's release tarball unpacks it.</summary>
    public const string ModelFolderName = "sherpa-onnx-moonshine-tiny-en-int8";

    /// <summary>Next to the exe: Assets\Models\&lt;model&gt;.</summary>
    public static string ModelFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Models", ModelFolderName);

    /// <summary>The files a working model folder has.</summary>
    public static IReadOnlyList<string> ModelFileNames { get; } =
        ["preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx", "tokens.txt"];

    /// <summary>Moonshine tiny hears English only.</summary>
    public static bool Hears(string? bareLanguageCode) => bareLanguageCode is null or "en";

    public static bool IsInstalledIn(string modelFolder) =>
        Speech.KokoroVoice.EngineIsAvailable() && ModelFileNames.All(name => File.Exists(Path.Combine(modelFolder, name)));

    /// <summary>Whether this build carries the model. Replaceable (tests).</summary>
    public static Func<bool> IsInstalledCheck { get; set; } = () => IsInstalledIn(ModelFolder);

    public static bool IsInstalled => IsInstalledCheck();
}

public enum OrbLook { Pill, Classic, Pixel }

public enum OrbTheme { Black, Coral, Mist }

public enum OrbSize { Full, Mini }

/// <summary>Light, dark, or following Windows (the Windows side maps it to the WPF theme).</summary>
public enum DictationAppearance { Light, Dark, System }

/// <summary>Native script or roman letters for Indian-language takes ("नमस्ते" or "namaste").</summary>
public enum DictationScript { Native, Roman }

/// <summary>Where the orb was last dragged to, in screen points.</summary>
public readonly record struct OrbPoint(double X, double Y);

/// <summary>The languages the engines can be pinned to. `Auto` lets the engine detect.</summary>
public sealed record DictationLanguage(string Code, string Name, string NativeName)
{
    public string Id => Code;

    public static DictationLanguage Auto { get; } = new("auto", "auto-detect", "auto-detect");

    public static IReadOnlyList<DictationLanguage> Choices { get; } =
    [
        Auto,
        new("en-IN", "English", "English"),
        new("hi-IN", "Hindi", "हिन्दी"),
        new("ta-IN", "Tamil", "தமிழ்"),
        new("te-IN", "Telugu", "తెలుగు"),
        new("kn-IN", "Kannada", "ಕನ್ನಡ"),
        new("ml-IN", "Malayalam", "മലയാളം"),
        new("bn-IN", "Bengali", "বাংলা"),
        new("mr-IN", "Marathi", "मराठी"),
        new("gu-IN", "Gujarati", "ગુજરાતી"),
        new("pa-IN", "Punjabi", "ਪੰਜਾਬੀ"),
        new("od-IN", "Odia", "ଓଡ଼ିଆ"),
        // Offered outside India (AppRegion): English as written there, and the European languages
        // the PC's recogniser, Parakeet and the online engines all hear.
        new("en-US", "English (US)", "English (US)"),
        new("en-GB", "English (UK)", "English (UK)"),
        new("es-ES", "Spanish", "Español"),
        new("fr-FR", "French", "Français"),
        new("de-DE", "German", "Deutsch"),
        new("it-IT", "Italian", "Italiano"),
        new("pt-BR", "Portuguese", "Português"),
    ];

    public static DictationLanguage Named(string? code) => Choices.FirstOrDefault(choice => choice.Code == code) ?? Auto;

    /// <summary>The bare language code the recognisers and the prompts take ("hi" for "hi-IN").</summary>
    public string? BareCode
    {
        get
        {
            if (Code == "auto") return null;
            var first = Code.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return first;
        }
    }

    // MARK: the region (AppRegion.swift)

    /// <summary>Whether this is one of the Indian languages (English counts as India's only in its en-IN form).</summary>
    public bool IsIndian => Code.EndsWith("-IN", StringComparison.Ordinal) && Code != "en-IN";

    /// <summary>The pinnable languages the region offers, in picker order, always keeping `current`.</summary>
    public static IReadOnlyList<DictationLanguage> PinnableChoices(AppRegion region, DictationLanguage current)
    {
        var offered = region == AppRegion.India ? IndianChoices : WorldChoices;
        if (current == Auto || offered.Contains(current)) return offered;
        return [.. offered, current];
    }

    /// <summary>What "Pin one" starts on when nothing was pinned before.</summary>
    public static DictationLanguage DefaultPinned(AppRegion region) => region == AppRegion.India ? Named("en-IN") : Named("en-US");

    private static List<DictationLanguage> IndianChoices =>
        Choices.Where(choice => choice != Auto && choice.Code.EndsWith("-IN", StringComparison.Ordinal)).ToList();

    private static List<DictationLanguage> WorldChoices =>
        Choices.Where(choice => choice != Auto && !choice.Code.EndsWith("-IN", StringComparison.Ordinal)).ToList();
}

/// <summary>Every dictation preference, as one observable object backed by Preferences.</summary>
public sealed class DictationSettings : ObservableObject
{
    private static readonly Lazy<DictationSettings> SharedInstance = new(() =>
    {
        var settings = new DictationSettings(Preferences.Shared);
        settings.FollowAccountProfile();
        return settings;
    });

    public static DictationSettings Shared => SharedInstance.Value;

    private readonly Preferences defaults;

    /// <summary>The preference keys, the Mac's names.</summary>
    public static class Keys
    {
        public const string Engine = "dictation.engine";
        public const string Language = "dictation.language";
        public const string Script = "dictation.script";
        public const string PolishWithModel = "dictation.polishWithModel";
        public const string PolishOfflineTakes = "dictation.polishOfflineTakes";
        /// <summary>Set once the person flips "also polish takes heard on this pc" themselves.</summary>
        public const string PolishOfflineTakesChosen = "dictation.polishOfflineTakesChosen";
        public const string DictationKey = "dictation.key";
        public const string TapKeepsListening = "dictation.tapKeepsListening";
        public const string OrbVisible = "dictation.orb.visible";
        public const string OrbLook = "dictation.orb.look";
        public const string OrbTheme = "dictation.orb.theme";
        public const string OrbSize = "dictation.orb.size";
        public const string OrbHidesWhenIdle = "dictation.orb.hidesWhenIdle";
        public const string OrbHidesWhenIdleDefaultApplied = "dictation.orb.hidesWhenIdle.defaultApplied";
        public const string OrbRestsExpanded = "dictation.orb.restsExpanded";
        public const string OrbOpensBoxWhenPasteUnverified = "dictation.orb.opensBoxWhenPasteUnverified";
        public const string OrbIsDraggable = "dictation.orb.draggable";
        public const string OrbPosition = "dictation.orb.position";
        public const string Tooltips = "dictation.tooltips";
        public const string Sounds = "dictation.sounds";
        public const string Haptics = "dictation.haptics";
        public const string ReduceAnimation = "dictation.reduceAnimation";
        public const string Appearance = "dictation.appearance";
        public const string AppIconColor = "general.appIconColor";
        public const string InactivityTimeoutMinutes = "dictation.inactivityTimeoutMinutes";
        public const string ClipboardHistoryEnabled = "dictation.clipboardHistoryEnabled";
        public const string Incognito = "dictation.incognito";
        public const string KeepMemoryOnThisMac = "dictation.keepMemoryOnThisMac";
        public const string RetainFailedTakeAudio = "dictation.retainFailedTakeAudio";
        public const string ReadNearbyText = "dictation.readNearbyText";
        public const string PauseBeforeSendingScreen = "openclicky.ask.pauseBeforeSendingScreen";
        public const string HasCompletedDictationOnboarding = "dictation.onboarded";
        public const string PreferredMicrophoneUID = "dictation.preferredMicrophoneUID";    }

    /// <summary>A fresh install's engine: the built-in Moonshine when this build carries it (no Windows
    /// online-speech prompt), else Windows speech. A stored choice ("offline" included) is kept.</summary>
    public static DictationEngineChoice DefaultEngine =>
        MoonshineFacts.IsInstalled ? DictationEngineChoice.Moonshine : DictationEngineChoice.Offline;

    public DictationSettings(Preferences defaults)
    {
        this.defaults = defaults;
        engine = defaults.GetEnum(Keys.Engine, DefaultEngine);
        languageCode = defaults.GetString(Keys.Language) ?? DictationLanguage.Auto.Code;
        script = defaults.GetEnum(Keys.Script, DictationScript.Native);
        // Off on a fresh install: words type straight in as you speak until "clean up my words with ai" is switched on.
        polishWithModel = defaults.GetBool(Keys.PolishWithModel);
        polishOfflineTakes = defaults.GetBool(Keys.PolishOfflineTakes);
        // Before the explicit flag existed, a stored value could only have come from the toggle.
        if (defaults.Contains(Keys.PolishOfflineTakes) && !defaults.Contains(Keys.PolishOfflineTakesChosen))
        {
            defaults.Set(Keys.PolishOfflineTakesChosen, true);
        }
        dictationKey = DictationHotkeys.FromRawValue(defaults.GetString(Keys.DictationKey));
        tapKeepsListening = defaults.GetBool(Keys.TapKeepsListening, defaultValue: false);
        orbVisible = defaults.GetBool(Keys.OrbVisible, defaultValue: true);
        orbLook = defaults.GetEnum(Keys.OrbLook, OrbLook.Pill);
        orbTheme = defaults.GetEnum(Keys.OrbTheme, OrbTheme.Black);
        orbSize = defaults.GetEnum(Keys.OrbSize, OrbSize.Full);
        // Out of the way until a take starts: the pill resting on the taskbar all day was the first
        // thing asked to go. Applied once, so a user who turns it off keeps it off.
        if (!defaults.GetBool(Keys.OrbHidesWhenIdleDefaultApplied))
        {
            defaults.Set(Keys.OrbHidesWhenIdleDefaultApplied, true);
            defaults.Set(Keys.OrbHidesWhenIdle, true);
        }
        orbHidesWhenIdle = defaults.GetBool(Keys.OrbHidesWhenIdle);
        orbRestsExpanded = defaults.GetBool(Keys.OrbRestsExpanded);
        orbOpensBoxWhenPasteUnverified = defaults.GetBool(Keys.OrbOpensBoxWhenPasteUnverified);
        orbIsDraggable = defaults.GetBool(Keys.OrbIsDraggable, defaultValue: true);
        tooltips = defaults.GetBool(Keys.Tooltips);
        sounds = defaults.GetBool(Keys.Sounds, defaultValue: true);
        haptics = defaults.GetBool(Keys.Haptics, defaultValue: true);
        reduceAnimation = defaults.GetBool(Keys.ReduceAnimation);
        appearance = defaults.GetEnum(Keys.Appearance, DictationAppearance.System);
        appIconColor = defaults.GetEnum(Keys.AppIconColor, AppIconColor.Signal);
        inactivityTimeoutMinutes = defaults.Contains(Keys.InactivityTimeoutMinutes) ? defaults.GetInt(Keys.InactivityTimeoutMinutes) : 3;
        clipboardHistoryEnabled = defaults.GetBool(Keys.ClipboardHistoryEnabled);
        incognito = defaults.GetBool(Keys.Incognito);
        keepMemoryOnThisPC = defaults.GetBool(Keys.KeepMemoryOnThisMac);
        retainFailedTakeAudio = defaults.GetBool(Keys.RetainFailedTakeAudio, defaultValue: true);
        readNearbyText = defaults.GetBool(Keys.ReadNearbyText, defaultValue: true);
        pauseBeforeSendingScreen = defaults.GetBool(Keys.PauseBeforeSendingScreen, defaultValue: true);
        hasCompletedDictationOnboarding = defaults.GetBool(Keys.HasCompletedDictationOnboarding);
        preferredMicrophoneUID = defaults.GetString(Keys.PreferredMicrophoneUID);
        orbPosition = DecodePoint(defaults.GetString(Keys.OrbPosition));    }

    /// <summary>Sets a property, raises PropertyChanged, and stores it (Swift's didSet stores every assignment).</summary>
    private void Store<T>(ref T field, T value, Action persist, string propertyName)
    {
        SetProperty(ref field, value, propertyName);
        persist();
    }

    // MARK: engine & language

    private DictationEngineChoice engine;
    public DictationEngineChoice Engine
    {
        get => engine;
        set => Store(ref engine, value, () => defaults.SetEnum(Keys.Engine, value), nameof(Engine));
    }

    private string languageCode;
    public string LanguageCode
    {
        get => languageCode;
        set
        {
            Store(ref languageCode, value, () => defaults.Set(Keys.Language, value), nameof(LanguageCode));
            OnPropertyChanged(nameof(Language));
        }
    }

    private DictationScript script;
    public DictationScript Script
    {
        get => script;
        set => Store(ref script, value, () => defaults.SetEnum(Keys.Script, value), nameof(Script));
    }

    private bool polishWithModel;
    /// <summary>Clean the raw transcript up with a model (style rules, punctuation) when one is configured.</summary>
    public bool PolishWithModel
    {
        get => polishWithModel;
        set => Store(ref polishWithModel, value, () => defaults.Set(Keys.PolishWithModel, value), nameof(PolishWithModel));
    }

    private bool polishOfflineTakes;
    private bool isApplyingAccountDefault;
    /// <summary>
    /// The offline engine's promise is that nothing leaves the PC; sending its words to a model
    /// for polish is off until asked for by name.
    /// </summary>
    public bool PolishOfflineTakes
    {
        get => polishOfflineTakes;
        set => Store(ref polishOfflineTakes, value, () =>
        {
            if (!isApplyingAccountDefault) defaults.Set(Keys.PolishOfflineTakes, value);
        }, nameof(PolishOfflineTakes));
    }

    /// <summary>The settings toggle: the person's own choice, which the account default never overrides.</summary>
    public void ChoosePolishOfflineTakes(bool isOn)
    {
        defaults.Set(Keys.PolishOfflineTakesChosen, true);
        PolishOfflineTakes = isOn;
    }

    /// <summary>
    /// A free account's takes go to its model anyway, so until the person chooses, polish of takes
    /// heard on this PC is on for accounts and off otherwise. The default is not stored, so it
    /// follows the account as it changes.
    /// </summary>
    public void ApplyAccountPolishDefault(AccountKind kind)
    {
        if (defaults.GetBool(Keys.PolishOfflineTakesChosen)) return;
        var wanted = kind == AccountKind.Account;
        if (polishOfflineTakes == wanted) return;
        isApplyingAccountDefault = true;
        PolishOfflineTakes = wanted;
        isApplyingAccountDefault = false;
    }

    /// <summary>Re-applies the default whenever credentials change or the backend's answer changes the account.</summary>
    private void FollowAccountProfile()
    {
        ApplyAccountPolishDefault(AccountCapabilities.Current(preferences: defaults).Kind);
        void Reapply() => ApplyAccountPolishDefault(AccountCapabilities.Current(preferences: defaults).Kind);
        OpenClickyConfiguration.CredentialsChanged += Reapply;
        AccountProfileStore.ProfileChanged += Reapply;
    }

    public DictationLanguage Language => DictationLanguage.Named(LanguageCode);

    /// <summary>
    /// The stored dictation language, readable from any thread: the polisher and the engine checks
    /// are built from background tasks too, and they read the same key `LanguageCode` writes.
    /// </summary>
    public static DictationLanguage StoredLanguage(Preferences? defaults = null) =>
        DictationLanguage.Named((defaults ?? Preferences.Shared).GetString(Keys.Language) ?? DictationLanguage.Auto.Code);

    // MARK: keys

    private DictationHotkey dictationKey;
    public DictationHotkey DictationKey
    {
        get => dictationKey;
        set => Store(ref dictationKey, value, () => defaults.Set(Keys.DictationKey, value.RawValue()), nameof(DictationKey));
    }

    private bool tapKeepsListening;
    /// <summary>Off (the default, as on the Mac): the key is held while you talk and a quick tap does nothing.
    /// On: a tap starts a take that keeps listening until the next tap.</summary>
    public bool TapKeepsListening
    {
        get => tapKeepsListening;
        set => Store(ref tapKeepsListening, value, () => defaults.Set(Keys.TapKeepsListening, value), nameof(TapKeepsListening));
    }

    // MARK: the orb

    private bool orbVisible;
    public bool OrbVisible
    {
        get => orbVisible;
        set => Store(ref orbVisible, value, () => defaults.Set(Keys.OrbVisible, value), nameof(OrbVisible));
    }

    private OrbLook orbLook;
    public OrbLook OrbLook
    {
        get => orbLook;
        set => Store(ref orbLook, value, () => defaults.SetEnum(Keys.OrbLook, value), nameof(OrbLook));
    }

    private OrbTheme orbTheme;
    public OrbTheme OrbTheme
    {
        get => orbTheme;
        set => Store(ref orbTheme, value, () => defaults.SetEnum(Keys.OrbTheme, value), nameof(OrbTheme));
    }

    private OrbSize orbSize;
    public OrbSize OrbSize
    {
        get => orbSize;
        set => Store(ref orbSize, value, () => defaults.SetEnum(Keys.OrbSize, value), nameof(OrbSize));
    }

    private bool orbHidesWhenIdle;
    /// <summary>"hide when not in use": appears when a take starts and hides when it is done.</summary>
    public bool OrbHidesWhenIdle
    {
        get => orbHidesWhenIdle;
        set => Store(ref orbHidesWhenIdle, value, () => defaults.Set(Keys.OrbHidesWhenIdle, value), nameof(OrbHidesWhenIdle));
    }

    private bool orbRestsExpanded;
    /// <summary>"rest with the box open": the transcript box stays open between takes.</summary>
    public bool OrbRestsExpanded
    {
        get => orbRestsExpanded;
        set => Store(ref orbRestsExpanded, value, () => defaults.Set(Keys.OrbRestsExpanded, value), nameof(OrbRestsExpanded));
    }

    private bool orbOpensBoxWhenPasteUnverified;
    /// <summary>"paste visibility · orb box": open the box when a paste could not be confirmed.</summary>
    public bool OrbOpensBoxWhenPasteUnverified
    {
        get => orbOpensBoxWhenPasteUnverified;
        set => Store(ref orbOpensBoxWhenPasteUnverified, value, () => defaults.Set(Keys.OrbOpensBoxWhenPasteUnverified, value), nameof(OrbOpensBoxWhenPasteUnverified));
    }

    private bool orbIsDraggable;
    public bool OrbIsDraggable
    {
        get => orbIsDraggable;
        set => Store(ref orbIsDraggable, value, () => defaults.Set(Keys.OrbIsDraggable, value), nameof(OrbIsDraggable));
    }

    private OrbPoint? orbPosition;
    /// <summary>Where the orb was last dragged to, in screen points; null = bottom centre of the main screen.</summary>
    public OrbPoint? OrbPosition
    {
        get => orbPosition;
        set => Store(ref orbPosition, value, () => defaults.Set(Keys.OrbPosition, EncodePoint(value)), nameof(OrbPosition));
    }

    private bool tooltips;
    public bool Tooltips
    {
        get => tooltips;
        set => Store(ref tooltips, value, () => defaults.Set(Keys.Tooltips, value), nameof(Tooltips));
    }

    private bool sounds;
    public bool Sounds
    {
        get => sounds;
        set => Store(ref sounds, value, () => defaults.Set(Keys.Sounds, value), nameof(Sounds));
    }

    private bool haptics;
    /// <summary>Kept for the shared preferences file; Windows has no haptics.</summary>
    public bool Haptics
    {
        get => haptics;
        set => Store(ref haptics, value, () => defaults.Set(Keys.Haptics, value), nameof(Haptics));
    }

    private bool reduceAnimation;
    public bool ReduceAnimation
    {
        get => reduceAnimation;
        set => Store(ref reduceAnimation, value, () => defaults.Set(Keys.ReduceAnimation, value), nameof(ReduceAnimation));
    }

    // MARK: general

    /// <summary>
    /// Which options the settings offer (AppRegion). Always the world since 2026-10-30: OpenClicky
    /// no longer offers India its own set, so the Region choice is gone. An option already in use
    /// (Sarvam, an Indian language) stays offered. The old `openclicky.region` value is ignored.
    /// </summary>
    public AppRegion Region => AppRegion.World;

    private DictationAppearance appearance;
    public DictationAppearance Appearance
    {
        get => appearance;
        set => Store(ref appearance, value, () => defaults.SetEnum(Keys.Appearance, value), nameof(Appearance));
    }

    private AppIconColor appIconColor;
    /// <summary>The app icon's colour on the window, the taskbar and the tray (AppIconColor).</summary>
    public AppIconColor AppIconColor
    {
        get => appIconColor;
        set => Store(ref appIconColor, value, () => defaults.SetEnum(Keys.AppIconColor, value), nameof(AppIconColor));
    }

    private int inactivityTimeoutMinutes;
    /// <summary>A take with this long of silence ends on its own. 0 = never.</summary>
    public int InactivityTimeoutMinutes
    {
        get => inactivityTimeoutMinutes;
        set => Store(ref inactivityTimeoutMinutes, value, () => defaults.Set(Keys.InactivityTimeoutMinutes, value), nameof(InactivityTimeoutMinutes));
    }

    private string? preferredMicrophoneUID;
    /// <summary>The chosen input device's id (the Windows endpoint id); null = the default device.</summary>
    public string? PreferredMicrophoneUID
    {
        get => preferredMicrophoneUID;
        set
        {
            Store(ref preferredMicrophoneUID, value, () => defaults.Set(Keys.PreferredMicrophoneUID, value), nameof(PreferredMicrophoneUID));
            PreferredMicrophoneUIDDidChange?.Invoke(value);
        }
    }

    /// <summary>The dictation manager records from the chosen device; it is told here when it changes.</summary>
    public Action<string?>? PreferredMicrophoneUIDDidChange { get; set; }

    // MARK: privacy

    private bool clipboardHistoryEnabled;
    public bool ClipboardHistoryEnabled
    {
        get => clipboardHistoryEnabled;
        set => Store(ref clipboardHistoryEnabled, value, () => defaults.Set(Keys.ClipboardHistoryEnabled, value), nameof(ClipboardHistoryEnabled));
    }

    private bool incognito;
    /// <summary>Takes still paste, but nothing is saved to history.</summary>
    public bool Incognito
    {
        get => incognito;
        set => Store(ref incognito, value, () => defaults.Set(Keys.Incognito, value), nameof(Incognito));
    }

    private bool keepMemoryOnThisPC;
    /// <summary>History and learned terms never leave this PC (no backend sync of either). Key: the Mac's.</summary>
    public bool KeepMemoryOnThisPC
    {
        get => keepMemoryOnThisPC;
        set => Store(ref keepMemoryOnThisPC, value, () => defaults.Set(Keys.KeepMemoryOnThisMac, value), nameof(KeepMemoryOnThisPC));
    }

    private bool retainFailedTakeAudio;
    public bool RetainFailedTakeAudio
    {
        get => retainFailedTakeAudio;
        set => Store(ref retainFailedTakeAudio, value, () => defaults.Set(Keys.RetainFailedTakeAudio, value), nameof(RetainFailedTakeAudio));
    }

    private bool readNearbyText;
    /// <summary>Read captions near the cursor through UI Automation so names on screen are spelled right.</summary>
    public bool ReadNearbyText
    {
        get => readNearbyText;
        set => Store(ref readNearbyText, value, () => defaults.Set(Keys.ReadNearbyText, value), nameof(ReadNearbyText));
    }

    private bool pauseBeforeSendingScreen;
    /// <summary>A question to Clicky waits a moment (AskSendPause) before the screen is captured and sent,
    /// so esc or a click on the island can still call it off. The Ctrl + Win lane only; on by default.</summary>
    public bool PauseBeforeSendingScreen
    {
        get => pauseBeforeSendingScreen;
        set => Store(ref pauseBeforeSendingScreen, value, () => defaults.Set(Keys.PauseBeforeSendingScreen, value), nameof(PauseBeforeSendingScreen));
    }

    private bool hasCompletedDictationOnboarding;
    public bool HasCompletedDictationOnboarding
    {
        get => hasCompletedDictationOnboarding;
        set => Store(ref hasCompletedDictationOnboarding, value, () => defaults.Set(Keys.HasCompletedDictationOnboarding, value), nameof(HasCompletedDictationOnboarding));
    }

    /// <summary>Settings → general → reset, and the orb page's reset: back to a fresh install's values.</summary>
    public void ResetOrbToDefaults()
    {
        OrbVisible = true; OrbLook = OrbLook.Pill; OrbTheme = OrbTheme.Black; OrbSize = OrbSize.Full;
        OrbHidesWhenIdle = true; OrbRestsExpanded = false; OrbOpensBoxWhenPasteUnverified = false;
        OrbIsDraggable = true; OrbPosition = null; Tooltips = false; Sounds = true; Haptics = true;
    }

    public void ResetGeneralToDefaults()
    {
        Appearance = DictationAppearance.System; AppIconColor = AppIconColor.Signal; InactivityTimeoutMinutes = 3; ReduceAnimation = false;
    }

    public void ResetShortcutsToDefaults()
    {
        DictationKey = DictationHotkeys.Default;
        TapKeepsListening = false;
    }

    internal static string? EncodePoint(OrbPoint? point) =>
        point is { } value
            ? $"{value.X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{value.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : null;

    internal static OrbPoint? DecodePoint(string? encoded)
    {
        if (encoded is null) return null;
        var parts = encoded.Split(',')
            .Select(part => double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? (double?)number : null)
            .Where(number => number is not null)
            .Select(number => number!.Value)
            .ToList();
        return parts.Count == 2 ? new OrbPoint(parts[0], parts[1]) : null;
    }
}
