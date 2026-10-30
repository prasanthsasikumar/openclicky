// Port of macos/OpenClicky/OpenClickyTests/ScreenNavigationTests.swift: the pure parts of moving
// around by voice. How far a scroll goes (in wheel notches here, pixels on the Mac), which clicks
// are left to the person, the SendInput coordinate (the Mac's AppKit flip), the Windows Search
// query a spoken name becomes (the Mac's Spotlight query), and the ranking. Not ported:
// ownerOfWindow (Windows asks WindowFromPoint, the click's own hit test). Added: the bounded walk
// and the check that Ctrl+F really moved the focus into a new text field.

using OpenClicky.Core.Actions;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Tests.Actions;

public sealed class ScreenNavigationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "screen-navigation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void APageIsMostOfTheWindowAndDownIsNegative()
    {
        var down = ScreenNavigation.ScrollSteps(ScrollDirection.Down, ScrollAmount.Page, 1200, 800);
        Assert.Single(down);
        // 80 % of 800 px is 640 px: six notches of about 100.
        Assert.Equal((-720, 0), down[0]);
        Assert.Equal(240, ScreenNavigation.ScrollSteps(ScrollDirection.Up, ScrollAmount.Little, 1200, 800)[0].Vertical);
        // Right is a positive tilt (MOUSEEVENTF_HWHEEL), left a negative one: the Mac's signs are the other way round.
        Assert.Equal(1200, ScreenNavigation.ScrollSteps(ScrollDirection.Right, ScrollAmount.Page, 1200, 800)[0].Horizontal);
        Assert.Equal(-1200, ScreenNavigation.ScrollSteps(ScrollDirection.Left, ScrollAmount.Page, 1200, 800)[0].Horizontal);
        // "To the bottom" is many big steps, enough for any page.
        var toTheBottom = ScreenNavigation.ScrollSteps(ScrollDirection.Down, ScrollAmount.End, 1200, 800);
        Assert.Equal(10, toTheBottom.Count);
        Assert.All(toTheBottom, step => Assert.True(step.Vertical <= -40 * ScreenNavigation.WheelDelta));
    }

    [Fact]
    public void ThePageIsMeasuredInDipsAndIsNeverLessThanANotch()
    {
        // The same window at 150 %: twice the pixels is the same page.
        Assert.Equal(ScreenNavigation.ScrollSteps(ScrollDirection.Down, ScrollAmount.Page, 1200, 800),
                     ScreenNavigation.ScrollSteps(ScrollDirection.Down, ScrollAmount.Page, 1800, 1200, scale: 1.5));
        Assert.Equal(120, ScreenNavigation.ScrollSteps(ScrollDirection.Up, ScrollAmount.Little, 100, 60)[0].Vertical);
        Assert.All(ScreenNavigation.ScrollSteps(ScrollDirection.Down, ScrollAmount.Page, 1200, 800),
                   step => Assert.Equal(0, step.Vertical % ScreenNavigation.WheelDelta));
    }

    [Fact]
    public void ClicksThatCommitSomethingAreLeftToThePerson()
    {
        Assert.True(ScreenNavigation.ClickIsRisky("send button", "Send"));
        Assert.True(ScreenNavigation.ClickIsRisky("checkout", "Proceed to checkout"));
        Assert.True(ScreenNavigation.ClickIsRisky("button", "Place order"));
        Assert.True(ScreenNavigation.ClickIsRisky("account", "Delete account"));
        Assert.True(ScreenNavigation.ClickIsRisky("menu", "Sign out"));
        // Whole words only: a link to someone's posts or a sort order is fine.
        Assert.False(ScreenNavigation.ClickIsRisky("first link", "Used Nissan R34 Skyline for Sale"));
        Assert.False(ScreenNavigation.ClickIsRisky("posts tab", "Posts"));
        Assert.False(ScreenNavigation.ClickIsRisky("sign in", "Sign in"));
        Assert.False(ScreenNavigation.ClickIsRisky("orders", "Your Orders"));
    }

    [Fact]
    public void APixelBecomesSendInputsAbsoluteCoordinateOnTheWholeDesktop()
    {
        // One 1920-wide monitor: its first and last pixels are the ends of the range.
        Assert.Equal(0, ScreenNavigation.AbsoluteMouseCoordinate(0, 0, 1920));
        Assert.Equal(65535, ScreenNavigation.AbsoluteMouseCoordinate(1919, 0, 1920));
        Assert.Equal(32785, ScreenNavigation.AbsoluteMouseCoordinate(960, 0, 1920));
        // A monitor to the left of the primary puts the desktop's origin at -1280.
        Assert.Equal(0, ScreenNavigation.AbsoluteMouseCoordinate(-1280, -1280, 3200));
        Assert.Equal(26222, ScreenNavigation.AbsoluteMouseCoordinate(0, -1280, 3200));
    }

    [Fact]
    public void ASpokenNameBecomesAQueryNothingSaidCanReshape()
    {
        Assert.Equal("SELECT TOP 400 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:C:/Users/tester' AND System.FileName LIKE '%tax%' AND System.FileName LIKE '%pdf%'",
                     ScreenNavigation.WindowsSearchQuery("Tax PDF", @"C:\Users\tester"));
        Assert.Equal("SELECT TOP 400 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:C:/Users/tester' AND System.FileName LIKE '%a%' AND System.FileName LIKE '%or%' AND System.FileName LIKE '%1%'",
                     ScreenNavigation.WindowsSearchQuery("a' OR '1%_", @"C:\Users\tester"));
        // The profile's own name is quoted, not trusted.
        Assert.Contains("SCOPE='file:C:/Users/O''Brien'", ScreenNavigation.WindowsSearchQuery("budget", @"C:\Users\O'Brien"));
        Assert.Null(ScreenNavigation.WindowsSearchQuery("  !! ", @"C:\Users\tester"));
        Assert.Equal(6, ScreenNavigation.FileSearchTerms("one two three four five six seven").Count);
    }

    [Fact]
    public void FoundFilesSkipAppDataAndPutExactNamesFirst()
    {
        var found = ScreenNavigation.RankFoundFiles(
        [
            @"C:\Users\tester\AppData\Local\Caches\budget.json",
            @"C:\Users\tester\Documents\old budget.xlsx",
            @"C:\Users\tester\.hidden\budget.txt",
            @"C:\Users\tester\Desktop\Budget.xlsx",
            @"C:\Users\tester\code\app\node_modules\budget\index.js",
            @"C:\Users\tester\Documents\budget 2026.xlsx",
        ], "budget");
        Assert.Equal(["Budget.xlsx", "budget 2026.xlsx", "old budget.xlsx"], found.Select(Path.GetFileName));
        // A word that starts a word beats the same letters inside one.
        var riti = ScreenNavigation.RankFoundFiles([@"C:\Users\tester\Docs\WritingPageView.cs", @"C:\Users\tester\Desktop\Riti"], "riti");
        Assert.Equal(["Riti", "WritingPageView.cs"], riti.Select(Path.GetFileName));
        var tax = ScreenNavigation.RankFoundFiles([@"C:\Users\tester\syntax notes.txt", @"C:\Users\tester\2025 Tax return.pdf"], "tax return");
        Assert.Equal(["2025 Tax return.pdf", "syntax notes.txt"], tax.Select(Path.GetFileName));
        Assert.Equal(5, ScreenNavigation.RankFoundFiles(Enumerable.Range(0, 9).Select(index => $@"C:\Users\tester\note {index}.txt"), "note").Count);
    }

    [Fact]
    public void TheWalkFindsNamesWithEveryWordAndSkipsWhatThePersonNeverSees()
    {
        Directory.CreateDirectory(Path.Combine(root, "Documents", "Taxes"));
        Directory.CreateDirectory(Path.Combine(root, "AppData", "Local"));
        Directory.CreateDirectory(Path.Combine(root, "project", "node_modules", "tax"));
        File.WriteAllText(Path.Combine(root, "Documents", "Taxes", "Tax return 2025.pdf"), "x");
        File.WriteAllText(Path.Combine(root, "Documents", "return policy.txt"), "x");
        File.WriteAllText(Path.Combine(root, "AppData", "Local", "tax return cache.db"), "x");
        File.WriteAllText(Path.Combine(root, "project", "node_modules", "tax", "return tax.js"), "x");

        var found = ScreenNavigation.WalkForFiles([root], "tax return", TimeSpan.FromSeconds(5));
        Assert.Equal([Path.Combine(root, "Documents", "Taxes", "Tax return 2025.pdf")], found);
        Assert.Empty(ScreenNavigation.WalkForFiles([root], "!!", TimeSpan.FromSeconds(5)));
        Assert.Empty(ScreenNavigation.WalkForFiles([Path.Combine(root, "missing")], "tax", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WordsAreTypedOnlyOnceTheFocusMovedIntoANewTextField()
    {
        var page = new FocusedTextTarget { ProcessId = 7, RuntimeId = "42.1", Role = "Document" };
        var findBar = new FocusedTextTarget { ProcessId = 7, RuntimeId = "42.9", Role = "Edit" };
        var theirTextBox = new FocusedTextTarget { ProcessId = 7, RuntimeId = "42.5", Role = "Edit" };
        Assert.True(ScreenNavigation.FocusMovedIntoNewTextField(page, findBar));
        // Ctrl+F did nothing and the person's text box still has the focus.
        Assert.False(ScreenNavigation.FocusMovedIntoNewTextField(theirTextBox, theirTextBox with { }));
        Assert.False(ScreenNavigation.FocusMovedIntoNewTextField(page, page));
        // A password box, a field with no runtime id, or a focus UI Automation could not read are never typed into.
        Assert.False(ScreenNavigation.FocusMovedIntoNewTextField(page, findBar with { IsSecure = true }));
        Assert.False(ScreenNavigation.FocusMovedIntoNewTextField(page, findBar with { RuntimeId = null }));
        Assert.False(ScreenNavigation.FocusMovedIntoNewTextField(null, findBar));
        Assert.False(ScreenNavigation.FocusMovedIntoNewTextField(page, null));
    }

    [Fact]
    public void OutlooksCtrlFForwardsSoItIsLeftAlone()
    {
        Assert.Contains("OUTLOOK.EXE", ScreenNavigation.CtrlFIsNotFindProcessNames);
        Assert.Contains("olk.exe", ScreenNavigation.CtrlFIsNotFindProcessNames);
        Assert.DoesNotContain("chrome.exe", ScreenNavigation.CtrlFIsNotFindProcessNames);
    }
}
