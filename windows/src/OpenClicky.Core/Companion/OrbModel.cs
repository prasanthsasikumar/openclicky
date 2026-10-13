//
//  OrbModel.cs
//  OpenClicky (Windows port of the model half of Dictation/OrbPanel.swift)
//
//  What the orb shows (phase, live words, the transcript box, the microphone level, the hint) and
//  the orb's settings as the dictation settings store them. The window is Orb/OrbWindow.cs.
//

using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Companion;

public enum OrbPhaseKind
{
    Idle,
    Listening,
    EditListening,
    Working,
    Done,
    Failed,
}

/// <summary>What the orb is showing right now (the Swift enum with associated messages).</summary>
public readonly record struct OrbPhase(OrbPhaseKind Kind, string Message = "")
{
    public static readonly OrbPhase Idle = new(OrbPhaseKind.Idle);
    public static readonly OrbPhase Listening = new(OrbPhaseKind.Listening);
    public static readonly OrbPhase EditListening = new(OrbPhaseKind.EditListening);
    public static OrbPhase Working(string message) => new(OrbPhaseKind.Working, message);
    public static OrbPhase Done(string message) => new(OrbPhaseKind.Done, message);
    public static OrbPhase Failed(string message) => new(OrbPhaseKind.Failed, message);
}

/// <summary>The sentences DictationTakeController puts in the orb, verbatim from the Mac (the controller is ported
/// separately; these are here so the demo and the views agree on them).</summary>
public static class OrbPhaseTexts
{
    public const string MovingYourWords = "moving your words";
    public const string FinishingYourEdit = "finishing your edit";
    public const string MovedToTextBox = "moved to text box";
    public const string MovedToTextBoxCleanedUpLocally = "moved to text box · cleaned up locally";
    public const string RetryingYourRecording = "retrying your recording";
    public const string DidNotCatchThat = "didn't catch that";
    public const string NoTextBox = "click into a text box for your words";
    public const string Listening = "listening…";
    public const string SayYourEdit = "say your edit…";
    public const string YourLastTake = "your last take";
    public const string Rewriting = "rewriting…";
    public const string TapReleaseToTranscribe = "tap / release to transcribe";
    public const string SayYourEditThenLetGo = "say your edit, then let go";

    /// <summary>The resting hint ("tap / hold right Ctrl to talk").</summary>
    public static string IdleHint(DictationHotkey dictationKey) => $"tap / hold {dictationKey.KeycapLabel()} to talk";

    /// <summary>The pill's tooltip.</summary>
    public static string PillToolTip(DictationHotkey dictationKey) => $"tap or hold {dictationKey.KeycapLabel()} to talk · tap the orb to talk";
}

public sealed partial class OrbModel : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBusy))] private OrbPhase phase = OrbPhase.Idle;
    /// <summary>Words as they arrive while listening.</summary>
    [ObservableProperty] private string liveTranscript = "";
    /// <summary>The transcript box above the pill: the text of the last take when it is open.</summary>
    [ObservableProperty] private string? boxText;
    /// <summary>The line under the box's text: why it is open.</summary>
    [ObservableProperty] private string boxReason = OrbPhaseTexts.YourLastTake;
    [ObservableProperty] private bool isBoxOpen;
    /// <summary>0…1 microphone level for the bars.</summary>
    [ObservableProperty] private double audioLevel;
    /// <summary>A hint under the pill ("tap / hold to talk"), when tooltips are on.</summary>
    [ObservableProperty] private string? hint;
    /// <summary>Rewrites offered under the box's text ("formal", "casual", "shorter").</summary>
    [ObservableProperty] private IReadOnlyList<string> quickRewrites = Array.Empty<string>();
    /// <summary>A rewrite is on its way; the box says so and keeps its old words until it lands.</summary>
    [ObservableProperty] private bool isRewriting;

    /// <summary>A rewrite chip was clicked.</summary>
    public Action<string>? OnQuickRewrite { get; set; }
    /// <summary>The box's text can be pasted into the app in front (null hides "paste").</summary>
    public Action? OnPasteFromBox { get; set; }
    /// <summary>A plain click on the pill (tap the orb to talk / to stop).</summary>
    public event Action? Clicked;

    public bool IsBusy => Phase.Kind is OrbPhaseKind.Listening or OrbPhaseKind.EditListening or OrbPhaseKind.Working;

    public void NotePillClicked() => Clicked?.Invoke();
}

/// <summary>The pill's own geometry, in DIPs.</summary>
public static class OrbMetrics
{
    public static (double Width, double Height) PillSize(OrbSizeChoice size) =>
        size == OrbSizeChoice.Mini ? (44, 20) : (76, 30);

    public const double BoxWidth = 380;
    public const double BoxHeight = 170;
    public const double HintHeight = 22;
    public const double Padding = 12;
    /// <summary>Where the pill rests when nothing was dragged: this far above the bottom of the work area.</summary>
    public const double RestingBottomInset = 18;
    /// <summary>The pill's width while it shows words (any phase but idle).</summary>
    public const double ExpandedPillWidth = 220;
    /// <summary>The window's content width for the box / words / resting pill (OrbPanelManager.layout).</summary>
    public static double ContentWidth(OrbPhaseKind phase, OrbSizeChoice size, bool showsBox)
    {
        var pill = PillSize(size);
        return Math.Max(pill.Width, showsBox ? BoxWidth : (phase == OrbPhaseKind.Idle ? pill.Width : 240));
    }
}

