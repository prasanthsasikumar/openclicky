//
//  FocusTimer.cs
//  OpenClicky (Windows port of Plugins/Timer/FocusTimerMachine.swift, TimerStore.swift and TimerSettings.swift)
//
//  The Timer plugin: a focus cycle (focus, short break, focus, … with a long break in place of
//  every fourth short one) and a stopwatch. The arithmetic has no clock of its own: every call
//  takes "now", so the store can tick it from a real timer and the tests from fixed times. The
//  store lives outside the island's views, so a run keeps going (and chimes between phases) after
//  the island closes. The view is OpenClicky.Windows/Plugins/Timer.
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.FocusTimer;

/// <summary>The plugin's component: a focus timer with breaks, and a stopwatch.</summary>
public static class TimerPlugin
{
    public static IslandComponent Component { get; } = new(
        IslandPluginRegistry.TimerId, "Timer", "", 110, IsPlugin: true,
        SettingsDetail: "Focus in rounds with short breaks and a long one every fourth round, or run a stopwatch. A soft chime marks each change, even with the island closed.");
}

/// <summary>A run that can be paused and resumed: the time banked by earlier runs plus the current run. Keeping the start
/// instead of counting ticks means a late or skipped tick never loses time.</summary>
public sealed class RunClock
{
    public TimeSpan Banked { get; private set; }
    public DateTimeOffset? RunningSince { get; private set; }

    public bool IsRunning => RunningSince is not null;

    public TimeSpan Elapsed(DateTimeOffset now)
    {
        var running = RunningSince is { } since ? now - since : TimeSpan.Zero;
        return Banked + (running < TimeSpan.Zero ? TimeSpan.Zero : running);
    }

    public void Start(DateTimeOffset now)
    {
        if (RunningSince is not null) return;
        RunningSince = now;
    }

    public void Pause(DateTimeOffset now)
    {
        if (RunningSince is null) return;
        Banked = Elapsed(now);
        RunningSince = null;
    }

    public void Reset()
    {
        Banked = TimeSpan.Zero;
        RunningSince = null;
    }

    /// <summary>Takes <paramref name="amount"/> off the banked time, for a phase that ended part-way through a run: the
    /// overshoot belongs to the next phase.</summary>
    public void RemoveElapsed(TimeSpan amount) => Banked -= amount;
}

public enum FocusPhase
{
    Focus,
    ShortBreak,
    LongBreak,
}

public static class FocusPhases
{
    public static string Title(this FocusPhase phase) => phase switch
    {
        FocusPhase.ShortBreak => "Break",
        FocusPhase.LongBreak => "Long break",
        _ => "Focus",
    };
}

/// <summary>How long each phase lasts; read from the settings by the store on every tick, so a change applies to the phase
/// under way.</summary>
public sealed record FocusDurations(TimeSpan Focus, TimeSpan ShortBreak, TimeSpan LongBreak)
{
    public TimeSpan DurationOf(FocusPhase phase) => phase switch
    {
        FocusPhase.ShortBreak => ShortBreak,
        FocusPhase.LongBreak => LongBreak,
        _ => Focus,
    };
}

/// <summary>The focus cycle: which phase it is in, how many focus rounds are done, and the phase's clock.</summary>
public sealed class FocusCycle
{
    /// <summary>A long break comes after this many focus rounds.</summary>
    public const int RoundsPerLongBreak = 4;

    public FocusPhase Phase { get; private set; } = FocusPhase.Focus;
    /// <summary>Focus rounds finished since the last reset.</summary>
    public int CompletedFocusRounds { get; private set; }
    public RunClock Clock { get; } = new();

    public bool IsRunning => Clock.IsRunning;
    /// <summary>Something is on the clock or a round is done: there is a cycle to reset.</summary>
    public bool HasProgress => Clock.IsRunning || Clock.Banked > TimeSpan.Zero || CompletedFocusRounds > 0 || Phase != FocusPhase.Focus;

    /// <summary>The focus round under way or coming next, 1 to 4.</summary>
    public int RoundInSet => CompletedFocusRounds % RoundsPerLongBreak + 1;

