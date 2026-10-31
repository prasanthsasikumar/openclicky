// Ports the model half of Dictation/UI/DictationWindow.swift and the page enum of
// Dictation/UI/SettingsPages.swift: OpenClicky's one window lists six pages in its sidebar
// (Dictation, Clicky, Island, Keys & Account, Privacy, General). Rows on a page open the pages under
// it (Your words, How it writes and The orb under Dictation; Calendars under Island), which keep
// their parent selected in the sidebar, and the back button returns. Every way into the window
// (Ctrl+, the tray, the island, the orb's menu) goes through DictationWindowModel.Open, which lands on
// the page it asks for; a settings page wins over a section, and a leftover search is cleared.
// SettingsLayout says which rows each page holds.

using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenClicky.Core.Settings;

/// <summary>The window's own pages, opened from Dictation and drawn like the settings pages.</summary>
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

    /// <summary>The Swift raw value ("words", "writing").</summary>
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
}

/// <summary>
/// The settings pages. Six are in the sidebar (<see cref="DictationSettingsPages.SidebarPages"/>, in
/// its order); The orb and Calendars are opened from a row on their parent page and keep the parent
/// selected in the sidebar.
/// </summary>
public enum DictationSettingsPage
{
    Dictation,
    Clicky,
    /// <summary>The island: where it lives, its tiles and tools, its look.</summary>
    Island,
    /// <summary>"Keys & Account".</summary>
    Account,
    Privacy,
    General,
    /// <summary>Opened from Dictation.</summary>
    Orb,
    /// <summary>Opened from Island.</summary>
    Calendars,
}

public static class DictationSettingsPages
{
    public static IReadOnlyList<DictationSettingsPage> AllCases { get; } = Enum.GetValues<DictationSettingsPage>();

    /// <summary>The pages the sidebar lists, in order.</summary>
    public static IReadOnlyList<DictationSettingsPage> SidebarPages { get; } =
    [
        DictationSettingsPage.Dictation, DictationSettingsPage.Clicky, DictationSettingsPage.Island,
        DictationSettingsPage.Account, DictationSettingsPage.Privacy, DictationSettingsPage.General,
    ];

    /// <summary>The Swift raw value ("dictation" … "calendars"), used in the tour's target ids.</summary>
    public static string RawValue(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.Dictation => "dictation",
        DictationSettingsPage.Clicky => "clicky",
        DictationSettingsPage.Island => "island",
        DictationSettingsPage.Account => "account",
        DictationSettingsPage.Privacy => "privacy",
        DictationSettingsPage.General => "general",
        DictationSettingsPage.Orb => "orb",
        _ => "calendars",
    };

    /// <summary>The sidebar page a page opened from a row belongs to; a sidebar page is its own.</summary>
    public static DictationSettingsPage SidebarPage(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.Orb => DictationSettingsPage.Dictation,
        DictationSettingsPage.Calendars => DictationSettingsPage.Island,
        _ => page,
    };

    public static string Title(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.Dictation => "Dictation",
        DictationSettingsPage.Clicky => "Clicky",
        DictationSettingsPage.Island => "Island",
        DictationSettingsPage.Account => "Keys & Account",
        DictationSettingsPage.Privacy => "Privacy",
        DictationSettingsPage.General => "General",
        DictationSettingsPage.Orb => "The orb",
        _ => "Calendars",
    };

    /// <summary>The heading search results are listed under: "Dictation › The orb" for a page opened from a row.</summary>
    public static string SearchTitle(this DictationSettingsPage page) =>
        page.SidebarPage() == page ? page.Title() : $"{page.SidebarPage().Title()} › {page.Title()}";

    /// <summary>The page's header card: one line on what lives here.</summary>
    public static string Subtitle(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.Dictation => "Hold your dictation key, speak, and the words land at your cursor.",
        DictationSettingsPage.Clicky => "Hold Ctrl and Win and ask about what’s on screen.",
        DictationSettingsPage.Island => "What lives at the top of your screen.",
        DictationSettingsPage.Account => "Your own keys come first. An account is optional.",
        DictationSettingsPage.Privacy => "What OpenClicky can see, and what it keeps.",
        DictationSettingsPage.General => "Startup, the tray and updates.",
        DictationSettingsPage.Orb => "The small narrator that shows while you dictate.",
        _ => "Which calendars the island shows.",
    };

