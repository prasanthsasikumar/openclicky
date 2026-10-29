// Port of BrowserTabLocatorTests.swift. Windows global coordinates are y-down from the primary monitor's top-left, like
// the Mac's CG global space, so the z-order fixtures carry over unchanged; the capture mapping test is re-expressed
// with the external display above the primary one at (-173, -1080).

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class BrowserTabLocatorTests
{
    private static readonly BrowserTab[] Tabs =
    {
        BrowserTab.From("changelog — OpenClicky", "https://github.com/prasanthsasikumar/openclicky/commits"),
        BrowserTab.From("Made a small ambient music website : r/VibeCodeDevs", "https://www.reddit.com/r/VibeCodeDevs/comments/1w8wc0e/"),
        BrowserTab.From("Usage - OpenAI API", "https://platform.openai.com/usage"),
        BrowserTab.From("GitHub", "https://github.com/"),
        BrowserTab.From("New Tab", "chrome://newtab/"),
    };

    [Fact]
    public void OnlyRequestsAboutATabAreHandled()
    {
        Assert.True(BrowserTabLocator.MentionsTab(new[] { "Reddit tab", "", "Where is the reddit tab? Point at it." }));
        Assert.True(BrowserTabLocator.MentionsTab(new[] { "browser tab", "", null }));
        Assert.False(BrowserTabLocator.MentionsTab(new[] { "New button", "New", "where is the button to create a new repository" }));
        // "table" is not "tab".
        Assert.False(BrowserTabLocator.MentionsTab(new[] { "pricing table", "", "show me the table" }));
    }

    [Fact]
    public void SiteNameInTheHostWinsOverTitles()
    {
        Assert.Equal(1, BrowserTabLocator.BestMatch(new[] { "Reddit tab", "reddit", "Where is the Reddit tab? Point at it." }, Tabs));
        Assert.Equal(2, BrowserTabLocator.BestMatch(new[] { "OpenAI usage tab", "", "where's my openai usage tab" }, Tabs));
    }

    [Fact]
    public void ABrowserBehindTheFrontAppIsOnlyPointedAtWhenTheUserNamesTheTab()
    {
        // A terminal in front, Chrome behind showing "New session - Claude". The model read that title off the
        // screenshot and passed it as the element text; the user never said it.
        var tabsBehind = Tabs.Append(BrowserTab.From("New session - Claude", "https://claude.ai/new")).ToArray();
        var background = BrowserTabLocator.MatchTexts(false, "tab title", "New session - Claude", "Show me how to minimize this tab.");
        Assert.Null(BrowserTabLocator.BestMatch(background, tabsBehind));
        // The user naming the site still finds it behind the front app.
        var named = BrowserTabLocator.MatchTexts(false, "tab", "", "where is my claude tab");
        Assert.Equal(5, BrowserTabLocator.BestMatch(named, tabsBehind));
        // The active browser keeps the model's reading of the strip.
        var active = BrowserTabLocator.MatchTexts(true, "tab title", "New session - Claude", "point at this tab");
        Assert.Equal(5, BrowserTabLocator.BestMatch(active, tabsBehind));
    }

    [Fact]
    public void TitleWordsMatchWhenTheHostDoesNot()
    {
        Assert.Equal(0, BrowserTabLocator.BestMatch(new[] { "changelog tab", "changelog", null }, Tabs));
    }

    [Fact]
    public void GenericWordsNeverPickATab()
    {
        Assert.Null(BrowserTabLocator.BestMatch(new[] { "the tab", "", "point at the tab" }, Tabs));
        Assert.Null(BrowserTabLocator.BestMatch(new[] { "settings tab", "", "where is the settings tab" }, Tabs));
        Assert.Null(BrowserTabLocator.BestMatch(new[] { "edge tab", "", "where is my edge tab" }, Tabs));
    }

    [Fact]
    public void TabFramesPairWithTabsByOrderOnly()
    {
        var frames = new[] { new ScreenRect(93, -1050, 94, 41), new ScreenRect(-59, -1050, 94, 41), new ScreenRect(17, -1050, 94, 41) };
        Assert.Equal(new ScreenRect(93, -1050, 94, 41), BrowserTabLocator.Frame(2, frames, 3));
        Assert.Equal(new ScreenRect(-59, -1050, 94, 41), BrowserTabLocator.Frame(0, frames, 3));
        Assert.Null(BrowserTabLocator.Frame(1, frames, 4));
    }

    [Fact]
    public void AWindowAboveTheBrowserHidesTheTab()
    {
        const int ownProcessId = 99;
        var terminal = new OnScreenWindow(10, new ScreenRect(0, 135, 1710, 850));
        var chrome = new OnScreenWindow(20, new ScreenRect(0, 30, 1710, 1050));
        var windows = new[] { terminal, chrome };
        Assert.False(BrowserTabLocator.IsHidden(new ScreenPoint(480, 50), 20, windows, ownProcessId));
        Assert.True(BrowserTabLocator.IsHidden(new ScreenPoint(480, 300), 20, windows, ownProcessId));
        Assert.True(BrowserTabLocator.IsHidden(new ScreenPoint(480, 5), 20, windows, ownProcessId));
        // Our own overlay windows never hide anything.
        var overlay = new OnScreenWindow(ownProcessId, new ScreenRect(0, 0, 1710, 1112));
        Assert.False(BrowserTabLocator.IsHidden(new ScreenPoint(480, 50), 20, new[] { overlay, terminal, chrome }, ownProcessId));
    }

    [Fact]
    public void GlobalPointsMapIntoTheCapturedScreenshot()
    {
        // External display above the primary one: (-173, -1080, 1920 × 1080), captured at 1280 × 720.
        var capture = new ScreenCaptureGeometry(new ScreenRect(-173, -1080, 1920, 1080), 1280, 720);
        // The tab centre, 20 below the display's top edge.
        var point = capture.ScreenshotPoint(new ScreenPoint(1331, -1060));
        Assert.True(Math.Abs(point.X - (1331 + 173) * 1280.0 / 1920) < 0.01);
        Assert.True(Math.Abs(point.Y - 20 * 720.0 / 1080) < 0.01);
        Assert.True(capture.ContainsScreenshotPoint(point));
        // A tab on the primary display (below) maps under the captured screenshot and is not pointed at.
        Assert.False(capture.ContainsScreenshotPoint(capture.ScreenshotPoint(new ScreenPoint(292, 30))));
    }

    private sealed class FakeTabSource : IBrowserTabSource
    {
        public IReadOnlyList<OnScreenWindow> Windows { get; init; } = Array.Empty<OnScreenWindow>();
        public IReadOnlyList<RunningBrowser> Browsers { get; init; } = Array.Empty<RunningBrowser>();
        public IReadOnlyList<BrowserTab> Tabs { get; init; } = Array.Empty<BrowserTab>();
        public IReadOnlyList<ScreenRect> Frames { get; init; } = Array.Empty<ScreenRect>();
        public IReadOnlyList<OnScreenWindow> OnScreenWindows() => Windows;
        public IReadOnlyList<RunningBrowser> RunningBrowsers() => Browsers;
        public IReadOnlyList<BrowserTab> TabList(RunningBrowser browser) => Tabs;
        public IReadOnlyList<ScreenRect> TabButtonFrames(RunningBrowser browser) => Frames;
    }

    [Fact]
    public void LocatePointsAtTheMatchingVisibleTab()
    {
        var source = new FakeTabSource
        {
            Windows = new[] { new OnScreenWindow(20, new ScreenRect(0, 0, 1920, 1040)) },
            Browsers = new[] { new RunningBrowser(20, "msedge.exe", IsActive: false), new RunningBrowser(30, "notepad.exe", IsActive: true) },
            Tabs = Tabs,
            Frames = Enumerable.Range(0, Tabs.Length).Select(index => new ScreenRect(10 + index * 200, 5, 200, 30)).ToArray(),
        };
        var capture = new ScreenCaptureGeometry(new ScreenRect(0, 0, 1920, 1080), 1280, 720);
        var located = BrowserTabLocator.Locate("Reddit tab", "reddit", "where is the reddit tab", capture, source, ownProcessId: 1);
        Assert.NotNull(located);
        Assert.Equal(Tabs[1], located!.Tab);
        Assert.Equal(310 * 1280.0 / 1920, located.Point.X, 6);
        Assert.Equal(20 * 720.0 / 1080, located.Point.Y, 6);
        Assert.Null(BrowserTabLocator.Locate("New button", "New", null, capture, source, ownProcessId: 1));
    }
}
