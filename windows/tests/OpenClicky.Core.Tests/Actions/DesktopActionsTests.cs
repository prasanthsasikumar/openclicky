// Port of macos/OpenClicky/OpenClickyTests/MacActionsTests.swift (MacActionsTests,
// MacActionRunnerTests, MacActionParsingTests, FastActionToolDefinitionTests) and the MacAction.parse
// round trip from PlanToolProtocolTests.swift. The fast lane's pure core: which folder a spoken
// location means, which names and URLs are allowed through, and the one sentence the assistant
// says for every outcome. Not ported here: the RealtimeVoiceClient.defaultInstructions check
// (Speech). Added: the Windows name rules and the reveal_in_finder alias.

using System.Text.Json.Nodes;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Tests.Actions;

public class DesktopActionsTests
{
    private static readonly string HomeDirectory = Path.Combine("C:", "Users", "tester");
    private static readonly string WorkspaceDirectory = Path.Combine("C:", "Users", "tester", "OpenClicky");

    [Fact]
    public void LocationsResolveToTheFoldersTheyName()
    {
        string Resolve(DesktopActionLocation location) => location.DirectoryPath(HomeDirectory, WorkspaceDirectory);
        Assert.Equal(Path.Combine(HomeDirectory, "Desktop"), Resolve(DesktopActionLocation.Desktop));
        Assert.Equal(Path.Combine(HomeDirectory, "Downloads"), Resolve(DesktopActionLocation.Downloads));
        Assert.Equal(Path.Combine(HomeDirectory, "Documents"), Resolve(DesktopActionLocation.Documents));
        Assert.Equal(WorkspaceDirectory, Resolve(DesktopActionLocation.Workspace));
        Assert.Equal(HomeDirectory, Resolve(DesktopActionLocation.Home));
    }

    [Fact]
    public void AnUnknownLocationIsRejectedRatherThanGuessed()
    {
        Assert.Equal(DesktopActionLocation.Desktop, DesktopActionLocations.Named("desktop"));
        Assert.Equal(DesktopActionLocation.Desktop, DesktopActionLocations.Named("Desktop"));
        Assert.Equal(DesktopActionLocation.Downloads, DesktopActionLocations.Named("  DOWNLOADS "));
        // The model invented a location: better to say so than to write somewhere the user didn't ask for.
        Assert.Null(DesktopActionLocations.Named("icloud"));
        Assert.Null(DesktopActionLocations.Named("onedrive"));
        Assert.Null(DesktopActionLocations.Named(""));
    }

    [Fact]
    public void FolderNamesThatCouldEscapeTheirLocationAreRefused()
    {
        Assert.Equal("Test", DesktopActionValidation.FolderName("  Test  "));
        Assert.Equal("Tax 2026", DesktopActionValidation.FolderName("Tax 2026"));
        Assert.Null(DesktopActionValidation.FolderName("a/b"));
        Assert.Null(DesktopActionValidation.FolderName("a:b"));
        Assert.Null(DesktopActionValidation.FolderName(".."));
        Assert.Null(DesktopActionValidation.FolderName(".hidden"));
        Assert.Null(DesktopActionValidation.FolderName(""));
        Assert.Null(DesktopActionValidation.FolderName("   "));
        Assert.Null(DesktopActionValidation.FolderName(new string('a', 256)));
        Assert.NotNull(DesktopActionValidation.FolderName(new string('a', 255)));
    }

