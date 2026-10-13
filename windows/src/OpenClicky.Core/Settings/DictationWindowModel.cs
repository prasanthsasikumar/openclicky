// Ports the model half of Dictation/UI/DictationWindow.swift and the page enum of
// Dictation/UI/SettingsPages.swift: OpenClicky's one window lists, in this order, General,
// Dictation, Clicky, Voice (the pages whose ShowsBeforeYourPages is true), "Your words", "How it
// writes", The Orb, Shortcuts, Keys & Plans, Account, Privacy & Security, About. Every way into the
// window (Ctrl+, the tray, the island, the orb's menu) goes through DictationWindowModel.Open, which
// lands on the page it asks for; a settings page wins over a section, and a leftover search is
// cleared. docs/superpowers/specs/2026-10-12-settings-revamp.md has the reasoning for the order.

using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenClicky.Core.Settings;

/// <summary>The window's own pages, listed among the settings pages and drawn like them.</summary>
public enum DictationSection
{
    /// <summary>What you dictated: try it here, this week, and every take (record and history, merged).</summary>
    Words,
    /// <summary>Dictionary, snippets and styles on one page.</summary>
    Writing,
}

public static class DictationSections
{
    public static IReadOnlyList<DictationSection> AllCases { get; } = [DictationSection.Words, DictationSection.Writing];

    /// <summary>The Swift raw value ("words", "writing"), used in the tour's target ids.</summary>
    public static string RawValue(this DictationSection section) => section switch
    {
        DictationSection.Words => "words",
        _ => "writing",
    };

    public static string SidebarTitle(this DictationSection section) => section switch
    {
        DictationSection.Words => "Your words",
        _ => "How it writes",
    };

    /// <summary>The sidebar icon: a Segoe Fluent Icons glyph, white on <see cref="TintHex"/>.</summary>
    public static string Glyph(this DictationSection section) => section switch
    {
        DictationSection.Words => "",
        _ => "",
    };

    public static string TintHex(this DictationSection section) => section switch
    {
        DictationSection.Words => "#D24E2A",
        _ => "#AF52DE",
    };
}

/// <summary>
/// The settings pages, in sidebar order. Your two pages sit among them, after the pages that say
/// <see cref="DictationSettingsPages.ShowsBeforeYourPages"/>.
/// </summary>
public enum DictationSettingsPage
{
    General,
    Dictation,
    Clicky,
    Voice,
    Orb,
    /// <summary>"Shortcuts" (the Mac's `keyboard` case).</summary>
    Keyboard,
    /// <summary>"Keys & Plans".</summary>
    Keys,
    Account,
    Privacy,
    About,
}

public static class DictationSettingsPages
{
    public static IReadOnlyList<DictationSettingsPage> AllCases { get; } = Enum.GetValues<DictationSettingsPage>();

    /// <summary>The Swift raw value ("general" … "about"), used in the tour's target ids.</summary>
    public static string RawValue(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.General => "general",
        DictationSettingsPage.Dictation => "dictation",
        DictationSettingsPage.Clicky => "clicky",
        DictationSettingsPage.Voice => "voice",
        DictationSettingsPage.Orb => "orb",
        DictationSettingsPage.Keyboard => "keyboard",
        DictationSettingsPage.Keys => "keys",
        DictationSettingsPage.Account => "account",
        DictationSettingsPage.Privacy => "privacy",
        _ => "about",
    };

    /// <summary>
    /// True for the pages the sidebar lists above your two pages: what you set up first (General,
    /// Dictation, Clicky, Voice) comes before what you dictated; the rest follows it.
    /// </summary>
    public static bool ShowsBeforeYourPages(this DictationSettingsPage page) =>
        page is DictationSettingsPage.General or DictationSettingsPage.Dictation or DictationSettingsPage.Clicky or DictationSettingsPage.Voice;

    public static IReadOnlyList<DictationSettingsPage> PagesBeforeYourPages { get; } = AllCases.Where(page => page.ShowsBeforeYourPages()).ToList();

    public static IReadOnlyList<DictationSettingsPage> PagesAfterYourPages { get; } = AllCases.Where(page => !page.ShowsBeforeYourPages()).ToList();

    public static string Title(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.General => "General",
        DictationSettingsPage.Dictation => "Dictation",
        DictationSettingsPage.Clicky => "Clicky",
        DictationSettingsPage.Voice => "Voice",
        DictationSettingsPage.Orb => "The Orb",
        DictationSettingsPage.Keyboard => "Shortcuts",
        DictationSettingsPage.Keys => "Keys & Plans",
        DictationSettingsPage.Account => "Account",
        DictationSettingsPage.Privacy => "Privacy & Security",
        _ => "About",
    };

