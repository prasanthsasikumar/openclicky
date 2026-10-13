//
//  PointerPresence.cs
//  OpenClicky (Windows port of PointerPresence.swift)
//
//  When the Clicky pointer is on screen while it follows the mouse. Over a video the system
//  cursor slips away once the mouse rests and comes back on a nudge; the pointer can do the same,
//  or stay out of sight until the mouse is shaken, the way macOS finds a lost cursor.
//

namespace OpenClicky.Core.Pointing;

public enum PointerPresence
{
    /// <summary>The pointer follows the mouse all the time.</summary>
    Always,
    /// <summary>It fades out once the mouse has rested a few seconds, and any movement brings it back.</summary>
    WhileMoving,
    /// <summary>It stays out of sight until the mouse is shaken, then follows until the mouse rests.</summary>
    OnShake,
}

public static class PointerPresenceExtensions
{
    public static readonly IReadOnlyList<PointerPresence> AllCases = new[] { PointerPresence.Always, PointerPresence.WhileMoving, PointerPresence.OnShake };

    /// <summary>The stored value (the Swift raw value).</summary>
    public static string RawValue(this PointerPresence presence) => presence switch
    {
        PointerPresence.Always => "always",
        PointerPresence.WhileMoving => "whileMoving",
        _ => "onShake",
    };

    public static PointerPresence? FromRawValue(string? rawValue) =>
        AllCases.Cast<PointerPresence?>().FirstOrDefault(presence => presence!.Value.RawValue() == rawValue);

    public static string Label(this PointerPresence presence) => presence switch
    {
        PointerPresence.Always => "always",
        PointerPresence.WhileMoving => "while the mouse moves",
        _ => "after a shake",
    };
}

/// <summary>The pointer's default: on screen all the time while someone is learning OpenClicky, then out of sight until a
/// shake once they've used it a few times. Someone who picked a setting keeps it.</summary>
public static class PointerPresenceDefault
{
    /// <summary>Uses (a dictation with words, a spoken answer) before the pointer starts hiding until a shake.</summary>
    public const int UsesBeforeShake = 5;

    /// <summary>The presence to switch to now, or null to leave it alone.</summary>
    public static PointerPresence? PresenceToSwitchTo(int useCount, bool personChoseOne, PointerPresence current)
    {
        if (personChoseOne || current != PointerPresence.Always || useCount < UsesBeforeShake) return null;
        return PointerPresence.OnShake;
    }
}

/// <summary>Spots a side-to-side (or up-and-down) shake: several quick reversals of direction, each after a swing long
/// enough that a wobble of the hand does not count. Fed with mouse samples. It asks for a deliberate shake: three
/// reversals of 25 pt woke the pointer during ordinary back-and-forth mousing, so it takes four, each after a swing of
/// at least 60 pt.</summary>
public sealed class MouseShakeDetector
{
    /// <summary>Reversals must all fall within this many seconds.</summary>
    public double Window { get; set; } = 0.8;
    /// <summary>A swing shorter than this before a reversal is ignored.</summary>
    public double MinimumSwing { get; set; } = 60;
    /// <summary>How many qualifying reversals make a shake.</summary>
    public int ReversalsNeeded { get; set; } = 4;

    private sealed class Axis
    {
        private double? last;
        private int direction;
        private double swingStart;
        private readonly List<double> reversals = new();

        public bool Feed(double value, double time, double minimumSwing, double window, int reversalsNeeded)
        {
            try
            {
                if (last is not { } lastValue)
                {
                    swingStart = value;
                    return false;
                }
                var delta = value - lastValue;
                if (Math.Abs(delta) < 1) return false;
                var newDirection = delta > 0 ? 1 : -1;
                if (newDirection != direction)
                {
                    if (direction != 0 && Math.Abs(lastValue - swingStart) >= minimumSwing)
                    {
                        reversals.Add(time);
                    }
                    swingStart = lastValue;
                    direction = newDirection;
                }
                reversals.RemoveAll(reversalTime => time - reversalTime > window);
                if (reversals.Count >= reversalsNeeded)
                {
                    reversals.Clear();
                    return true;
                }
                return false;
            }
            finally
            {
                last = value;
            }
        }
    }

    private readonly Axis horizontal = new();
    private readonly Axis vertical = new();

    /// <summary>Returns true on the sample that completes a shake.</summary>
    public bool Feed(ScreenPoint point, double time)
    {
        var shookSideways = horizontal.Feed(point.X, time, MinimumSwing, Window, ReversalsNeeded);
        var shookUpDown = vertical.Feed(point.Y, time, MinimumSwing, Window, ReversalsNeeded);
        return shookSideways || shookUpDown;
    }
}

/// <summary>Decides, sample by sample, whether the pointer should be on screen for the chosen presence.</summary>
public sealed class PointerPresenceTracker
{
    private PointerPresence presence;

    public PointerPresence Presence
    {
        get => presence;
        set
        {
            presence = value;
            if (value == PointerPresence.Always) IsAwake = true;
        }
    }

    /// <summary>Seconds of still mouse before the pointer rests away.</summary>
    public double RestDelay { get; set; }
    public bool IsAwake { get; private set; }
    /// <summary>True on the one update that completed a shake (only watched for in the shake mode).</summary>
    public bool ShookOnLastUpdate { get; private set; }

    private double? lastMovedAt;
    private ScreenPoint? lastPoint;
    private readonly MouseShakeDetector shake = new();

    public PointerPresenceTracker(PointerPresence presence, double restDelay = 3)
    {
        this.presence = presence;
        RestDelay = restDelay;
        IsAwake = presence != PointerPresence.OnShake;
    }

    /// <summary>Feed the mouse position; returns whether the pointer should be on screen.</summary>
    public bool Update(ScreenPoint mouse, double time)
    {
        bool moved;
        if (lastPoint is { } previous)
        {
            moved = mouse.DistanceTo(previous) >= 1;
        }
        else
        {
            moved = false;
            lastMovedAt = time;
        }
        lastPoint = mouse;
        if (moved) lastMovedAt = time;
        var shaken = shake.Feed(mouse, time);
        ShookOnLastUpdate = presence == PointerPresence.OnShake && shaken;

        switch (presence)
        {
            case PointerPresence.Always:
                IsAwake = true;
                break;
            case PointerPresence.WhileMoving:
                IsAwake = moved || time - (lastMovedAt ?? time) < RestDelay;
                break;
            case PointerPresence.OnShake:
                if (shaken)
                {
                    IsAwake = true;
                    lastMovedAt = time;
                }
                if (IsAwake && !moved && time - (lastMovedAt ?? time) >= RestDelay) IsAwake = false;
                break;
        }
        return IsAwake;
    }

    /// <summary>Show the pointer now (it just flew back to the mouse, say); it rests away again as usual.</summary>
    public void Wake(double time)
    {
        IsAwake = true;
        lastMovedAt = time;
    }
}