    [Theory]
    [InlineData(@"a\b")]
    [InlineData(@"..\escape")]
    [InlineData("C:")]
    [InlineData("what?")]
    [InlineData("star*")]
    [InlineData("pipe|name")]
    [InlineData("<angle>")]
    [InlineData("say \"hi\"")]
    [InlineData("tab\tname")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("nul.txt")]
    [InlineData("Aux")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT3")]
    [InlineData("NUL .txt")]
    public void WindowsRefusesTheseNamesSoTheyAreRefusedFirst(string name) =>
        Assert.Null(DesktopActionValidation.FolderName(name));

    [Theory]
    [InlineData("CONFIG")]
    [InlineData("COM10")]
    [InlineData("console notes")]
    [InlineData("Report v1.2")]
    [InlineData("Ünïcödé café")]
    public void OrdinaryNamesThatOnlyLookLikeReservedOnesAreAllowed(string name) =>
        Assert.Equal(name, DesktopActionValidation.FolderName(name));

    [Fact]
    public void OnlyWebUrlsOpen()
    {
        Assert.Equal("https://example.com/", DesktopActionValidation.WebUrl("https://example.com")?.AbsoluteUri);
        Assert.Equal("http://example.com/", DesktopActionValidation.WebUrl(" http://example.com ")?.AbsoluteUri);
        // A file: or custom scheme would launch a handler of the model's choosing.
        Assert.Null(DesktopActionValidation.WebUrl("file:///etc/passwd"));
        Assert.Null(DesktopActionValidation.WebUrl(@"file://C:/Windows/win.ini"));
        Assert.Null(DesktopActionValidation.WebUrl("ms-settings:sound"));
        Assert.Null(DesktopActionValidation.WebUrl("x-apple.systempreferences:foo"));
        Assert.Null(DesktopActionValidation.WebUrl("not a url"));
        Assert.Null(DesktopActionValidation.WebUrl(@"C:\Windows\System32\calc.exe"));
    }

    [Fact]
    public void EveryOutcomeHasOneSentenceToSay()
    {
        Assert.Equal("Opened Spotify.", new DesktopActionOutcome.OpenedApp("Spotify").SpokenSentence);
        Assert.Equal("I couldn't find an app called Spotify.", new DesktopActionOutcome.AppNotFound("Spotify").SpokenSentence);
        Assert.Equal("Created Test on your Desktop.", new DesktopActionOutcome.CreatedFolder("Test", DesktopActionLocation.Desktop).SpokenSentence);
        Assert.Equal("Test is already on your Desktop.", new DesktopActionOutcome.FolderAlreadyExists("Test", DesktopActionLocation.Desktop).SpokenSentence);
        Assert.Equal("Showing Test on your Desktop.", new DesktopActionOutcome.Revealed("Test", DesktopActionLocation.Desktop).SpokenSentence);
        Assert.Equal("I couldn't find Test on your Desktop.", new DesktopActionOutcome.NothingToReveal("Test", DesktopActionLocation.Desktop).SpokenSentence);
        Assert.Equal("Opened it in your browser.", new DesktopActionOutcome.OpenedUrl("https://example.com").SpokenSentence);
        Assert.Equal("Volume set to 40 percent.", new DesktopActionOutcome.VolumeSet(40).SpokenSentence);
        Assert.Equal("Done.", new DesktopActionOutcome.MediaControlled("playpause").SpokenSentence);
        Assert.Equal("That name has characters a folder can't have.", new DesktopActionOutcome.InvalidName().SpokenSentence);
        Assert.Equal("That app name has characters an app name can't have.", new DesktopActionOutcome.InvalidAppName().SpokenSentence);
        Assert.Equal("I don't know where icloud is.", new DesktopActionOutcome.UnknownLocation("icloud").SpokenSentence);
        Assert.Equal("That doesn't look like a web address I can open.", new DesktopActionOutcome.InvalidUrl().SpokenSentence);
        Assert.Equal("Windows hasn't allowed access to your Documents folder. Windows Security can allow it.",
                     new DesktopActionOutcome.PermissionDenied(DesktopActionLocation.Documents).SpokenSentence);
        Assert.Equal("That didn't work: disk full.", new DesktopActionOutcome.Failed("disk full").SpokenSentence);
    }

    [Fact]
    public void OnlyTheOutcomesThatDidSomethingCountAsDone()
    {
        Assert.True(new DesktopActionOutcome.OpenedApp("Spotify").DidSomething);
        Assert.True(new DesktopActionOutcome.FolderAlreadyExists("Test", DesktopActionLocation.Desktop).DidSomething);
        Assert.True(new DesktopActionOutcome.VolumeSet(10).DidSomething);
        Assert.False(new DesktopActionOutcome.AppNotFound("Spotify").DidSomething);
        Assert.False(new DesktopActionOutcome.InvalidName().DidSomething);
        Assert.False(new DesktopActionOutcome.Failed("x").DidSomething);
    }

    [Fact]
    public void TheSpokenLocationNameReadsNaturally()
    {
        Assert.Equal("your Desktop", DesktopActionLocation.Desktop.SpokenName());
        Assert.Equal("your OpenClicky folder", DesktopActionLocation.Workspace.SpokenName());
        Assert.Equal("your home folder", DesktopActionLocation.Home.SpokenName());
    }

    [Fact]
    public void TheLogLinesKeepTheMacPrefixForTheMeasureScript()
    {
        Assert.Equal("mac action: open_app Opened Spotify. in 0.42 s",
                     DesktopActionLog.Performed("open_app", new DesktopActionOutcome.OpenedApp("Spotify"), TimeSpan.FromMilliseconds(420)));
        Assert.Equal("mac action: create_folder rejected, I don't know where icloud is.",
                     DesktopActionLog.Rejected("create_folder", new DesktopActionOutcome.UnknownLocation("icloud")));
    }
}

public sealed class DesktopActionRunnerTests : IDisposable
{
    /// <summary>The OS calls replaced by recorders.</summary>
    private sealed class RecordingExecutor : IDesktopActionExecutor
    {
        public List<DesktopAppTarget> LaunchedApplications { get; } = [];
        public List<Uri> OpenedUrls { get; } = [];
        public List<string> RevealedPaths { get; } = [];
        public List<int> VolumeLevels { get; } = [];
        public List<string> MediaKeys { get; } = [];
        public Dictionary<string, string> KnownFolders { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, DesktopAppTarget> InstalledApplications { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["spotify"] = new DesktopAppTarget("Spotify", DesktopAppTargetKind.Shortcut, @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Spotify.lnk"),
        };

        public DesktopAppTarget? FindApplication(string name) => InstalledApplications.GetValueOrDefault(name);
        public bool LaunchApplication(DesktopAppTarget target) { LaunchedApplications.Add(target); return true; }
        public bool OpenUrl(Uri url) { OpenedUrls.Add(url); return true; }
        public string? KnownFolderPath(string location) => KnownFolders.GetValueOrDefault(location);
        public bool RevealInExplorer(string path) { RevealedPaths.Add(path); return true; }
        public bool SetVolume(int percent) { VolumeLevels.Add(percent); return true; }
        public int? GetVolume() => VolumeLevels.LastOrDefault();
        public bool SendMediaKey(string action) { MediaKeys.Add(action); return true; }

        public ScrollResult ScrollResultToGive { get; set; } = ScrollResult.Scrolled;
        public List<string> Scrolls { get; } = [];
        public List<string> OpenedFolders { get; } = [];
        public IReadOnlyList<string> FilesToFind { get; set; } = [];
        public List<string> FileSearches { get; } = [];
        public FindOnPageResult FindOnPageResultToGive { get; set; } = FindOnPageResult.Typed;
        public List<string> PageSearches { get; } = [];

        public ScrollResult ScrollFrontWindow(ScrollDirection direction, ScrollAmount amount)
        {
            Scrolls.Add($"{direction.RawValue()} {amount.RawValue()}");
            return ScrollResultToGive;
        }

        public bool OpenFolder(string path) { OpenedFolders.Add(path); return true; }

        public Task<IReadOnlyList<string>> FindFilesAsync(string words)
        {
            FileSearches.Add(words);
            return Task.FromResult(FilesToFind);
        }

        public Task<FindOnPageResult> FindOnPageAsync(string text)
        {
            PageSearches.Add(text);
            return Task.FromResult(FindOnPageResultToGive);
        }

        public BrowserTabSwitchResult TabSwitchResultToGive { get; set; } = new BrowserTabSwitchResult.Switched("Cars & Bids");
        public List<BrowserTabTarget> TabTargets { get; } = [];

        public Task<BrowserTabSwitchResult> SwitchTabAsync(BrowserTabTarget target)
        {
            TabTargets.Add(target);
            return Task.FromResult(TabSwitchResultToGive);
        }
    }

    private readonly string root;
    private readonly RecordingExecutor recorder = new();

    public DesktopActionRunnerTests()
    {
        root = Path.Combine(Path.GetTempPath(), "desktop-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Desktop"));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private DesktopActionRunner MakeRunner(bool usesKnownFolders = false) =>
        new(recorder, root, Path.Combine(root, "OpenClicky"), usesKnownFolders);

    [Fact]
    public async Task OpeningAnInstalledAppLaunchesItAndSaysSo()
    {
        var outcome = await MakeRunner().PerformAsync(new DesktopAction.OpenApp("Spotify"));
        Assert.Equal(new DesktopActionOutcome.OpenedApp("Spotify"), outcome);
        Assert.Equal([recorder.InstalledApplications["spotify"]], recorder.LaunchedApplications);
    }

    [Fact]
    public async Task AnAppThatIsNotInstalledIsReportedNotGuessedAt()
    {
        var outcome = await MakeRunner().PerformAsync(new DesktopAction.OpenApp("Ableton"));
        Assert.Equal(new DesktopActionOutcome.AppNotFound("Ableton"), outcome);
        Assert.Empty(recorder.LaunchedApplications);
    }

    [Fact]
    public async Task CreatingAFolderWritesItAndTheSecondAttemptSaysItIsAlreadyThere()
    {
        var runner = MakeRunner();
        var first = await runner.PerformAsync(new DesktopAction.CreateFolder("Test", DesktopActionLocation.Desktop));
        Assert.Equal(new DesktopActionOutcome.CreatedFolder("Test", DesktopActionLocation.Desktop), first);
        Assert.True(Directory.Exists(Path.Combine(root, "Desktop", "Test")));

        var second = await runner.PerformAsync(new DesktopAction.CreateFolder("Test", DesktopActionLocation.Desktop));
        Assert.Equal(new DesktopActionOutcome.FolderAlreadyExists("Test", DesktopActionLocation.Desktop), second);
    }

    [Fact]
    public async Task TheWorkspaceIsCreatedWhenItIsNotThereYet()
    {
        var outcome = await MakeRunner().PerformAsync(new DesktopAction.CreateFolder("Notes", DesktopActionLocation.Workspace));
        Assert.Equal(new DesktopActionOutcome.CreatedFolder("Notes", DesktopActionLocation.Workspace), outcome);
        Assert.True(Directory.Exists(Path.Combine(root, "OpenClicky", "Notes")));
    }

    [Fact]
    public async Task AnUnsafeFolderNameIsRefusedBeforeAnythingIsWritten()
    {
        var runner = MakeRunner();
        Assert.Equal(new DesktopActionOutcome.InvalidName(), await runner.PerformAsync(new DesktopAction.CreateFolder("../escape", DesktopActionLocation.Desktop)));
        Assert.Equal(new DesktopActionOutcome.InvalidName(), await runner.PerformAsync(new DesktopAction.CreateFolder(@"..\escape", DesktopActionLocation.Desktop)));
        Assert.Equal(new DesktopActionOutcome.InvalidName(), await runner.PerformAsync(new DesktopAction.CreateFolder("CON", DesktopActionLocation.Desktop)));
        Assert.False(Directory.Exists(Path.Combine(root, "escape")));
    }

    [Fact]
    public async Task RevealingSomethingThatIsNotThereSaysSo()
    {
        Assert.Equal(new DesktopActionOutcome.NothingToReveal("Missing", DesktopActionLocation.Desktop),
                     await MakeRunner().PerformAsync(new DesktopAction.RevealInExplorer("Missing", DesktopActionLocation.Desktop)));
        Assert.Empty(recorder.RevealedPaths);
    }

    [Fact]
    public async Task OnlyWebUrlsAreOpenedAndVolumeIsClamped()
    {
        var runner = MakeRunner();
        Assert.Equal(new DesktopActionOutcome.InvalidUrl(), await runner.PerformAsync(new DesktopAction.OpenUrl("file:///etc/passwd")));
        Assert.Empty(recorder.OpenedUrls);

        Assert.Equal(new DesktopActionOutcome.OpenedUrl("https://example.com"), await runner.PerformAsync(new DesktopAction.OpenUrl("https://example.com")));
        Assert.Equal(["https://example.com/"], recorder.OpenedUrls.Select(url => url.AbsoluteUri));

        Assert.Equal(new DesktopActionOutcome.VolumeSet(100), await runner.PerformAsync(new DesktopAction.SetVolume(400)));
        Assert.Equal(new DesktopActionOutcome.VolumeSet(0), await runner.PerformAsync(new DesktopAction.SetVolume(-5)));
        Assert.Equal([100, 0], recorder.VolumeLevels);
    }

    [Fact]
    public async Task RevealingAnExistingFolderShowsItAndReportsSuccess()
    {
        var desktopPath = Path.Combine(root, "Desktop");
        Directory.CreateDirectory(Path.Combine(desktopPath, "TestFolder"));
        var outcome = await MakeRunner().PerformAsync(new DesktopAction.RevealInExplorer("TestFolder", DesktopActionLocation.Desktop));
        Assert.Equal(new DesktopActionOutcome.Revealed("TestFolder", DesktopActionLocation.Desktop), outcome);
        Assert.Equal([Path.Combine(desktopPath, "TestFolder")], recorder.RevealedPaths);
    }

    [Fact]
    public async Task RevealingAnExistingFileShowsItToo()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Desktop", "notes.txt"), "x");
        var outcome = await MakeRunner().PerformAsync(new DesktopAction.RevealInExplorer("notes.txt", DesktopActionLocation.Desktop));
        Assert.Equal(new DesktopActionOutcome.Revealed("notes.txt", DesktopActionLocation.Desktop), outcome);
    }

