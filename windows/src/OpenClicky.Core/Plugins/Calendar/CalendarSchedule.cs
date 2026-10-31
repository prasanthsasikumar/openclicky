//
//  CalendarSchedule.cs
//  OpenClicky (Windows port of Plugins/Calendar/CalendarSchedule.swift)
//
//  The calendar column's pure parts: the week the island shows (today and the six days after it),
//  which events land on which day, the dots under a day's number, the calendars grouped by the
//  account they belong to, and how an event's time reads. No Windows calendar API here, so all of
//  it is unit-tested with plain values; CalendarStore turns the platform's appointments into these.
//

using System.Globalization;

namespace OpenClicky.Core.Plugins.Calendar;

/// <summary>A calendar's colour as plain sRGB components (0…1), so events and calendars compare as values.</summary>
public readonly record struct CalendarColor(double Red, double Green, double Blue)
{
    public static CalendarColor Grey { get; } = new(0.56, 0.56, 0.58);

    public static CalendarColor FromBytes(byte red, byte green, byte blue) => new(red / 255.0, green / 255.0, blue / 255.0);
}

/// <summary>One occurrence of an event. A repeating event has one item per occurrence, so <c>Id</c> adds the start.</summary>
/// <param name="EventId">The platform's id for the appointment (its LocalId on Windows), to open it.</param>
public sealed record CalendarEventItem(
    string Id,
    string EventId,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    bool IsRecurring,
    string CalendarId,
    CalendarColor Color);

/// <summary>One day of the week strip: its midnight and the visible events that touch it.</summary>
public sealed record CalendarDay(DateTimeOffset Start, IReadOnlyList<CalendarEventItem> Events)
{
    public IReadOnlyList<CalendarColor> DotColors => CalendarSchedule.DotColors(Events);

    public bool Equals(CalendarDay? other) => other is not null && Start == other.Start && Events.SequenceEqual(other.Events);
    public override int GetHashCode() => HashCode.Combine(Start, Events.Count);
}

/// <summary>One calendar as Settings lists it.</summary>
/// <param name="SourceTitle">The account it belongs to ("Outlook", a Google address).</param>
/// <param name="AllowsNewEvents">False for subscribed and read-only calendars.</param>
public sealed record CalendarInfo(string Id, string Title, CalendarColor Color, string SourceTitle, bool AllowsNewEvents);

/// <summary>The calendars of one account, as one Settings section.</summary>
public sealed record CalendarSourceGroup(string Title, IReadOnlyList<CalendarInfo> Calendars)
{
    public bool Equals(CalendarSourceGroup? other) => other is not null && Title == other.Title && Calendars.SequenceEqual(other.Calendars);
    public override int GetHashCode() => Title.GetHashCode();
}

public static class CalendarSchedule
{
    /// <summary>Today and the six days after it.</summary>
    public const int DayCount = 7;
    /// <summary>A day shows at most this many dots under its number, however busy it is.</summary>
    public const int MaximumDotCount = 3;

