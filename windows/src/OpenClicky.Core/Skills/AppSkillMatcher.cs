// Port of macos/OpenClicky/OpenClicky/AppSkillMatcher.swift
//
// Picks the app-teaching skill for whatever is in front of the user. Pure function so it is
// testable; the Windows frontmost-app observer (GetForegroundWindow → process, UI Automation for
// the browser URL) produces the context.

namespace OpenClicky.Core.Skills;

public sealed record FrontAppContext
{
    /// <summary>Mac bundle identifier. On Windows usually null (kept so skills and tests written
    /// against bundle ids still work).</summary>
    public string? BundleIdentifier { get; init; }

    /// <summary>Windows process image name of the frontmost app (`chrome.exe`, `Code.exe`).</summary>
    public string? ProcessName { get; init; }

    public string? AppName { get; init; }

    /// <summary>The front browser tab's URL when it could be read (UI Automation), else null.</summary>
    public Uri? Url { get; init; }

    public string? WindowTitle { get; init; }

    /// <summary>True for the known browsers; only then is the window title searched for a site.</summary>
    public bool IsBrowser { get; init; }
}

public static class AppSkillMatcher
{
    /// <summary>
    /// Order: a site skill matching the URL host (exact or `.suffix`), then (browsers only) a site
    /// skill named in the window title, then the app skill for the bundle identifier or process
    /// name. Among site matches the most specific site wins (`mail.google.com` beats `google.com`);
    /// otherwise the first listed skill. The title fallback is limited to browsers so a terminal
    /// titled "ssh — github.com" keeps the terminal skill.
    /// </summary>
    public static SkillFile? Match(FrontAppContext context, IReadOnlyList<SkillFile> skills)
    {
        var host = context.Url?.Host;
        if (!string.IsNullOrEmpty(host))
        {
            var lowered = host.ToLowerInvariant();
            var bySite = BestSiteMatch(skills, site => HostMatches(lowered, site));
            if (bySite is not null) return bySite;
        }
        if (context.IsBrowser && !string.IsNullOrEmpty(context.WindowTitle))
        {
            var title = context.WindowTitle.ToLowerInvariant();
            var byTitle = BestSiteMatch(skills, site => title.Contains(site.ToLowerInvariant(), StringComparison.Ordinal));
            if (byTitle is not null) return byTitle;
        }
        if (context.BundleIdentifier is not null || context.ProcessName is not null)
        {
            return skills.FirstOrDefault(skill => skill.Apps.Any(app => AppMatches(app, context)));
        }
        return null;
    }

    /// <summary>An `apps:` entry names either a Mac bundle id or a Windows exe name; both compare
    /// case-insensitively.</summary>
    private static bool AppMatches(string app, FrontAppContext context) =>
        (context.BundleIdentifier is not null && string.Equals(app, context.BundleIdentifier, StringComparison.OrdinalIgnoreCase))
        || (context.ProcessName is not null && string.Equals(app, context.ProcessName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The skill whose longest matching site is the longest overall; ties keep list order.</summary>
    private static SkillFile? BestSiteMatch(IReadOnlyList<SkillFile> skills, Func<string, bool> matches)
    {
        SkillFile? best = null;
        var bestLength = -1;
        foreach (var skill in skills)
        {
            var matching = skill.Sites.Where(matches).ToList();
            if (matching.Count == 0) continue;
            var longest = matching.Max(site => site.Length);
            if (best is null || longest > bestLength)
            {
                best = skill;
                bestLength = longest;
            }
        }
        return best;
    }

    public static bool HostMatches(string host, string site)
    {
        var lowered = site.ToLowerInvariant();
        return host == lowered || host.EndsWith("." + lowered, StringComparison.Ordinal);
    }
}