    [Fact]
    public async Task TheShellsKnownFolderIsUsedWhenTheDesktopIsRedirected()
    {
        var redirectedDesktop = Path.Combine(root, "OneDrive", "Desktop");
        Directory.CreateDirectory(redirectedDesktop);
        recorder.KnownFolders["desktop"] = redirectedDesktop;
        var outcome = await MakeRunner(usesKnownFolders: true).PerformAsync(new DesktopAction.CreateFolder("Test", DesktopActionLocation.Desktop));
        Assert.Equal(new DesktopActionOutcome.CreatedFolder("Test", DesktopActionLocation.Desktop), outcome);
        Assert.True(Directory.Exists(Path.Combine(redirectedDesktop, "Test")));
        Assert.False(Directory.Exists(Path.Combine(root, "Desktop", "Test")));
    }

    [Fact]
    public async Task MediaControlWithAValidActionSendsTheKeyAndReportsSuccess()
    {
        var outcome = await MakeRunner().PerformAsync(new DesktopAction.MediaControl("next"));
        Assert.Equal(new DesktopActionOutcome.MediaControlled("next"), outcome);
        Assert.Equal(["next"], recorder.MediaKeys);
    }

    [Fact]
    public async Task MediaControlWithAnInvalidActionDoesNotSendAndReportsFailure()
    {
        var outcome = await MakeRunner().PerformAsync(new DesktopAction.MediaControl("shuffle"));
        Assert.IsType<DesktopActionOutcome.Failed>(outcome);
        Assert.Empty(recorder.MediaKeys);
    }

