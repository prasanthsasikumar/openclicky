// Ports IslandToolsPluginTests.swift: the Timer, Notes and Clipboard plugins. The focus cycle's phases and rounds,
// pausing with a fixed clock, the notes file, and the clipboard list.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.ClipboardList;
using OpenClicky.Core.Plugins.FocusTimer;
using OpenClicky.Core.Plugins.Notes;

namespace OpenClicky.Core.Tests.Plugins;

public class FocusTimerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
    private static readonly FocusDurations Durations = new(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void RunClockBanksTimeAcrossPauses()
    {
        var clock = new RunClock();
        clock.Start(Start);
        Assert.Equal(TimeSpan.FromSeconds(90), clock.Elapsed(At(90)));
        clock.Pause(At(90));
        // Paused: time on the wall does not count.
        Assert.Equal(TimeSpan.FromSeconds(90), clock.Elapsed(At(1_000)));
        clock.Start(At(1_000));
        Assert.Equal(TimeSpan.FromSeconds(120), clock.Elapsed(At(1_030)));
        // Starting twice keeps the first start; pausing twice keeps the first pause.
        clock.Start(At(1_020));
        Assert.Equal(TimeSpan.FromSeconds(120), clock.Elapsed(At(1_030)));
        clock.Pause(At(1_030));
        clock.Pause(At(2_000));
        Assert.Equal(TimeSpan.FromSeconds(120), clock.Elapsed(At(5_000)));
        Assert.False(clock.IsRunning);
        clock.Reset();
        Assert.Equal(TimeSpan.Zero, clock.Elapsed(Start));
    }

    [Fact]
    public void FocusGivesWayToAShortBreakThenFocusAgain()
    {
        var cycle = new FocusCycle();
        cycle.Start(Start);
        Assert.Empty(cycle.Advance(At(24 * 60), Durations));
        Assert.Equal(TimeSpan.FromSeconds(60), cycle.Remaining(At(24 * 60), Durations));
        Assert.Equal([FocusPhase.ShortBreak], cycle.Advance(At(25 * 60), Durations));
        Assert.True(cycle.CompletedFocusRounds == 1 && cycle.RoundInSet == 2 && cycle.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), cycle.Remaining(At(25 * 60), Durations));
        Assert.Equal([FocusPhase.Focus], cycle.Advance(At(30 * 60), Durations));
        Assert.Equal(FocusPhase.Focus, cycle.Phase);
        Assert.Equal(1, cycle.CompletedFocusRounds);
    }

    [Fact]
    public void EveryFourthFocusRoundEndsInALongBreak()
    {
        var cycle = new FocusCycle();
        cycle.Start(Start);
        var entered = new List<FocusPhase>();
        var time = TimeSpan.Zero;
        // Three focus + break pairs, then the fourth focus round.
        for (var round = 0; round < 3; round++)
        {
            time += Durations.Focus;
            entered.AddRange(cycle.Advance(Start + time, Durations));
            time += Durations.ShortBreak;
            entered.AddRange(cycle.Advance(Start + time, Durations));
        }
        time += Durations.Focus;
        entered.AddRange(cycle.Advance(Start + time, Durations));
        Assert.Equal([FocusPhase.ShortBreak, FocusPhase.Focus, FocusPhase.ShortBreak, FocusPhase.Focus, FocusPhase.ShortBreak, FocusPhase.Focus, FocusPhase.LongBreak], entered);
        Assert.True(cycle.CompletedFocusRounds == 4 && cycle.RoundInSet == 1);
        time += Durations.LongBreak;
        Assert.Equal([FocusPhase.Focus], cycle.Advance(Start + time, Durations));
        // The next set of four starts over.
        time += Durations.Focus;
        Assert.Equal([FocusPhase.ShortBreak], cycle.Advance(Start + time, Durations));
    }

    [Fact]
    public void ALateTickCarriesTheOvershootIntoTheNextPhase()
    {
        var cycle = new FocusCycle();
        cycle.Start(Start);
        // 25 min focus + 5 min break + 2 min: one sleep through two changes.
        var late = At(32 * 60);
        Assert.Equal([FocusPhase.ShortBreak, FocusPhase.Focus], cycle.Advance(late, Durations));
        Assert.Equal(TimeSpan.FromMinutes(23), cycle.Remaining(late, Durations));
    }

    [Fact]
    public void PausingStopsTheCountdownAndResumingPicksUpWhereItWas()
    {
        var cycle = new FocusCycle();
        cycle.Start(Start);
        cycle.Pause(At(10 * 60));
        var muchLater = At(3 * 3600);
        Assert.Empty(cycle.Advance(muchLater, Durations));
        Assert.Equal(TimeSpan.FromMinutes(15), cycle.Remaining(muchLater, Durations));
        Assert.False(cycle.IsRunning);
        cycle.Start(muchLater);
        Assert.Equal(TimeSpan.FromMinutes(10), cycle.Remaining(muchLater.AddMinutes(5), Durations));
        Assert.Equal([FocusPhase.ShortBreak], cycle.Advance(muchLater.AddMinutes(15), Durations));
    }

    [Fact]
    public void ResetGoesBackToTheFirstFocusRound()
    {
        var cycle = new FocusCycle();
        Assert.False(cycle.HasProgress);
        cycle.Start(Start);
        cycle.Advance(At(26 * 60), Durations);
        Assert.True(cycle.HasProgress);
        cycle.Reset();
        Assert.True(cycle.Phase == FocusPhase.Focus && cycle.CompletedFocusRounds == 0 && !cycle.IsRunning && !cycle.HasProgress);
        Assert.Equal(TimeSpan.FromMinutes(25), cycle.Remaining(Start, Durations));
    }

    [Fact]
    public void TheClockReadsLikeATimer()
    {
        Assert.Equal("25:00", TimerFormatter.Countdown(TimeSpan.FromMinutes(25)));
        Assert.Equal("0:01", TimerFormatter.Countdown(TimeSpan.FromSeconds(0.2)));
        Assert.Equal("0:00", TimerFormatter.Countdown(TimeSpan.Zero));
        Assert.Equal("12:04", TimerFormatter.Countdown(TimeSpan.FromSeconds(12 * 60 + 3.5)));
        Assert.Equal("1:02:03", TimerFormatter.Clock(TimeSpan.FromSeconds(3723.9)));
    }

    [Fact]
    public void MinutesComeFromSettingsWithDefaults()
    {
        var preferences = Preferences.InMemory();
        var settings = new TimerSettings(preferences);
        Assert.Equal(Durations, settings.Durations);
        var changes = 0;
        using var observation = settings.Observe(() => changes++);
        settings.FocusMinutes = 50;
        settings.BreakMinutes = 10;
        preferences.Set(TimerSettings.LongBreakMinutesKey, -3);
        Assert.Equal(new FocusDurations(TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)), settings.Durations);
        Assert.Equal(3, changes);
        Assert.Equal([5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60], TimerSettings.MinuteChoices);
        Assert.Contains(7, TimerSettings.ChoicesIncluding(7));
    }

    [Fact]
    public void TheStoreChimesOnAPhaseChangeAndWritesItsPeekLine()
    {
        var now = Start;
        var chimes = new List<FocusPhase>();
        var delays = new ManualTicks();
        var store = new TimerStore(() => now, () => Durations, chimes.Add, delays.Delay);
        Assert.Null(store.PeekLine);
        Assert.False(store.IsRunning);
        store.ToggleRunning();
        Assert.True(store.IsTicking);
        now = At(12 * 60 + 56);
        store.Tick();
        Assert.True(store.IsRunning);
        Assert.Equal("Focus 12:04", store.PeekLine);
        Assert.Empty(chimes);
        now = At(25 * 60 + 10);
        store.Tick();
        Assert.Equal([FocusPhase.ShortBreak], chimes);
        Assert.Equal("Break 4:50", store.PeekLine);
        store.ToggleRunning();
        Assert.False(store.IsRunning);
        Assert.Null(store.PeekLine);
        Assert.False(store.IsTicking);
        store.Mode = TimerMode.Stopwatch;
        store.ToggleRunning();
        now = now.AddSeconds(65);
        store.Tick();
        Assert.Equal("Stopwatch 1:05", store.PeekLine);
        store.ResetShownMode();
        Assert.False(store.IsRunning);
        Assert.Equal(TimeSpan.Zero, store.StopwatchElapsed);
    }

    /// <summary>Delays that never finish on their own: the store's loop waits until cancelled.</summary>
    private sealed class ManualTicks
    {
        public Task Delay(TimeSpan interval, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }
}

