//
//  TeacherIslandView.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherIslandView.swift)
//
//  The Teacher's row on the island: what is on the board and a button that clears it, or, with
//  nothing up, a line saying what to ask. A switch on the right turns lessons and drawn answers on
//  and off, the same switch Settings has; the row stays, so it is here to switch back on. Follows
//  the app's one board (TeacherBoard.Shared).
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Teacher;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Plugins.Teacher;

internal sealed class TeacherIslandView : Grid, IIslandContent
{
    private readonly TeacherBoard board = TeacherBoard.Shared;
    private readonly TextBlock firstLine;
    private readonly TextBlock secondLine;
    private readonly Border clearButton;
    private readonly Border switchTrack;
    private readonly Ellipse switchThumb;
    private IDisposable? switchObservation;

    private static Preferences Preferences => TeacherPlugin.Preferences();

    public TeacherIslandView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 4, 0);

        var icon = Glyph(TeacherPlugin.Component.Glyph, 18, DS.Brush(System.Windows.Media.Colors.White, 0.85));
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 12, 0);
        Children.Add(icon);

        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        firstLine = Text("", 11, DS.HUD.Text2Brush, trimming: TextTrimming.CharacterEllipsis);
        secondLine = Text("", 13, DS.HUD.TextBrush, FontWeights.SemiBold, trimming: TextTrimming.CharacterEllipsis);
        lines.Children.Add(firstLine);
        lines.Children.Add(secondLine);
        SetColumn(lines, 1);
        Children.Add(lines);

        var clearLabel = Text("clear", 12, DS.HUD.TextBrush, FontWeights.SemiBold);
        clearLabel.VerticalAlignment = VerticalAlignment.Center;
        clearButton = Clickable(clearLabel, () => board.Clear("the island"), "Clear the board", DS.HUD.BandControlBrush,
            new CornerRadius(DS.HUD.PillHeight / 2));
        clearButton.Height = DS.HUD.PillHeight;
        clearButton.Padding = new Thickness(12, 0, 12, 0);
        clearButton.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetName(clearButton, "Clear the board");
        SetColumn(clearButton, 2);
        Children.Add(clearButton);

        // The row's switch. The hit area is taller than the track so it is easy to press.
        switchThumb = new Ellipse { Width = 14, Height = 14, Fill = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        switchTrack = new Border { Width = 34, Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = switchThumb };
        var switchButton = Clickable(switchTrack, () => TeacherPlugin.SetLessonsOn(!TeacherPlugin.IsEnabled, Preferences), "Teacher lessons");
        switchButton.Padding = new Thickness(0, 8, 0, 8);
        switchButton.Margin = new Thickness(12, 0, 0, 0);
        switchButton.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(switchButton, 3);
        Children.Add(switchButton);

        Loaded += (_, _) =>
        {
            board.Changed += Refresh;
            switchObservation = Preferences.Observe(TeacherPlugin.LessonsKey, () => Dispatcher.BeginInvoke(Refresh));
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            board.Changed -= Refresh;
            switchObservation?.Dispose();
            switchObservation = null;
        };
        Refresh();
    }

    public void Refresh()
    {
        var isOn = TeacherPlugin.IsEnabled;
        switchTrack.Background = isOn ? DS.HUD.GoodBrush : DS.HUD.BandControlBrush;
        switchThumb.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        switchTrack.ToolTip = isOn ? "Lessons are on" : "Lessons are off";
        AutomationProperties.SetName(switchTrack, switchTrack.ToolTip as string ?? "");

        if (board.Lesson is { } lesson)
        {
            // "on the board" over the lesson's title.
            firstLine.Text = "on the board";
            secondLine.Text = lesson.Title;
            Swap(firstLine, secondLine);
            clearButton.Visibility = Visibility.Visible;
        }
        else if (!isOn)
        {
            firstLine.Text = "lessons are off";
            secondLine.Text = "switch on and ask me to teach you something";
            Swap(secondLine, firstLine);
            clearButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            // What to ask, over what happens then.
            firstLine.Text = "ask me to teach you something";
            secondLine.Text = "i'll draw it on your screen while i explain";
            Swap(secondLine, firstLine);
            clearButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The small grey line and the white one swap places between the two states, as on the Mac.</summary>
    private void Swap(TextBlock small, TextBlock large)
    {
        small.FontSize = 11;
        small.FontWeight = FontWeights.Normal;
        small.Foreground = DS.HUD.Text2Brush;
        large.FontSize = 13;
        large.FontWeight = FontWeights.SemiBold;
        large.Foreground = DS.HUD.TextBrush;
    }

    public void Tick() { }
}