    [Fact]
    public async Task ScrollingGoesToTheWindowInFrontAndSaysWhere()
    {
        var runner = MakeRunner();
        Assert.Equal(new DesktopActionOutcome.Scrolled(ScrollDirection.Down, ScrollAmount.Page), await runner.PerformAsync(new DesktopAction.Scroll(ScrollDirection.Down, ScrollAmount.Page)));
        Assert.Equal(new DesktopActionOutcome.Scrolled(ScrollDirection.Up, ScrollAmount.End), await runner.PerformAsync(new DesktopAction.Scroll(ScrollDirection.Up, ScrollAmount.End)));
        Assert.Equal(["down page", "up end"], recorder.Scrolls);
        Assert.Equal("Scrolled to the top.", new DesktopActionOutcome.Scrolled(ScrollDirection.Up, ScrollAmount.End).SpokenSentence);
        Assert.Equal("Scrolled down a little.", new DesktopActionOutcome.Scrolled(ScrollDirection.Down, ScrollAmount.Little).SpokenSentence);
        Assert.Equal("Scrolled right.", new DesktopActionOutcome.Scrolled(ScrollDirection.Right, ScrollAmount.Page).SpokenSentence);
        Assert.StartsWith("Scrolled down. Say only a word or two", new DesktopActionOutcome.Scrolled(ScrollDirection.Down, ScrollAmount.Page).ToolOutput);

        recorder.ScrollResultToGive = ScrollResult.NoWindow;
        Assert.Equal(new DesktopActionOutcome.NothingToScroll(), await runner.PerformAsync(new DesktopAction.Scroll(ScrollDirection.Down, ScrollAmount.Page)));
        recorder.ScrollResultToGive = ScrollResult.NotAllowed;
        Assert.Equal(new DesktopActionOutcome.InputBlocked(), await runner.PerformAsync(new DesktopAction.Scroll(ScrollDirection.Down, ScrollAmount.Page)));
    }

    /// <summary>"Open downloads" went to reveal_in_finder as a folder called Downloads inside Downloads,
    /// which does not exist (the Mac, 2026-10-29).</summary>
    [Fact]
    public async Task OpeningAFolderOpensTheLocationItselfOrAFolderInIt()
    {
        Directory.CreateDirectory(Path.Combine(root, "Downloads"));
        Directory.CreateDirectory(Path.Combine(root, "Desktop", "Riti"));
        File.WriteAllText(Path.Combine(root, "Desktop", "notes.txt"), "x");
        var runner = MakeRunner();

        Assert.Equal(new DesktopActionOutcome.OpenedFolder(null, DesktopActionLocation.Downloads),
                     await runner.PerformAsync(new DesktopAction.OpenFolder(null, DesktopActionLocation.Downloads)));
        Assert.Equal(new DesktopActionOutcome.OpenedFolder("Riti", DesktopActionLocation.Desktop),
                     await runner.PerformAsync(new DesktopAction.OpenFolder("Riti", DesktopActionLocation.Desktop)));
        Assert.Equal(new DesktopActionOutcome.NothingToReveal("Missing", DesktopActionLocation.Desktop),
                     await runner.PerformAsync(new DesktopAction.OpenFolder("Missing", DesktopActionLocation.Desktop)));
        // A file is not a folder to open.
        Assert.Equal(new DesktopActionOutcome.NothingToReveal("notes.txt", DesktopActionLocation.Desktop),
                     await runner.PerformAsync(new DesktopAction.OpenFolder("notes.txt", DesktopActionLocation.Desktop)));
        Assert.Equal(new DesktopActionOutcome.InvalidName(), await runner.PerformAsync(new DesktopAction.OpenFolder(@"..\Windows", DesktopActionLocation.Desktop)));
        // The workspace before anything was made in it.
        Assert.IsType<DesktopActionOutcome.Failed>(await runner.PerformAsync(new DesktopAction.OpenFolder(null, DesktopActionLocation.Workspace)));
        Assert.Equal([Path.Combine(root, "Downloads"), Path.Combine(root, "Desktop", "Riti")], recorder.OpenedFolders);
        Assert.Equal("Opened your Downloads folder.", new DesktopActionOutcome.OpenedFolder(null, DesktopActionLocation.Downloads).SpokenSentence);
        Assert.Equal("Opened Riti on your Desktop.", new DesktopActionOutcome.OpenedFolder("Riti", DesktopActionLocation.Desktop).SpokenSentence);
        Assert.True(new DesktopActionOutcome.OpenedFolder(null, DesktopActionLocation.Downloads).ThenLooksAtScreen);
    }