    public TimeSpan Remaining(DateTimeOffset now, FocusDurations durations)
    {
        var left = durations.DurationOf(Phase) - Clock.Elapsed(now);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public void Start(DateTimeOffset now) => Clock.Start(now);
    public void Pause(DateTimeOffset now) => Clock.Pause(now);

    /// <summary>Back to the first focus round, stopped.</summary>
    public void Reset()
    {
        Phase = FocusPhase.Focus;
        CompletedFocusRounds = 0;
        Clock.Reset();
    }

    /// <summary>Moves past every phase that has run out by <paramref name="now"/> and returns the phases entered, in order
    /// (empty when the current one still has time). The next phase starts running at the moment the last one ended, so a PC
    /// that slept through a break wakes up in the right place.</summary>
    public IReadOnlyList<FocusPhase> Advance(DateTimeOffset now, FocusDurations durations)
    {
        var entered = new List<FocusPhase>();
        // A zero-length phase would loop forever; Settings never allows one, but guard anyway.
        var shortest = new[] { durations.Focus, durations.ShortBreak, durations.LongBreak }.Min();
        if (shortest <= TimeSpan.Zero) return entered;
        while (Clock.Elapsed(now) >= durations.DurationOf(Phase))
        {
            Clock.RemoveElapsed(durations.DurationOf(Phase));
            if (Phase == FocusPhase.Focus)
            {
                CompletedFocusRounds++;
                Phase = CompletedFocusRounds % RoundsPerLongBreak == 0 ? FocusPhase.LongBreak : FocusPhase.ShortBreak;
            }
            else
            {
                Phase = FocusPhase.Focus;
            }
            entered.Add(Phase);
        }
        return entered;
    }
}

/// <summary>"12:04", or "1:02:03" past an hour.</summary>
public static class TimerFormatter
{
    public static string Clock(TimeSpan time)
    {
        var total = Math.Max(0, (long)Math.Floor(time.TotalSeconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }

    /// <summary>A countdown shows the second it is in, so a fresh 25 minute phase reads 25:00 and the last second reads
    /// 0:01, never 0:00 while time is left.</summary>
    public static string Countdown(TimeSpan remaining) => Clock(TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds)));
}

public enum TimerMode
{
    Focus,
    Stopwatch,
}

public static class TimerModes
{
    public static string Title(this TimerMode mode) => mode == TimerMode.Stopwatch ? "Stopwatch" : "Focus";
}

/// <summary>How long each phase lasts, in minutes, in the Mac's keys (Settings → Island › Tools › Timer).</summary>
public sealed class TimerSettings(Preferences preferences)
{
    public const string FocusMinutesKey = "openclicky.timer.focusMinutes";
    public const string BreakMinutesKey = "openclicky.timer.breakMinutes";
    public const string LongBreakMinutesKey = "openclicky.timer.longBreakMinutes";

    public const int DefaultFocusMinutes = 25;
    public const int DefaultBreakMinutes = 5;
    public const int DefaultLongBreakMinutes = 15;

    public static IReadOnlyList<string> Keys { get; } = [FocusMinutesKey, BreakMinutesKey, LongBreakMinutesKey];

    /// <summary>What the pickers offer: 5 to 60 in steps of 5.</summary>
    public static IReadOnlyList<int> MinuteChoices { get; } = Enumerable.Range(1, 12).Select(step => step * 5).ToList();

    /// <summary>The choices with <paramref name="current"/> added when it is outside them, so a stored value always shows.</summary>
    public static IReadOnlyList<int> ChoicesIncluding(int current) =>
        MinuteChoices.Contains(current) ? MinuteChoices : [.. MinuteChoices.Append(current).Order()];

    /// <summary>"Focus": how long each focus round lasts.</summary>
    public int FocusMinutes
    {
        get => Minutes(FocusMinutesKey, DefaultFocusMinutes);
        set => preferences.Set(FocusMinutesKey, value);
    }

    /// <summary>"Break": the short break after each focus round.</summary>
    public int BreakMinutes
    {
        get => Minutes(BreakMinutesKey, DefaultBreakMinutes);
        set => preferences.Set(BreakMinutesKey, value);
    }

    /// <summary>"Long break": the longer break after every fourth round.</summary>
    public int LongBreakMinutes
    {
        get => Minutes(LongBreakMinutesKey, DefaultLongBreakMinutes);
        set => preferences.Set(LongBreakMinutesKey, value);
    }

    public FocusDurations Durations => new(TimeSpan.FromMinutes(FocusMinutes), TimeSpan.FromMinutes(BreakMinutes), TimeSpan.FromMinutes(LongBreakMinutes));

    /// <summary>Calls <paramref name="changed"/> whenever a length changes; dispose to stop.</summary>
    public IDisposable Observe(Action changed) => PreferenceObservations.ObserveAll(preferences, Keys, changed);