public class ClipboardHistoryTests
{
    [Fact]
    public void TheSameCopyAgainMovesToTheTopKeepingItsRow()
    {
        var history = new ClipboardHistory();
        history.Add(new ClipboardContent.Text("one"));
        history.Add(new ClipboardContent.Text("two"));
        var firstId = history.Entries[1].Id;
        history.Add(new ClipboardContent.Text("one"));
        Assert.Equal([new ClipboardContent.Text("one"), new ClipboardContent.Text("two")], history.Entries.Select(entry => entry.Content));
        Assert.Equal(firstId, history.Entries[0].Id);
    }

    [Fact]
    public void ItKeepsTheLastThirty()
    {
        var history = new ClipboardHistory();
        for (var index = 0; index < 40; index++) history.Add(new ClipboardContent.Text($"copy {index}"));
        Assert.Equal(ClipboardHistory.Capacity, history.Entries.Count);
        Assert.Equal(new ClipboardContent.Text("copy 39"), history.Entries[0].Content);
        Assert.Equal(new ClipboardContent.Text("copy 10"), history.Entries[^1].Content);
    }

    [Fact]
    public void PasswordManagerCopiesAreSkipped()
    {
        var history = new ClipboardHistory();
        Assert.False(history.Add(new ClipboardContent.Text("secret"), isTransient: true));
        Assert.Empty(history.Entries);
        Assert.True(history.Add(new ClipboardContent.Text("fine")));
        Assert.Single(history.Entries);
    }

