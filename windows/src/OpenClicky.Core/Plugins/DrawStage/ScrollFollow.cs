//
//  ScrollFollow.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Drawings that follow a scrolling page: marks drawn straight over the screen (a circle round a
//  button, an arrow to a line of code) point at what is under them, so when that scrolls they should
//  go with it. The Windows side grabs a strip of the screen under each such mark a few times a
//  second (the drawing itself is left out of screen captures) and asks this class how the strip
//  moved since the last grab.
//
//  The answer comes from matching lines of pixels: every row of the strip is hashed, and each row of
//  the new strip votes for the shift that puts it on an identical row of the old one. A scroll moves
//  whole rows unchanged, so most rows agree on one shift; rows of one colour (gaps between lines of
//  text) say nothing and don't vote, nor do rows that are just as they were (a toolbar over a page,
//  or a still screen), and the rows that agree on the shift give the part of the strip that scrolled. The
//  same with columns finds a sideways scroll. Anything else (a video playing, a page redrawn) finds
//  no clear shift and the marks stay.
//

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <summary>How a strip of the screen moved: by (<paramref name="Dx"/>, <paramref name="Dy"/>) pixels, one of them 0. The
/// part of the strip that scrolled runs from <paramref name="AreaStart"/> to <paramref name="AreaEnd"/> (exclusive) along
/// the way it moved, in the strip's pixels, before and after the move together: a mark there moves, and one that leaves
/// it scrolled out of sight.</summary>
public readonly record struct ScrollShift(int Dx, int Dy, int AreaStart, int AreaEnd, int AgreeingLines);

public static class ScrollFollow
{
    /// <summary>Lines that must agree on a shift, at the least, and as a share of the lines that aren't one colour.</summary>
    public const int FewestAgreeing = 6;
    public const double LeastShare = 0.2;

    /// <summary>A hash seen on more lines than this is left out of the vote, to keep it quick. Lines that repeat a few times
    /// (the same words on every sixth line, a table's rows) still vote: their wrong shifts scatter while the right one adds up.</summary>
    private const int MostRepeats = 64;

    /// <summary>How the strip moved from <paramref name="before"/> to <paramref name="after"/>: two grabs of the same screen
    /// rectangle, 32-bit pixels (BGRA), <paramref name="width"/> × <paramref name="height"/>, rows <paramref name="stride"/>
    /// bytes apart. Null when it didn't move, or not clearly by a scroll.</summary>
    public static ScrollShift? Find(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int width, int height, int stride)
    {
        if (width < 8 || height < 8 || stride < width * 4 || before.Length < stride * (height - 1) + width * 4 || after.Length < before.Length)
        {
            return null;
        }
        var oldRows = RowHashes(before, width, height, stride);
        var newRows = RowHashes(after, width, height, stride);
        if (Match(oldRows, newRows) is { } down) return new ScrollShift(0, down.Shift, down.Start, down.End, down.Agreeing);
        var oldColumns = ColumnHashes(before, width, height, stride);
        var newColumns = ColumnHashes(after, width, height, stride);
        if (Match(oldColumns, newColumns) is { } across) return new ScrollShift(across.Shift, 0, across.Start, across.End, across.Agreeing);
        return null;
    }

