// Port of macos/OpenClicky/OpenClicky/Dictation/SpokenNumbers.swift
//
// Spoken English numbers written as digits, without a model: "three hundred and twenty five" →
// "325", "fifty percent" → "50%". Some engines spell numbers out. The usual style rule decides:
// one to nine stay words ("one of them", "two books"), ten and up and anything counted in hundreds
// or more become digits. A run of numbers that can't be one number ("two three four", a phone
// number read out) is left as it was said.

namespace OpenClicky.Core.Dictation;

public static class SpokenNumbers
{
    private static readonly Dictionary<string, long> Units = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
    };
    private static readonly Dictionary<string, long> Teens = new()
    {
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };
    private static readonly Dictionary<string, long> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };
    private static readonly Dictionary<string, long> Scales = new()
    {
        ["thousand"] = 1_000, ["million"] = 1_000_000, ["billion"] = 1_000_000_000,
    };

    /// <summary>What a number word is, for which word may follow which.</summary>
    private enum Kind { Unit, Teen, Tens, Hundred, Scale }

    private static Kind? KindOf(string word)
    {
        if (Units.ContainsKey(word)) return Kind.Unit;
        if (Teens.ContainsKey(word)) return Kind.Teen;
        if (Tens.ContainsKey(word)) return Kind.Tens;
        if (word == "hundred") return Kind.Hundred;
        if (Scales.ContainsKey(word)) return Kind.Scale;
        return null;
    }

    /// <summary>Whether `next` can carry on the number `previous` is part of.</summary>
    private static bool CanFollow(Kind next, Kind previous) => previous switch
    {
        Kind.Unit or Kind.Teen => next is Kind.Hundred or Kind.Scale,
        Kind.Tens => next is Kind.Unit or Kind.Scale,
        Kind.Hundred => next is Kind.Unit or Kind.Teen or Kind.Tens or Kind.Scale,
        _ => next is Kind.Unit or Kind.Teen or Kind.Tens,
    };

    /// <summary>`text` with its spoken numbers as digits.</summary>
    public static string Digitize(string text)
    {
        var tokens = text.Split(' ');
        var output = new List<string>();
        var index = 0;
        while (index < tokens.Length)
        {
            if (NumberRun(tokens, index) is var (written, end))
            {
                output.Add(written);
                index = end;
            }
            else
            {
                output.Add(tokens[index]);
                index += 1;
            }
        }
        return string.Join(" ", output);
    }

    /// <summary>The number starting at `start`, if one does and it should be written as digits.</summary>
    private static (string Written, int End)? NumberRun(string[] tokens, int start)
    {
        var words = new List<string>();
        Kind? previousKind = null;
        var index = start;
        var trailing = "";
        var leading = "";
        while (index < tokens.Length)
        {
            var (lead, core, trail) = Split(tokens[index]);
            var lowercasedCore = core.ToLowerInvariant();
            var pieces = lowercasedCore.Split('-', StringSplitOptions.RemoveEmptyEntries); // "twenty-five"
            var kinds = pieces.Select(KindOf).ToList();
            // "and" joins only inside a number: "three hundred and twenty".
            if (lowercasedCore == "and" && previousKind is Kind.Hundred or Kind.Scale && lead.Length == 0 && trail.Length == 0
                && index + 1 < tokens.Length && KindOf(Split(tokens[index + 1]).Core.ToLowerInvariant()) is Kind nextKind
                && nextKind != Kind.Hundred && nextKind != Kind.Scale)
            {
                index += 1;
                continue;
            }
            if (pieces.Length == 0 || kinds.Any(kind => kind is null) || !(lead.Length == 0 || words.Count == 0)) break;
            var fits = true;
            var kindAfter = previousKind;
            foreach (var pieceKind in kinds.Select(kind => kind!.Value))
            {
                if (kindAfter is Kind afterKind && !CanFollow(pieceKind, afterKind)) { fits = false; break; }
                kindAfter = pieceKind;
            }
            if (!fits) break;
            if (words.Count == 0) leading = lead;
            words.AddRange(pieces);
            previousKind = kindAfter;
            index += 1;
            // Punctuation ends the number: "twenty, thirty" is two.
            if (trail.Length > 0) { trailing = trail; break; }
        }
        if (words.Count == 0 || Value(words) is not long value) return null;
        // A lone "hundred" or "thousand" ("a hundred people") keeps its words, and so does one to nine.
        var startsWithScale = KindOf(words[0]) is Kind.Hundred or Kind.Scale;
        var isCounted = words.Any(word => KindOf(word) is Kind.Hundred or Kind.Scale);
        if (startsWithScale || !(value >= 10 || isCounted)) return null;

        var written = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // "fifty percent" → "50%".
        if (trailing.Length == 0 && index < tokens.Length)
        {
            var (_, nextCore, nextTrail) = Split(tokens[index]);
            if (nextCore.ToLowerInvariant() == "percent")
            {
                written += "%";
                trailing = nextTrail;
                index += 1;
            }
        }
        return (leading + written + trailing, index);
    }

    private static long? Value(List<string> words)
    {
        long total = 0;
        long current = 0;
        foreach (var word in words)
        {
            if (Units.TryGetValue(word, out var number) || Teens.TryGetValue(word, out number) || Tens.TryGetValue(word, out number))
            {
                current += number;
            }
            else if (word == "hundred")
            {
                current = Math.Max(current, 1) * 100;
            }
            else if (Scales.TryGetValue(word, out var scale))
            {
                total += Math.Max(current, 1) * scale;
                current = 0;
            }
            else
            {
                return null;
            }
        }
        return total + current;
    }

    /// <summary>A token's leading punctuation, its letters, and its trailing punctuation.</summary>
    private static (string Lead, string Core, string Trail) Split(string token)
    {
        var start = 0;
        var end = token.Length;
        while (start < end && !char.IsLetter(token[start]) && !char.IsNumber(token[start])) start++;
        while (end > start && !char.IsLetter(token[end - 1]) && !char.IsNumber(token[end - 1])) end--;
        return (token[..start], token[start..end], token[end..]);
    }
}