    [Fact]
    public void FilesAndImagesCompareByWhatTheyHold()
    {
        var history = new ClipboardHistory();
        history.Add(new ClipboardContent.Files([@"C:\a.pdf", @"C:\b.pdf"]));
        history.Add(new ClipboardContent.Image([1, 2, 3], 2, 2));
        history.Add(new ClipboardContent.Files([@"c:\A.pdf", @"C:\b.pdf"]));
        history.Add(new ClipboardContent.Image([1, 2, 3], 2, 2));
        Assert.Equal(2, history.Entries.Count);
    }

    [Fact]
    public void RowsReadAsOneShortLine()
    {
        Assert.Equal("first line", new ClipboardContent.Text("  first   line\nsecond").Label);
        Assert.Equal("example.com/a?b=1", new ClipboardContent.Link(new Uri("https://example.com/a?b=1")).Label);
        Assert.Equal("a.pdf and 1 more", new ClipboardContent.Files([@"C:\tmp\a.pdf", @"C:\tmp\b.pdf"]).Label);
        Assert.Equal("Image 1200 × 800", new ClipboardContent.Image([], 1200, 800).Label);
    }

    [Fact]
    public void OnlyASingleWebOrMailAddressCountsAsALink()
    {
        Assert.NotNull(ClipboardContent.LinkIn("https://example.com"));
        Assert.NotNull(ClipboardContent.LinkIn("mailto:someone@example.com"));
        Assert.Null(ClipboardContent.LinkIn("see https://example.com"));
        Assert.Null(ClipboardContent.LinkIn("file:///etc/hosts"));
        Assert.Null(ClipboardContent.LinkIn("hello"));
        Assert.Null(ClipboardContent.FromText("   "));
        Assert.IsType<ClipboardContent.Link>(ClipboardContent.FromText(" https://example.com "));
        Assert.Equal(ClipboardContent.MaxTextLength, ((ClipboardContent.Text)ClipboardContent.FromText(new string('x', 30_000))!).Value.Length);
    }

    [Fact]
    public void TheStoreReadsTheClipboardSkipsTransientCopiesAndForgetsWhenSwitchedOff()
    {
        var clipboard = new FakeClipboard();
        var store = new ClipboardStore(clipboard, (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
        clipboard.Copy(new ClipboardContent.Text("before"));
        store.SetEnabled(true);
        // What was on the clipboard before the switch went on is not news.
        store.Poll();
        Assert.Empty(store.History.Entries);
        clipboard.Copy(new ClipboardContent.Text("hello"));
        store.Poll();
        clipboard.Copy(new ClipboardContent.Text("hunter2"), transient: true);
        store.Poll();
        clipboard.Copy(ClipboardContent.FromText("https://example.com")!);
        store.Poll();
        Assert.Equal(["example.com", "hello"], store.History.Entries.Select(entry => entry.Content.Label));
        store.CopyBack(store.History.Entries[1]);
        Assert.Equal(new ClipboardContent.Text("hello"), clipboard.Content);
        store.Poll();
        Assert.Equal(["hello", "example.com"], store.History.Entries.Select(entry => entry.Content.Label));
        store.SetEnabled(false);
        Assert.Empty(store.History.Entries);
        Assert.False(store.IsPolling);
    }

    private sealed class FakeClipboard : IClipboardListSource
    {
        private bool isTransient;
        public ClipboardContent? Content { get; private set; }
        public uint SequenceNumber { get; private set; } = 1;

        public void Copy(ClipboardContent content, bool transient = false)
        {
            Content = content;
            isTransient = transient;
            SequenceNumber++;
        }

        public bool ContainsTransientContent() => isTransient;
        public ClipboardContent? Read() => Content;
        public void Write(ClipboardContent content) => Copy(content);
    }
}

public class NotesStoreTests
{
    [Fact]
    public async Task TypingIsWrittenOnceItPausesAndReadBackNextTime()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"notes-tests-{Guid.NewGuid():N}");
        try
        {
            var filePath = Path.Combine(directory, "Notes", "notes.txt");
            var store = new NotesStore(filePath, TimeSpan.FromMilliseconds(50));
            Assert.Equal("", store.Text);
            store.Update("milk");
            store.Update("milk, eggs");
            Assert.True(store.HasPendingSave);
            Assert.False(File.Exists(filePath));
            for (var attempt = 0; attempt < 80 && store.HasPendingSave; attempt++) await Task.Delay(25);
            Assert.Equal("milk, eggs", await File.ReadAllTextAsync(filePath));
            store.Update("milk, eggs, bread");
            store.SaveNow();
            Assert.Equal("milk, eggs, bread", new NotesStore(filePath).Text);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
