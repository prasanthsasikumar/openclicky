//
//  CalendarDashboardColumn.cs
//  OpenClicky (Windows port of Plugins/Calendar/CalendarDashboardColumn.swift)
//
//  The calendar column on home: the month and a week strip (today and the six days after it, a dot
//  per event under each number), and under it the chosen day's events. Clicking a day chooses it,
//  clicking an event brings the Calendar app forward. Without the calendar the column says why,
//  with the one button that can help. The Mac's "+" (adding an event from the island) is not here:
//  Windows lets only a packaged app write appointments.
//

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Calendar;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Plugins.Calendar;

internal sealed class CalendarDashboardColumn : Grid, IIslandContent
{
    /// <summary>The app's one store (the Mac's CalendarStore.shared).</summary>
    public static CalendarStore Store { get; } = new(Preferences.Shared, new WindowsCalendarSource());

    private const double HeaderHeight = 40;
    private const string CalendarGlyph = "";
    private static readonly SolidColorBrush DimText = DS.Brush(Colors.White, 0.6);

    private readonly TextBlock month = Text("", 21, DS.HUD.TextBrush, FontWeights.Bold);
    private readonly UniformGrid week = new() { Rows = 1, Columns = CalendarSchedule.DayCount, Margin = new Thickness(4, 0, 0, 0) };
    private readonly ContentControl content = new() { Focusable = false };
    private DateTimeOffset? selectedDayStart;
    /// <summary>The minute the list was drawn in, so an event that just ended dims without anything else changing.</summary>
    private long drawnMinute;