    [Fact]
    public async Task FindingFilesSelectsTheBestMatchAndNeverLogsNames()
    {
        var runner = MakeRunner();
        Assert.Equal(new DesktopActionOutcome.NoFilesFound("tax return"), await runner.PerformAsync(new DesktopAction.FindFiles("tax return")));
        Assert.Empty(recorder.RevealedPaths);
        Assert.Equal("find_files: nothing found", new DesktopActionOutcome.NoFilesFound("tax return").LogDescription);

        IReadOnlyList<string> files = [@"C:\Users\tester\Documents\Tax return 2025.pdf", @"C:\Users\tester\Desktop\tax return draft.docx"];
        recorder.FilesToFind = files;
        var outcome = await runner.PerformAsync(new DesktopAction.FindFiles("tax return"));
        Assert.Equal(new DesktopActionOutcome.FoundFiles("tax return", files), outcome);
        Assert.Equal([files[0]], recorder.RevealedPaths);
        Assert.Equal(["tax return", "tax return"], recorder.FileSearches);
        Assert.Equal("Found 2 matches; Tax return 2025.pdf is selected in File Explorer.", outcome.SpokenSentence);
        Assert.Equal("Found Riti.txt; it's selected in File Explorer.", new DesktopActionOutcome.FoundFiles("riti", [@"C:\Riti.txt"]).SpokenSentence);
        Assert.Contains("tax return draft.docx — in Desktop", outcome.ToolOutput);
        Assert.DoesNotContain("Tax", outcome.LogDescription);
        Assert.Equal("find_files: 2 found", outcome.LogDescription);
        Assert.True(outcome.ThenLooksAtScreen);
        Assert.True(outcome.DidSomething);
    }

    [Fact]
    public async Task FindingOnThePageTypesOnlyIntoAFindBar()
    {
        var runner = MakeRunner();
        Assert.Equal(new DesktopActionOutcome.SearchedPage("price"), await runner.PerformAsync(new DesktopAction.FindOnPage("price")));
        recorder.FindOnPageResultToGive = FindOnPageResult.NoFindField;
        Assert.Equal(new DesktopActionOutcome.NoFindBar(), await runner.PerformAsync(new DesktopAction.FindOnPage("price")));
        recorder.FindOnPageResultToGive = FindOnPageResult.NoApp;
        Assert.Equal(new DesktopActionOutcome.NothingInFront(), await runner.PerformAsync(new DesktopAction.FindOnPage("price")));
        recorder.FindOnPageResultToGive = FindOnPageResult.NotAllowed;
        Assert.Equal(new DesktopActionOutcome.InputBlocked(), await runner.PerformAsync(new DesktopAction.FindOnPage("price")));
        Assert.Equal(["price", "price", "price", "price"], recorder.PageSearches);
        Assert.DoesNotContain("secret", new DesktopActionOutcome.SearchedPage("secret words").LogDescription);
        Assert.StartsWith("This app didn't open a find bar, so I didn't type anything. Do not try again", new DesktopActionOutcome.NoFindBar().ToolOutput);
    }

    /// <summary>"Show me that tab" was answered with how to click the tab strip (2026-10-29).</summary>
    [Fact]
    public async Task SwitchingTabsSaysWhichTabAndLooks()
    {
        var runner = MakeRunner();
        var outcome = await runner.PerformAsync(new DesktopAction.SwitchTab(new BrowserTabTarget.Newest()));
        Assert.Equal(new DesktopActionOutcome.SwitchedTab("Cars & Bids"), outcome);
        Assert.True(outcome.ThenLooksAtScreen);
        Assert.True(outcome.DidSomething);
        Assert.DoesNotContain("Cars", outcome.LogDescription);
        recorder.TabSwitchResultToGive = new BrowserTabSwitchResult.NoSuchTab();
        Assert.Equal(new DesktopActionOutcome.NoSuchTab(), await runner.PerformAsync(new DesktopAction.SwitchTab(new BrowserTabTarget.Named("reddit"))));
        recorder.TabSwitchResultToGive = new BrowserTabSwitchResult.NoBrowser();
        Assert.Equal(new DesktopActionOutcome.NoBrowserOpen(), await runner.PerformAsync(new DesktopAction.SwitchTab(new BrowserTabTarget.Next())));
        recorder.TabSwitchResultToGive = new BrowserTabSwitchResult.NotAllowed();
        Assert.Equal(new DesktopActionOutcome.InputBlocked(), await runner.PerformAsync(new DesktopAction.SwitchTab(new BrowserTabTarget.First())));
        Assert.Equal([new BrowserTabTarget.Newest(), new BrowserTabTarget.Named("reddit"), new BrowserTabTarget.Next(), new BrowserTabTarget.First()], recorder.TabTargets);
    }

    [Fact]
    public async Task AToolCallIsParsedPerformedAndAnythingElseIsLeftAlone()
    {
        var runner = MakeRunner();
        Assert.Equal(new DesktopActionOutcome.VolumeSet(30), await runner.PerformToolCallAsync("set_volume", new JsonObject { ["level"] = 30 }));
        Assert.Equal(new DesktopActionOutcome.UnknownLocation("icloud"),
                     await runner.PerformToolCallAsync("create_folder", new JsonObject { ["name"] = "x", ["location"] = "icloud" }));
        Assert.Null(await runner.PerformToolCallAsync("send_to_agent", new JsonObject { ["task"] = "x" }));
    }
}

public class DesktopActionParsingTests
{
    private static DesktopActionParseResult Parse(string toolName, JsonObject arguments) => DesktopAction.Parse(toolName, arguments);

    private static DesktopActionParseResult.Action Action(DesktopAction action) => new(action);

