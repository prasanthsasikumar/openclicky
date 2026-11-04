//
//  ScreenMarker.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Marking part of the screen before asking: the island's marker button (or its shortcut, Ctrl+Alt+M
//  unless Settings pick another, only while drawn answers are on) lets the
//  person draw a loop with a yellow marker round a few lines of code, part of a photo, a chart. The
//  next question is about that part: the screenshot goes with a close-up of it, and the question
//  says where it is, so "how does this work?" is answered about what was marked and not the whole
//  window. The mark stays drawn while that answer is given, then goes; one left unasked about goes
//  after a few minutes.
//
//  The drawing window is the platform's (IScreenMarkerSurface); what the mark means is decided here.
//

using OpenClicky.Core.Answers;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Companion;

/// <summary>The keys that start a mark from anywhere (Settings → Clicky → Drawings).</summary>
public enum MarkerShortcut
{
    CtrlAltM,
    CtrlAltShiftM,
    Off,
}

public static class MarkerShortcuts
{
    public static IReadOnlyList<(MarkerShortcut Shortcut, string Title)> Choices { get; } =
        [(MarkerShortcut.CtrlAltM, "Ctrl+Alt+M"), (MarkerShortcut.CtrlAltShiftM, "Ctrl+Alt+Shift+M"), (MarkerShortcut.Off, "None")];

    /// <summary>shell.json's `markerShortcut` ("ctrlAltShiftM", "off"); Ctrl+Alt+M when unset.</summary>
    public static MarkerShortcut From(string? raw) => raw switch
    {
        "ctrlAltShiftM" => MarkerShortcut.CtrlAltShiftM,
        "off" => MarkerShortcut.Off,
        _ => MarkerShortcut.CtrlAltM,
    };

    public static string? RawValue(MarkerShortcut shortcut) => shortcut switch
    {
        MarkerShortcut.CtrlAltShiftM => "ctrlAltShiftM",
        MarkerShortcut.Off => "off",
        _ => null,
    };

    public static string Title(MarkerShortcut shortcut) => Choices.First(choice => choice.Shortcut == shortcut).Title;

    /// <summary>The shortcut to hold now: none while drawn answers are off (the Teacher plugin or its drawing stage), so the
    /// keys stay the other apps' (Word's Ctrl+Alt+M inserts a comment), and none when Settings say so.</summary>
    public static MarkerShortcut? Wanted(string? raw, bool drawingsOn) =>
        drawingsOn && From(raw) is var shortcut && shortcut != MarkerShortcut.Off ? shortcut : null;
}

/// <summary>The window the person marks the screen in (the Windows app's ScreenMarkerWindow).</summary>
public interface IScreenMarkerSurface
{
    /// <summary>Lets the person draw a mark on the monitor the pointer is on; <paramref name="done"/> gets its bounds in
    /// global physical pixels, or null when they gave up (Esc, a right click, a dot).</summary>
    void BeginMarking(Action<PixelRect?> done);

    /// <summary>Takes the mark off the screen.</summary>
    void HideMark();
}

/// <summary>A marked part of the screen, in global physical pixels.</summary>
public sealed record MarkedArea(PixelRect Bounds, DateTimeOffset MarkedAt)
{
    /// <summary>How long a mark waits for its question.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    /// <summary>The space kept round the mark in its close-up, so the loop's own edge doesn't cut words off.</summary>
    public const int CloseUpMargin = 24;

    public bool IsFresh(DateTimeOffset now) => now - MarkedAt <= Lifetime;

    /// <summary>The mark in the capture whose monitor holds most of it, in that screenshot's pixels (clipped to it).</summary>
    public (PixelRect Area, CompanionScreenCapture Capture)? In(IReadOnlyList<CompanionScreenCapture> captures)
    {
        (PixelRect Area, CompanionScreenCapture Capture)? best = null;
        var bestOverlap = 0L;
        foreach (var capture in captures)
        {
            var frame = capture.DisplayFrame;
            var left = Math.Max(Bounds.Left, frame.Left);
            var top = Math.Max(Bounds.Top, frame.Top);
            var right = Math.Min(Bounds.Left + Bounds.Width, frame.Left + frame.Width);
            var bottom = Math.Min(Bounds.Top + Bounds.Height, frame.Top + frame.Height);
            if (right <= left || bottom <= top) continue;
            var overlap = (long)(right - left) * (bottom - top);
            if (overlap <= bestOverlap) continue;
            var (x1, y1) = capture.ScreenshotPoint(new PixelPoint(left, top));
            var (x2, y2) = capture.ScreenshotPoint(new PixelPoint(right, bottom));
            bestOverlap = overlap;
            best = (new PixelRect((int)x1, (int)y1, Math.Max(1, (int)(x2 - x1)), Math.Max(1, (int)(y2 - y1))), capture);
        }
        return best;
    }

