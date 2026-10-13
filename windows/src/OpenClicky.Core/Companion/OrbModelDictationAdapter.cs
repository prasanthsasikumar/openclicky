// The companion's OrbModel (what the orb window draws) as the take controller's IDictationOrb: the
// controller's DictationOrbPhase records map onto OrbPhase, everything else is the same property.

using OpenClicky.Core.Dictation;

namespace OpenClicky.Core.Companion;

public sealed class OrbModelDictationAdapter(OrbModel orb) : IDictationOrb
{
    public OrbModel Orb { get; } = orb;

    public DictationOrbPhase Phase
    {
        get => ToDictation(Orb.Phase);
        set => Orb.Phase = ToOrb(value);
    }

    public static OrbPhase ToOrb(DictationOrbPhase phase) => phase switch
    {
        DictationOrbPhase.ListeningPhase => OrbPhase.Listening,
        DictationOrbPhase.EditListeningPhase => OrbPhase.EditListening,
        DictationOrbPhase.WorkingPhase working => OrbPhase.Working(working.Message),
        DictationOrbPhase.DonePhase done => OrbPhase.Done(done.Message),
        DictationOrbPhase.FailedPhase failed => OrbPhase.Failed(failed.Message),
        _ => OrbPhase.Idle,
    };

    public static DictationOrbPhase ToDictation(OrbPhase phase) => phase.Kind switch
    {
        OrbPhaseKind.Listening => DictationOrbPhase.Listening,
        OrbPhaseKind.EditListening => DictationOrbPhase.EditListening,
        OrbPhaseKind.Working => DictationOrbPhase.Working(phase.Message),
        OrbPhaseKind.Done => DictationOrbPhase.Done(phase.Message),
        OrbPhaseKind.Failed => DictationOrbPhase.Failed(phase.Message),
        _ => DictationOrbPhase.Idle,
    };

    public string LiveTranscript { get => Orb.LiveTranscript; set => Orb.LiveTranscript = value; }
    public string? BoxText { get => Orb.BoxText; set => Orb.BoxText = value; }
    public string BoxReason { get => Orb.BoxReason; set => Orb.BoxReason = value; }
    public bool IsBoxOpen { get => Orb.IsBoxOpen; set => Orb.IsBoxOpen = value; }
    public double AudioLevel { get => Orb.AudioLevel; set => Orb.AudioLevel = value; }
    public string? Hint { get => Orb.Hint; set => Orb.Hint = value; }
    public IReadOnlyList<string> QuickRewrites { get => Orb.QuickRewrites; set => Orb.QuickRewrites = value; }
    public bool IsRewriting { get => Orb.IsRewriting; set => Orb.IsRewriting = value; }
    public Action<string>? OnQuickRewrite { get => Orb.OnQuickRewrite; set => Orb.OnQuickRewrite = value; }
    public Action? OnPasteFromBox { get => Orb.OnPasteFromBox; set => Orb.OnPasteFromBox = value; }
}
