// Port of BrowserControlTests.swift: which tab "next", "the newest" or "the Cars & Bids tab" means.
// The click tool's new_tab argument is in RealtimeVoiceClientTests. Not ported: the AppleScript
// string literal (Windows types the address instead). Windows additions: a title-only tab named by
// its words run together (UI Automation gives only the selected tab's URL), the keys that reach a
// tab, and which link values may be loaded.

using OpenClicky.Core.Actions;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Actions;

public class BrowserControlTests
{
    private static readonly IReadOnlyList<BrowserTab> Tabs =
    [
        BrowserTab.From("skyline r34 - Google Search", "https://www.google.com/search?q=skyline"),
        BrowserTab.From("Used Nissan R34 Skyline for Sale", "https://carsandbids.com/search/nissan"),
        BrowserTab.From("YouTube", "https://www.youtube.com/"),
    ];

    [Fact]
    public void PositionsWrapLikeControlTab()
    {
        Assert.Equal(2, BrowserControl.TabIndex(new BrowserTabTarget.Next(), Tabs, 1));
        Assert.Equal(1, BrowserControl.TabIndex(new BrowserTabTarget.Next(), Tabs, 3));
        Assert.Equal(3, BrowserControl.TabIndex(new BrowserTabTarget.Previous(), Tabs, 1));
        Assert.Equal(1, BrowserControl.TabIndex(new BrowserTabTarget.Previous(), Tabs, 2));
        Assert.Equal(3, BrowserControl.TabIndex(new BrowserTabTarget.Newest(), Tabs, 1));
        Assert.Equal(1, BrowserControl.TabIndex(new BrowserTabTarget.First(), Tabs, 3));
        Assert.Null(BrowserControl.TabIndex(new BrowserTabTarget.Next(), [], 1));
    }

    [Fact]
    public void ANamedTabIsFoundBySiteOrTitle()
    {
        Assert.Equal(2, BrowserControl.TabIndex(new BrowserTabTarget.Named("cars and bids"), Tabs, 1));
        Assert.Equal(3, BrowserControl.TabIndex(new BrowserTabTarget.Named("youtube"), Tabs, 1));
        Assert.Null(BrowserControl.TabIndex(new BrowserTabTarget.Named("reddit"), Tabs, 1));
    }

    [Fact]
    public void WithoutAUrlTheTitleNamesTheTab()
    {
        // UI Automation reads only the selected tab's address; the others are titles.
        IReadOnlyList<BrowserTab> titlesOnly =
        [
            BrowserTab.From("skyline r34 - Google Search", "https://www.google.com/search?q=skyline"),
            BrowserTab.From("Used Nissan R34 Skyline for Sale | Cars & Bids", null),
            BrowserTab.From("YouTube", null),
        ];
        Assert.Equal(2, BrowserControl.TabIndex(new BrowserTabTarget.Named("cars and bids"), titlesOnly, 1));
        Assert.Equal(3, BrowserControl.TabIndex(new BrowserTabTarget.Named("you tube"), titlesOnly, 1));
    }

    [Fact]
    public void TheHoverCardLinesAreNotPartOfTheTitle()
    {
        // Read live from Chrome's tab strip, 2026-10-29.
        Assert.Equal("Claude Code by Anthropic | AI Coding Agent, Terminal, IDE",
                     BrowserControl.TabTitle("Claude Code by Anthropic | AI Coding Agent, Terminal, IDE - Memory usage - 244 MB"));
        Assert.Equal("Lo-fi beats - YouTube", BrowserControl.TabTitle("Lo-fi beats - YouTube - Audio playing"));
        Assert.Equal("GitHub", BrowserControl.TabTitle("GitHub"));
    }

    [Fact]
    public void KeysReachATabByPlaceOrBySteps()
    {
        Assert.Equal(new TabShortcut(3, 0), BrowserControl.Shortcut(1, 3, 12));
        // Ctrl+9 is the last tab, whatever the count.
        Assert.Equal(new TabShortcut(9, 0), BrowserControl.Shortcut(1, 12, 12));
        Assert.Equal(new TabShortcut(null, 2), BrowserControl.Shortcut(9, 11, 12));
        Assert.Equal(new TabShortcut(null, -1), BrowserControl.Shortcut(11, 10, 12));
        Assert.Equal(new TabShortcut(null, 0), BrowserControl.Shortcut(4, 4, 12));
    }

    [Fact]
    public void OnlyAWebAddressIsLoadedForALink()
    {
        Assert.Equal(new Uri("https://carsandbids.com/auctions/x"), BrowserControl.WebLink(" https://carsandbids.com/auctions/x "));
        Assert.NotNull(BrowserControl.WebLink("http://example.com"));
        Assert.Null(BrowserControl.WebLink("javascript:void(0)"));
        Assert.Null(BrowserControl.WebLink("mailto:someone@example.com"));
        Assert.Null(BrowserControl.WebLink("file:///C:/Windows/System32"));
        Assert.Null(BrowserControl.WebLink("/relative/path"));
        Assert.Null(BrowserControl.WebLink("https://example.com/\nmore"));
        Assert.Null(BrowserControl.WebLink(null));
    }
}
