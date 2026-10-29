//
//  AgentsIslandView.cs
//  OpenClicky (Windows port of Plugins/CodingAgents/AgentsIslandView.swift)
//
//  The Coding agents plugin's row: up to three session rows (agent, project, what it is doing, a
//  running bar while live) on the left, Claude's two plan-limit rings on the right. The app's one
//  watcher and usage store live here; the island manager switches them with the plugin.
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenClicky.Core.Plugins.CodingAgents;
using OpenClicky.Core.Plugins.SystemStats;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Plugins.SystemStats;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Plugins.CodingAgents;

internal sealed class AgentsIslandView : Grid, IIslandContent
{
    /// <summary>The app's one session watcher (the Mac's NotchHUDManager.agentSessions).</summary>
    public static AgentSessionWatcher Sessions { get; } = new(AgentLogRoots.Standard);
    /// <summary>The app's one plan-limits store (the Mac's NotchHUDManager.claudeUsage).</summary>
    public static ClaudeUsageStore Usage { get; } = ClaudeUsageStore.Live();

    /// <summary>The plugin's switch: on, the watcher polls slowly (for the peek line) and the limits may be asked for.</summary>
    public static void SetEnabled(bool isEnabled)
    {
        Sessions.SetEnabled(isEnabled);
        Usage.SetEnabled(isEnabled);
    }