    /// <summary>The shift most lines agree on, other than none, with the span the agreeing lines cover before and after it.</summary>
    internal static (int Shift, int Start, int End, int Agreeing)? Match(ulong?[] old, ulong?[] now)
    {
        var where = new Dictionary<ulong, List<int>>();
        for (var i = 0; i < old.Length; i++)
        {
            if (old[i] is not { } hash) continue;
            if (!where.TryGetValue(hash, out var list)) where[hash] = list = [];
            list.Add(i);
        }
        var votes = new Dictionary<int, int>();
        var counted = 0;
        for (var i = 0; i < now.Length; i++)
        {
            // A line that is just as it was says nothing: it may not have moved, or moved by a whole number of repeats of a
            // page that repeats (so a still screen never looks scrolled).
            if (now[i] is not { } hash || (i < old.Length && old[i] == hash)) continue;
            counted++;
            if (!where.TryGetValue(hash, out var list) || list.Count > MostRepeats) continue;
            foreach (var at in list)
            {
                var shift = i - at;
                votes[shift] = votes.GetValueOrDefault(shift) + 1;
            }
        }
        var best = 0;
        var bestVotes = 0;
        foreach (var (shift, count) in votes)
        {
            if (shift == 0) continue;
            // Ties go to the smaller move: a page scrolls by a little far more often than by a lot.
            if (count > bestVotes || (count == bestVotes && Math.Abs(shift) < Math.Abs(best))) (best, bestVotes) = (shift, count);
        }
        if (bestVotes < FewestAgreeing || bestVotes < LeastShare * counted) return null;
        // The lines that agree: where they are now, and where they were (now - shift); the scrolled part covers both.
        int first = int.MaxValue, last = int.MinValue;
        for (var i = 0; i < now.Length; i++)
        {
            if (now[i] is not { } hash || (i < old.Length && old[i] == hash) || !where.TryGetValue(hash, out var list) || !list.Contains(i - best)) continue;
            first = Math.Min(first, Math.Min(i, i - best));
            last = Math.Max(last, Math.Max(i, i - best));
        }
        return (best, Math.Max(0, first), Math.Min(now.Length, last + 1), bestVotes);
    }

    /// <summary>Each row's hash; null for a row of one colour.</summary>
    internal static ulong?[] RowHashes(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var hashes = new ulong?[height];
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            var plain = true;
            var hash = Offset;
            var first = Colour(row, 0);
            for (var x = 0; x < width; x++)
            {
                var colour = Colour(row, x);
                if (colour != first) plain = false;
                hash = (hash ^ colour) * Prime;
            }
            hashes[y] = plain ? null : hash;
        }
        return hashes;
    }

    /// <summary>Each column's hash; null for a column of one colour.</summary>
    internal static ulong?[] ColumnHashes(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var hashes = new ulong[width];
        var plain = new bool[width];
        var first = new uint[width];
        Array.Fill(hashes, Offset);
        Array.Fill(plain, true);
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                var colour = Colour(row, x);
                if (y == 0) first[x] = colour;
                else if (colour != first[x]) plain[x] = false;
                hashes[x] = (hashes[x] ^ colour) * Prime;
            }
        }
        var result = new ulong?[width];
        for (var x = 0; x < width; x++) result[x] = plain[x] ? null : hashes[x];
        return result;
    }

    /// <summary>A pixel's colour without its alpha (a screen grab's alpha says nothing).</summary>
    private static uint Colour(ReadOnlySpan<byte> row, int x) => (uint)(row[x * 4] | row[x * 4 + 1] << 8 | row[x * 4 + 2] << 16);

    // FNV-1a, 64-bit.
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;
}

/// <summary>A mark drawn over the screen, pointing at what is under it: the page's id for it and its box in screen pixels.</summary>
public sealed record ScreenMark(string Id, PixelRect Box);

/// <summary>A mark to move with the screen under it, by (<paramref name="Dx"/>, <paramref name="Dy"/>) screen pixels. The part of
/// the screen that scrolled runs from <paramref name="AreaStart"/> to <paramref name="AreaEnd"/> along the way it moved (screen
/// pixels, y for a scroll down or up, x for one across): moved out of it, the mark has scrolled out of sight.</summary>
public sealed record MarkMove(string Id, int Dx, int Dy, int AreaStart, int AreaEnd);

