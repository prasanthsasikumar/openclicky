// Port of macos/OpenClicky/OpenClicky/AskLaneControl.swift
//
// The person stays in charge of the ask lane. Esc stops it at any time: a press still listening,
// a question on its way, an answer being said, or a guide waiting for a click. A dictation take
// keeps esc to itself, as it always has. The rules are pure, so they are unit-tested without
// events; CompanionManager.StopAskLane does the stopping. Also here: the pause before the screen
// is sent.

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

/// <summary>
/// The pause between a question and its screenshot. The question is heard, then for a moment the
/// pointer and the island say the screen is about to be sent, and esc or a click on the island
/// calls it off before anything is captured. Settings → Privacy turns it off.
/// </summary>
public static class AskSendPause
{
    public const double DefaultSeconds = 1.2;
    public const string Caption = "sending your screen · esc to cancel";

    /// <summary>
    /// How long to wait before capturing the screen, or null to capture at once. A guide's own
    /// "what's next?" after each click never waits: the task was already let go ahead.
    /// </summary>
    public static double? Seconds(bool isEnabled, bool isGuidedStepContinuation, double pauseSeconds = DefaultSeconds)
    {
        if (!isEnabled || isGuidedStepContinuation || pauseSeconds <= 0) return null;
        return pauseSeconds;
    }

    /// <summary>
    /// Waits out the pause; false when the question was cancelled meanwhile (esc, the island, a new
    /// question), in which case nothing may be captured or sent.
    /// </summary>
    public static async Task<bool> WaitOutAsync(double seconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        return !cancellationToken.IsCancellationRequested;
    }
}