    /// <summary>The close-up to cut: the area with a margin, kept inside the image.</summary>
    public static PixelRect CloseUpRect(PixelRect area, int imageWidth, int imageHeight)
    {
        var left = Math.Max(0, area.Left - CloseUpMargin);
        var top = Math.Max(0, area.Top - CloseUpMargin);
        var right = Math.Min(imageWidth, area.Left + area.Width + CloseUpMargin);
        var bottom = Math.Min(imageHeight, area.Top + area.Height + CloseUpMargin);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>Added to the question.</summary>
    public static string PromptLine(PixelRect area, string screenLabel) =>
        $"Before asking, the user marked a part of the screen with a marker: x {area.Left} to {area.Left + area.Width}, y {area.Top} to {area.Top + area.Height} in the screenshot labelled {screenLabel} (a close-up of it is attached). Their question is about what is inside that marked area (a few lines of code, part of a picture, a chart…): answer about exactly that, and when you point or draw, point at it there.";

    /// <summary>The close-up image's label.</summary>
    public const string CloseUpLabel = "Close-up: the part of the screen the user marked, cut from the screenshot with a little space round it. For looking only: never use this image's coordinates in a [POINT:x,y] tag or a drawing.";
}

/// <summary>The mark in progress or waiting for its question (one at a time).</summary>
public sealed class ScreenMarker
{
    public static ScreenMarker Shared { get; } = new();

    /// <summary>Set by the composition root; without it there is nothing to mark with.</summary>
    public IScreenMarkerSurface? Surface { get; set; }

    /// <summary>The mark waiting for its question, if any.</summary>
    public MarkedArea? Current { get; private set; }

    /// <summary>A mark was drawn (it waits for the next question).</summary>
    public event Action<MarkedArea>? Marked;

    /// <summary>Starts drawing a mark (a mark already up is replaced).</summary>
    public void Begin(Func<DateTimeOffset>? now = null)
    {
        if (Surface is not { } surface) return;
        Current = null;
        surface.BeginMarking(bounds => Accept(bounds, (now ?? (() => DateTimeOffset.Now))()));
    }

    /// <summary>A finished mark from the surface; a dot or nothing is no mark.</summary>
    public void Accept(PixelRect? bounds, DateTimeOffset at)
    {
        if (bounds is not { Width: >= 12, Height: >= 12 } area)
        {
            Clear();
            return;
        }
        Current = new MarkedArea(area, at);
        Marked?.Invoke(Current);
    }

    /// <summary>The mark for a question asked now: null when there is none or it has waited too long (it is then cleared).</summary>
    public MarkedArea? ForQuestion(DateTimeOffset now)
    {
        if (Current is { } mark && !mark.IsFresh(now)) Clear();
        return Current;
    }

    /// <summary>Its question has been answered (or it was given up): the mark goes.</summary>
    public void Clear()
    {
        Current = null;
        Surface?.HideMark();
    }

    /// <summary>Whether esc takes the mark away: one thing per press, so only when it had nothing else to stop (an answer,
    /// the fast voice, a take) and took no drawing off the screen; the next press then takes the mark.</summary>
    public static bool EscClearsTheMark(EscapeTarget target, bool clearedADrawing, bool hasAMark) =>
        hasAMark && !clearedADrawing && target == EscapeTarget.Nothing;

    /// <summary>Takes <paramref name="mark"/> away if it is still the current one (a newer mark stays).</summary>
    public void ClearIfCurrent(MarkedArea? mark)
    {
        if (mark is not null && ReferenceEquals(Current, mark)) Clear();
    }
}
