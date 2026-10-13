// Port of macos/OpenClicky/OpenClicky/SpokenChunker.swift
//
// Cuts Claude's streamed answer into pieces the voice can start on before the answer is finished.
// The first sentence goes out the moment it ends (once it is at least `firstChunkMinimum`
// characters, so "Dr." or "1." alone is never a piece), so the voice starts within a second or two
// of the first words; later sentences are grouped (at least `laterChunkMinimum` characters) so a
// long answer costs a handful of speech requests, not one per sentence.
//
// A sentence ends at ". " "! " "? ", at a new line, and at the danda (। ॥) and the CJK stops
// (。！？) with or without a space after them. No piece is longer than `maximumPieceLength` UTF-16
// units: the backend's /tts speaks at most 400 (a JavaScript length) and silently drops the rest,
// so a long sentence is cut at a space or comma instead.
//
// The [POINT:…] tag is never spoken. The ask prompt puts it first, so the chunker holds the text
// back until the tag is complete and records it in `PointTag`; a tag at the end is found when the
// stream finishes. The guide's [NEXT] marker (`GuidedStepMarker`) is never spoken either.
//
// Index notes for the C# port: Swift walks `Character`s (grapheme clusters); this port keeps
// UTF-16 offsets (which is what the length limit is measured in anyway) and counts/cuts by text
// elements (StringInfo) wherever Swift counted or cut by Character, so a piece never splits a
// grapheme.

using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClicky.Core.Answers;

public sealed class SpokenChunker
{
    /// <summary>The [POINT:…] tag, once it has been seen in full (leading, or trailing at Finish()).</summary>
    public string? PointTag { get; private set; }

    private readonly int laterChunkMinimum;
    private readonly int firstChunkMinimum;
    private readonly int maximumPieceLength;
    /// <summary>How many UTF-16 units of the accumulated answer have already been handed out (or skipped as the tag).</summary>
    private int consumedLength;
    private bool hasEmittedFirstChunk;
    private bool leadingTagResolved;
    private string latestAccumulatedText = "";

    public SpokenChunker(int laterChunkMinimum = 120, int firstChunkMinimum = 12, int maximumPieceLength = 350)
    {
        this.laterChunkMinimum = laterChunkMinimum;
        this.firstChunkMinimum = firstChunkMinimum;
        this.maximumPieceLength = maximumPieceLength;
    }

    /// <summary>Takes the whole answer so far and returns the pieces that are now ready to speak, in order.</summary>
    public IReadOnlyList<string> Feed(string accumulatedText)
    {
        latestAccumulatedText = accumulatedText;
        if (!ResolveLeadingTag(accumulatedText)) return Array.Empty<string>();

        var readyChunks = new List<string>();
        while (NextCompleteChunk(accumulatedText) is string chunk)
        {
            // An empty chunk is only whitespace that was passed over (or a lone [NEXT] marker).
            var spokenChunk = GuidedStepMarker.RemovingMarkers(chunk);
            if (spokenChunk.Length > 0) readyChunks.Add(spokenChunk);
        }
        return readyChunks;
    }

    /// <summary>The stream ended: whatever is left (minus a trailing tag) is the last piece, or the last few.</summary>
    public IReadOnlyList<string> Finish()
    {
        var accumulatedText = latestAccumulatedText;
        if (!leadingTagResolved)
        {
            // The answer ended while still looking like an unfinished tag: speak it as text.
            leadingTagResolved = true;
        }
        // Without the marker first, so "… [POINT:10,20:x] [NEXT]" still finds the trailing tag.
        var remainingText = GuidedStepMarker.RemovingMarkers(accumulatedText.Substring(Math.Min(consumedLength, accumulatedText.Length)));
        consumedLength = accumulatedText.Length;

        // A trailing tag is never spoken, even after a leading one; the first tag seen is the one that points.
        var trailingTagMatch = TrailingTagPattern.Match(remainingText);
        if (trailingTagMatch.Success)
        {
            PointTag ??= trailingTagMatch.Value.Trim();
            remainingText = remainingText.Remove(trailingTagMatch.Index, trailingTagMatch.Length);
        }
        var lastChunks = new List<string>();
        var unspokenStart = 0;
        while (remainingText.Length - unspokenStart > maximumPieceLength)
        {
            var cut = LastSentenceBoundaryWithinMaximum(remainingText, unspokenStart) ?? HardSplitIndex(remainingText, unspokenStart);
            lastChunks.Add(remainingText[unspokenStart..cut].Trim());
            unspokenStart = cut;
        }
        lastChunks.Add(remainingText[unspokenStart..].Trim());
        return lastChunks.Select(GuidedStepMarker.RemovingMarkers).Where(chunk => chunk.Length > 0).ToList();
    }