    /// <summary>The sidebar icon: a Segoe Fluent Icons glyph (empty: the window draws the orb's capsule), white on <see cref="TintHex"/>.</summary>
    public static string Glyph(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.General => "",
        DictationSettingsPage.Dictation => "",
        DictationSettingsPage.Clicky => "",
        DictationSettingsPage.Voice => "",
        DictationSettingsPage.Orb => "",
        DictationSettingsPage.Keyboard => "",
        DictationSettingsPage.Keys => "",
        DictationSettingsPage.Account => "",
        DictationSettingsPage.Privacy => "",
        _ => "",
    };

    /// <summary>The Mac's sidebar tints, as hex.</summary>
    public static string TintHex(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.General => "#8E8E93",
        DictationSettingsPage.Dictation => "#FF3B30",
        DictationSettingsPage.Clicky => "#FF9500",
        DictationSettingsPage.Voice => "#FE2D55",
        DictationSettingsPage.Orb => "#1C1C1E",
        DictationSettingsPage.Keyboard => "#636366",
        DictationSettingsPage.Keys => "#8B6B00",
        DictationSettingsPage.Account => "#34C759",
        DictationSettingsPage.Privacy => "#007AFF",
        _ => "#AEAEB2",
    };

    public static DictationSettingsPage? FromRawValue(string? raw) =>
        AllCases.Cast<DictationSettingsPage?>().FirstOrDefault(page => page!.Value.RawValue() == raw);
}

/// <summary>What the window shows: one of your pages or one settings page.</summary>
public sealed record DictationDestination
{
    public DictationSection? Section { get; }
    public DictationSettingsPage? Page { get; }

    private DictationDestination(DictationSection? section, DictationSettingsPage? page)
    {
        Section = section;
        Page = page;
    }

    public static DictationDestination Of(DictationSection section) => new(section, null);

    public static DictationDestination Of(DictationSettingsPage page) => new(null, page);

    /// <summary>The name it has in the sidebar.</summary>
    public string Title => Page is { } page ? page.Title() : Section!.Value.SidebarTitle();

    /// <summary>Every destination in sidebar order.</summary>
    public static IReadOnlyList<DictationDestination> SidebarOrder { get; } =
    [
        .. DictationSettingsPages.PagesBeforeYourPages.Select(Of),
        .. DictationSections.AllCases.Select(Of),
        .. DictationSettingsPages.PagesAfterYourPages.Select(Of),
    ];

    public override string ToString() => Page is { } page ? $"settings({page.RawValue()})" : $"section({Section!.Value.RawValue()})";
}

public sealed class DictationWindowModel : ObservableObject
{
    private DictationDestination destination = DictationDestination.Of(DictationSection.Words);
    private string? pendingHistorySearch;
    private string searchText = "";

    public DictationDestination Destination
    {
        get => destination;
        set => SetProperty(ref destination, value);
    }

    /// <summary>A search the Your words page picks up when it next appears (the orb's "find in history").</summary>
    public string? PendingHistorySearch
    {
        get => pendingHistorySearch;
        set => SetProperty(ref pendingHistorySearch, value);
    }

    /// <summary>While this holds words the window shows matching settings from every page.</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value ?? ""))
            {
                OnPropertyChanged(nameof(TrimmedSearchText));
                OnPropertyChanged(nameof(IsSearching));
            }
        }
    }

    public string TrimmedSearchText => SearchText.Trim();

    public bool IsSearching => TrimmedSearchText.Length > 0;

    /// <summary>A click in the sidebar. Clears any search so the chosen page is what shows.</summary>
    public void Select(DictationDestination chosenDestination)
    {
        SearchText = "";
        Destination = chosenDestination;
    }

    public void Select(DictationSection section) => Select(DictationDestination.Of(section));

    public void Select(DictationSettingsPage page) => Select(DictationDestination.Of(page));

    /// <summary>
    /// Every way into the window comes through here. A settings page wins over a section; with
    /// neither, the window just comes forward as it was (a leftover search included).
    /// </summary>
    public void Open(DictationSection? section = null, DictationSettingsPage? settingsPage = null)
    {
        if (settingsPage is { } requestedPage)
        {
            Select(DictationDestination.Of(requestedPage));
        }
        else if (section is { } requestedSection)
        {
            Select(DictationDestination.Of(requestedSection));
        }
    }
}
