// Ports Dictation/UI/SettingsIslandPages.swift and the island plugins' settings rows (Weather,
// Calendar, Timer): how the island opens and looks, the home tiles' switches, the temperature unit
// and place, the timer's lengths, calendar access and, on the Calendars page, which calendars show.
// The plugin switches themselves are SettingsPluginsPage.cs's; SettingsLayout places them all.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins.Calendar;
using OpenClicky.Core.Plugins.FocusTimer;
using OpenClicky.Core.Plugins.Weather;
using OpenClicky.Core.Settings;
using OpenClicky.Windows.Plugins.Calendar;
using OpenClicky.Windows.Plugins.Weather;

namespace OpenClicky.Windows.AppWindow;

internal sealed partial class SettingsRowFactory
{
    private IslandAppearanceSettings IslandAppearance => new(services.Preferences);
    private WeatherSettings Weather => new(services.Preferences);
    private CalendarSettings Calendar => new(services.Preferences);
    private TimerSettings Timer => new(services.Preferences);

    private UIElement? IslandRow(SettingsItem item, string query)
    {
        switch (item.Id)
        {
            case "appearance.peekDirection":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, item.Detail, query,
                    Ui.Picker(Enum.GetValues<IslandPeekDirection>().Select(direction => (direction, direction.Title())), IslandAppearance.PeekDirection,
                        direction => IslandAppearance.PeekDirection = direction, 160))), IslandAppearance.Observe);
            case "appearance.style":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, IslandAppearance.Look.Footnote(), query,
                    Ui.Picker(new[] { IslandLook.Glass, IslandLook.Blend, IslandLook.Black }.Select(look => (look, look.Title())), IslandAppearance.Look,
                        look => IslandAppearance.Look = look, 160))), IslandAppearance.Observe);
            case "calendar.enabled":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, item.Detail, query,
                    Ui.Toggle(Calendar.IsEnabled, isOn => Calendar.IsEnabled = isOn))), Calendar.Observe);
            case "weather.enabled":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, item.Detail, query, Ui.Toggle(Weather.IsEnabled, isOn =>
                {
                    Weather.IsEnabled = isOn;
                    if (isOn && Weather.UsesLocation) _ = WeatherTile.Store.RequestLocationPermissionAsync();
                }))), Weather.Observe);
            case "weather.unit":
                return new LiveContent(() => Ui.Card(Ui.Row(item.Title, item.Detail, query,
                    Ui.Picker(new[] { WeatherUnit.Celsius, WeatherUnit.Fahrenheit }.Select(unit => (unit, unit.Title())), Weather.Unit,
                        unit => Weather.Unit = unit, 140))), Weather.Observe);
            case "weather.location":
                return new LiveContent(() => Ui.Card(WeatherLocationRow(item, query)), Weather.Observe);
            case "timer.focusMinutes":
                return TimerMinutesRow(item, query, () => Timer.FocusMinutes, minutes => Timer.FocusMinutes = minutes);
            case "timer.breakMinutes":
                return TimerMinutesRow(item, query, () => Timer.BreakMinutes, minutes => Timer.BreakMinutes = minutes);
            case "timer.longBreakMinutes":
                return TimerMinutesRow(item, query, () => Timer.LongBreakMinutes, minutes => Timer.LongBreakMinutes = minutes);
            case "calendar.access":
                return new LiveContent(() => Ui.Card(CalendarAccessRow(item, query)), CalendarChanges);
            case "calendar.calendars":
                return CalendarListRows(query);
            default:
                return null;
        }
    }

    private LiveContent TimerMinutesRow(SettingsItem item, string query, Func<int> read, Action<int> write) =>
        new(() => Ui.Card(Ui.Row(item.Title, item.Detail, query,
            Ui.Picker(TimerSettings.ChoicesIncluding(read()).Select(minutes => (minutes, $"{minutes} min")), read(), write, 120))),
            changed => PreferenceObservations.ObserveAll(services.Preferences, TimerSettings.Keys, changed));

    /// <summary>"Use my location", and with it off, the place typed in (saved on Enter or when the field loses focus).</summary>
    private FrameworkElement WeatherLocationRow(SettingsItem item, string query)
    {
        var weather = Weather;
        var useLocation = Ui.Toggle(weather.UsesLocation, isOn =>
        {
            Weather.UsesLocation = isOn;
            if (isOn && Weather.IsEnabled) _ = WeatherTile.Store.RequestLocationPermissionAsync();
        });
        var stack = new StackPanel();
        stack.Children.Add(Ui.Row("Use my location", "Where Windows says this PC is. Off: the place below.", query, useLocation));
        if (!weather.UsesLocation)
        {
            var field = new Wpf.Ui.Controls.TextBox { Text = weather.Place, PlaceholderText = "A city, like Lisbon", MinWidth = 220 };
            void Save()
            {
                if (field.Text.Trim() != Weather.Place) Weather.Place = field.Text;
            }
            field.LostKeyboardFocus += (_, _) => Save();
            field.KeyDown += (_, arguments) =>
            {
                if (arguments.Key == Key.Enter) Save();
            };
            var place = Ui.Row("Place", "The weather for this town or city.", query, field);
            place.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(place);
        }
        return stack;
    }

    private static Func<Action, IDisposable> CalendarChanges => Changes.Event(
        handler => CalendarDashboardColumn.Store.Changed += handler, handler => CalendarDashboardColumn.Store.Changed -= handler);

    private static Grid CalendarAccessRow(SettingsItem item, string query)
    {
        var store = CalendarDashboardColumn.Store;
        var (detail, button) = store.Access switch
        {
            CalendarAccess.Allowed => ("Allowed. The island reads your calendars on this PC; nothing is changed or sent.", null),
            CalendarAccess.Denied => ("Off in Windows Settings › Privacy › Calendar.", "Open Settings"),
            CalendarAccess.Unavailable => ("Windows didn't share its calendar with OpenClicky.", "Try Again"),
            _ => (item.Detail, "Allow"),
        };
        return Ui.Row(item.Title, detail, query, button is null ? null : Ui.Button(button, () => _ = store.RequestAccessAsync()));
    }

    /// <summary>One card per account with a switch per calendar; the list fills once the store has read the calendars.</summary>
    private LiveContent CalendarListRows(string query)
    {
        var store = CalendarDashboardColumn.Store;
        if (store.Calendars.Count == 0) _ = store.RefreshAsync();
        return new LiveContent(() =>
        {
            var stack = new StackPanel();
            var groups = CalendarSchedule.SourceGroups(store.Calendars);
            if (groups.Count == 0)
            {
                stack.Children.Add(Ui.Card(Ui.Row("No calendars yet",
                    store.Access == CalendarAccess.Allowed ? "Add an account in Windows' Calendar app and it shows here." : "Allow calendar access on the Privacy page first.",
                    query, null)));
                return stack;
            }
            foreach (var group in groups)
            {
                stack.Children.Add(SettingsFormView.SectionHeader(group.Title));
                foreach (var calendar in group.Calendars)
                {
                    var id = calendar.Id;
                    stack.Children.Add(Ui.Card(Ui.Row(calendar.Title, null, query,
                        Ui.Toggle(Calendar.IsCalendarVisible(id), isOn => Calendar.SetCalendarVisible(id, isOn)))));
                }
            }
            return stack;
        }, CalendarChanges, Calendar.Observe);
    }
}
