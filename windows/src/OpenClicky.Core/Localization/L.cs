// The app's words in the person's language. Every on-screen or spoken string a child meets goes through L.T("…") (or
// L.F("… {0} …", value) when it carries a value), keyed by its English, which is also the fallback: a string with no
// translation shows in English. Translations are JSON files embedded here (Localization/ur.json, English → Urdu), so
// Core stays free of any UI framework and the Mac can read the same files. LocalizationTests checks that every L.T/L.F
// string in the sources has a translation, and that the placeholders match.
//
// The language is set once at launch from Settings (AppLanguage); the windows read it as they are built, so a change
// shows the next time OpenClicky starts.

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace OpenClicky.Core.Localization;

public static class L
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> tables = new();

    /// <summary>The screen language's code: "en" (the default) or "ur".</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Urdu (and later Arabic) reads right to left: the app's windows lay out from the right.</summary>
    public static bool IsRightToLeft => IsRightToLeftLanguage(Language);

    public static bool IsRightToLeftLanguage(string code) => code is "ur" or "ar";

    /// <summary>The languages with a translation file, after English.</summary>
    public static IReadOnlyList<string> Translated { get; } = ["ur"];

    public static void SetLanguage(string? code) => Language = code is { } wanted && Translated.Contains(wanted) ? wanted : "en";

    /// <summary><paramref name="english"/> in the screen language; itself when there is no translation.</summary>
    public static string T(string english) => In(Language, english);

    /// <summary>A fixed sentence the app says (a notice, a caption): <see cref="T"/>, and failing that the same words in
    /// lower case, as the notices are listed (RecordedVoice.Notices). Words with no translation stay as they are.</summary>
    public static string Sentence(string english) => SentenceIn(Language, english);

    /// <summary><see cref="Sentence"/> in <paramref name="code"/>.</summary>
    public static string SentenceIn(string code, string english)
    {
        if (code == "en") return english;
        var table = Table(code);
        if (table.TryGetValue(english, out var exact) && exact.Length > 0) return exact;
        return table.TryGetValue(english.Trim().ToLowerInvariant(), out var lower) && lower.Length > 0 ? lower : english;
    }

    /// <summary>A string with values in it ("hold {0} and say something"), in the screen language, filled in.</summary>
    public static string F(string englishFormat, params object?[] values) =>
        string.Format(CultureInfo.InvariantCulture, In(Language, englishFormat), values);

    /// <summary><paramref name="english"/> in <paramref name="code"/> (tests, and the language picker's own names).</summary>
    public static string In(string code, string english) =>
        code == "en" || !Table(code).TryGetValue(english, out var translated) || translated.Length == 0 ? english : translated;

    /// <summary>A language's whole table, English → translation (empty for English or an unknown code).</summary>
    public static IReadOnlyDictionary<string, string> Table(string code) => tables.GetOrAdd(code, Load);

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        if (code == "en") return new Dictionary<string, string>();
        var assembly = typeof(L).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(resource => resource.EndsWith($".Localization.{code}.json", StringComparison.Ordinal));
        if (name is null) return new Dictionary<string, string>();
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}

/// <summary>Settings → Clicky → Talking → Screen language: the same as Clicky's spoken language (the default), or one
/// picked. Read once at launch.</summary>
public static class AppLanguage
{
    public const string PreferenceKey = "app.language";
    public const string SameAsSpoken = "same";

    /// <summary>The choices with their names, each written in its own language.</summary>
    public static IReadOnlyList<(string Value, string Title)> Choices { get; } =
        [(SameAsSpoken, "Same as Clicky's language"), ("en", "English"), ("ur", "اردو")];

    /// <summary>The screen language for a stored choice and Clicky's spoken language (ReplyLanguage's code).</summary>
    public static string Resolve(string? stored, string spokenCode)
    {
        var choice = stored?.Trim() is { Length: > 0 } picked ? picked : SameAsSpoken;
        var wanted = choice == SameAsSpoken ? spokenCode : choice;
        return L.Translated.Contains(wanted) ? wanted : "en";
    }
}

/// <summary>Which way a piece of text reads: right to left when its first letter is Arabic-script (Urdu, Arabic, Persian)
/// or Hebrew. Set per text, not per window: the pointer and the island are laid out by position, which a mirrored window
/// would turn round.</summary>
public static class TextDirection
{
    public static bool IsRightToLeft(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var character in text)
        {
            if (char.IsLetter(character)) return character is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';
        }
        return false;
    }
}
