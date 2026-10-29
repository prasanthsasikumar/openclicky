// Port of macos/OpenClicky/OpenClicky/GuidedSteps.swift
//
// Step by step guidance in the ask lane. Asked how to do something that takes several steps on
// screen ("how do i send an email with an attachment"), Clicky says and points at only the first
// step and ends its reply with the [NEXT] marker. OpenClicky then waits for the person to do it:
// a click, or Return or Tab after typing. A moment later (so a menu or a dialog has time to open)
// it asks again on its own with a fresh screenshot, and the reply is the next step, until a reply
// comes without the marker.
//
// The click that finishes a step is checked against where the pointer pointed for it. One far from
// it ("off target") gets a caption at once and a different "what's next?", asking the model to
// check the new screen and point back if need be. It is a hint, never a block: the pointed spot is
// approximate. Return and Tab have no position, so they count as unknown.
//
// Clicks and keys are watched only while a guide waits for a step. On the Mac that is a
// listen-only global NSEvent monitor; on Windows the OS side implements `IGuidedStepInputSource`
// (low-level mouse/keyboard hooks, listen-only, ignoring OpenClicky's own windows) and maps each
// event with `GuidedStepInputMapping`. Esc, a new question, the island's stop button, two minutes
// of nothing, or the step limit end the guide.

using System.Text.RegularExpressions;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Answers;

/// <summary>The [NEXT] marker that ends a reply which is one step of a guide.</summary>
public static class GuidedStepMarker
{
    public const string Marker = "[NEXT]";

    /// <summary>Matches the marker however a model spaces or cases it ("[next]", "[ NEXT ]").</summary>
    private static readonly Regex MarkerPattern = new(@"\[\s*next\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool ContainsMarker(string text) => MarkerPattern.IsMatch(text);

    /// <summary>The text without any marker, trimmed: what is spoken and kept in the conversation.</summary>
    public static string RemovingMarkers(string text) => MarkerPattern.Replace(text, "").Trim();
}

/// <summary>What the person did while a step was waiting, as the watcher reads an event.</summary>
public enum GuidedStepInput
{
    Click,
    /// <summary>Return or Tab: what ends typing into a field.</summary>
    StepKey,
    EscapeKey,
    OtherKey,
}

/// <summary>What the watcher does about one input. Pure, so the rules are unit-tested without events.</summary>
public abstract record GuidedStepReaction
{
    private GuidedStepReaction() { }

    /// <summary>The step looks done: ask for the next one once the screen has had `SettleSeconds` to change.</summary>
    public sealed record AskForNextStep(double SettleSeconds) : GuidedStepReaction;
    /// <summary>The person is still busy (typing after a click): wait longer before asking.</summary>
    public sealed record PostponePendingAsk(double SettleSeconds) : GuidedStepReaction;
    /// <summary>Esc: the person wants Clicky to stop.</summary>
    public sealed record EndGuide : GuidedStepReaction;
    public sealed record Ignore : GuidedStepReaction;
}

public static class GuidedStepRules
{
    /// <summary>A menu, sheet or dialog opened by the click needs a moment before the screenshot shows it.</summary>
    public const double SettleSecondsAfterClick = 1.5;
    public const double SettleSecondsAfterStepKey = 1.2;
    /// <summary>Typing after a click waits for this much quiet, so a slow typist's pause is not the end of the step.</summary>
    public const double SettleSecondsWhileTyping = 4;
    /// <summary>No click or Return for this long and the guide is over (they gave up or went elsewhere).</summary>
    public const double IdleSecondsBeforeGivingUp = 120;
    /// <summary>A runaway guide (a model that always says there is more) stops here.</summary>
    public const int MaximumSteps = 15;

    public static GuidedStepReaction Reaction(GuidedStepInput input, bool askIsPending) => input switch
    {
        GuidedStepInput.Click => new GuidedStepReaction.AskForNextStep(SettleSecondsAfterClick),
        GuidedStepInput.StepKey => new GuidedStepReaction.AskForNextStep(SettleSecondsAfterStepKey),
        GuidedStepInput.EscapeKey => new GuidedStepReaction.EndGuide(),
        // Typing on its own is not a finished step, but typing right after a click means the click
        // only put the cursor in a field.
        _ => askIsPending ? new GuidedStepReaction.PostponePendingAsk(SettleSecondsWhileTyping) : new GuidedStepReaction.Ignore(),
    };

