// The assistant's name (2026-11-01): people can call it what they like ("Dyna", "Professor Pip") in Settings →
// Clicky. The models are told who they are by it: every system prompt that names the assistant goes through
// Introduce, which swaps "you're openclicky" for the chosen name and adds one line asking the model to answer to it.
// The app itself (its files, folders, the backend) stays OpenClicky.

using System.Text.RegularExpressions;

namespace OpenClicky.Core.Configuration;

public static partial class AssistantName
{
    public const string Default = "OpenClicky";
    public const int MaximumLength = 30;

    /// <summary>shell.json's `assistantName`, cleaned (letters, digits, spaces, '-' and '.', at most 30), else "OpenClicky".</summary>
    public static string Current => Cleaned(OpenClickyConfiguration.Settings.AssistantName) ?? Default;

    /// <summary>A name fit to be said and shown; null when nothing usable is left.</summary>
    public static string? Cleaned(string? raw)
    {
        if (raw is null) return null;
        var kept = string.Join(' ', Unwanted().Replace(raw, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (kept.Length > MaximumLength) kept = kept[..MaximumLength].TrimEnd();
        return kept.Length == 0 ? null : kept;
    }

    /// <summary>
    /// <paramref name="prompt"/> with the assistant called <paramref name="name"/> (default: <see cref="Current"/>): "you're
    /// openclicky" / "You are OpenClicky" become the name, and a line asks the model to answer to it. The default name
    /// leaves the prompt as it is.
    /// </summary>
    public static string Introduce(string prompt, string? name = null)
    {
        name ??= Current;
        if (string.Equals(name, Default, StringComparison.OrdinalIgnoreCase)) return prompt;
        var renamed = PersonaLowercase().Replace(prompt, $"you're {name}");
        renamed = PersonaCapitalized().Replace(renamed, $"You are {name}");
        return renamed + $"\n\nyour name is {name}. answer to it, and say it when they ask who you are (you run on OpenClicky, which they installed).";
    }

    [GeneratedRegex(@"[^\p{L}\p{N} .\-']+")]
    private static partial Regex Unwanted();

    [GeneratedRegex(@"you're openclicky", RegexOptions.IgnoreCase)]
    private static partial Regex PersonaLowercase();

    [GeneratedRegex(@"You are OpenClicky")]
    private static partial Regex PersonaCapitalized();
}
