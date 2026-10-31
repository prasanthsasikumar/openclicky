// Ports the SettingsItem / SettingsSectionGroup / settingsHighlighted parts of
// Dictation/UI/SettingsCatalog.swift. On the Mac an item carries its SwiftUI view; here Core keeps
// what an item is (title, copy, page, section, search words, whether it applies now) and the
// Windows window builds the row for each item id (SettingsRowFactory), so a page and a search
// result still draw the very same row.

using System.Globalization;

namespace OpenClicky.Core.Settings;

public enum SettingsItemChrome
{
    /// <summary>A row inside the section's group.</summary>
    Card,
    /// <summary>A note under the section's group (the privacy summary, where keys go).</summary>
    Footer,
}

/// <summary>One setting: what it is called, what it says, where it lives.</summary>
public sealed record SettingsItem
{
    public required string Id { get; init; }
    /// <summary>Where the row is drawn. Set by <see cref="SettingsLayout"/>, which places every row; a builder leaves it.</summary>
    public DictationSettingsPage Page { get; init; } = DictationSettingsPage.General;
    /// <summary>
    /// The label above the group ("Microphone"); empty for a page's unlabelled first group. Set by
    /// <see cref="SettingsLayout"/>, except for rows whose builder names their own group (one per calendar account).
    /// </summary>
    public string Section { get; init; } = "";
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>Other words someone might search for ("startup" for open at sign-in).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public SettingsItemChrome Chrome { get; init; } = SettingsItemChrome.Card;
    /// <summary>False hides the item from its page and from search (the script row outside India).</summary>
    public Func<bool> IsRelevant { get; init; } = () => true;
    /// <summary>
    /// Set for an item only search shows: a way to one of your own pages (Your words, How it
    /// writes), which aren't drawn from the catalog. It names the page in the search results.
    /// </summary>
    public string? SearchOnlyGroup { get; init; }
    /// <summary>For a search-only item: the page its button opens.</summary>
    public DictationSection? OpensSection { get; init; }
    /// <summary>For a row that opens a page of its own (The orb from Dictation): where it goes.</summary>
    public DictationDestination? OpensDestination { get; init; }
    /// <summary>
    /// True for rows in a page's folded group at the bottom ("More"): rarely changed, hidden until
    /// the group is opened. Search ignores the fold.
    /// </summary>
    public bool IsAdvanced { get; init; }
    /// <summary>False for a row only search finds (incognito, whose switch lives in the sidebar's foot).</summary>
    public bool ShowsOnPage { get; init; } = true;

    /// <summary>The heading it is listed under in search results.</summary>
    public string SearchGroupTitle => SearchOnlyGroup ?? Page.SearchTitle();

    /// <summary>Every word of the query (split on spaces) is somewhere in the title, copy, section, page or keywords, ignoring case.</summary>
    public bool Matches(string query)
    {
        var searchableText = string.Join(" ", new[] { Title, Detail, Section, SearchGroupTitle }.Concat(Keywords));
        var compareInfo = CultureInfo.CurrentCulture.CompareInfo;
        return query
            .ToLower(CultureInfo.CurrentCulture)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => compareInfo.IndexOf(searchableText, word, CompareOptions.IgnoreCase) >= 0);
    }
}

/// <summary>One section of a page: its rows, and the notes under it.</summary>
public sealed record SettingsSectionGroup(string Id, string Title, IReadOnlyList<SettingsItem> Items, IReadOnlyList<SettingsItem> Footers)
{
    /// <summary>The page's folded group ("More"), drawn closed until opened.</summary>
    public bool IsAdvanced { get; init; }

    /// <summary>
    /// Consecutive items with the same section form one group; a footer item joins the group
    /// before it. <paramref name="foldsAdvanced"/> marks a page's folded group; search results never fold.
    /// </summary>
    public static IReadOnlyList<SettingsSectionGroup> Groups(IEnumerable<SettingsItem> items, Func<SettingsItem, string>? title = null, bool foldsAdvanced = false)
    {
        title ??= item => item.Section;
        var groups = new List<SettingsSectionGroup>();
        foreach (var item in items)
        {
            var groupTitle = title(item);
            if (groups.Count > 0 && groups[^1].Title == groupTitle)
            {
                var last = groups[^1];
                groups[^1] = item.Chrome == SettingsItemChrome.Footer
                    ? last with { Footers = [.. last.Footers, item] }
                    : last with { Items = [.. last.Items, item] };
            }
            else if (item.Chrome == SettingsItemChrome.Footer)
            {
                groups.Add(new SettingsSectionGroup($"{groups.Count}-{groupTitle}", groupTitle, [], [item]) { IsAdvanced = foldsAdvanced && item.IsAdvanced });
            }
            else
            {
                groups.Add(new SettingsSectionGroup($"{groups.Count}-{groupTitle}", groupTitle, [item], []) { IsAdvanced = foldsAdvanced && item.IsAdvanced });
            }
        }
        return groups;
    }

    /// <summary>
    /// Search results by page, the pages with a row whose title holds the query first ("dictionary"
    /// shows the Dictionary before the privacy rows that mention it); otherwise in sidebar order.
    /// </summary>
    public static IReadOnlyList<SettingsSectionGroup> SearchResultGroups(IEnumerable<SettingsItem> matches, string query)
    {
        var groups = Groups(matches, item => item.SearchGroupTitle);
        var compareInfo = CultureInfo.CurrentCulture.CompareInfo;
        bool NamesTheQuery(SettingsSectionGroup group) =>
            group.Items.Any(item => compareInfo.IndexOf(item.Title, query, CompareOptions.IgnoreCase) >= 0);
        return [.. groups.Where(NamesTheQuery), .. groups.Where(group => !NamesTheQuery(group))];
    }
}

/// <summary>Where the search's words sit in a row's title or copy, for the highlighter.</summary>
public static class SettingsHighlight
{
    public readonly record struct Range(int Start, int Length);

    /// <summary>
    /// Every occurrence of every word of <paramref name="query"/> in <paramref name="text"/>, ignoring
    /// case and accents, in order and without overlaps.
    /// </summary>
    public static IReadOnlyList<Range> Ranges(string text, string query)
    {
        var compareInfo = CultureInfo.CurrentCulture.CompareInfo;
        var found = new List<Range>();
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var searchStart = 0;
            while (searchStart < text.Length)
            {
                var indexInRest = compareInfo.IndexOf(text.AsSpan(searchStart), word.AsSpan(),
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out var matchLength);
                if (indexInRest < 0 || matchLength == 0) break;
                var index = searchStart + indexInRest;
                found.Add(new Range(index, matchLength));
                searchStart = index + matchLength;
            }
        }
        // Words can overlap ("fn" and "f"); merge so each character is marked once.
        var merged = new List<Range>();
        foreach (var range in found.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                var end = Math.Max(last.Start + last.Length, range.Start + range.Length);
                merged[^1] = new Range(last.Start, end - last.Start);
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }
}
