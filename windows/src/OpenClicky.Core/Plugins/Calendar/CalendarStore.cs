//
//  CalendarStore.cs
//  OpenClicky (Windows port of Plugins/Calendar/CalendarStore.swift)
//
//  The calendar column's one store: the week's events per day, the calendars and which of them
//  are hidden. It reads the calendar only while the calendar is on and a view is watching (the
//  island's column, the Settings page), again every five minutes and when the day changes while it
//  watches. The Mac reads EventKit; Windows reads its appointment store
//  (OpenClicky.Windows/Plugins/Calendar), which an app installed without a package may not be
//  given at all: then the column says so ("Unavailable") rather than failing. Adding events from
//  the island is the Mac's alone for now: Windows only lets a packaged app write appointments.
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.Calendar;

/// <summary>What the app may do with the calendar.</summary>
public enum CalendarAccess
{
    /// <summary>Not read yet.</summary>
    Unknown,
    Allowed,
    /// <summary>Turned off in Settings → Privacy → Calendar (for this app or every app).</summary>
    Denied,
    /// <summary>Windows gives this kind of app no calendar at all (an app installed without a package).</summary>
    Unavailable,
}

/// <summary>One read of the calendar: what may be read, the calendars, and the events in the span asked for.</summary>
public sealed record CalendarReadResult(CalendarAccess Access, IReadOnlyList<CalendarInfo> Calendars, IReadOnlyList<CalendarEventItem> Events)
{
    public static CalendarReadResult Without(CalendarAccess access) => new(access, [], []);
}

/// <summary>Where the events come from: Windows' appointments, or a test double.</summary>
public interface ICalendarSource
{
    /// <summary>The calendars and every event between <paramref name="start"/> and <paramref name="end"/>. Never throws for a
    /// missing permission: it reports it as the access.</summary>
    Task<CalendarReadResult> ReadAsync(DateTimeOffset start, DateTimeOffset end);

    /// <summary>The privacy settings' calendar page, for "Allow calendar access" once it was turned down.</summary>
    void OpenPrivacySettings();

    /// <summary>Shows the event in the calendar app (Windows can only bring the app forward).</summary>
    void Open(CalendarEventItem item);
}

/// <summary>The calendar's rows in Settings (Island › Calendar on the Mac), in the Mac's keys; <see cref="Observe"/> follows
/// any of them changing.</summary>
public sealed class CalendarSettings(Preferences preferences)
{
    public const string EnabledKey = IslandHomeLayout.CalendarEnabledKey;
    public const string HiddenCalendarsKey = "openclicky.calendar.hidden";

    public static IReadOnlyList<string> Keys { get; } = [EnabledKey, HiddenCalendarsKey];

    /// <summary>"Calendar": your week and today's events on the open island. On by default.</summary>
    public bool IsEnabled
    {
        get => preferences.GetBool(EnabledKey, true);
        set => preferences.Set(EnabledKey, value);
    }

    /// <summary>The calendars the person switched off under "Calendars".</summary>
    public IReadOnlySet<string> HiddenCalendarIds
    {
        get => (preferences.GetStringArray(HiddenCalendarsKey) ?? []).ToHashSet(StringComparer.Ordinal);
        set => preferences.Set(HiddenCalendarsKey, value.Order(StringComparer.Ordinal).ToList());
    }

    public bool IsCalendarVisible(string calendarId) => !HiddenCalendarIds.Contains(calendarId);

    /// <summary>One calendar's switch under "Calendars".</summary>
    public void SetCalendarVisible(string calendarId, bool isVisible)
    {
        var hidden = HiddenCalendarIds.ToHashSet(StringComparer.Ordinal);
        var changed = isVisible ? hidden.Remove(calendarId) : hidden.Add(calendarId);
        if (changed) HiddenCalendarIds = hidden;
    }

    public IDisposable Observe(Action changed) => PreferenceObservations.ObserveAll(preferences, Keys, changed);
}

/// <summary>
/// One per app. Use it on the UI thread: reads come back on the thread that started them, and <see cref="Changed"/> is
/// raised there.
/// </summary>
public sealed class CalendarStore : IDisposable
{
    /// <summary>The Mac reads again when the calendar database changes; here a watched week is read every five minutes.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly CalendarSettings settings;
    private readonly ICalendarSource source;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<TimeZoneInfo> zone;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly IDisposable settingsObservation;
    /// <summary>Every fetched event, hidden calendars included, so hiding or showing a calendar redraws without asking again.</summary>
    private IReadOnlyList<CalendarEventItem> fetchedEvents = [];
    private int watchers;
    private CancellationTokenSource? refreshLoop;
    private int generation;

    public CalendarStore(Preferences preferences, ICalendarSource source, Func<DateTimeOffset>? clock = null, Func<TimeZoneInfo>? zone = null,
                         Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        settings = new CalendarSettings(preferences);
        this.source = source;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.zone = zone ?? (() => TimeZoneInfo.Local);
        this.delay = delay ?? Task.Delay;
        IsEnabled = settings.IsEnabled;
        HiddenCalendarIds = settings.HiddenCalendarIds;
        Days = EmptyWeek();
        settingsObservation = settings.Observe(SettingsDidChange);
    }

