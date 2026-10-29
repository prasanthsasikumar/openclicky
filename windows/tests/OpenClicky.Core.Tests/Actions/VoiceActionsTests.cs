// The fast lane's newer actions, ported from the Mac's MacActionsTests, WindowMinimizerTests,
// MusicLookupTests, EmailActionsTests and the prompt tests of 070fbd8: web search, minimize and
// restore, play music, pause and play on the player, check and draft email (never sent), act then
// look, and the instructions that do commands and teach questions.

using System.Text.Json.Nodes;
using OpenClicky.Core.Actions;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Actions;

public class VoiceActionsTests
{
    private sealed class FakeExecutor : IDesktopActionExecutor
    {
        public List<Uri> OpenedUrls { get; } = [];
        public List<string> MediaKeys { get; } = [];
        public List<PlayerCommand> PlayerCommands { get; } = [];
        public List<string> SpotifyUris { get; } = [];
        public bool PlayerTakesCommands { get; set; }
        public bool SpotifyInstalled { get; set; } = true;
        public bool SpotifyRunning { get; set; } = true;
        public SpotifyPlayResult PlayResult { get; set; } = SpotifyPlayResult.Playing;
        public Func<string?, int?> Minimize { get; set; } = _ => 3;
        public Func<string?, int?> Restore { get; set; } = _ => 3;

        public DesktopAppTarget? FindApplication(string name) => null;
        public bool LaunchApplication(DesktopAppTarget target) => true;
        public bool OpenUrl(Uri url) { OpenedUrls.Add(url); return true; }
        public string? KnownFolderPath(string location) => null;
        public bool RevealInExplorer(string path) => true;
        public bool SetVolume(int percent) => true;
        public int? GetVolume() => 50;
        public bool SendMediaKey(string action) { MediaKeys.Add(action); return true; }
        public Task<bool> ControlPlayerAsync(PlayerCommand command) { PlayerCommands.Add(command); return Task.FromResult(PlayerTakesCommands); }
        public int? MinimizeAllWindows(string? keepApp) => Minimize(keepApp);
        public int? RestoreMinimizedWindows(string? app) => Restore(app);
        public bool SpotifyIsInstalled() => SpotifyInstalled;
        public bool SpotifyIsRunning() => SpotifyRunning;
        public Task<bool> LaunchSpotifyAsync() => Task.FromResult(false);
        public Task<SpotifyPlayResult> PlaySpotifyTrackAsync(string trackUri, string trackName) { SpotifyUris.Add(trackUri); return Task.FromResult(PlayResult); }
        public bool OpenSpotifyUri(string uri) { SpotifyUris.Add(uri); return true; }
    }

    private static DesktopActionRunner Runner(FakeExecutor executor, DesktopActionExtras? extras = null) =>
        new(executor, Path.GetTempPath(), Path.GetTempPath(), extras: extras);

    private static DesktopActionExtras Tracks(params MusicTrack[] tracks) =>
        new() { LookUpTrack = _ => Task.FromResult<IReadOnlyList<MusicTrack>>(tracks) };

    // MARK: web search

    [Fact]
    public void EachSiteHasItsOwnSearchPageAndThePlusAndAmpersandSurvive()
    {
        Assert.Equal("https://www.google.com/search?q=rotary%20engines", WebSearchSite.Web.Url("rotary engines").AbsoluteUri);
        Assert.Equal("https://www.youtube.com/results?search_query=lo-fi", WebSearchSite.YouTube.Url("lo-fi").AbsoluteUri);
        Assert.Equal("https://www.google.com/search?q=c%2B%2B%20%26%20rust", WebSearchSite.Web.Url("c++ & rust").AbsoluteUri);
        Assert.StartsWith("https://www.google.com/maps/search/", WebSearchSite.Maps.Url("cafes").AbsoluteUri);
        Assert.Equal(WebSearchSite.Amazon, WebSearchSites.Named("Amazon"));
        Assert.Null(WebSearchSites.Named("bing"));
    }

