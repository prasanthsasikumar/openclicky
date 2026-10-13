// Ports Dictation/AppRegion.swift: India or the rest of the world (Settings → General → Region).
// Much of OpenClicky is built for Indian users: Sarvam, ten Indian dictation languages, seven
// Indian Clicky languages, the native-or-roman script. Outside India those are clutter, so the
// region decides what the settings offer. It never changes what is set.
//
// Until someone picks, the region follows the PC: India when its region (RegionInfo, from the
// culture) is India or its time zone is India's. The DictationLanguage extensions of the Swift
// file live with DictationLanguage in OpenClicky.Core.Dictation.

using System.Globalization;

namespace OpenClicky.Core.Configuration;

public enum AppRegion
{
    India,
    World,
}

public static class AppRegions
{
    public static string Id(this AppRegion region) => region.RawValue();

    public static string Title(this AppRegion region) => region switch
    {
        AppRegion.India => "India",
        _ => "Rest of the world",
    };

    /// <summary>Whether the settings offer what only Indian users need.</summary>
    public static bool OffersIndianOptions(this AppRegion region) => region == AppRegion.India;

    /// <summary>The Clicky languages from ReplyLanguage.Choices that only India needs.</summary>
    public static IReadOnlySet<string> IndianReplyLanguageCodes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "hi", "ta", "te", "bn", "mr", "kn", "ml" };

    /// <summary>What this PC looks like it is set up for. Time zones may be IANA or Windows ids.</summary>
    public static AppRegion Detected(string? regionCode, string timeZoneIdentifier)
    {
        if (regionCode == "IN") return AppRegion.India;
        if (timeZoneIdentifier is "Asia/Kolkata" or "Asia/Calcutta" or "India Standard Time") return AppRegion.India;
        return AppRegion.World;
    }

    /// <summary>`Detected` with this PC's region and time zone.</summary>
    public static AppRegion Detected()
    {
        string? regionCode = null;
        try { regionCode = RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch (ArgumentException) { }
        return Detected(regionCode, TimeZoneInfo.Local.Id);
    }
}
