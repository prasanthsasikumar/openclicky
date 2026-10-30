//
//  ScreenTextLocator.cs
//  OpenClicky (Windows port of the ranking half of ScreenTextLocator.swift)
//
//  `point_at` snapping. The Realtime model's pixel coordinates are consistently 30–100 px off (its
//  vision grounding is coarse), so the buddy would land next to the element instead of on it. The
//  tool therefore also asks for the element's visible text; the screenshot the model saw is OCR'd
//  (Windows.Media.Ocr in the Windows project, upscaled 2× because UI captions are ~11 px tall at
//  1280 px), and the guess is snapped to the matching text nearest to it. Icon-only elements keep
//  the model's coordinates. The recogniser itself is OS code (OpenClicky.Windows/Platform); this
//  file only ranks what it found.
//

using System.Globalization;

namespace OpenClicky.Core.Pointing;

/// <summary>One line of text found on a capture, in screenshot pixels (origin top-left, y down).</summary>
/// <param name="Text">The line as read.</param>
/// <param name="Box">The line's bounding box in the capture's own pixels.</param>
/// <param name="RangeBox">Box of a character range (UTF-16 start, length) within <paramref name="Text"/>, when the
/// recogniser can supply one (Windows OCR gives per-word boxes). Without it the range is placed proportionally.</param>
public sealed record ScreenTextLine(string Text, ScreenRect Box, Func<int, int, ScreenRect?>? RangeBox = null)
{
    public ScreenRect BoxFor(int start, int length)
    {
        if (RangeBox?.Invoke(start, length) is { } precise) return precise;
        // The Swift counts Characters (grapheme clusters); UTF-16 units differ only for emoji and
        // combining marks, which UI captions rarely carry.
        double count = Math.Max(Text.Length, 1);
        var startFraction = start / count;
        var endFraction = (start + length) / count;
        return new ScreenRect(Box.Left + Box.Width * startFraction, Box.Top, Box.Width * (endFraction - startFraction), Box.Height);
    }
}

/// <param name="Text">The recognized line the match was found in (as read, so "HNew" or "Now" are possible).</param>
/// <param name="Distance">Distance from the model's guess, in screenshot pixels.</param>
public sealed record ScreenTextMatch(string Text, ScreenPoint Center, double Distance);

public static class ScreenTextLocator
{
    /// <summary>Words that describe a control rather than name it; never matched on their own.</summary>
    public static readonly IReadOnlySet<string> GenericWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "button", "btn", "menu", "icon", "link", "tab", "field", "box", "option", "item", "toggle", "checkbox",
        "dropdown", "bar", "panel", "window", "dialog", "input", "label", "text", "list", "row", "section", "control",
        "the", "and", "for", "with", "top", "left", "right", "bottom", "here", "this", "that",
    };

    /// <summary>The on-screen occurrence of <paramref name="hint"/> nearest to <paramref name="guess"/>: the whole hint first,
    /// then its distinctive words. Exact (case-insensitive) matches only: the hint is the model's idea of the caption,
    /// and a near miss is as likely a different element ("reditt" in the tray for a Reddit browser tab) as an OCR typo,
    /// so near misses are left to the Claude fallback. Null when the hint is empty/generic or nothing matching is within
    /// <paramref name="maxDistance"/> px.</summary>
    public static ScreenTextMatch? Locate(string hint, ScreenPoint guess, IReadOnlyList<ScreenTextLine> lines, double maxDistance, double ambiguityMargin)
    {
        var needle = Normalize(hint);
        if (needle.Length < 3 || GenericWords.Contains(needle)) return null;

        // A line that is the caption itself (a button reading "Accept All") before a sentence that only
        // mentions it ("By clicking "Accept All", you consent…"), which is what a cookie banner put
        // nearer the guess (2026-10-29).
        var exactMatches = ExactMatches(needle, lines);
        var wholeLineMatches = exactMatches.Where(exact => Normalize(exact.Text) == needle).ToList();
        if (Nearest(wholeLineMatches, guess, maxDistance, ambiguityMargin) is { } lineMatch) return lineMatch;
        if (Nearest(exactMatches, guess, maxDistance, ambiguityMargin) is { } match) return match;

        var words = needle.Split(' ')
            .Where(word => word.Length >= 3 && !GenericWords.Contains(word))
            .OrderByDescending(word => word.Length); // stable, like Swift's sort on these short lists
        foreach (var word in words)
        {
            if (word == needle) continue;
            if (Nearest(ExactMatches(word, lines), guess, maxDistance, ambiguityMargin) is { } wordMatch) return wordMatch;
        }
        return null;
    }

    // Matching

    private static string Normalize(string text)
    {
        var parts = text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Trim(PunctuationAndSymbols(part)))
            .Where(part => part.Length > 0);
        return string.Join(' ', parts);
    }

    /// <summary>The punctuation and symbol characters at the ends of <paramref name="part"/>, for Trim.</summary>
    private static char[] PunctuationAndSymbols(string part) =>
        part.Where(character => char.IsPunctuation(character) || char.IsSymbol(character)).Distinct().ToArray();

    private static readonly CompareInfo InvariantCompare = CultureInfo.InvariantCulture.CompareInfo;

    private static List<ScreenTextMatch> ExactMatches(string needle, IReadOnlyList<ScreenTextLine> lines)
    {
        var matches = new List<ScreenTextMatch>();
        foreach (var line in lines)
        {
            var searchStart = 0;
            while (searchStart <= line.Text.Length)
            {
                // Case- and diacritic-insensitive, as Swift's range(of:options:[.caseInsensitive, .diacriticInsensitive]).
                var found = InvariantCompare.IndexOf(line.Text.AsSpan(searchStart), needle.AsSpan(),
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out var matchLength);
                if (found < 0 || matchLength == 0) break;
                var start = searchStart + found;
                var box = line.BoxFor(start, matchLength);
                matches.Add(new ScreenTextMatch(line.Text, box.Center, 0));
                var end = start + matchLength;
                if (end >= line.Text.Length) break;
                searchStart = end;
            }
        }
        return matches;
    }

    /// <summary>The nearest match, unless two of them are close enough together that the model's own error decides
    /// between them. The guess is 30–100 px off, so when the runner-up is within <paramref name="ambiguityMargin"/> px of
    /// the winner ("Edit" repeated down a list of rows), distance is noise rather than evidence: the caller is told
    /// nothing was resolved and falls through to the grounding pass, which is given the user's actual request and can
    /// pick the right one.</summary>
    private static ScreenTextMatch? Nearest(List<ScreenTextMatch> matches, ScreenPoint guess, double maxDistance, double ambiguityMargin)
    {
        var matchesWithinRange = matches
            .Select(match => match with { Distance = match.Center.DistanceTo(guess) })
            .Where(match => match.Distance <= maxDistance)
            .OrderBy(match => match.Distance)
            .ToList();
        if (matchesWithinRange.Count == 0) return null;
        var nearestMatch = matchesWithinRange[0];
        if (matchesWithinRange.Count > 1 && matchesWithinRange[1].Distance - nearestMatch.Distance < ambiguityMargin) return null;
        return nearestMatch;
    }
}
