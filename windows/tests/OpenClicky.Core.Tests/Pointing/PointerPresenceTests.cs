// Port of PointerPresenceTests.swift.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class PointerPresenceTests
{
    private const double FrameInterval = 1.0 / 30.0;

    /// <summary>Feeds a straight move over <paramref name="duration"/> seconds, sampled at 30 Hz.</summary>
    private static double Sweep(PointerPresenceTracker tracker, ScreenPoint from, ScreenPoint to, double start, double duration)
    {
        var steps = Math.Max(1, (int)(duration / FrameInterval));
        var time = start;
        for (var step = 1; step <= steps; step++)
        {
            var fraction = (double)step / steps;
            time = start + duration * step / steps;
            tracker.Update(new ScreenPoint(from.X + (to.X - from.X) * fraction, from.Y + (to.Y - from.Y) * fraction), time);
        }
        return time;
    }

    private static double Rest(PointerPresenceTracker tracker, ScreenPoint point, double start, double duration) =>
        Sweep(tracker, point, point, start, duration);

    /// <summary>A deliberate side-to-side shake: five 120 pt swings (four reversals) in about two thirds of a second.</summary>
    private static double Shake(PointerPresenceTracker tracker, ScreenPoint point, double start)
    {
        var time = start;
        var from = point;
        for (var swing = 0; swing < 5; swing++)
        {
            var to = new ScreenPoint(point.X + (swing % 2 == 0 ? 120 : -120), point.Y);
            time = Sweep(tracker, from, to, time, 0.13);
            from = to;
        }
        return time;
    }

    [Fact]
    public void AlwaysStaysAwakeThroughALongRest()
    {
        var tracker = new PointerPresenceTracker(PointerPresence.Always);
        Assert.True(tracker.IsAwake);
        Rest(tracker, new ScreenPoint(100, 100), 0, 10);
        Assert.True(tracker.IsAwake);
    }

    [Fact]
    public void WhileMovingRestsAwayAfterTheDelayAndReturnsOnMovement()
    {
        var tracker = new PointerPresenceTracker(PointerPresence.WhileMoving, 3);
        Assert.True(tracker.IsAwake);
        var time = Rest(tracker, new ScreenPoint(100, 100), 0, 2.5);
        Assert.True(tracker.IsAwake); // still within the rest delay
        time = Rest(tracker, new ScreenPoint(100, 100), time, 1);
        Assert.False(tracker.IsAwake); // the mouse has rested past the delay
        Sweep(tracker, new ScreenPoint(100, 100), new ScreenPoint(140, 100), time, 0.1);
        Assert.True(tracker.IsAwake); // any movement brings it back
    }

    [Fact]
    public void WhileMovingIgnoresSensorJitter()
    {
        var tracker = new PointerPresenceTracker(PointerPresence.WhileMoving, 3);
        var time = Rest(tracker, new ScreenPoint(100, 100), 0, 3.5);
        Assert.False(tracker.IsAwake);
        Sweep(tracker, new ScreenPoint(100, 100), new ScreenPoint(100.4, 100.3), time, 0.1);
        Assert.False(tracker.IsAwake); // sub-point drift is not movement
    }

    [Fact]
    public void OnShakeStartsHiddenAndIgnoresPlainMovement()
    {
        var tracker = new PointerPresenceTracker(PointerPresence.OnShake, 3);
        Assert.False(tracker.IsAwake);
        Sweep(tracker, new ScreenPoint(0, 0), new ScreenPoint(800, 500), 0, 1);
        Assert.False(tracker.IsAwake);
    }

    [Fact]
    public void OnShakeWakesOnAShakeAndRestsAwayAgain()
    {
        var tracker = new PointerPresenceTracker(PointerPresence.OnShake, 3);
        var time = Shake(tracker, new ScreenPoint(400, 300), 0);
        Assert.True(tracker.IsAwake); // a shake summons the pointer
        time = Sweep(tracker, new ScreenPoint(400, 300), new ScreenPoint(600, 300), time, 1);
        Assert.True(tracker.IsAwake); // it follows while the mouse keeps moving
        Rest(tracker, new ScreenPoint(600, 300), time, 3.5);
        Assert.False(tracker.IsAwake); // and rests away once the mouse stops
    }

    [Fact]
    public void ASlowBackAndForthIsNotAShake()
    {
        var tracker = new PointerPresenceTracker(PointerPresence.OnShake, 3);
        double time = 0;
        var from = new ScreenPoint(400, 300);
        for (var swing = 0; swing < 4; swing++)
        {
            var to = new ScreenPoint(400 + (swing % 2 == 0 ? 120 : -120), 300);
            time = Sweep(tracker, from, to, time, 0.8);
            from = to;
        }
        Assert.False(tracker.IsAwake);
    }

    [Fact]
    public void WakeShowsThePointerUntilTheMouseRests()
    {
        var tracker = new PointerPresenceTracker(PointerPresence.OnShake, 3);
        tracker.Wake(10);
        Assert.True(tracker.IsAwake);
        Rest(tracker, new ScreenPoint(100, 100), 10, 3.5);
        Assert.False(tracker.IsAwake);
    }

    [Fact]
    public void SwitchingToAlwaysWakesImmediately()
    {
        var tracker = new PointerPresenceTracker(PointerPresence.OnShake, 3);
        Assert.False(tracker.IsAwake);
        tracker.Presence = PointerPresence.Always;
        tracker.Update(new ScreenPoint(1, 1), 0.5);
        Assert.True(tracker.IsAwake);
    }

    [Fact]
    public void OnlyTheShakeModeReportsAShake()
    {
        var shakeMode = new PointerPresenceTracker(PointerPresence.OnShake, 3);
        var alwaysMode = new PointerPresenceTracker(PointerPresence.Always, 3);
        var shakeSeen = false;
        var alwaysSeen = false;
        double time = 0;
        var from = new ScreenPoint(400, 300);
        for (var swing = 0; swing < 5; swing++)
        {
            var to = new ScreenPoint(400 + (swing % 2 == 0 ? 120 : -120), 300);
            const int steps = 4;
            for (var step = 1; step <= steps; step++)
            {
                time += 0.033;
                var point = new ScreenPoint(from.X + (to.X - from.X) * step / steps, 300);
                shakeMode.Update(point, time);
                alwaysMode.Update(point, time);
                if (shakeMode.ShookOnLastUpdate) shakeSeen = true;
                if (alwaysMode.ShookOnLastUpdate) alwaysSeen = true;
            }
            from = to;
        }
        Assert.True(shakeSeen);
        Assert.False(alwaysSeen);
    }

    private static bool Shakes(double[] turns)
    {
        var detector = new MouseShakeDetector();
        double time = 0;
        for (var index = 1; index < turns.Length; index++)
        {
            var previous = turns[index - 1];
            for (var step = 1; step <= 4; step++)
            {
                time += 0.033;
                if (detector.Feed(new ScreenPoint(previous + (turns[index] - previous) * step / 4, 50), time)) return true;
            }
        }
        return false;
    }

    [Fact]
    public void AQuickWobbleOrThreeTurnsIsNotAShake()
    {
        Assert.False(Shakes(new double[] { 100, 220, 100, 220, 100 })); // three reversals used to be enough
        Assert.False(Shakes(new double[] { 100, 140, 100, 140, 100, 140, 100, 140 })); // a 40 pt jiggle is the hand
    }

    [Fact]
    public void ShakeDetectorNeedsReversalsWithinTheWindow()
    {
        Assert.True(Shakes(new double[] { 100, 220, 100, 220, 100, 220 }));
    }

    [Fact]
    public void TheDefaultSwitchesToShakeAfterFiveUsesUnlessChosen()
    {
        Assert.Null(PointerPresenceDefault.PresenceToSwitchTo(4, false, PointerPresence.Always));
        Assert.Equal(PointerPresence.OnShake, PointerPresenceDefault.PresenceToSwitchTo(5, false, PointerPresence.Always));
        Assert.Null(PointerPresenceDefault.PresenceToSwitchTo(9, true, PointerPresence.Always));
        Assert.Null(PointerPresenceDefault.PresenceToSwitchTo(9, false, PointerPresence.WhileMoving));
        Assert.Equal(PointerPresence.WhileMoving, PointerPresenceExtensions.FromRawValue("whileMoving"));
    }
}