    /// <summary>Unset reads as the default; anything outside 1…240 is treated as unset too.</summary>
    private int Minutes(string key, int fallback)
    {
        var stored = preferences.GetInt(key, 0);
        return stored is >= 1 and <= 240 ? stored : fallback;
    }
}

/// <summary>
/// The Timer plugin's one live instance. The tick runs only while a clock does, twice a second; it is a delay loop, so
/// use the store on the UI thread and <see cref="Changed"/> comes back there.
/// </summary>
public sealed class TimerStore
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.5);

    private readonly Func<DateTimeOffset> clock;
    private readonly Func<FocusDurations> durations;
    private readonly Action<FocusPhase> chime;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private CancellationTokenSource? ticking;
    private TimerMode mode = TimerMode.Focus;

    /// <param name="durations">Read on every tick; the app passes its <see cref="TimerSettings"/>.</param>
    /// <param name="chime">A short sound between phases (OpenClicky.Windows plays one).</param>
    public TimerStore(Func<DateTimeOffset>? clock = null, Func<FocusDurations>? durations = null, Action<FocusPhase>? chime = null,
                      Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.durations = durations ?? (() => new TimerSettings(Preferences.InMemory()).Durations);
        this.chime = chime ?? (_ => { });
        this.delay = delay ?? Task.Delay;
        Now = this.clock();
    }

    /// <summary>The time the views read; moved on by the tick while anything runs.</summary>
    public DateTimeOffset Now { get; private set; }

    public FocusCycle Cycle { get; } = new();
    public RunClock Stopwatch { get; } = new();

    /// <summary>The time, a phase, a mode or a run changed.</summary>
    public event Action? Changed;

    /// <summary>The mode the island shows; the other keeps running.</summary>
    public TimerMode Mode
    {
        get => mode;
        set
        {
            if (mode == value) return;
            mode = value;
            Changed?.Invoke();
        }
    }

    public FocusDurations Durations => durations();

    /// <summary>Either clock is running.</summary>
    public bool IsRunning => Cycle.IsRunning || Stopwatch.IsRunning;

    /// <summary>The shown mode's clock is running.</summary>
    public bool IsShownModeRunning => Mode == TimerMode.Focus ? Cycle.IsRunning : Stopwatch.IsRunning;

    /// <summary>The shown mode has something to reset.</summary>
    public bool ShownModeHasProgress => Mode == TimerMode.Focus ? Cycle.HasProgress : Stopwatch.Elapsed(Now) > TimeSpan.Zero;

    public TimeSpan FocusRemaining => Cycle.Remaining(Now, Durations);
    public TimeSpan StopwatchElapsed => Stopwatch.Elapsed(Now);

    public bool IsTicking => ticking is not null;

    /// <summary>One short line for elsewhere on the island ("Focus 12:04", "Break 3:10", "Stopwatch 4:31"): the running
    /// clock, the focus cycle first when both run; null when neither runs.</summary>
    public string? PeekLine
    {
        get
        {
            if (Cycle.IsRunning) return $"{Cycle.Phase.Title()} {TimerFormatter.Countdown(FocusRemaining)}";
            if (Stopwatch.IsRunning) return $"{TimerMode.Stopwatch.Title()} {TimerFormatter.Clock(StopwatchElapsed)}";
            return null;
        }
    }

    /// <summary>Starts or pauses the shown mode's clock.</summary>
    public void ToggleRunning()
    {
        Tick();
        var at = clock();
        if (Mode == TimerMode.Focus)
        {
            if (Cycle.IsRunning) Cycle.Pause(at);
            else Cycle.Start(at);
        }
        else
        {
            if (Stopwatch.IsRunning) Stopwatch.Pause(at);
            else Stopwatch.Start(at);
        }
        FollowRunning();
        Changed?.Invoke();
    }

    /// <summary>Stops the shown mode's clock and puts it back to the start.</summary>
    public void ResetShownMode()
    {
        if (Mode == TimerMode.Focus) Cycle.Reset();
        else Stopwatch.Reset();
        Now = clock();
        FollowRunning();
        Changed?.Invoke();
    }

    /// <summary>Reads the clock, moves the focus cycle past any phase that ran out, and chimes once if it did (several at
    /// once only after the PC slept; one chime is enough).</summary>
    public void Tick()
    {
        Now = clock();
        var entered = Cycle.Advance(Now, Durations);
        if (entered.Count > 0) chime(entered[^1]);
        Changed?.Invoke();
    }

    private void FollowRunning()
    {
        if (IsRunning && ticking is null)
        {
            var cancellation = new CancellationTokenSource();
            ticking = cancellation;
            _ = TickLoopAsync(cancellation.Token);
        }
        else if (!IsRunning && ticking is not null)
        {
            ticking.Cancel();
            ticking = null;
        }
    }

    private async Task TickLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await delay(TickInterval, cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (cancellationToken.IsCancellationRequested) return;
            Tick();
        }
    }
}