    public CalendarDashboardColumn()
    {
        Width = IslandDash.CalendarWidth;
        Height = IslandDash.ContentHeight;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { LastChildFill = true };
        month.Width = 46;
        month.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(month, Dock.Left);
        header.Children.Add(month);
        header.Children.Add(week);
        Children.Add(header);

        content.Margin = new Thickness(0, 6, 0, 0);
        SetRow(content, 1);
        Children.Add(content);

        Loaded += (_, _) =>
        {
            Store.Changed += OnStoreChanged;
            Store.BeginWatching();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Store.Changed -= OnStoreChanged;
            Store.EndWatching();
        };
        Refresh();
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    /// <summary>The chosen day, or today when nothing is chosen or the chosen day has left the week.</summary>
    private CalendarDay? SelectedDay => Store.Days.FirstOrDefault(day => day.Start == selectedDayStart) ?? Store.Days.FirstOrDefault();

    public void Refresh()
    {
        var selected = SelectedDay;
        var zone = Store.Zone;
        month.Text = TimeZoneInfo.ConvertTime(selected?.Start ?? DateTimeOffset.Now, zone).ToString("MMM", CultureInfo.CurrentCulture);
        var today = CalendarSchedule.LocalDate(DateTimeOffset.Now, zone);
        week.Children.Clear();
        foreach (var day in Store.Days)
        {
            week.Children.Add(DayCell(day, CalendarSchedule.LocalDate(day.Start, zone) == today, day.Start == selected?.Start,
                showsDots: Store.Access == CalendarAccess.Allowed));
        }
        content.Content = Body(selected, zone);
        drawnMinute = DateTimeOffset.Now.ToUnixTimeSeconds() / 60;
    }

    /// <summary>Redrawn once a minute so an event that just ended dims.</summary>
    public void Tick()
    {
        if (DateTimeOffset.Now.ToUnixTimeSeconds() / 60 != drawnMinute) Refresh();
    }

    // MARK: - The week strip

    private FrameworkElement DayCell(CalendarDay day, bool isToday, bool isSelected, bool showsDots)
    {
        var local = TimeZoneInfo.ConvertTime(day.Start, Store.Zone);
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var weekday = Text(local.ToString("ddd", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture), 8, DS.Brush(Colors.White, 0.75), FontWeights.SemiBold);
        weekday.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(weekday);
        var number = Text(local.Day.ToString(CultureInfo.CurrentCulture), 13,
            isToday ? IslandDashUi.TodayBrush : DS.Brush(Colors.White, 0.85),
            isSelected ? FontWeights.Bold : isToday ? FontWeights.SemiBold : FontWeights.Medium);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.Margin = new Thickness(0, 1, 0, 1);
        stack.Children.Add(number);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Height = 3, HorizontalAlignment = HorizontalAlignment.Center };
        if (showsDots)
        {
            foreach (var colour in day.DotColors)
            {
                dots.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = Brush(colour), Margin = new Thickness(1, 0, 1, 0) });
            }
        }
        stack.Children.Add(dots);

        var restingFill = isSelected ? DS.Brush(Colors.White, 0.1) : Brushes.Transparent;
        var cell = Clickable(stack, () =>
        {
            selectedDayStart = day.Start;
            Refresh();
        }, local.ToString("D", CultureInfo.CurrentCulture), restingFill, new CornerRadius(7));
        cell.Padding = new Thickness(0, 3, 0, 3);
        cell.Margin = new Thickness(1, 0, 1, 0);
        cell.MouseEnter += (_, _) => { if (!isSelected) cell.Background = DS.Brush(Colors.White, 0.06); };
        cell.MouseLeave += (_, _) => cell.Background = restingFill;
        AutomationProperties.SetItemStatus(cell, (isSelected ? "selected, " : "") + (day.Events.Count == 0 ? "no events" : $"{day.Events.Count} events"));
        return cell;
    }

    // MARK: - The day's events

    private FrameworkElement Body(CalendarDay? selected, TimeZoneInfo zone)
    {
        switch (Store.Access)
        {
            case CalendarAccess.Unknown:
                return Message("Reading your calendar…");
            case CalendarAccess.Denied:
                return Message("Calendar access is off for OpenClicky. Turn it on in Windows Settings.",
                    IslandDashUi.PillButton("Open privacy settings", () => _ = Store.RequestAccessAsync()));
            case CalendarAccess.Unavailable:
                return Message("Windows didn't share its calendar with OpenClicky.",
                    IslandDashUi.PillButton("Try again", () => _ = Store.RefreshAsync()));
        }
        if (selected is null || selected.Events.Count == 0)
        {
            var isToday = selected is null || CalendarSchedule.LocalDate(selected.Start, zone) == CalendarSchedule.LocalDate(DateTimeOffset.Now, zone);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(Glyph(CalendarGlyph, 15, DimText));
            var words = Text(isToday ? "No events today" : "Nothing planned", 15, DimText, FontWeights.SemiBold);
            words.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(words);
            return row;
        }
        var list = new StackPanel();
        var now = DateTimeOffset.Now;
        foreach (var item in selected.Events)
        {
            var eventRow = EventRow(item, CalendarSchedule.TimeText(item, selected.Start, zone), CalendarSchedule.IsPast(item, now));
            if (list.Children.Count > 0) eventRow.Margin = new Thickness(0, 4, 0, 0);
            list.Children.Add(eventRow);
        }
        return new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
    }

    private Border EventRow(CalendarEventItem item, string timeText, bool isPast)
    {
        var row = new DockPanel { LastChildFill = true };
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = Brush(item.Color), Margin = new Thickness(0, 0, 7, 0) };
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        var words = new StackPanel();
        words.Children.Add(Text(item.Title, 11, DS.HUD.TextBrush, FontWeights.SemiBold, trimming: TextTrimming.CharacterEllipsis));
        var time = Text(timeText, 10, IslandDashUi.SecondaryTextBrush, trimming: TextTrimming.CharacterEllipsis);
        time.Margin = new Thickness(0, 1, 0, 0);
        words.Children.Add(time);
        row.Children.Add(words);
        var hover = DS.Brush(Colors.White, 0.07);
        var border = Clickable(row, () => Store.Open(item), "open in Calendar", Brushes.Transparent, new CornerRadius(6));
        border.Padding = new Thickness(4, 3, 4, 3);
        border.Opacity = isPast ? 0.5 : 1;
        // Clickable dims to 0.85 under the pointer; a past event keeps its own dimming, lit by the fill instead.
        border.MouseEnter += (_, _) => { border.Background = hover; border.Opacity = isPast ? 0.5 : 1; };
        border.MouseLeave += (_, _) => { border.Background = Brushes.Transparent; border.Opacity = isPast ? 0.5 : 1; };
        AutomationProperties.SetName(border, $"{item.Title}, {timeText}");
        return border;
    }

    private static FrameworkElement Message(string line, FrameworkElement? button = null)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var words = Text(line, 11, DimText, wrapping: TextWrapping.Wrap, trimming: TextTrimming.CharacterEllipsis);
        words.TextAlignment = TextAlignment.Center;
        words.HorizontalAlignment = HorizontalAlignment.Center;
        words.MaxHeight = 32;
        stack.Children.Add(words);
        if (button is not null)
        {
            button.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(button);
        }
        return stack;
    }

    private static SolidColorBrush Brush(CalendarColor colour) =>
        DS.Brush(Color.FromRgb((byte)Math.Round(colour.Red * 255), (byte)Math.Round(colour.Green * 255), (byte)Math.Round(colour.Blue * 255)));
}
