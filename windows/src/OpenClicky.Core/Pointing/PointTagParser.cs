//
//  PointTagParser.cs
//  OpenClicky (Windows port of CompanionManager.swift's PointingParseResult / parsePointingCoordinates)
//

using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClicky.Core.Pointing;

/// <summary>Result of parsing a [POINT:...] tag from Claude's response.</summary>
/// <param name="SpokenText">The response text with the [POINT:...] tag removed — this is what gets spoken.</param>
/// <param name="Coordinate">The parsed pixel coordinate, or null if Claude said "none" or no tag was found.</param>
/// <param name="ElementLabel">Short label describing the element (e.g. "run button"), or "none".</param>
/// <param name="ScreenNumber">Which screen the coordinate refers to (1-based), or null to default to the cursor screen.</param>
public sealed record PointingParseResult(string SpokenText, ScreenPoint? Coordinate, string? ElementLabel, int? ScreenNumber);

public static class PointTagParser
{
    private const string TagPattern = @"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]";

    /// <summary>Matches [POINT:none] or [POINT:123,456:label] or [POINT:123,456:label:screen2] at the end of the
    /// text (with trailing whitespace). <c>\z</c> stands for Swift's <c>$</c> without multiline.</summary>
    private static readonly Regex TrailingTagRegex = new(TagPattern + @"\s*\z", RegexOptions.CultureInvariant);
    /// <summary>The same, as the whole string.</summary>
    private static readonly Regex WholeTagRegex = new("^" + TagPattern + @"\z", RegexOptions.CultureInvariant);
    private static readonly Regex LeadingTagRegex = new(@"^\s*\[POINT:[^\]]*\]", RegexOptions.CultureInvariant);

    /// <summary>Parses a [POINT:x,y:label:screenN] or [POINT:none] tag from the start or the end of Claude's
    /// response (the ask prompt puts it first so the pointer can move as the voice starts; the onboarding demo puts
    /// it last). Returns the spoken text (tag removed) and the optional coordinate + label + screen number.</summary>
    public static PointingParseResult ParsePointingCoordinates(string responseText)
    {
        // No recursion: a leading tag is cut off, then the trailing tag is looked for once in what is left. Moving the
        // leading tag to the end and parsing again recursed forever (and crashed the app) when two tags stood with
        // nothing else between them.
        string? leadingTag = null;
        var textAfterLeadingTag = responseText;
        var leadingMatch = LeadingTagRegex.Match(responseText);
        if (leadingMatch.Success)
        {
            leadingTag = leadingMatch.Value.Trim();
            textAfterLeadingTag = responseText[(leadingMatch.Index + leadingMatch.Length)..];
        }

        string? trailingTag = null;
        var spokenText = textAfterLeadingTag;
        var trailingMatch = TrailingTagRegex.Match(textAfterLeadingTag);
        if (trailingMatch.Success)
        {
            trailingTag = trailingMatch.Value.Trim();
            spokenText = textAfterLeadingTag[..trailingMatch.Index];
        }
        spokenText = spokenText.Trim();

        // The first tag that reads as one decides where to point (the prompt puts it first).
        ReadTag? firstReadableTag = null;
        foreach (var tag in new[] { leadingTag, trailingTag })
        {
            if (tag is null) continue;
            firstReadableTag = ReadPointTag(tag);
            if (firstReadableTag is not null) break;
        }
        if (firstReadableTag is null)
        {
            // No tag found at all (a malformed leading tag is still never spoken).
            return new PointingParseResult(leadingTag is null ? responseText : spokenText, null, null, null);
        }
        return new PointingParseResult(spokenText, firstReadableTag.Coordinate, firstReadableTag.ElementLabel, firstReadableTag.ScreenNumber);
    }

    private sealed record ReadTag(ScreenPoint? Coordinate, string? ElementLabel, int? ScreenNumber);

    /// <summary>One tag on its own, read: null when it is not a well-formed tag; a null coordinate for [POINT:none].</summary>
    private static ReadTag? ReadPointTag(string pointTag)
    {
        var match = WholeTagRegex.Match(pointTag);
        if (!match.Success) return null;
        if (!match.Groups[1].Success || !match.Groups[2].Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return new ReadTag(null, "none", null);
        }
        string? elementLabel = match.Groups[3].Success ? match.Groups[3].Value.Trim(' ', '\t') : null;
        int? screenNumber = match.Groups[4].Success && int.TryParse(match.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var screen) ? screen : null;
        return new ReadTag(new ScreenPoint(x, y), elementLabel, screenNumber);
    }
}
