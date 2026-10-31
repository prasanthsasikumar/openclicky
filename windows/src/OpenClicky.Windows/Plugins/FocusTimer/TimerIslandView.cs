//
//  TimerIslandView.cs
//  OpenClicky (Windows port of Plugins/Timer/TimerIslandView.swift and TimerStore.playChime)
//
//  The Timer plugin's view: a focus/break cycle or a stopwatch, the time large, the phase above it,
//  start/pause and reset on the right, the mode switch on top. The app's one TimerStore lives here
//  so a run keeps going (and chimes between phases) with the island closed.
//

using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.FocusTimer;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Plugins.FocusTimer;

internal sealed class TimerIslandView : Grid, IIslandContent
{
    /// <summary>The app's one store (the Mac's TimerStore.shared); the lengths are read from Settings on every tick.</summary>
    public static TimerStore Store { get; } = new(durations: () => new TimerSettings(Preferences.Shared).Durations, chime: PlayChime);

    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string ResetGlyph = "";

    private readonly SegmentedSwitch modeSwitch;
    private readonly StackPanel roundDots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock phaseLine = Text("", 12, IslandDashUi.SecondaryTextBrush, FontWeights.SemiBold, trimming: TextTrimming.CharacterEllipsis);
    private readonly TextBlock time = Text("", 28, DS.HUD.TextBrush, FontWeights.SemiBold, family: DS.Fonts.Mono);
    private readonly IslandIconButton startPause;
    private readonly IslandIconButton reset;

    public TimerIslandView()
    {
        Margin = new Thickness(0, 4, 0, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new DockPanel { LastChildFill = false };
        modeSwitch = new SegmentedSwitch([TimerMode.Focus.Title(), TimerMode.Stopwatch.Title()], index => Store.Mode = index == 0 ? TimerMode.Focus : TimerMode.Stopwatch);
        DockPanel.SetDock(modeSwitch, Dock.Left);
        top.Children.Add(modeSwitch);
        DockPanel.SetDock(roundDots, Dock.Right);
        top.Children.Add(roundDots);
        Children.Add(top);

        var bottom = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
        startPause = new IslandIconButton(PlayGlyph, 14, "start", Store.ToggleRunning, frameScale: 2.3);
        startPause.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(startPause);
        reset = new IslandIconButton(ResetGlyph, 14, "reset", Store.ResetShownMode, frameScale: 2.3);
        buttons.Children.Add(reset);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        clock.Children.Add(phaseLine);
        time.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        time.LineHeight = 34;
        clock.Children.Add(time);
        bottom.Children.Add(clock);
        SetRow(bottom, 1);
        Children.Add(bottom);

        Loaded += (_, _) =>
        {
            Store.Changed += OnStoreChanged;
            // The tick only runs while a clock does; reading the time on appear keeps a paused or finished clock honest
            // after the island was closed for a while.
            Store.Tick();
        };
        Unloaded += (_, _) => Store.Changed -= OnStoreChanged;
        Refresh();
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        modeSwitch.SelectedIndex = Store.Mode == TimerMode.Focus ? 0 : 1;
        var isFocus = Store.Mode == TimerMode.Focus;
        time.Text = isFocus ? TimerFormatter.Countdown(Store.FocusRemaining) : TimerFormatter.Clock(Store.StopwatchElapsed);
        AutomationProperties.SetName(time, time.Text);
        phaseLine.Text = isFocus
            ? Store.Cycle.Phase.Title() + (Store.Cycle.IsRunning ? "" : Store.Cycle.HasProgress ? " · paused" : "")
            : Store.Stopwatch.IsRunning ? "Running" : Store.StopwatchElapsed > TimeSpan.Zero ? "Paused" : "Ready";
        var running = Store.IsShownModeRunning;
        startPause.Glyph = running ? PauseGlyph : PlayGlyph;
        startPause.IsActive = running;
        startPause.SetToolTip(running ? "pause" : "start");
        reset.IsEnabled = Store.ShownModeHasProgress;
        RefreshRoundDots(isFocus);
    }

    /// <summary>Four dots: the focus rounds done in this set of four, the current one brighter.</summary>
    private void RefreshRoundDots(bool isFocus)
    {
        roundDots.Visibility = isFocus ? Visibility.Visible : Visibility.Collapsed;
        var done = Store.Cycle.CompletedFocusRounds % FocusCycle.RoundsPerLongBreak;
        var current = Store.Cycle.Phase == FocusPhase.Focus ? done : -1;
        roundDots.Children.Clear();
        for (var index = 0; index < FocusCycle.RoundsPerLongBreak; index++)
        {
            var opacity = index < done ? 0.85 : index == current ? 0.45 : 0.15;
            roundDots.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = DS.Brush(Colors.White, opacity), Margin = new Thickness(index == 0 ? 0 : 5, 0, 0, 0) });
        }
        AutomationProperties.SetName(roundDots, $"Round {Store.Cycle.RoundInSet} of {FocusCycle.RoundsPerLongBreak}");
    }

    public void Tick() { }

    /// <summary>A short Windows sound between phases: a brighter one when focus starts, a softer one for a break.</summary>
    private static void PlayChime(FocusPhase phase)
    {
        var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media",
            phase == FocusPhase.Focus ? "Windows Notify Calendar.wav" : "Windows Notify System Generic.wav");
        try
        {
            if (File.Exists(file))
            {
                // Not disposed here: the sound plays on after Play returns.
                new SoundPlayer(file).Play();
                return;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            AppLog.Write($"timer: chime failed ({error.Message})");
        }
        (phase == FocusPhase.Focus ? SystemSounds.Asterisk : SystemSounds.Beep).Play();
    }
}

/// <summary>A small segment switch on the island: a faint track, the chosen segment lit.</summary>
internal sealed class SegmentedSwitch : Border
{
    private readonly List<Border> segments = [];
    private int selectedIndex = -1;

    public SegmentedSwitch(IReadOnlyList<string> titles, Action<int> onSelect)
    {
        Background = DS.Brush(Colors.White, 0.08);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(2);
        HorizontalAlignment = HorizontalAlignment.Left;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var index = 0; index < titles.Count; index++)
        {
            var position = index;
            var label = Text(titles[index], 12, DS.HUD.TextBrush, FontWeights.Medium);
            var segment = Clickable(label, () => onSelect(position), titles[index], Brushes.Transparent, new CornerRadius(10));
            segment.Padding = new Thickness(10, 3, 10, 3);
            if (index > 0) segment.Margin = new Thickness(2, 0, 0, 0);
            segments.Add(segment);
            row.Children.Add(segment);
        }
        Child = row;
        SelectedIndex = 0;
    }

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (selectedIndex == value) return;
            selectedIndex = value;
            for (var index = 0; index < segments.Count; index++)
            {
                var isSelected = index == value;
                segments[index].Background = isSelected ? DS.Brush(Colors.White, 0.2) : Brushes.Transparent;
                if (segments[index].Child is TextBlock label) label.Opacity = isSelected ? 1 : 0.6;
                AutomationProperties.SetItemStatus(segments[index], isSelected ? "selected" : "");
            }
        }
    }
}
