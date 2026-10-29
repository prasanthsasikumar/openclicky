// Port of macos/OpenClicky/OpenClicky/AskLaneControl.swift
//
// The person stays in charge of the ask lane. Esc stops it at any time: a press still listening,
// a question on its way, an answer being said, or a guide waiting for a click. A dictation take
// keeps esc to itself, as it always has. The rules are pure, so they are unit-tested without
// events; CompanionManager.StopAskLane does the stopping. Also here: the pause before the screen
// is sent, and the [UNSURE] marker for a reply that cannot find what to point at.

using System.Text.RegularExpressions;

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

/// <summary>
/// The [UNSURE] marker that opens a reply when the model cannot find the control on screen. Such a
/// reply carries no point (a tag sent anyway is ignored), says in words what to look for or asks
/// what they see, and is never spoken with the marker. Guessing with the pointer would send the
/// person to the wrong place with all the confidence of a right one.
/// </summary>
public static class UnsureMarker
{
    public const string Marker = "[UNSURE]";
    public const string IslandStatus = "not sure · tell me what you see";

    /// <summary>Matches the marker however a model spaces or cases it ("[unsure]", "[ UNSURE ]").</summary>
    private static readonly Regex MarkerPattern = new(@"\[\s*unsure\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingMarkerPattern = new(@"^\s*\[\s*unsure\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool ContainsMarker(string text) => MarkerPattern.IsMatch(text);

    /// <summary>The text without any marker, trimmed.</summary>
    public static string RemovingMarkers(string text) => MarkerPattern.Replace(text, "").Trim();

    /// <summary>The length (UTF-16) of a marker opening <paramref name="text"/> (after any whitespace), or null when it does not open with one.</summary>
    public static int? LeadingMarkerLength(string text)
    {
        var match = LeadingMarkerPattern.Match(text);
        return match.Success ? match.Index + match.Length : null;
    }

    /// <summary>True while a streamed answer's opening ("[", "[UNS") could still turn out to be the marker.</summary>
    public static bool CouldStillBecomeMarker(string opening)
    {
        var soFar = opening[..Math.Min(Marker.Length, opening.Length)].ToUpperInvariant();
        return soFar.Length < Marker.Length && Marker.StartsWith(soFar, StringComparison.Ordinal);
    }
}