    public CalendarAccess Access { get; private set; } = CalendarAccess.Unknown;

    /// <summary>Today and the six days after it, each with its visible events. Empty days without access, so the week strip
    /// still draws.</summary>
    public IReadOnlyList<CalendarDay> Days { get; private set; }

    /// <summary>Every calendar, for Settings.</summary>
    public IReadOnlyList<CalendarInfo> Calendars { get; private set; } = [];

    public IReadOnlySet<string> HiddenCalendarIds { get; private set; }

    public bool IsEnabled { get; private set; }

    public bool IsWatching => watchers > 0;

    public TimeZoneInfo Zone => zone();

    /// <summary>The access, the days or the calendars changed.</summary>
    public event Action? Changed;

    /// <summary>A view that shows the calendar appeared: the first one reads it now and every five minutes.</summary>
    public void BeginWatching()
    {
        watchers++;
        if (watchers != 1) return;
        var cancellation = new CancellationTokenSource();
        refreshLoop = cancellation;
        _ = RefreshLoopAsync(cancellation.Token);
    }

    /// <summary>A view that showed the calendar went away: the last one lets go of the events.</summary>
    public void EndWatching()
    {
        watchers = Math.Max(0, watchers - 1);
        if (watchers > 0) return;
        refreshLoop?.Cancel();
        refreshLoop = null;
        generation++;
        fetchedEvents = [];
        RebuildDays();
    }

    /// <summary>"Allow calendar access": once it was turned down, the privacy settings; then read again.</summary>
    public async Task RequestAccessAsync()
    {
        if (Access == CalendarAccess.Denied) source.OpenPrivacySettings();
        await RefreshAsync();
    }

    public void Open(CalendarEventItem item) => source.Open(item);

    /// <summary>Reads the week again, but only while the calendar is on and a view is watching; otherwise the days are
    /// left empty. A read that outlives the last watcher (or another read) is dropped.</summary>
    public async Task RefreshAsync()
    {
        var dayStarts = CalendarSchedule.DayStarts(clock(), zone());
        if (!IsWatching || !IsEnabled || CalendarSchedule.FetchInterval(dayStarts, zone()) is not { } interval)
        {
            fetchedEvents = [];
            RebuildDays(dayStarts);
            return;
        }
        var mine = ++generation;
        CalendarReadResult result;
        try { result = await source.ReadAsync(interval.Start, interval.End); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"calendar: read failed ({error.GetType().Name})");
            result = CalendarReadResult.Without(CalendarAccess.Unavailable);
        }
        if (mine != generation) return;
        var accessChanged = result.Access != Access;
        Access = result.Access;
        var calendarsChanged = !result.Calendars.SequenceEqual(Calendars);
        Calendars = result.Calendars;
        fetchedEvents = result.Events;
        if (!RebuildDays(dayStarts) && (accessChanged || calendarsChanged)) Changed?.Invoke();
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RefreshAsync();
            // Wake at the next refresh, or at midnight if that comes first: the week starts on another day.
            var now = clock();
            var untilMidnight = CalendarSchedule.DayEnd(CalendarSchedule.DayStarts(now, zone(), 1)[0], zone()) - now + TimeSpan.FromSeconds(1);
            var wait = untilMidnight < RefreshInterval && untilMidnight > TimeSpan.Zero ? untilMidnight : RefreshInterval;
            try { await delay(wait, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    // MARK: - Hidden calendars and the switch

    private void SettingsDidChange()
    {
        var hidden = settings.HiddenCalendarIds;
        var isEnabled = settings.IsEnabled;
        var hiddenChanged = !hidden.SetEquals(HiddenCalendarIds);
        var enabledChanged = isEnabled != IsEnabled;
        if (!hiddenChanged && !enabledChanged) return;
        HiddenCalendarIds = hidden;
        IsEnabled = isEnabled;
        if (enabledChanged) _ = RefreshAsync();
        else if (!RebuildDays()) Changed?.Invoke();
    }

    /// <summary>Returns true when the days changed (and <see cref="Changed"/> was raised).</summary>
    private bool RebuildDays(IReadOnlyList<DateTimeOffset>? dayStarts = null)
    {
        var starts = dayStarts ?? CalendarSchedule.DayStarts(clock(), zone());
        var next = CalendarSchedule.Days(fetchedEvents, starts, HiddenCalendarIds, zone());
        if (next.SequenceEqual(Days)) return false;
        Days = next;
        Changed?.Invoke();
        return true;
    }

    private IReadOnlyList<CalendarDay> EmptyWeek() =>
        CalendarSchedule.DayStarts(clock(), zone()).Select(start => new CalendarDay(start, [])).ToList();

    public void Dispose()
    {
        settingsObservation.Dispose();
        refreshLoop?.Cancel();
        refreshLoop = null;
    }
}