    private readonly StackPanel sessionsColumn = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly ContentControl limits = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Top };
    /// <summary>The running bars of the live rows, slid every frame.</summary>
    private readonly List<(Canvas Bar, Border Highlight)> bars = [];

    public AgentsIslandView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(sessionsColumn);
        limits.Margin = new Thickness(16, 0, 0, 0);
        SetColumn(limits, 1);
        Children.Add(limits);
        Loaded += (_, _) =>
        {
            Sessions.Changed += OnStoreChanged;
            Usage.Changed += OnStoreChanged;
            Sessions.BeginWatching();
            Usage.BeginWatching();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Sessions.Changed -= OnStoreChanged;
            Usage.Changed -= OnStoreChanged;
            Sessions.EndWatching();
            Usage.EndWatching();
        };
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        RefreshSessions();
        limits.Content = LimitsView(Usage.State);
    }

    private void RefreshSessions()
    {
        sessionsColumn.Children.Clear();
        bars.Clear();
        var rows = AgentRow.For(Sessions.Sessions, DateTimeOffset.Now);
        if (rows.Count == 0)
        {
            sessionsColumn.Children.Add(IslandUi.Text("nothing running", 13, DS.HUD.TextBrush));
            var hint = IslandUi.Text("start claude or codex in a terminal and it shows up here", 11, DS.HUD.Text3Brush, wrapping: TextWrapping.Wrap);
            hint.MaxHeight = 30;
            sessionsColumn.Children.Add(hint);
            return;
        }
        foreach (var row in rows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, sessionsColumn.Children.Count > 0 ? 4 : 0, 0, 0) };
            line.Children.Add(IslandUi.Text(row.Agent, 11, DS.HUD.TextBrush, FontWeights.SemiBold));
            var project = IslandUi.Text(row.Project, 11, DS.HUD.Text2Brush, trimming: TextTrimming.CharacterEllipsis);
            project.Margin = new Thickness(6, 0, 0, 0);
            project.MaxWidth = 120;
            line.Children.Add(project);
            var activity = IslandUi.Text(row.Line, 11, row.IsLive ? DS.HUD.TextBrush : DS.HUD.Text3Brush, trimming: TextTrimming.CharacterEllipsis);
            activity.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(activity);
            AutomationProperties.SetName(line, $"{row.Agent} {row.Project} {row.Line}");
            sessionsColumn.Children.Add(line);
            if (row.IsLive)
            {
                var bar = new Canvas { Height = 2, ClipToBounds = true, Margin = new Thickness(0, 3, 0, 0), Background = DS.Brush(Colors.White, 0.12) };
                var highlight = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = DS.HUD.PointerBrush };
                bar.Children.Add(highlight);
                sessionsColumn.Children.Add(bar);
                bars.Add((bar, highlight));
            }
        }
        if (AgentRow.OverflowText(Sessions.Sessions.Count) is { } overflow)
        {
            sessionsColumn.Children.Add(IslandUi.Text(overflow, 10, DS.HUD.Text3Brush));
        }
    }

    /// <summary>The live rows' bars: a third-wide highlight sliding back and forth every 1.1 s.</summary>
    public void Tick()
    {
        var phase = (1 - Math.Cos(Overlay.MotionClock.Now * Math.PI / 1.1)) / 2;
        foreach (var (bar, highlight) in bars)
        {
            if (bar.ActualWidth <= 0) continue;
            highlight.Width = bar.ActualWidth * 0.3;
            Canvas.SetLeft(highlight, phase * bar.ActualWidth * 0.7);
        }
    }

    private static FrameworkElement? LimitsView(ClaudeUsageState state) => state switch
    {
        ClaudeUsageState.SignInNeeded => LimitsNote("sign in to claude to see limits"),
        ClaudeUsageState.CredentialsRefused => LimitsNote("can't read claude's sign-in to see limits"),
        ClaudeUsageState.Figures figures => UsageRings(figures.Usage, figures.IsStale),
        _ => null,
    };

    private static TextBlock LimitsNote(string text)
    {
        var note = IslandUi.Text(text, 10, DS.HUD.Text3Brush, wrapping: TextWrapping.Wrap);
        note.Width = 96;
        note.TextAlignment = TextAlignment.Right;
        return note;
    }

    private static StackPanel UsageRings(ClaudeUsage usage, bool isStale)
    {
        var rings = new StackPanel { Orientation = Orientation.Horizontal, Opacity = isStale ? 0.5 : 1 };
        rings.Children.Add(UsageRing("5 h", usage.FiveHour));
        var sevenDay = UsageRing("7 d", usage.SevenDay);
        sevenDay.Margin = new Thickness(10, 0, 0, 0);
        rings.Children.Add(sevenDay);
        return rings;
    }

    /// <summary>One plan window: a ring with the percentage inside, the window's name and its reset under it.</summary>
    private static FrameworkElement UsageRing(string label, UsageWindow window)
    {
        var color = window.Fraction >= 1 ? DS.HUD.PointerBrush : window.Fraction > 0.8 ? DS.Brush(DS.Colors.Warning) : DS.HUD.TextBrush;
        var ring = new Grid { Width = SystemIslandView.RingSize, Height = SystemIslandView.RingSize, HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(new Ellipse { Stroke = DS.Brush(Colors.White, 0.12), StrokeThickness = SystemIslandView.RingThickness });
        if (window.Fraction > 0)
        {
            ring.Children.Add(new Path
            {
                Data = SystemIslandView.Arc(window.Fraction),
                Stroke = color,
                StrokeThickness = SystemIslandView.RingThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
        var percent = IslandUi.Text(SystemStatsFormatter.Percent(window.Fraction), 8, DS.HUD.TextBrush, FontWeights.SemiBold);
        percent.HorizontalAlignment = HorizontalAlignment.Center;
        percent.VerticalAlignment = VerticalAlignment.Center;
        ring.Children.Add(percent);
        var stack = new StackPanel { Width = 78 };
        stack.Children.Add(ring);
        var name = IslandUi.Text(label, 10, DS.HUD.Text2Brush);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(name);
        var reset = IslandUi.Text(ClaudeUsagePolicy.ResetText(window.ResetsAt, DateTimeOffset.Now), 9, DS.HUD.Text3Brush, trimming: TextTrimming.CharacterEllipsis);
        reset.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(reset);
        AutomationProperties.SetName(stack, $"{label} window {SystemStatsFormatter.Percent(window.Fraction)} used");
        return stack;
    }
}