    /// <summary>The sidebar icon: a Segoe Fluent Icons glyph (empty: the window draws the orb's capsule), white on <see cref="TintHex"/>.</summary>
    public static string Glyph(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.Dictation => "",
        DictationSettingsPage.Clicky => "",
        DictationSettingsPage.Island => "",
        DictationSettingsPage.Account => "",
        DictationSettingsPage.Privacy => "",
        DictationSettingsPage.General => "",
        DictationSettingsPage.Orb => "",
        _ => "",
    };

    /// <summary>The Mac's sidebar tints, as hex.</summary>
    public static string TintHex(this DictationSettingsPage page) => page switch
    {
        DictationSettingsPage.Dictation or DictationSettingsPage.Orb => "#0A84FF",
        DictationSettingsPage.Clicky => "#AF52DE",
        DictationSettingsPage.Island or DictationSettingsPage.Calendars => "#1C1C1E",
        DictationSettingsPage.Account => "#34C759",
        DictationSettingsPage.Privacy => "#5E5CE6",
        _ => "#8E8E93",
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

    /// <summary>Its own name, as its page's title shows it.</summary>
    public string Title => Page is { } page ? page.Title() : Section!.Value.SidebarTitle();

    /// <summary>The sidebar page that stays selected while this one shows: Dictation for your pages.</summary>
    public DictationSettingsPage SidebarPage => Page is { } page ? page.SidebarPage() : DictationSettingsPage.Dictation;

    /// <summary>The sidebar's destinations, in order.</summary>
    public static IReadOnlyList<DictationDestination> SidebarOrder { get; } = DictationSettingsPages.SidebarPages.Select(Of).ToList();

    public override string ToString() => Page is { } page ? $"settings({page.RawValue()})" : $"section({Section!.Value.RawValue()})";
}

public sealed class DictationWindowModel : ObservableObject
{
    private DictationDestination destination = DictationDestination.Of(DictationSettingsPage.Dictation);
    private readonly List<DictationDestination> backStack = [];
    private string? pendingHistorySearch;
    private string searchText = "";

    public DictationDestination Destination
    {
        get => destination;
        set => SetProperty(ref destination, value);
    }

    /// <summary>
    /// The pages left by opening a row (Dictation, then The orb): the back button returns through
    /// them. A sidebar click or a way in from outside starts it again.
    /// </summary>
    public IReadOnlyList<DictationDestination> BackStack => backStack;

    public bool CanGoBack => backStack.Count > 0;

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

    /// <summary>A click in the sidebar. Clears any search and the way back, so the chosen page is what shows.</summary>
    public void Select(DictationDestination chosenDestination)
    {
        SearchText = "";
        SetBackStack(() => backStack.Clear());
        Destination = chosenDestination;
    }

    public void Select(DictationSection section) => Select(DictationDestination.Of(section));

    public void Select(DictationSettingsPage page) => Select(DictationDestination.Of(page));

    /// <summary>A row that opens another page (The orb from Dictation): back returns to this one.</summary>
    public void Push(DictationDestination chosenDestination)
    {
        if (chosenDestination == Destination) return;
        SetBackStack(() => backStack.Add(Destination));
        SearchText = "";
        Destination = chosenDestination;
    }

    public void GoBack()
    {
        if (backStack.Count == 0) return;
        var previousDestination = backStack[^1];
        SetBackStack(() => backStack.RemoveAt(backStack.Count - 1));
        SearchText = "";
        Destination = previousDestination;
    }

    private void SetBackStack(Action change)
    {
        var couldGoBack = CanGoBack;
        change();
        OnPropertyChanged(nameof(BackStack));
        if (couldGoBack != CanGoBack) OnPropertyChanged(nameof(CanGoBack));
    }

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