// TODO(integration): DictationSettings (Core/Dictation, ported concurrently) owns these enums and keys; bind the orb
// to it and drop OrbSettings once it lands. The names here carry "Choice" so both can coexist until then.
public enum OrbLookChoice { Pill, Classic, Pixel }
public enum OrbThemeChoice { Black, Coral, Mist }
public enum OrbSizeChoice { Full, Mini }
public enum DictationAppearanceChoice { Light, Dark, System }

/// <summary>The orb's settings, read from the preferences under the Mac's DictationSettings key names.</summary>
public sealed partial class OrbSettings : ObservableObject
{
    public static class Keys
    {
        public const string DictationKey = "dictation.key";
        public const string OrbVisible = "dictation.orb.visible";
        public const string OrbLook = "dictation.orb.look";
        public const string OrbTheme = "dictation.orb.theme";
        public const string OrbSize = "dictation.orb.size";
        public const string OrbHidesWhenIdle = "dictation.orb.hidesWhenIdle";
        public const string OrbHidesWhenIdleDefaultApplied = "dictation.orb.hidesWhenIdle.defaultApplied";
        public const string OrbRestsExpanded = "dictation.orb.restsExpanded";
        public const string OrbIsDraggable = "dictation.orb.draggable";
        public const string OrbPosition = "dictation.orb.position";
        public const string Tooltips = "dictation.tooltips";
        public const string ReduceAnimation = "dictation.reduceAnimation";
        public const string Appearance = "dictation.appearance";
    }

    private readonly Preferences preferences;

    [ObservableProperty] private bool orbVisible = true;
    [ObservableProperty] private OrbLookChoice orbLook = OrbLookChoice.Pill;
    [ObservableProperty] private OrbThemeChoice orbTheme = OrbThemeChoice.Black;
    [ObservableProperty] private OrbSizeChoice orbSize = OrbSizeChoice.Full;
    [ObservableProperty] private bool orbHidesWhenIdle = true;
    [ObservableProperty] private bool orbRestsExpanded;
    [ObservableProperty] private bool orbIsDraggable = true;
    [ObservableProperty] private bool tooltips;
    [ObservableProperty] private bool reduceAnimation;
    [ObservableProperty] private DictationAppearanceChoice appearance = DictationAppearanceChoice.System;
    [ObservableProperty] private DictationHotkey dictationKey = DictationHotkeys.Default;

    public OrbSettings(Preferences preferences)
    {
        this.preferences = preferences;
        // The Mac turned "hide when not in use" on once for everyone, then left it to the person.
        if (!preferences.GetBool(Keys.OrbHidesWhenIdleDefaultApplied))
        {
            preferences.Set(Keys.OrbHidesWhenIdleDefaultApplied, true);
            preferences.Set(Keys.OrbHidesWhenIdle, true);
        }
        Reload();
        preferences.Changed += key =>
        {
            if (key.StartsWith("dictation.", StringComparison.Ordinal)) SettingsChanged?.Invoke();
        };
    }

    /// <summary>A dictation preference changed (raised on the thread that wrote it); call <see cref="Reload"/> on the UI thread.</summary>
    public event Action? SettingsChanged;

    public void Reload()
    {
        OrbVisible = preferences.GetOptionalBool(Keys.OrbVisible) ?? true;
        OrbLook = RawValues.FromRawValue<OrbLookChoice>(preferences.GetString(Keys.OrbLook)) ?? OrbLookChoice.Pill;
        OrbTheme = RawValues.FromRawValue<OrbThemeChoice>(preferences.GetString(Keys.OrbTheme)) ?? OrbThemeChoice.Black;
        OrbSize = RawValues.FromRawValue<OrbSizeChoice>(preferences.GetString(Keys.OrbSize)) ?? OrbSizeChoice.Full;
        OrbHidesWhenIdle = preferences.GetBool(Keys.OrbHidesWhenIdle);
        OrbRestsExpanded = preferences.GetBool(Keys.OrbRestsExpanded);
        OrbIsDraggable = preferences.GetOptionalBool(Keys.OrbIsDraggable) ?? true;
        Tooltips = preferences.GetBool(Keys.Tooltips);
        ReduceAnimation = preferences.GetBool(Keys.ReduceAnimation);
        Appearance = RawValues.FromRawValue<DictationAppearanceChoice>(preferences.GetString(Keys.Appearance)) ?? DictationAppearanceChoice.System;
        DictationKey = DictationHotkeys.FromRawValue(preferences.GetString(Keys.DictationKey));
    }

    /// <summary>Where the pill's bottom centre was dragged to, in physical virtual-desktop pixels ("x,y", the Mac's
    /// encoding; the Mac stores AppKit points, so a value is only meaningful on the platform that wrote it).</summary>
    public (double X, double Y)? OrbPosition
    {
        get => DecodePoint(preferences.GetString(Keys.OrbPosition));
        set => preferences.Set(Keys.OrbPosition, value is { } point ? EncodePoint(point) : null);
    }

    public static string EncodePoint((double X, double Y) point) =>
        FormattableString.Invariant($"{point.X},{point.Y}");

    public static (double X, double Y)? DecodePoint(string? encoded)
    {
        if (encoded is null) return null;
        var parts = encoded.Split(',');
        if (parts.Length != 2) return null;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out var x)) return null;
        if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out var y)) return null;
        return (x, y);
    }
}