/// <summary>A grab of a screen rectangle: 32-bit pixels (BGRA), rows <paramref name="Stride"/> bytes apart.</summary>
public sealed record PixelGrab(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>Watches the screen under the marks that point at it and says which to move when it scrolls. Each mark has a strip
/// of the screen over it, the monitor's height tall (so a toolbar that stays put shows where the scrolling part ends);
/// marks close together share one. <see cref="Tick"/> grabs every strip and compares it with the last grab: a strip that
/// moved by a scroll moves the marks in the part of it that scrolled. A move is only believed just after the person did
/// something (a wheel turned, a key, the mouse): a page that changes by itself (a video, an animation) moves nothing. While
/// the person does nothing the strips are grabbed only every <see cref="IdleEvery"/>th tick, to keep the last grab fresh.</summary>
public sealed class ScreenMarkFollower(Func<PixelRect, PixelGrab?> grab, Func<TimeSpan> sinceInput, PixelRect screen)
{
    /// <summary>Half a strip's width: wide enough to see what's under a mark, narrow enough to grab a few times a second.</summary>
    public const int HalfStrip = 120;

    /// <summary>How long after the person's last input a moving screen is taken as their scroll.</summary>
    public static readonly TimeSpan InputWindow = TimeSpan.FromSeconds(1.5);

    public const int IdleEvery = 5;
    private int idleTicks;

    private List<ScreenMark> marks = [];
    private readonly Dictionary<PixelRect, PixelGrab> lastGrabs = [];

    public IReadOnlyList<ScreenMark> Marks => marks;

    /// <summary>The marks to follow now (the page says so after each one is drawn, moved or forgotten).</summary>
    public void SetMarks(IReadOnlyList<ScreenMark> now)
    {
        marks = [.. now.Where(mark => !mark.Box.IsEmpty && mark.Box.IntersectsWith(screen))];
        var strips = Strips().Select(strip => strip.Area).ToHashSet();
        foreach (var gone in lastGrabs.Keys.Where(area => !strips.Contains(area)).ToList()) lastGrabs.Remove(gone);
    }

    /// <summary>Grabs every strip and returns the marks to move (none, mostly).</summary>
    public IReadOnlyList<MarkMove> Tick()
    {
        if (marks.Count == 0) return [];
        var moves = new List<MarkMove>();
        var believed = sinceInput() <= InputWindow;
        if (believed) idleTicks = 0;
        else if (idleTicks++ % IdleEvery != 0) return [];
        foreach (var (area, ids) in Strips())
        {
            var now = grab(area);
            if (now is null) continue;
            var before = lastGrabs.GetValueOrDefault(area);
            lastGrabs[area] = now;
            if (!believed || before is null || before.Width != now.Width || before.Height != now.Height) continue;
            if (ScrollFollow.Find(before.Pixels, now.Pixels, now.Width, now.Height, now.Stride) is not { } shift) continue;
            var across = shift.Dx != 0;
            var start = (across ? area.Left : area.Top) + shift.AreaStart;
            var end = (across ? area.Left : area.Top) + shift.AreaEnd;
            for (var i = 0; i < marks.Count; i++)
            {
                var mark = marks[i];
                if (!ids.Contains(mark.Id)) continue;
                var centre = across ? mark.Box.Center.X : mark.Box.Center.Y;
                if (centre < start || centre >= end) continue;
                moves.Add(new MarkMove(mark.Id, shift.Dx, shift.Dy, start, end));
                marks[i] = mark with { Box = mark.Box with { Left = mark.Box.Left + shift.Dx, Top = mark.Box.Top + shift.Dy } };
            }
        }
        return moves;
    }

    /// <summary>The strips to grab: one per mark, centred on it, the screen's height; marks whose strips overlap by more than
    /// half share the first one's.</summary>
    internal List<(PixelRect Area, HashSet<string> Ids)> Strips()
    {
        var strips = new List<(PixelRect Area, HashSet<string> Ids)>();
        foreach (var mark in marks)
        {
            var centre = mark.Box.Center.X;
            var shared = strips.FindIndex(strip => Math.Abs(strip.Area.Left + strip.Area.Width / 2 - centre) < HalfStrip);
            if (shared >= 0) { strips[shared].Ids.Add(mark.Id); continue; }
            var left = Math.Clamp(centre - HalfStrip, screen.Left, Math.Max(screen.Left, screen.Right - 2 * HalfStrip));
            var area = new PixelRect(left, screen.Top, Math.Min(2 * HalfStrip, screen.Width), screen.Height);
            strips.Add((area, [mark.Id]));
        }
        return strips;
    }
}
