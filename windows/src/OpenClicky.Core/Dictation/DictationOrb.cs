// The orb as the take controller drives it (the state half of Dictation/OrbPanel.swift's OrbModel):
// the phase, the hint pill, the live words, the transcript box and its rewrite chips. The orb's
// window lives in OpenClicky.Windows/Orb; Companion's orb model is being written alongside.
//
// TODO(integration): Companion's OrbModel implements IDictationOrb (or the orb view binds a
// DictationOrbState directly), and the composition root hands it to DictationTakeController.

using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenClicky.Core.Dictation;

/// <summary>Where the orb is in a take (Swift OrbPhase). Records, so two phases compare by value.</summary>
public abstract record DictationOrbPhase
{
    private DictationOrbPhase() { }

    public sealed record IdlePhase : DictationOrbPhase;
    public sealed record ListeningPhase : DictationOrbPhase;
    /// <summary>A Hey Clicky edit is being spoken.</summary>
    public sealed record EditListeningPhase : DictationOrbPhase;
    public sealed record WorkingPhase(string Message) : DictationOrbPhase;
    public sealed record DonePhase(string Message) : DictationOrbPhase;
    public sealed record FailedPhase(string Message) : DictationOrbPhase;

    public static DictationOrbPhase Idle { get; } = new IdlePhase();
    public static DictationOrbPhase Listening { get; } = new ListeningPhase();
    public static DictationOrbPhase EditListening { get; } = new EditListeningPhase();
    public static DictationOrbPhase Working(string message) => new WorkingPhase(message);
    public static DictationOrbPhase Done(string message) => new DonePhase(message);
    public static DictationOrbPhase Failed(string message) => new FailedPhase(message);

    /// <summary>Listening or working: the orb is busy.</summary>
    public bool IsBusy => this is ListeningPhase or EditListeningPhase or WorkingPhase;
}

/// <summary>What the take controller sets on the orb (Swift OrbModel's published state and its two callbacks).</summary>
public interface IDictationOrb
{
    DictationOrbPhase Phase { get; set; }
    /// <summary>Words as they arrive while listening.</summary>
    string LiveTranscript { get; set; }
    /// <summary>The transcript box above the pill: the text of the last take when it is open.</summary>
    string? BoxText { get; set; }
    /// <summary>The line under the box's text: why it is open.</summary>
    string BoxReason { get; set; }
    bool IsBoxOpen { get; set; }
    /// <summary>0…1 microphone level for the bars.</summary>
    double AudioLevel { get; set; }
    /// <summary>A hint under the pill ("tap / hold to talk"), when tooltips are on.</summary>
    string? Hint { get; set; }
    /// <summary>Rewrites offered under the box's text ("formal", "casual", "shorter") when a model can do them.</summary>
    IReadOnlyList<string> QuickRewrites { get; set; }
    /// <summary>A rewrite is on its way; the box says so and keeps its old words until it lands.</summary>
    bool IsRewriting { get; set; }
    /// <summary>A rewrite chip was clicked (its label).</summary>
    Action<string>? OnQuickRewrite { get; set; }
    /// <summary>The box's "paste" was clicked.</summary>
    Action? OnPasteFromBox { get; set; }
}

/// <summary>The plain observable orb state (what tests and, until Companion's model lands, the orb view use).</summary>
public sealed partial class DictationOrbState : ObservableObject, IDictationOrb
{
    [ObservableProperty] private DictationOrbPhase phase = DictationOrbPhase.Idle;
    [ObservableProperty] private string liveTranscript = "";
    [ObservableProperty] private string? boxText;
    [ObservableProperty] private string boxReason = "your last take";
    [ObservableProperty] private bool isBoxOpen;
    [ObservableProperty] private double audioLevel;
    [ObservableProperty] private string? hint;
    [ObservableProperty] private IReadOnlyList<string> quickRewrites = [];
    [ObservableProperty] private bool isRewriting;

    public Action<string>? OnQuickRewrite { get; set; }
    public Action? OnPasteFromBox { get; set; }

    public bool IsBusy => Phase.IsBusy;
}