    // MARK: - Private

    private static readonly Regex CompleteLeadingTagPattern = new(@"^\s*\[POINT:[^\]]*\]", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingTagPattern = new(@"\[POINT:[^\]]*\]\s*$", RegexOptions.CultureInvariant);

    /// <summary>True once speech may start: either the answer does not open with a tag, or the tag is complete.</summary>
    private bool ResolveLeadingTag(string accumulatedText)
    {
        if (leadingTagResolved) return true;
        var trimmedStart = accumulatedText.TrimStart();
        if (trimmedStart.Length == 0) return false;
        // "[" or "[POI…" could still become a tag: wait for more text before deciding.
        var firstCharacters = trimmedStart[..Math.Min(7, trimmedStart.Length)];
        if ("[POINT:".StartsWith(firstCharacters, StringComparison.Ordinal) || trimmedStart.StartsWith("[POINT:", StringComparison.Ordinal))
        {
            var tagMatch = CompleteLeadingTagPattern.Match(accumulatedText);
            if (!tagMatch.Success) return false;
            PointTag = tagMatch.Value.Trim();
            consumedLength = tagMatch.Index + tagMatch.Length;
        }
        leadingTagResolved = true;
        return true;
    }

    /// <summary>
    /// The next run of whole sentences long enough to send, or null when the text so far has none.
    /// Text already longer than the voice speaks is cut without waiting for a sentence end; an empty
    /// string means only whitespace was passed over.
    /// </summary>
    private string? NextCompleteChunk(string accumulatedText)
    {
        var unconsumedStart = consumedLength;
        var minimumLength = hasEmittedFirstChunk ? laterChunkMinimum : firstChunkMinimum;

        var searchStart = unconsumedStart;
        while (SentenceBoundary(accumulatedText, unconsumedStart, searchStart) is int boundary)
        {
            // Grouping further would make a piece longer than the voice speaks: cut below instead.
            if (boundary - unconsumedStart > maximumPieceLength) break;
            if (TextElementCount(accumulatedText[unconsumedStart..boundary].Trim()) >= minimumLength)
            {
                return Consume(accumulatedText, boundary);
            }
            searchStart = boundary;
        }
        if (accumulatedText.Length - unconsumedStart <= maximumPieceLength) return null;
        var cut = LastSentenceBoundaryWithinMaximum(accumulatedText, unconsumedStart) ?? HardSplitIndex(accumulatedText, unconsumedStart);
        return Consume(accumulatedText, cut);
    }

    private string Consume(string accumulatedText, int end)
    {
        var chunk = accumulatedText[consumedLength..end].Trim();
        consumedLength = end;
        if (chunk.Length > 0) hasEmittedFirstChunk = true;
        return chunk;
    }

    /// <summary>The last sentence end whose text before it still fits in one piece.</summary>
    private int? LastSentenceBoundaryWithinMaximum(string text, int rangeStart)
    {
        int? lastFittingBoundary = null;
        var searchStart = rangeStart;
        while (SentenceBoundary(text, rangeStart, searchStart) is int boundary && boundary - rangeStart <= maximumPieceLength)
        {
            if (!string.IsNullOrWhiteSpace(text[rangeStart..boundary])) lastFittingBoundary = boundary;
            searchStart = boundary;
        }
        return lastFittingBoundary;
    }

    /// <summary>
    /// Where to cut a run with no sentence end that fits: after the last space or comma within the
    /// maximum (when that is at least halfway, so the piece is worth a request), else at the maximum
    /// itself (CJK without punctuation has no spaces to cut at). Walks whole text elements.
    /// </summary>
    private int HardSplitIndex(string text, int rangeStart)
    {
        var lengthSoFar = 0;
        var index = rangeStart;
        int? lastBreakAfterSpaceOrComma = null;
        while (index < text.Length)
        {
            var elementLength = StringInfo.GetNextTextElementLength(text, index);
            if (lengthSoFar + elementLength > maximumPieceLength) break;
            var firstCharacter = text[index];
            lengthSoFar += elementLength;
            index += elementLength;
            if ((char.IsWhiteSpace(firstCharacter) || ClauseBreaks.Contains(firstCharacter)) && lengthSoFar >= maximumPieceLength / 2)
            {
                lastBreakAfterSpaceOrComma = index;
            }
        }
        // Always move forward, even if a single element were longer than the maximum.
        if (index == rangeStart) return rangeStart + Math.Max(1, StringInfo.GetNextTextElementLength(text, rangeStart));
        return lastBreakAfterSpaceOrComma ?? index;
    }

    private static int TextElementCount(string text) => text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>Ends a sentence only with whitespace after it ("3.5" and "e.g" are not ends).</summary>
    private static readonly HashSet<char> LatinSentenceEnds = new() { '.', '!', '?' };
    /// <summary>End a sentence on their own: danda, double danda, ideographic full stop, full-width ! and ?.</summary>
    private static readonly HashSet<char> StandaloneSentenceEnds = new() { '।', '॥', '。', '！', '？' };
    private static readonly HashSet<char> ClauseBreaks = new() { ',', ';', ':', '，', '、', '；', '：' };
    /// <summary>Words that end with a "." without ending the sentence ("Dr. Smith").</summary>
    private static readonly HashSet<string> AbbreviationsBeforePeriod = new(StringComparer.Ordinal)
    {
        "dr", "mr", "mrs", "ms", "st", "prof", "jr", "sr", "vs", "e.g", "i.e", "etc",
    };

    /// <summary>
    /// The index just after a sentence end, searching `text` from `startIndex`; `rangeStart` is where
    /// the unconsumed text begins. A "." inside a number ("3.5"), at the very end of the text so far
    /// (more may follow), after an abbreviation ("Dr.") or after a list number at the start of a line
    /// ("1. Open the menu") is not a boundary.
    /// </summary>
    private static int? SentenceBoundary(string text, int rangeStart, int startIndex)
    {
        var index = startIndex;
        while (index < text.Length)
        {
            var character = text[index];
            var nextIndex = index + 1;
            if (StandaloneSentenceEnds.Contains(character) || character == '\n') return nextIndex;
            if (LatinSentenceEnds.Contains(character) && nextIndex < text.Length && char.IsWhiteSpace(text[nextIndex]))
            {
                if (character != '.' || !IsPeriodInsideASentence(index, text, rangeStart)) return nextIndex;
            }
            index = nextIndex;
        }
        return null;
    }

    /// <summary>True when the "." at `periodIndex` closes an abbreviation or a list number, not a sentence.</summary>
    private static bool IsPeriodInsideASentence(int periodIndex, string text, int rangeStart)
    {
        var wordStart = periodIndex;
        while (wordStart > rangeStart)
        {
            var previousCharacter = text[wordStart - 1];
            // Marks count as part of the word: Swift walks whole grapheme clusters, whose combining
            // vowel signs (Devanagari, Tamil…) are not letters on their own.
            if (!(char.IsLetter(previousCharacter) || char.IsNumber(previousCharacter) || previousCharacter == '.'
                  || char.IsSurrogate(previousCharacter) || IsMark(previousCharacter))) break;
            wordStart--;
        }
        var word = text[wordStart..periodIndex].ToLowerInvariant();
        if (AbbreviationsBeforePeriod.Contains(word)) return true;
        // "1." is a list number only at the start of a line; "in 1990." ends a sentence.
        if (word.Length == 0 || !word.All(char.IsNumber)) return false;
        for (var index = wordStart - 1; index >= rangeStart; index--)
        {
            if (text[index] == '\n') break;
            if (!char.IsWhiteSpace(text[index])) return false;
        }
        return true;
    }

    private static bool IsMark(char character)
    {
        var category = char.GetUnicodeCategory(character);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }
}