    /// <summary>How OpenClicky's own question starts, as the ask prompt describes it.</summary>
    public const string NextStepRequestOpening = "(i did that step. this is my screen now. what's next?";
    /// <summary>How it starts instead when the click landed far from where the pointer pointed.</summary>
    public const string OffTargetRequestOpening = "(i clicked about";

    /// <summary>
    /// The question OpenClicky asks for the person once they did a step. It repeats what they first
    /// asked, because the conversation keeps only the last ten exchanges. A click that was off target
    /// says how far off, so the model checks the new screen before going on.
    /// </summary>
    public static string NextStepRequest(string guideGoal, GuidedStepClickCheck.Verdict? clickCheck = null)
    {
        if (clickCheck is GuidedStepClickCheck.Verdict.OffTarget offTarget)
        {
            return OffTargetRequestOpening + $" {offTarget.PointsAway} points away from where you pointed. this is my screen now. "
                + "check whether that was the right thing; if not, gently point me back. what i asked was: " + guideGoal + ")";
        }
        return NextStepRequestOpening + " what i asked was: " + guideGoal + ")";
    }
}

/// <summary>
/// Was the click that finished a step where the pointer pointed? Pointing is 30 to 100 points off
/// at times (see ScreenTextLocator), so only a click well beyond that counts as off target, and even
/// then it is only a hint to the model.
/// </summary>
public static class GuidedStepClickCheck
{
    /// <summary>Farther than this from the pointed spot (points: DIPs on Windows) is off target.</summary>
    public const double OffTargetDistance = 120;
    public const string OffTargetCaption = "that wasn't the spot i pointed at, let me look";

    public abstract record Verdict
    {
        private Verdict() { }

        /// <summary>No click (Return or Tab finished the step) or nothing was pointed at.</summary>
        public sealed record Unknown : Verdict;
        public sealed record OnTarget : Verdict;
        /// <summary>About this many points away, rounded to ten.</summary>
        public sealed record OffTarget(int PointsAway) : Verdict;
    }

    /// <summary>
    /// Both points in the same space: global physical pixels on Windows (the Mac's were AppKit
    /// points). <paramref name="pixelsPerPoint"/> is the scale of the monitor the pointer pointed on,
    /// so the threshold and the distance said are in points, as on the Mac, whatever the DPI.
    /// </summary>
    public static Verdict Check(ScreenPoint? pointedAt, ScreenPoint? clickedAt, double pixelsPerPoint = 1, double threshold = OffTargetDistance)
    {
        if (pointedAt is not { } pointed || clickedAt is not { } clicked) return new Verdict.Unknown();
        var distance = Math.Sqrt(Math.Pow(clicked.X - pointed.X, 2) + Math.Pow(clicked.Y - pointed.Y, 2)) / Math.Max(pixelsPerPoint, 0.01);
        if (distance <= threshold) return new Verdict.OnTarget();
        return new Verdict.OffTarget((int)Math.Round(distance / 10, MidpointRounding.AwayFromZero) * 10);
    }

    /// <summary>For the log: "on target", "off target by 180", "unknown".</summary>
    public static string Describe(Verdict verdict) => verdict switch
    {
        Verdict.OnTarget => "on target",
        Verdict.OffTarget offTarget => $"off target by {offTarget.PointsAway}",
        _ => "unknown",
    };
}

/// <summary>Maps Windows input to a GuidedStepInput (the Mac read NSEvent key codes 36/76/48/53).</summary>
public static class GuidedStepInputMapping
{
    public const int VirtualKeyReturn = 0x0D; // VK_RETURN (Enter and keypad Enter share it)
    public const int VirtualKeyTab = 0x09;    // VK_TAB
    public const int VirtualKeyEscape = 0x1B; // VK_ESCAPE

    /// <summary>A key going down, by its virtual-key code.</summary>
    public static GuidedStepInput ForKeyDown(int virtualKeyCode) => virtualKeyCode switch
    {
        VirtualKeyReturn or VirtualKeyTab => GuidedStepInput.StepKey,
        VirtualKeyEscape => GuidedStepInput.EscapeKey,
        _ => GuidedStepInput.OtherKey,
    };

