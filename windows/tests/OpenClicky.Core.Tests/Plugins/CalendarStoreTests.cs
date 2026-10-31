// Ports CalendarStoreTests.swift: the week the column shows, which events land on which day, the dots under a day,
// hidden calendars, the calendars grouped by account and how a time reads; and, new on Windows, the store with a fake
// calendar (the access it reports, the switch and the hidden calendars). The Mac's quick add and its links into
// Calendar have no Windows counterpart, so their tests are not ported.

using System.Globalization;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Calendar;

namespace OpenClicky.Core.Tests.Plugins;

public class CalendarStoreTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("New Zealand Standard Time");

    private static DateTimeOffset Date(int year, int month, int day, int hour = 0, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    private static readonly CalendarColor Red = new(1, 0, 0);
    private static readonly CalendarColor Green = new(0, 1, 0);
    private static readonly CalendarColor Blue = new(0, 0, 1);
    private static readonly CalendarColor Grey = new(0.5, 0.5, 0.5);

    private static CalendarEventItem Event(string title, DateTimeOffset start, DateTimeOffset end, string calendarId = "work", CalendarColor? color = null,
                                           bool isAllDay = false) =>
        new($"{title}@{start.UtcTicks}", $"{title}-id", title, start, end, isAllDay, false, calendarId, color ?? Red);

    [Fact]
    public void TheWeekIsTodayAndTheSixDaysAfterItFromMidnight()
    {
        var starts = CalendarSchedule.DayStarts(Date(2026, 10, 30, 15, 42), Zone);
        Assert.Equal(7, starts.Count);
        Assert.Equal(Date(2026, 10, 30), starts[0]);
        Assert.Equal(Date(2026, 11, 5), starts[^1]);
        var interval = CalendarSchedule.FetchInterval(starts, Zone);
        Assert.Equal(Date(2026, 10, 30), interval?.Start);
        Assert.Equal(Date(2026, 11, 6), interval?.End);
    }

    [Fact]
    public void TheWeekKeepsWholeDaysAcrossADaylightSavingChange()
    {
        // New Zealand's clocks go back on 5 April 2026: that day is 25 hours long.
        var starts = CalendarSchedule.DayStarts(Date(2026, 4, 4, 10), Zone);
        Assert.Equal(Date(2026, 4, 5), starts[1]);
        Assert.Equal(Date(2026, 4, 6), starts[2]);
        Assert.Equal(TimeSpan.FromHours(25), starts[2] - starts[1]);
    }

    [Fact]
    public void EventsLandOnEveryDayTheyTouchInListOrder()
    {
        var starts = CalendarSchedule.DayStarts(Date(2026, 10, 30, 8), Zone);
        var standup = Event("Standup", Date(2026, 10, 30, 9), Date(2026, 10, 30, 9, 15));
        var lunch = Event("Lunch", Date(2026, 10, 30, 12), Date(2026, 10, 30, 13));
        var holiday = Event("Holiday", Date(2026, 10, 30), Date(2026, 10, 31), isAllDay: true);
        var trip = Event("Trip", Date(2026, 10, 31, 18), Date(2026, 11, 2, 10));
        var endsAtMidnight = Event("Late", Date(2026, 10, 30, 23), Date(2026, 10, 31));
        var reminder = Event("Ping", Date(2026, 11, 1, 7), Date(2026, 11, 1, 7));

        var days = CalendarSchedule.Days([lunch, trip, standup, reminder, endsAtMidnight, holiday], starts, new HashSet<string>(), Zone);

        Assert.Equal(7, days.Count);
        Assert.Equal(["Holiday", "Standup", "Lunch", "Late"], days[0].Events.Select(item => item.Title));
        Assert.Equal(["Trip"], days[1].Events.Select(item => item.Title));
        Assert.Equal(["Trip", "Ping"], days[2].Events.Select(item => item.Title));
        Assert.Equal(["Trip"], days[3].Events.Select(item => item.Title));
        Assert.Empty(days[4].Events);
    }

    [Fact]
    public void DotsAreOnePerEventInItsCalendarsColourAndAtMostThree()
    {
        var start = Date(2026, 10, 30, 9);
        CalendarEventItem[] events =
        [
            Event("A", start, start.AddMinutes(10), color: Red),
            Event("B", start.AddMinutes(1), start.AddMinutes(10), color: Green),
            Event("C", start.AddMinutes(2), start.AddMinutes(10), color: Green),
            Event("D", start.AddMinutes(3), start.AddMinutes(10), color: Blue),
        ];
        Assert.Equal([Red, Green, Green], CalendarSchedule.DotColors(events));
        Assert.Equal([Red], CalendarSchedule.DotColors(events[..1]));
        Assert.Empty(CalendarSchedule.DotColors([]));
        Assert.Equal(3, new CalendarDay(Date(2026, 10, 30), events).DotColors.Count);
    }

    [Fact]
    public void HiddenCalendarsLeaveTheListAndTheDots()
    {
        var starts = CalendarSchedule.DayStarts(Date(2026, 10, 30, 8), Zone);
        var work = Event("Review", Date(2026, 10, 30, 10), Date(2026, 10, 30, 11), calendarId: "work", color: Red);
        var birthday = Event("Birthday", Date(2026, 10, 30), Date(2026, 10, 31), calendarId: "birthdays", color: Grey, isAllDay: true);

        var allVisible = CalendarSchedule.Days([work, birthday], starts, new HashSet<string>(), Zone);
        Assert.Equal(["Birthday", "Review"], allVisible[0].Events.Select(item => item.Title));

        var birthdaysHidden = CalendarSchedule.Days([work, birthday], starts, new HashSet<string> { "birthdays" }, Zone);
        Assert.Equal(["Review"], birthdaysHidden[0].Events.Select(item => item.Title));
        Assert.Equal([Red], birthdaysHidden[0].DotColors);

        var everythingHidden = CalendarSchedule.Days([work, birthday], starts, new HashSet<string> { "work", "birthdays" }, Zone);
        Assert.All(everythingHidden, day => Assert.Empty(day.Events));
        Assert.Equal(7, everythingHidden.Count);
    }

    [Fact]
    public void CalendarsAreGroupedByAccountInAlphabeticalOrder()
    {
        CalendarInfo[] calendars =
        [
            new("1", "Work", Red, "Outlook", true),
            new("2", "family", Green, "Outlook", true),
            new("3", "Team", Blue, "me@example.com", true),
            new("4", "Birthdays", Grey, "Other", false),
        ];
        var groups = CalendarSchedule.SourceGroups(calendars);
        Assert.Equal(["me@example.com", "Other", "Outlook"], groups.Select(group => group.Title));
        Assert.Equal(["family", "Work"], groups[2].Calendars.Select(calendar => calendar.Title));
        Assert.Equal(["3"], groups[0].Calendars.Select(calendar => calendar.Id));
        Assert.Empty(CalendarSchedule.SourceGroups([]));
    }

    [Fact]
    public void TimesReadAsAllDayOrAStartAndEnd()
    {
        var dayStart = Date(2026, 10, 30);
        var english = new CultureInfo("en-NZ");
        var holiday = Event("Holiday", dayStart, Date(2026, 10, 31), isAllDay: true);
        Assert.Equal("All day", CalendarSchedule.TimeText(holiday, dayStart, Zone, english));

        var trip = Event("Trip", Date(2026, 10, 29, 18), Date(2026, 11, 1, 10));
        Assert.Equal("All day", CalendarSchedule.TimeText(trip, dayStart, Zone, english));

        var review = Event("Review", Date(2026, 10, 30, 9, 30), Date(2026, 10, 30, 10));
        Assert.Equal("09:30 – 10:00", CalendarSchedule.TimeText(review, dayStart, Zone, new CultureInfo("en-GB")));
        var ping = Event("Ping", Date(2026, 10, 30, 7), Date(2026, 10, 30, 7));
        Assert.Equal("07:00", CalendarSchedule.TimeText(ping, dayStart, Zone, new CultureInfo("en-GB")));
    }

    [Fact]
    public void PastEventsAreTheOnesThatHaveEnded()
    {
        var review = Event("Review", Date(2026, 10, 30, 9), Date(2026, 10, 30, 10));
        Assert.False(CalendarSchedule.IsPast(review, Date(2026, 10, 30, 9, 30)));
        Assert.True(CalendarSchedule.IsPast(review, Date(2026, 10, 30, 10)));
    }

    // MARK: - The store

    [Fact]
    public void TheSettingsKeepTheMacsKeysAndDefaults()
    {
        var preferences = Preferences.InMemory();
        var settings = new CalendarSettings(preferences);
        Assert.True(settings.IsEnabled);
        Assert.Empty(settings.HiddenCalendarIds);
        settings.SetCalendarVisible("b", false);
        settings.SetCalendarVisible("a", false);
        Assert.Equal(["a", "b"], preferences.GetStringArray("openclicky.calendar.hidden"));
        Assert.False(settings.IsCalendarVisible("a"));
        settings.SetCalendarVisible("a", true);
        Assert.Equal(["b"], preferences.GetStringArray("openclicky.calendar.hidden"));
        settings.IsEnabled = false;
        Assert.False(preferences.GetBool("openclicky.calendar.enabled", true));
    }

    [Fact]
    public async Task TheStoreReadsOnlyWhileWatchedAndFollowsHiddenCalendars()
    {
        var preferences = Preferences.InMemory();
        var now = Date(2026, 10, 30, 8);
        var source = new FakeCalendar
        {
            Result = new CalendarReadResult(CalendarAccess.Allowed,
                [new CalendarInfo("work", "Work", Red, "Outlook", true), new CalendarInfo("home", "Home", Green, "Outlook", true)],
                [Event("Review", Date(2026, 10, 30, 10), Date(2026, 10, 30, 11), "work"), Event("Dinner", Date(2026, 10, 30, 19), Date(2026, 10, 30, 21), "home", Green)]),
        };
        using var store = new CalendarStore(preferences, source, () => now, () => Zone, (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
        Assert.Equal(CalendarAccess.Unknown, store.Access);
        Assert.Equal(7, store.Days.Count);
        Assert.Equal(0, source.Reads);

        store.BeginWatching();
        await Task.Yield();
        Assert.Equal(1, source.Reads);
        Assert.Equal(CalendarAccess.Allowed, store.Access);
        Assert.Equal(2, store.Calendars.Count);
        Assert.Equal(["Review", "Dinner"], store.Days[0].Events.Select(item => item.Title));
        Assert.Equal((Date(2026, 10, 30), Date(2026, 11, 6)), source.LastSpan);

        new CalendarSettings(preferences).SetCalendarVisible("home", false);
        Assert.Equal(["Review"], store.Days[0].Events.Select(item => item.Title));
        Assert.Equal(1, source.Reads);

        store.EndWatching();
        Assert.All(store.Days, day => Assert.Empty(day.Events));
    }

    [Fact]
    public async Task ARefusedCalendarIsReportedAndAskingAgainOpensThePrivacySettings()
    {
        var preferences = Preferences.InMemory();
        var source = new FakeCalendar { Result = CalendarReadResult.Without(CalendarAccess.Denied) };
        using var store = new CalendarStore(preferences, source, () => Date(2026, 10, 30, 8), () => Zone, (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
        store.BeginWatching();
        await Task.Yield();
        Assert.Equal(CalendarAccess.Denied, store.Access);
        Assert.All(store.Days, day => Assert.Empty(day.Events));
        source.Result = CalendarReadResult.Without(CalendarAccess.Allowed);
        await store.RequestAccessAsync();
        Assert.Equal(1, source.PrivacyOpened);
        Assert.Equal(CalendarAccess.Allowed, store.Access);

        source.Throws = true;
        await store.RefreshAsync();
        Assert.Equal(CalendarAccess.Unavailable, store.Access);
    }

    private sealed class FakeCalendar : ICalendarSource
    {
        public CalendarReadResult Result { get; set; } = CalendarReadResult.Without(CalendarAccess.Allowed);
        public bool Throws { get; set; }
        public int Reads { get; private set; }
        public int PrivacyOpened { get; private set; }
        public (DateTimeOffset, DateTimeOffset)? LastSpan { get; private set; }

        public Task<CalendarReadResult> ReadAsync(DateTimeOffset start, DateTimeOffset end)
        {
            Reads++;
            LastSpan = (start, end);
            if (Throws) throw new InvalidOperationException("the store went away");
            return Task.FromResult(Result);
        }

        public void OpenPrivacySettings() => PrivacyOpened++;
        public void Open(CalendarEventItem item) { }
    }
}
