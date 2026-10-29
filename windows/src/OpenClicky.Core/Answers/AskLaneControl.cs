// Port of macos/OpenClicky/OpenClicky/AskLaneControl.swift
//
// The person stays in charge of the ask lane. Esc stops it at any time: a press still listening,
// a question on its way, an answer being said, or a guide waiting for a click. A dictation take
// keeps esc to itself, as it always has. The rules are pure, so they are unit-tested without
// events; CompanionManager.StopAskLane does the stopping.

namespace OpenClicky.Core.Answers;

/// <summary>Where one press of esc goes.</summary>
public enum EscapeTarget
{
    /// <summary>A dictation take is in progress: esc cancels the take, and nothing else.</summary>
    DictationTake,
    /// <summary>The ask lane has something under way: esc stops it.</summary>
    AskLane,
    Nothing,
    /// <summary>The fast voice lane: stop its reply and drop what its actions were about to say.</summary>
    Realtime,
}

public static class AskLaneEscape
{
    /// <summary>
    /// <paramref name="realtimeIsBusy"/>: the fast voice lane is answering, speaking, or still working
    /// on an action: esc stops that too, as it stops the ask lane.
    /// </summary>
    public static EscapeTarget Target(bool dictationTakeInProgress, bool askLaneCanBeStopped, bool realtimeIsBusy = false)
    {
        if (dictationTakeInProgress) return EscapeTarget.DictationTake;
        if (askLaneCanBeStopped) return EscapeTarget.AskLane;
        return realtimeIsBusy ? EscapeTarget.Realtime : EscapeTarget.Nothing;
    }

    /// <summary>
    /// Whether the ask lane has anything to stop: a Ctrl + Win press not yet settled (listening or
    /// transcribing), a question on its way or being answered, or a guide (waiting for a click, or
    /// fetching its next step).
    /// </summary>
    public static bool CanStop(bool pressIsSettled, bool isAskInFlight, int? guideStep) =>
        !pressIsSettled || isAskInFlight || guideStep is not null;
}
