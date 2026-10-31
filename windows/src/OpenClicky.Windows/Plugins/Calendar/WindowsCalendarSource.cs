//
//  WindowsCalendarSource.cs
//  OpenClicky (Windows port of CalendarStore.swift's EventKit half)
//
//  The calendar column's events from Windows' appointment store (the calendars the Windows
//  Calendar and Mail apps and the accounts in Settings → Accounts sync), read only
//  (AppointmentManager.RequestStoreAsync(AllCalendarsReadOnly)). There is no prompt: Windows
//  either hands over the store or refuses, and refusing comes in two kinds, turned off in
//  Settings → Privacy → Calendar (UnauthorizedAccessException) or not offered to this kind of app
//  at all (anything else). Checked 2026-10-30: an unpackaged process on Windows 11 is given the
//  store. Calendars that only live in the new Outlook are not in it.
//

using System.Diagnostics;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Calendar;
using Windows.ApplicationModel.Appointments;

namespace OpenClicky.Windows.Plugins.Calendar;

internal sealed class WindowsCalendarSource : ICalendarSource
{
    private const string PrivacySettingsUri = "ms-settings:privacy-calendar";
    /// <summary>The Calendar app (or whatever handles the protocol); Windows offers no link to one appointment.</summary>
    private const string CalendarAppUri = "outlookcal:";
    private const uint MaximumAppointments = 400;

    private AppointmentStore? store;

    public async Task<CalendarReadResult> ReadAsync(DateTimeOffset start, DateTimeOffset end)
    {
        try
        {
            store ??= await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
        }
        catch (UnauthorizedAccessException)
        {
            return CalendarReadResult.Without(CalendarAccess.Denied);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"calendar: no appointment store ({error.GetType().Name}: {error.Message})");
            return CalendarReadResult.Without(CalendarAccess.Unavailable);
        }
        if (store is null) return CalendarReadResult.Without(CalendarAccess.Unavailable);

        try
        {
            var calendars = await store.FindAppointmentCalendarsAsync();
            var infos = new List<CalendarInfo>();
            var colours = new Dictionary<string, CalendarColor>(StringComparer.Ordinal);
            foreach (var calendar in calendars)
            {
                var colour = CalendarColor.FromBytes(calendar.DisplayColor.R, calendar.DisplayColor.G, calendar.DisplayColor.B);
                colours[calendar.LocalId] = colour;
                var source = string.IsNullOrWhiteSpace(calendar.SourceDisplayName) ? "Other" : calendar.SourceDisplayName;
                infos.Add(new CalendarInfo(calendar.LocalId, calendar.DisplayName, colour, source, CanAdd(calendar)));
            }

            var options = new FindAppointmentsOptions { MaxCount = MaximumAppointments };
            foreach (var property in new[]
                     {
                         AppointmentProperties.Subject, AppointmentProperties.StartTime, AppointmentProperties.Duration,
                         AppointmentProperties.AllDay, AppointmentProperties.Recurrence, AppointmentProperties.OriginalStartTime,
                     })
            {
                options.FetchProperties.Add(property);
            }
            var appointments = await store.FindAppointmentsAsync(start, end - start, options);
            var events = new List<CalendarEventItem>();
            foreach (var appointment in appointments)
            {
                var eventStart = appointment.StartTime;
                var eventEnd = eventStart + appointment.Duration;
                var calendarId = appointment.CalendarId ?? "";
                var title = string.IsNullOrWhiteSpace(appointment.Subject) ? "Untitled" : appointment.Subject;
                var isRecurring = appointment.Recurrence is not null || appointment.OriginalStartTime is not null;
                events.Add(new CalendarEventItem(
                    $"{appointment.LocalId}@{eventStart.UtcTicks}", appointment.LocalId, title, eventStart, eventEnd,
                    appointment.AllDay, isRecurring, calendarId, colours.TryGetValue(calendarId, out var colour) ? colour : CalendarColor.Grey));
            }
            return new CalendarReadResult(CalendarAccess.Allowed, infos, events);
        }
        catch (UnauthorizedAccessException)
        {
            store = null;
            return CalendarReadResult.Without(CalendarAccess.Denied);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The store went stale (an account removed, the service restarted): ask for a new one next time.
            store = null;
            AppLog.Write($"calendar: read failed ({error.GetType().Name}: {error.Message})");
            return CalendarReadResult.Without(CalendarAccess.Unavailable);
        }
    }

    private static bool CanAdd(AppointmentCalendar calendar)
    {
        try { return calendar.CanCreateOrUpdateAppointments; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    public void OpenPrivacySettings() => Launch(PrivacySettingsUri);

    public void Open(CalendarEventItem item) => Launch(CalendarAppUri);

    private static void Launch(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"calendar: could not open {uri} ({error.Message})");
        }
    }
}