    [Fact]
    public void ParsesEachToolIntoItsAction()
    {
        Assert.Equal(Action(new DesktopAction.OpenApp("Spotify")), Parse("open_app", new JsonObject { ["name"] = "Spotify" }));
        Assert.Equal(Action(new DesktopAction.OpenUrl("https://example.com")), Parse("open_url", new JsonObject { ["url"] = "https://example.com" }));
        Assert.Equal(Action(new DesktopAction.CreateFolder("Test", DesktopActionLocation.Desktop)),
                     Parse("create_folder", new JsonObject { ["name"] = "Test", ["location"] = "desktop" }));
        Assert.Equal(Action(new DesktopAction.RevealInExplorer("Test", DesktopActionLocation.Downloads)),
                     Parse("reveal_in_explorer", new JsonObject { ["name"] = "Test", ["location"] = "downloads" }));
        Assert.Equal(Action(new DesktopAction.MediaControl("next")), Parse("media_control", new JsonObject { ["action"] = "next" }));
    }

    [Fact]
    public void TheMovingAroundToolsParseTheirArguments()
    {
        Assert.Equal(Action(new DesktopAction.Scroll(ScrollDirection.Down, ScrollAmount.Page)), Parse("scroll", new JsonObject { ["direction"] = "Down" }));
        Assert.Equal(Action(new DesktopAction.Scroll(ScrollDirection.Up, ScrollAmount.End)), Parse("scroll", new JsonObject { ["direction"] = "up", ["amount"] = "end" }));
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.Failed("I need up, down, left or right")),
                     Parse("scroll", new JsonObject { ["direction"] = "sideways" }));
        Assert.Equal(Action(new DesktopAction.OpenFolder(null, DesktopActionLocation.Downloads)), Parse("open_folder", new JsonObject { ["location"] = "downloads" }));
        Assert.Equal(Action(new DesktopAction.OpenFolder("Projects", DesktopActionLocation.Documents)),
                     Parse("open_folder", new JsonObject { ["location"] = "documents", ["name"] = "Projects" }));
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.UnknownLocation("icloud")), Parse("open_folder", new JsonObject { ["location"] = "icloud" }));
        Assert.Equal(Action(new DesktopAction.FindFiles("tax return")), Parse("find_files", new JsonObject { ["query"] = "tax return" }));
        Assert.Equal(Action(new DesktopAction.FindOnPage("price")), Parse("find_on_page", new JsonObject { ["text"] = "price" }));
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidQuery()), Parse("find_on_page", new JsonObject { ["text"] = new string('a', 101) }));
        Assert.Equal(Action(new DesktopAction.FindOnPage(new string('a', 100))), Parse("find_on_page", new JsonObject { ["text"] = new string('a', 100) }));
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidQuery()), Parse("find_on_page", new JsonObject { ["text"] = "a\nb" }));
        Assert.Equal(new DesktopActionParseResult.NotAFastAction(), Parse("click", new JsonObject { ["x"] = 1, ["y"] = 2 }));
        Assert.Equal(Action(new DesktopAction.SwitchTab(new BrowserTabTarget.Newest())), Parse("switch_tab", new JsonObject { ["which"] = "newest" }));
        Assert.Equal(Action(new DesktopAction.SwitchTab(new BrowserTabTarget.Named("cars and bids"))),
                     Parse("switch_tab", new JsonObject { ["which"] = "named", ["name"] = "cars and bids" }));
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.NoSuchTab()), Parse("switch_tab", new JsonObject { ["which"] = "named" }));
        Assert.Equal(Action(new DesktopAction.SwitchTab(new BrowserTabTarget.Next())), Parse("switch_tab", new JsonObject { ["which"] = "Next" }));
        // No `which` but a name: the name.
        Assert.Equal(Action(new DesktopAction.SwitchTab(new BrowserTabTarget.Named("youtube"))), Parse("switch_tab", new JsonObject { ["name"] = "youtube" }));
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.NoSuchTab()), Parse("switch_tab", new JsonObject { ["which"] = "last-ish" }));
    }

    [Fact]
    public void TheMacRevealToolNameIsStillAccepted()
    {
        Assert.Equal(Action(new DesktopAction.RevealInExplorer("Test", DesktopActionLocation.Downloads)),
                     Parse("reveal_in_finder", new JsonObject { ["name"] = "Test", ["location"] = "downloads" }));
        Assert.Contains("reveal_in_finder", DesktopAction.ToolNames);
    }

    [Fact]
    public void AcceptsTheNumberOrTheNumericStringTheModelSends()
    {
        // The Realtime model sends integers, but occasionally quotes them.
        Assert.Equal(Action(new DesktopAction.SetVolume(40)), Parse("set_volume", new JsonObject { ["level"] = 40 }));
        Assert.Equal(Action(new DesktopAction.SetVolume(40)), Parse("set_volume", new JsonObject { ["level"] = "40" }));
        Assert.Equal(Action(new DesktopAction.SetVolume(40)), Parse("set_volume", new JsonObject { ["level"] = 40.6 }));
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.Failed("I need a volume between 0 and 100")),
                     Parse("set_volume", new JsonObject { ["level"] = "loud" }));
    }

    [Fact]
    public void AMissingOrUnknownLocationIsAnAnswerNotAGuess()
    {
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.UnknownLocation("icloud")),
                     Parse("create_folder", new JsonObject { ["name"] = "Test", ["location"] = "icloud" }));
        // No location at all: the workspace is the one place that needs no permission.
        Assert.Equal(Action(new DesktopAction.CreateFolder("Test", DesktopActionLocation.Workspace)),
                     Parse("create_folder", new JsonObject { ["name"] = "Test" }));

        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.UnknownLocation("icloud")),
                     Parse("reveal_in_explorer", new JsonObject { ["name"] = "Test", ["location"] = "icloud" }));
        Assert.Equal(Action(new DesktopAction.RevealInExplorer("Test", DesktopActionLocation.Workspace)),
                     Parse("reveal_in_explorer", new JsonObject { ["name"] = "Test" }));
    }

    [Fact]
    public void OpenAppRejectsAnAppNameThatEscapesTheAppLookup()
    {
        var invalid = new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidAppName());
        Assert.Equal(invalid, Parse("open_app", new JsonObject { ["name"] = "../System/Applications/Calculator" }));
        Assert.Equal(invalid, Parse("open_app", new JsonObject { ["name"] = @"..\Windows\System32\calc" }));
        Assert.Equal(invalid, Parse("open_app", new JsonObject { ["name"] = @"C:\Windows\System32\cmd.exe" }));
        Assert.Equal(invalid, Parse("open_app", new JsonObject { ["name"] = "com.spotify.client:evil" }));
        Assert.Equal(invalid, Parse("open_app", new JsonObject { ["name"] = ".hidden" }));
    }

    [Fact]
    public void OthersToolsAreLeftAlone()
    {
        Assert.Equal(new DesktopActionParseResult.NotAFastAction(), Parse("send_to_agent", new JsonObject { ["task"] = "x" }));
        Assert.Equal(new DesktopActionParseResult.NotAFastAction(), Parse("point_at", []));
    }

    [Fact]
    public void StringArgumentsAndRealtimeJsonTextParseTheSameWay()
    {
        Assert.Equal(Action(new DesktopAction.SetVolume(40)),
                     DesktopAction.Parse("set_volume", new Dictionary<string, string> { ["level"] = "40" }));
        Assert.Equal(Action(new DesktopAction.CreateFolder("Test", DesktopActionLocation.Desktop)),
                     DesktopAction.ParseJson("create_folder", """{"name":"Test","location":"desktop"}"""));
        // Unreadable arguments are no arguments: open_app then has no name to open.
        Assert.Equal(new DesktopActionParseResult.BadArguments(new DesktopActionOutcome.InvalidAppName()),
                     DesktopAction.ParseJson("open_app", "{not json"));
    }
}

