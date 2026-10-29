//
//  TeacherIslandView.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherIslandView.swift)
//
//  The Teacher's row on the island: what is on the board and a button that clears it, or, with
//  nothing up, a line saying what to ask. Follows the app's one board (TeacherBoard.Shared).
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

    public TeacherIslandView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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

        Loaded += (_, _) =>
        {
            board.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => board.Changed -= Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (board.Lesson is { } lesson)
        {
            // "on the board" over the lesson's title.
            firstLine.Text = "on the board";
            secondLine.Text = lesson.Title;
            Swap(firstLine, secondLine);
            clearButton.Visibility = Visibility.Visible;
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
