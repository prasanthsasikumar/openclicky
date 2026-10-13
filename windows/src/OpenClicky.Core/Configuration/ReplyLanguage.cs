// Ports ReplyLanguage.swift: the one language OpenClicky speaks, from `language` in
// %USERPROFILE%\.openclicky\shell.json (Settings → Voice → Language), English until another is
// chosen.
//
// The Realtime prompt used to say "Speak English unless the user speaks another language", and
// any such freedom gets used: in the sibling app Saathi, clean English audio was answered in
// Italian, Japanese and Portuguese once the model had followed one misheard turn. So there is one
// language, stated in every prompt, and the transcription is pinned to it too.

using System.Globalization;

namespace OpenClicky.Core.Configuration;

public static class ReplyLanguage
{
    public sealed record Choice(string Code, string Name)
    {
        public string Id => Code;
    }

    /// <summary>What the Settings picker offers. Short on purpose: it is a picker in a small panel.</summary>
    public static IReadOnlyList<Choice> Choices { get; } =
    [
        new("en", "English"),
        new("hi", "हिन्दी"),
        new("ta", "தமிழ்"),
        new("te", "తెలుగు"),
        new("bn", "বাংলা"),
        new("mr", "मराठी"),
        new("kn", "ಕನ್ನಡ"),
        new("ml", "മലയാളം"),
        new("es", "Español"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("zh", "中文"),
    ];

    /// <summary>
    /// The configured language as an ISO-639-1 code, which is what the transcription models take:
    /// "en-US" in shell.json is sent as "en".
    /// </summary>
    public static string CurrentCode => NormalizedCode(OpenClickyConfiguration.Settings.Language);

    public static string NormalizedCode(string? configured)
    {
        var trimmed = configured?.Trim() ?? "";
        var first = trimmed.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? "en" : first.ToLowerInvariant();
    }

    /// <summary>The language's name in English, for the prompt: "English", "Hindi".</summary>
    public static string EnglishName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            var name = culture.IsNeutralCulture || culture.Parent == CultureInfo.InvariantCulture
                ? culture.EnglishName
                : culture.Parent.EnglishName;
            // An unknown code comes back as a made-up culture ("Unknown language (xx)" or the code).
            if (string.IsNullOrEmpty(name) || name.StartsWith("Unknown", StringComparison.Ordinal)) return code;
            return name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Added to every system prompt, Realtime and Claude alike.</summary>
    public static string Instruction(string code)
    {
        var name = EnglishName(code);
        return $"Always speak and write in {name}. Every reply is in {name}: whatever language the user "
            + "seems to use, whatever language the transcript shows, and whatever language any earlier "
            + "reply in this conversation was in. Only the user changes this, in OpenClicky's settings. "
            + "If a turn is silent, only noise, or too short to make out, do not invent what was said: "
            + $"say in {name} that you did not catch that and ask them to say it again.";
    }

    /// <summary>
    /// A culture name for the speech recognisers: the configured language in this PC's region where
    /// that pairing exists ("en" on an Indian PC is en-IN), else the language on its own.
    /// </summary>
    public static string RecognitionLocale
    {
        get
        {
            var code = CurrentCode;
            string? region = null;
            try { region = RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch (ArgumentException) { }
            return string.IsNullOrEmpty(region) ? code : $"{code}-{region}";
        }
    }

    /// <summary>The Clicky languages the region offers, always keeping `currentCode` (AppRegion.swift).</summary>
    public static IReadOnlyList<Choice> ChoicesFor(AppRegion region, string currentCode) =>
        Choices.Where(choice =>
            region.OffersIndianOptions()
            || !AppRegions.IndianReplyLanguageCodes.Contains(choice.Code)
            || choice.Code == currentCode).ToList();
}