    [Fact]
    public void ASearchNeedsUsableWordsAndAKnownSite()
    {
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("web_search", new JsonObject { ["query"] = "  " }));
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("web_search", new JsonObject { ["query"] = "a\nb" }));
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("web_search", new JsonObject { ["query"] = "x", ["site"] = "bing" }));
        var parsed = Assert.IsType<DesktopActionParseResult.Action>(DesktopAction.Parse("web_search", new JsonObject { ["query"] = "lo-fi", ["site"] = "youtube" }));
        Assert.Equal(new DesktopAction.WebSearch("lo-fi", WebSearchSite.YouTube), parsed.Value);
    }

    [Fact]
    public async Task ASearchOpensThePageAndThenLooksAtIt()
    {
        var executor = new FakeExecutor();
        var outcome = await Runner(executor).PerformAsync(new DesktopAction.WebSearch("rotary engines", WebSearchSite.Web));
        Assert.Equal(new DesktopActionOutcome.Searched("rotary engines", WebSearchSite.Web), outcome);
        Assert.Single(executor.OpenedUrls);
        Assert.True(outcome.ThenLooksAtScreen);
        Assert.Equal("Searching for rotary engines.", outcome.SpokenSentence);
        Assert.Contains("latest screenshot", outcome.ToolOutput);
    }

    // MARK: minimize and restore

    [Fact]
    public async Task MinimizeAndRestoreSayHowManyAndNameTheKeptApp()
    {
        var executor = new FakeExecutor { Minimize = keep => keep == "Slack" ? null : 4, Restore = app => app is null ? 4 : 0 };
        var runner = Runner(executor);
        Assert.Equal("Minimized 4 windows.", (await runner.PerformAsync(new DesktopAction.MinimizeAll(null))).SpokenSentence);
        Assert.Equal("Minimized 4 windows; Terminal stays up.", (await runner.PerformAsync(new DesktopAction.MinimizeAll("Terminal"))).SpokenSentence);
        var notRunning = await runner.PerformAsync(new DesktopAction.MinimizeAll("Slack"));
        Assert.Equal(new DesktopActionOutcome.WindowAppNotRunning("Slack"), notRunning);
        Assert.Contains("open_app", notRunning.ToolOutput);
        Assert.Equal("Brought 4 windows back.", (await runner.PerformAsync(new DesktopAction.RestoreWindows(null))).SpokenSentence);
        Assert.Equal("Terminal is in front now.", (await runner.PerformAsync(new DesktopAction.RestoreWindows("Terminal"))).SpokenSentence);
        Assert.Equal("Nothing to minimize.", new DesktopActionOutcome.MinimizedWindows(0, null).SpokenSentence);
        Assert.Equal("I hadn't minimized anything.", new DesktopActionOutcome.RestoredWindows(0, null).SpokenSentence);
    }

    [Fact]
    public void AnAppNameForMinimizeOrRestoreGetsOpenAppsChecks()
    {
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("minimize_all", new JsonObject { ["keep"] = "..\\cmd" }));
        var keep = Assert.IsType<DesktopActionParseResult.Action>(DesktopAction.Parse("minimize_all", new JsonObject { ["keep"] = "Terminal" }));
        Assert.Equal(new DesktopAction.MinimizeAll("Terminal"), keep.Value);
        var all = Assert.IsType<DesktopActionParseResult.Action>(DesktopAction.Parse("restore_windows", new JsonObject()));
        Assert.Equal(new DesktopAction.RestoreWindows(null), all.Value);
    }

    private static OnScreenWindowInfo Window(int processId, string title = "Doc", string className = "Chrome_WidgetWin_1", int width = 800,
                                             bool topMost = false, bool tool = false, bool cloaked = false) =>
        new(processId * 10, processId, "app.exe", title, className, new PixelRect(0, 0, width, 600), cloaked, false, topMost, tool, 0);

    [Fact]
    public void OnlyOrdinaryWindowsOfOtherAppsAreMinimized()
    {
        var onScreen = new[]
        {
            Window(1), Window(2), Window(3, title: ""), Window(4, className: "Shell_TrayWnd"), Window(5, width: 30),
            Window(6, topMost: true), Window(7, tool: true), Window(8, cloaked: true), Window(99),
        };
        var picked = WindowMinimizer.WindowsToMinimize(onScreen, ownProcessId: 99, keptProcessIds: new HashSet<int> { 2 });
        Assert.Equal([1], picked.Select(window => window.ProcessId));
    }

    [Fact]
    public void ASpokenAppNameMatchesExactlyFirstThenByWholeWords()
    {
        var running = new[]
        {
            new RunningAppName(1, "Windows Terminal", "WindowsTerminal.exe"),
            new RunningAppName(2, "Google Chrome", "chrome.exe"),
            new RunningAppName(3, "Notepad", "notepad.exe"),
            new RunningAppName(4, "Chromecast helper", "cast.exe"),
        };
        Assert.Equal(new HashSet<int> { 1 }, WindowMinimizer.ProcessIdsOfAppsNamed("the terminal", running));
        Assert.Equal(new HashSet<int> { 2 }, WindowMinimizer.ProcessIdsOfAppsNamed("Chrome", running));
        Assert.Equal(new HashSet<int> { 3 }, WindowMinimizer.ProcessIdsOfAppsNamed("notepad.exe", running));
        Assert.Equal(new HashSet<int> { 1 }, WindowMinimizer.ProcessIdsOfAppsNamed("WindowsTerminal", running));
        Assert.Empty(WindowMinimizer.ProcessIdsOfAppsNamed("Slack", running));
    }

    // MARK: pause and play

    /// <summary>"Stop that song" goes to the player: pause with nothing playing goes nowhere (the key would
    /// start something), play falls back to the key.</summary>
    [Fact]
    public async Task PauseAndPlayGoToThePlayerNeverThePlayPauseKey()
    {
        var executor = new FakeExecutor { PlayerTakesCommands = true };
        var runner = Runner(executor);
        Assert.Equal("Paused.", (await runner.PerformAsync(new DesktopAction.MediaControl("pause"))).SpokenSentence);
        Assert.Equal("Playing.", (await runner.PerformAsync(new DesktopAction.MediaControl("play"))).SpokenSentence);
        Assert.Empty(executor.MediaKeys);

        executor.PlayerTakesCommands = false;
        Assert.IsType<DesktopActionOutcome.Failed>(await runner.PerformAsync(new DesktopAction.MediaControl("pause")));
        Assert.Empty(executor.MediaKeys);
        Assert.IsType<DesktopActionOutcome.MediaControlled>(await runner.PerformAsync(new DesktopAction.MediaControl("play")));
        Assert.Equal(["playpause"], executor.MediaKeys);
    }

    // MARK: play music

    [Fact]
    public void ThePickSharesTheMostWordsWithWhatWasSaid()
    {
        var remix = new MusicTrack("spotify:track:remix", "Blinding Lights - Remix", ["Someone Else"]);
        var original = new MusicTrack("spotify:track:original", "Blinding Lights", ["The Weeknd"]);
        Assert.Equal(original, MusicTrackPicker.Pick([remix, original], "blinding lights by the weeknd"));
        Assert.Equal(remix, MusicTrackPicker.Pick([remix, original], "zzz"));
        Assert.Null(MusicTrackPicker.Pick([], "anything"));
    }

    [Fact]
    public void OwnCredentialsGoStraightToSpotifyElseTheBackend()
    {
        var backend = new Uri("https://api.example.com");
        Assert.Equal(new MusicLookupRoute.Spotify("id", "secret"), MusicLookupRoute.For(" id:secret ", backend));
        Assert.Equal(new MusicLookupRoute.Backend(backend), MusicLookupRoute.For("no-colon", backend));
        Assert.Equal(new MusicLookupRoute.Backend(backend), MusicLookupRoute.For(null, backend));
        Assert.IsType<MusicLookupRoute.None>(MusicLookupRoute.For(":secret", null));
    }

    [Fact]
    public void BothCatalogueRepliesDecodeIntoTracks()
    {
        var backend = MusicLookup.DecodeBackendResults("""{"tracks":[{"uri":"spotify:track:1","name":"Song","artists":["A","B"],"album":"x"}]}""");
        Assert.Equal([new MusicTrack("spotify:track:1", "Song", ["A", "B"])], backend);
        var spotify = MusicLookup.DecodeSpotifyResults("""{"tracks":{"items":[{"uri":"spotify:track:2","name":"Tune","artists":[{"name":"C"}]}]}}""");
        Assert.Equal([new MusicTrack("spotify:track:2", "Tune", ["C"])], spotify);
        Assert.Equal("spotify:search:blinding%20lights", MusicLookup.SpotifySearchUri("blinding lights"));
    }

    [Fact]
    public async Task AFoundTrackPlaysAndNothingFoundOpensSpotifysSearch()
    {
        var executor = new FakeExecutor();
        var track = new MusicTrack("spotify:track:abc", "Blinding Lights", ["The Weeknd"]);
        Assert.Equal(new DesktopActionOutcome.PlayingTrack("Blinding Lights", "The Weeknd"),
                     await Runner(executor, Tracks(track)).PerformAsync(new DesktopAction.PlayMusic("blinding lights")));
        Assert.Equal(["spotify:track:abc"], executor.SpotifyUris);

        // Opened but not confirmed: the sentence never claims a song that may not be playing.
        executor.PlayResult = SpotifyPlayResult.Opened;
        var opened = await Runner(executor, Tracks(track)).PerformAsync(new DesktopAction.PlayMusic("blinding lights"));
        Assert.Contains("press play", opened.SpokenSentence);

        var searchExecutor = new FakeExecutor();
        var noTrack = await Runner(searchExecutor).PerformAsync(new DesktopAction.PlayMusic("lo-fi beats"));
        Assert.Equal(new DesktopActionOutcome.OpenedSpotifySearch("lo-fi beats"), noTrack);
        Assert.Equal(["spotify:search:lo-fi%20beats"], searchExecutor.SpotifyUris);

        // The backend is not trusted with what Spotify is told to open: only a track URI plays.
        var oddExecutor = new FakeExecutor();
        await Runner(oddExecutor, Tracks(new MusicTrack("https://evil.example", "Song", []))).PerformAsync(new DesktopAction.PlayMusic("song"));
        Assert.StartsWith("spotify:search:", Assert.Single(oddExecutor.SpotifyUris));
    }

    [Fact]
    public async Task NoSpotifySaysSoAndAClosedOneThatWillNotStartSaysWait()
    {
        Assert.IsType<DesktopActionOutcome.SpotifyNotInstalled>(
            await Runner(new FakeExecutor { SpotifyInstalled = false }).PerformAsync(new DesktopAction.PlayMusic("x")));
        Assert.IsType<DesktopActionOutcome.SpotifyStarting>(
            await Runner(new FakeExecutor { SpotifyRunning = false }).PerformAsync(new DesktopAction.PlayMusic("x")));
    }

    // MARK: email

    [Fact]
    public void TheInboxIsTheOneNamedElseTheSettingElseGmail()
    {
        Assert.Equal(EmailApp.Outlook, EmailApps.Choose("Outlook", "gmail"));
        Assert.Equal(EmailApp.Outlook, EmailApps.Choose(null, "outlook"));
        Assert.Equal(EmailApp.Gmail, EmailApps.Choose(null, null));
        // The Mac's Apple Mail means nothing here.
        Assert.Equal(EmailApp.Gmail, EmailApps.Choose("mail", "mail"));
    }

    [Fact]
    public void TheComposePagesCarryEveryFieldEncoded()
    {
        var gmail = EmailApp.Gmail.ComposeUrl("ana@example.com", "Late & sorry", "Running late\n#sorry +10 min").AbsoluteUri;
        Assert.Equal("https://mail.google.com/mail/?view=cm&fs=1&to=ana%40example.com&su=Late%20%26%20sorry&body=Running%20late%0A%23sorry%20%2B10%20min", gmail);
        var outlook = EmailApp.Outlook.ComposeUrl("Ana", "Hi", "x").AbsoluteUri;
        Assert.StartsWith("https://outlook.live.com/mail/0/deeplink/compose?to=Ana&subject=Hi&body=x", outlook);
    }

    [Fact]
    public async Task CheckingOpensTheInboxAndDraftingNeverSends()
    {
        var executor = new FakeExecutor();
        var runner = Runner(executor);
        var check = await runner.PerformAsync(new DesktopAction.CheckEmail(null));
        Assert.Equal(new DesktopActionOutcome.OpenedInbox(EmailApp.Gmail), check);
        Assert.True(check.ThenLooksAtScreen);
        Assert.Contains("Unread rows are in bold", check.ToolOutput);

        var draft = await runner.PerformAsync(new DesktopAction.DraftEmail("ana@example.com", "Late", "I'll be late", "outlook"));
        Assert.Equal(new DesktopActionOutcome.DraftedEmail(EmailApp.Outlook, "ana@example.com", 12), draft);
        Assert.StartsWith("https://outlook.live.com/mail/0/deeplink/compose", executor.OpenedUrls[^1].AbsoluteUri);
        Assert.Contains("I don't send email", draft.SpokenSentence);
        Assert.Contains("you never send email", draft.ToolOutput);
        // The log never carries who it was for or what it said.
        Assert.DoesNotContain("ana@", draft.LogDescription);
        Assert.Contains("12-character body, address given", draft.LogDescription);
    }

    [Fact]
    public void ADraftNeedsSomeoneToWriteToAndSomethingToSay()
    {
        Assert.Equal(new DesktopActionOutcome.InvalidRecipient(),
                     Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("draft_email", new JsonObject { ["to"] = "<x>", ["body"] = "hi" })).Outcome);
        Assert.IsType<DesktopActionParseResult.BadArguments>(DesktopAction.Parse("draft_email", new JsonObject { ["to"] = "Ana", ["body"] = " " }));
        Assert.Equal(2_000, DesktopActionValidation.EmailBody(new string('a', 3_000)).Length);
        Assert.Null(DesktopActionValidation.Recipient(new string('a', 81)));
    }

    // MARK: act, then look

    [Fact]
    public void OnlyActionsThatOpenAPageLookAgain()
    {
        Assert.True(new DesktopActionOutcome.OpenedUrl("https://example.com").ThenLooksAtScreen);
        Assert.False(new DesktopActionOutcome.OpenedApp("Notepad").ThenLooksAtScreen);
        Assert.False(new DesktopActionOutcome.VolumeSet(10).ThenLooksAtScreen);
        Assert.False(new DesktopActionOutcome.MinimizedWindows(2, null).ThenLooksAtScreen);
    }

    /// <summary>The settle wait: up to 1.5 s for another app to come in front, then a second to paint, four in all.</summary>
    [Fact]
    public void TheScreenSettlesASecondAfterTheFrontChangesOrAfterTwoAndAHalfSeconds()
    {
        Assert.NotNull(RealtimeVoiceClient.SettleDelay(null, TimeSpan.FromSeconds(2.4)));
        Assert.Null(RealtimeVoiceClient.SettleDelay(null, TimeSpan.FromSeconds(2.5)));
        Assert.NotNull(RealtimeVoiceClient.SettleDelay(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.4)));
        Assert.Null(RealtimeVoiceClient.SettleDelay(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5)));
        Assert.Null(RealtimeVoiceClient.SettleDelay(TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(4)));
    }

    // MARK: the instructions

    /// <summary>A command is done, a question is taught, and email is never sent: in the Realtime prompt and
    /// in the ask lane's quick-actions line.</summary>
    [Fact]
    public void TheInstructionsDoCommandsAndTeachQuestions()
    {
        foreach (var tool in new[] { "minimize_all", "web_search", "play_music", "check_email", "draft_email", "restore_windows" })
        {
            Assert.Contains(tool, RealtimeVoiceClient.DefaultInstructions);
        }
        Assert.Contains(AskLanePrompts.CommandOrQuestionInstruction, RealtimeVoiceClient.DefaultInstructions);
        Assert.Contains("Do not explain how they could do it themselves", AskLanePrompts.CommandOrQuestionInstruction);
        Assert.Contains("never send email", AskLanePrompts.CommandOrQuestionInstruction);
        Assert.Contains(AskLanePrompts.CommandOrQuestionInstruction, AskLanePrompts.AccountToolsInstruction);
        Assert.Contains("check your email", AskLanePrompts.AccountToolsInstruction);
    }
}