    /// <summary>Midnight of a calendar date in <paramref name="zone"/>.</summary>
    public static DateTimeOffset StartOfDay(DateTime date, TimeZoneInfo zone)
    {
        var midnight = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        // A zone whose clocks jump at midnight has no such moment: the day starts when the clocks land.
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(30);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    /// <summary>The local calendar date of a moment in <paramref name="zone"/>.</summary>
    public static DateTime LocalDate(DateTimeOffset moment, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(moment, zone).Date;

    /// <summary>Midnight of today and of each of the following days, in <paramref name="zone"/>.</summary>
    public static IReadOnlyList<DateTimeOffset> DayStarts(DateTimeOffset now, TimeZoneInfo zone, int count = DayCount)
    {
        var today = LocalDate(now, zone);
        return Enumerable.Range(0, count).Select(offset => StartOfDay(today.AddDays(offset), zone)).ToList();
    }

    /// <summary>The midnight after <paramref name="dayStart"/>.</summary>
    public static DateTimeOffset DayEnd(DateTimeOffset dayStart, TimeZoneInfo zone) => StartOfDay(LocalDate(dayStart, zone).AddDays(1), zone);

    /// <summary>The span the calendar is asked about: from the first day's midnight to the midnight after the last day.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? FetchInterval(IReadOnlyList<DateTimeOffset> dayStarts, TimeZoneInfo zone)
    {
        if (dayStarts.Count == 0) return null;
        return (dayStarts[0], DayEnd(dayStarts[^1], zone));
    }

    /// <summary>The events of each day, without the hidden calendars' events. An event that runs over several days (a trip,
    /// a conference) is listed on every day it touches. All-day events come first, then by start time, then by title so
    /// the order never flickers between fetches.</summary>
    public static IReadOnlyList<CalendarDay> Days(IReadOnlyList<CalendarEventItem> events, IReadOnlyList<DateTimeOffset> dayStarts,
                                                 IReadOnlySet<string> hiddenCalendarIds, TimeZoneInfo zone)
    {
        var visible = events.Where(item => !hiddenCalendarIds.Contains(item.CalendarId)).ToList();
        return dayStarts.Select(dayStart =>
        {
            var dayEnd = DayEnd(dayStart, zone);
            var onThisDay = visible.Where(item => EventTouchesDay(item, dayStart, dayEnd)).ToList();
            onThisDay.Sort(ListOrder);
            return new CalendarDay(dayStart, onThisDay);
        }).ToList();
    }

    /// <summary>An event touches a day when it overlaps it. An event with no length (a reminder-like 10:00–10:00) touches
    /// the day its start falls on.</summary>
    public static bool EventTouchesDay(CalendarEventItem item, DateTimeOffset dayStart, DateTimeOffset dayEnd)
    {
        if (item.End <= item.Start) return item.Start >= dayStart && item.Start < dayEnd;
        return item.Start < dayEnd && item.End > dayStart;
    }

    private static int ListOrder(CalendarEventItem first, CalendarEventItem second)
    {
        if (first.IsAllDay != second.IsAllDay) return first.IsAllDay ? -1 : 1;
        var byStart = first.Start.CompareTo(second.Start);
        if (byStart != 0) return byStart;
        return CompareNames(first.Title, second.Title);
    }

    /// <summary>One dot per event in its calendar's colour, at most three: the dots say "busy", not how busy.</summary>
    public static IReadOnlyList<CalendarColor> DotColors(IReadOnlyList<CalendarEventItem> events) =>
        events.Take(MaximumDotCount).Select(item => item.Color).ToList();

    /// <summary>The calendars grouped by account, accounts and calendars in alphabetical order, so Settings shows one
    /// section per account.</summary>
    public static IReadOnlyList<CalendarSourceGroup> SourceGroups(IReadOnlyList<CalendarInfo> calendars)
    {
        var bySource = calendars.GroupBy(calendar => calendar.SourceTitle).ToList();
        bySource.Sort((first, second) => CompareNames(first.Key, second.Key));
        return bySource.Select(group =>
        {
            var sorted = group.ToList();
            sorted.Sort((first, second) => CompareNames(first.Title, second.Title));
            return new CalendarSourceGroup(group.Key, sorted);
        }).ToList();
    }

    /// <summary>True once the event is over; the list dims it.</summary>
    public static bool IsPast(CalendarEventItem item, DateTimeOffset now) => item.End <= now;

    /// <summary>How the event's time reads under its title: "All day", or "9:30 – 10:00" in the person's own clock style.
    /// A timed event that covers the whole of this day (the middle of a trip) reads as all day too.</summary>
    public static string TimeText(CalendarEventItem item, DateTimeOffset dayStart, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var dayEnd = DayEnd(dayStart, zone);
        if (item.IsAllDay || (item.Start <= dayStart && item.End >= dayEnd)) return "All day";
        string Format(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, zone).ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        if (item.End <= item.Start) return Format(item.Start);
        return $"{Format(item.Start)} – {Format(item.End)}";
    }

    /// <summary>Names as a person sorts them: ignoring case and accents.</summary>
    private static int CompareNames(string first, string second) =>
        string.Compare(first, second, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
}