public class FastActionToolDefinitionTests
{
    [Fact]
    public void EveryFastActionIsOfferedToTheModel()
    {
        var names = DesktopAction.RealtimeToolDefinitions().Select(definition => definition["name"]!.GetValue<string>());
        Assert.Equal(["open_app", "open_url", "create_folder", "reveal_in_explorer", "set_volume", "media_control",
                      "web_search", "minimize_all", "check_email", "draft_email", "play_music", "restore_windows",
                      "scroll", "open_folder", "find_files", "find_on_page", "switch_tab"], names);
    }

    [Fact]
    public void CreateFolderDeclaresTheClosedLocationSet()
    {
        var definition = DesktopAction.RealtimeToolDefinitions().Single(tool => tool["name"]!.GetValue<string>() == "create_folder");
        var parameters = definition["parameters"]!.AsObject();
        var location = parameters["properties"]!["location"]!.AsObject();
        Assert.Equal(["desktop", "downloads", "documents", "workspace", "home"], location["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
        // Only the name is required; an unspecified location means the workspace.
        Assert.Equal(["name"], parameters["required"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public void TheDescriptionsNameWindowsThingsNotMacOnes()
    {
        var descriptions = string.Join("\n", DesktopAction.RealtimeToolDefinitions().Select(tool => tool["description"]!.GetValue<string>()));
        Assert.DoesNotContain("Mac", descriptions);
        Assert.DoesNotContain("Finder", descriptions);
        Assert.Contains("File Explorer", descriptions);
    }

    [Fact]
    public void TheAnthropicDefinitionsCarryTheSameSchemas()
    {
        var realtime = DesktopAction.RealtimeToolDefinitions();
        var anthropic = DesktopAction.AnthropicToolDefinitions();
        Assert.Equal(realtime.Count, anthropic.Count);
        for (var index = 0; index < realtime.Count; index++)
        {
            Assert.Equal(realtime[index]["name"]!.GetValue<string>(), anthropic[index]["name"]!.GetValue<string>());
            Assert.Equal(realtime[index]["description"]!.GetValue<string>(), anthropic[index]["description"]!.GetValue<string>());
            Assert.Equal(realtime[index]["parameters"]!.ToJsonString(), anthropic[index]["input_schema"]!.ToJsonString());
            Assert.Null(anthropic[index]["type"]);
        }
        // They fit the ask lane's hook.
        var tools = AskLanePrompts.AskTools(offersQuickActions: true, hasWritingTarget: false, anthropic);
        Assert.Equal(17, tools.Count);
    }

    /// <summary>
    /// Nothing else keeps the schema's argument keys and Parse's argument keys in agreement: a
    /// divergence there does not fail loudly, it just makes Parse take its "argument absent" branch
    /// and hand the runner an empty name or URL. This walks every definition's own `properties` keys,
    /// builds arguments from them, and asserts Parse turns those exact values into the matching action.
    /// </summary>
    [Fact]
    public void EveryDeclaredArgumentKeyParsesIntoTheMatchingAction()
    {
        foreach (var definition in DesktopAction.RealtimeToolDefinitions())
        {
            var toolName = definition["name"]!.GetValue<string>();
            var properties = definition["parameters"]!["properties"]!.AsObject();
            var arguments = new JsonObject();
            foreach (var (key, propertySchema) in properties)
            {
                if (propertySchema!["enum"] is JsonArray enumValues) arguments[key] = enumValues[0]!.GetValue<string>();
                else if (propertySchema["type"]!.GetValue<string>() == "integer") arguments[key] = 55;
                else arguments[key] = "Test Value";
            }

            var result = DesktopAction.Parse(toolName, arguments);
            var parsed = Assert.IsType<DesktopActionParseResult.Action>(result);
            switch (parsed.Value)
            {
                case DesktopAction.OpenApp openApp:
                    Assert.Equal(arguments["name"]!.GetValue<string>(), openApp.Name);
                    break;
                case DesktopAction.OpenUrl openUrl:
                    Assert.Equal(arguments["url"]!.GetValue<string>(), openUrl.Raw);
                    break;
                case DesktopAction.CreateFolder createFolder:
                    Assert.Equal(arguments["name"]!.GetValue<string>(), createFolder.Name);
                    Assert.Equal(arguments["location"]!.GetValue<string>(), createFolder.Location.RawValue());
                    break;
                case DesktopAction.RevealInExplorer reveal:
                    Assert.Equal(arguments["name"]!.GetValue<string>(), reveal.Name);
                    Assert.Equal(arguments["location"]!.GetValue<string>(), reveal.Location.RawValue());
                    break;
                case DesktopAction.SetVolume setVolume:
                    Assert.Equal(arguments["level"]!.GetValue<int>(), setVolume.Level);
                    break;
                case DesktopAction.MediaControl mediaControl:
                    Assert.Equal(arguments["action"]!.GetValue<string>(), mediaControl.Action);
                    break;
                case DesktopAction.WebSearch webSearch:
                    Assert.Equal(arguments["query"]!.GetValue<string>(), webSearch.Query);
                    Assert.Equal(arguments["site"]!.GetValue<string>(), webSearch.Site.RawValue());
                    break;
                case DesktopAction.MinimizeAll minimizeAll:
                    Assert.Equal(arguments["keep"]!.GetValue<string>(), minimizeAll.KeepApp);
                    break;
                case DesktopAction.RestoreWindows restoreWindows:
                    Assert.Equal(arguments["app"]!.GetValue<string>(), restoreWindows.App);
                    break;
                case DesktopAction.CheckEmail checkEmail:
                    Assert.Equal(arguments["app"]!.GetValue<string>(), checkEmail.App);
                    break;
                case DesktopAction.DraftEmail draftEmail:
                    Assert.Equal(arguments["to"]!.GetValue<string>(), draftEmail.To);
                    Assert.Equal(arguments["subject"]!.GetValue<string>(), draftEmail.Subject);
                    Assert.Equal(arguments["body"]!.GetValue<string>(), draftEmail.Body);
                    Assert.Equal(arguments["app"]!.GetValue<string>(), draftEmail.App);
                    break;
                case DesktopAction.PlayMusic playMusic:
                    Assert.Equal(arguments["query"]!.GetValue<string>(), playMusic.Query);
                    break;
                case DesktopAction.Scroll scroll:
                    Assert.Equal(arguments["direction"]!.GetValue<string>(), scroll.Direction.RawValue());
                    Assert.Equal(arguments["amount"]!.GetValue<string>(), scroll.Amount.RawValue());
                    break;
                case DesktopAction.OpenFolder openFolder:
                    Assert.Equal(arguments["name"]!.GetValue<string>(), openFolder.Name);
                    Assert.Equal(arguments["location"]!.GetValue<string>(), openFolder.Location.RawValue());
                    break;
                case DesktopAction.FindFiles findFiles:
                    Assert.Equal(arguments["query"]!.GetValue<string>(), findFiles.Query);
                    break;
                case DesktopAction.FindOnPage findOnPage:
                    Assert.Equal(arguments["text"]!.GetValue<string>(), findOnPage.Text);
                    break;
                case DesktopAction.SwitchTab switchTab:
                    Assert.Equal("named", arguments["which"]!.GetValue<string>());
                    Assert.Equal(new BrowserTabTarget.Named(arguments["name"]!.GetValue<string>()), switchTab.Target);
                    break;
                default:
                    Assert.Fail($"{toolName} parsed into an unexpected action");
                    break;
            }
        }
    }
}

/// <summary>The plan round trip from PlanToolProtocolTests.swift, with the real quick-action definitions.</summary>
public class DesktopActionPlanToolTests
{
    private static readonly IReadOnlyList<AnswerRequest.Tool> QuickActionTools =
        DesktopAction.AnthropicToolDefinitions().Select(ClaudeAPI.AnswerTool).OfType<AnswerRequest.Tool>().ToList();

    private static readonly IReadOnlySet<string> QuickActionToolNames = QuickActionTools.Select(tool => tool.Name).ToHashSet();

    [Fact]
    public void InstructionBlockNamesEveryRealToolWithItsDescriptionAndSchema()
    {
        var block = PlanToolProtocol.InstructionBlock(QuickActionTools);
        Assert.Equal(17, QuickActionTools.Count);
        foreach (var tool in QuickActionTools)
        {
            Assert.Contains($"- {tool.Name}: {tool.Description}", block);
            Assert.Contains(PlanToolProtocol.CompactSchemaText(tool.InputSchemaJson), block);
        }
    }

    [Fact]
    public void APlanActionLineParsesAsADesktopAction()
    {
        var (text, toolCalls) = PlanToolProtocol.Parse(
            "making that folder on your desktop now.\n[ACTION: create_folder {\"name\":\"test-plan-actions\",\"location\":\"desktop\"}]",
            QuickActionToolNames);
        Assert.Equal("making that folder on your desktop now.", text);
        var toolCall = Assert.Single(toolCalls);
        Assert.Equal(new ClaudeToolCall("plan-action-1", "create_folder",
                                        new Dictionary<string, string> { ["name"] = "test-plan-actions", ["location"] = "desktop" }), toolCall);
        // The call reads exactly as DesktopAction expects.
        Assert.Equal(new DesktopActionParseResult.Action(new DesktopAction.CreateFolder("test-plan-actions", DesktopActionLocation.Desktop)),
                     DesktopAction.Parse(toolCall.Name, toolCall.Arguments));
    }

    [Fact]
    public void APlanVolumeComesBackAsAStringAndStillParses()
    {
        var (_, toolCalls) = PlanToolProtocol.Parse("louder.\n[ACTION: set_volume {\"level\":40}]", QuickActionToolNames);
        var toolCall = Assert.Single(toolCalls);
        Assert.Equal(new DesktopActionParseResult.Action(new DesktopAction.SetVolume(40)), DesktopAction.Parse(toolCall.Name, toolCall.Arguments));
    }
}