    /// <summary>A left mouse button coming up.</summary>
    public static GuidedStepInput ForLeftMouseUp() => GuidedStepInput.Click;
}

/// <summary>
/// The OS side of the guide watcher: a listen-only source of clicks (left button up) and key downs
/// anywhere except OpenClicky's own windows, already mapped with GuidedStepInputMapping.
/// </summary>
public interface IGuidedStepInputSource
{
    /// <summary>
    /// Raised for each input while started; may be raised on any thread. A click comes with where it
    /// landed (global physical pixels); a key with null.
    /// </summary>
    event Action<GuidedStepInput, ScreenPoint?>? InputReceived;
    void Start();
    void Stop();
}

/// <summary>
/// Watches for the person doing the step Clicky just gave. One watcher per waiting step. The
/// callbacks run on the thread pool; the caller marshals to the UI thread.
/// </summary>
public sealed class GuidedStepWatcher
{
    public enum EndReason
    {
        Escape,
        GaveUp,
    }

    public static string Describe(EndReason reason) => reason == EndReason.Escape ? "escape" : "gave up";

    private readonly IGuidedStepInputSource inputSource;
    /// <summary>Called with where the click that finished the step landed (global physical pixels), or null when Return or Tab finished it.</summary>
    private readonly Action<ScreenPoint?> onStepDone;
    private readonly Action<EndReason> onEnded;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object gate = new();
    private bool isWatching;
    private CancellationTokenSource? pendingAsk;
    private CancellationTokenSource? idleTimeout;
    /// <summary>The last click while this step waited; Return or Tab after it clears it (no position).</summary>
    private ScreenPoint? lastClickLocation;

    public GuidedStepWatcher(IGuidedStepInputSource inputSource, Action<ScreenPoint?> onStepDone, Action<EndReason> onEnded,
                             Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.inputSource = inputSource;
        this.onStepDone = onStepDone;
        this.onEnded = onEnded;
        this.delay = delay ?? Task.Delay;
    }

    public void Start()
    {
        CancellationTokenSource idleToken;
        lock (gate)
        {
            isWatching = true;
            idleTimeout = idleToken = new CancellationTokenSource();
        }
        inputSource.InputReceived += Handle;
        inputSource.Start();
        _ = RunAfter(TimeSpan.FromSeconds(GuidedStepRules.IdleSecondsBeforeGivingUp), idleToken.Token, () => Finish(EndReason.GaveUp));
    }

    /// <summary>Stops watching without reporting anything (a new question or the stop button).</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (isWatching)
            {
                inputSource.InputReceived -= Handle;
                inputSource.Stop();
            }
            isWatching = false;
            pendingAsk?.Cancel();
            pendingAsk = null;
            idleTimeout?.Cancel();
            idleTimeout = null;
        }
    }

    private void Handle(GuidedStepInput input, ScreenPoint? clickLocation)
    {
        CancellationTokenSource askToken;
        double settleSeconds;
        GuidedStepReaction reaction;
        lock (gate)
        {
            if (!isWatching) return;
            if (input == GuidedStepInput.Click) lastClickLocation = clickLocation;
            else if (input == GuidedStepInput.StepKey) lastClickLocation = null;
            reaction = GuidedStepRules.Reaction(input, askIsPending: pendingAsk != null);
            switch (reaction)
            {
                case GuidedStepReaction.AskForNextStep ask: settleSeconds = ask.SettleSeconds; break;
                case GuidedStepReaction.PostponePendingAsk postpone: settleSeconds = postpone.SettleSeconds; break;
                case GuidedStepReaction.EndGuide: settleSeconds = 0; break;
                default: return;
            }
            if (reaction is GuidedStepReaction.EndGuide)
            {
                askToken = null!;
            }
            else
            {
                // Another click or key restarts the wait, so a double click or a quick second click
                // (a menu, then its item) is one step.
                pendingAsk?.Cancel();
                pendingAsk = askToken = new CancellationTokenSource();
            }
        }
        if (reaction is GuidedStepReaction.EndGuide)
        {
            Finish(EndReason.Escape);
            return;
        }
        _ = RunAfter(TimeSpan.FromSeconds(settleSeconds), askToken.Token, () =>
        {
            ScreenPoint? finishingClick;
            lock (gate) finishingClick = lastClickLocation;
            Stop();
            onStepDone(finishingClick);
        });
    }

    private void Finish(EndReason reason)
    {
        lock (gate)
        {
            if (!isWatching) return;
        }
        Stop();
        onEnded(reason);
    }

    private async Task RunAfter(TimeSpan wait, CancellationToken cancellationToken, Action action)
    {
        try
        {
            await delay(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested) return;
        action();
    }
}
