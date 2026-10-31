//
//  IslandHomeView.cs
//  OpenClicky (Windows port of IslandHome.swift's IslandHomeView)
//
//  Home in the island's slot: the columns that are on side by side (now playing, the calendar,
//  then the weather and System tiles packed together), a hairline between groups. The sizes and
//  the island's width for them are Core's IslandHomeLayout; with none on, or in a side drawer, the
//  panel shows the shortcuts row instead (FullPanelView), so this view is only made for a row.
//

using System.Windows;
using System.Windows.Controls;
using OpenClicky.Core.Companion;
using OpenClicky.Windows.Plugins.Calendar;
using OpenClicky.Windows.Plugins.Media;
using OpenClicky.Windows.Plugins.SystemStats;
using OpenClicky.Windows.Plugins.Weather;

namespace OpenClicky.Windows.Island;

internal sealed class IslandHomeView : StackPanel, IIslandContent
{
    private readonly List<IIslandContent> parts = [];

    public IslandHomeView(IslandHomeLayout layout)
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        foreach (var group in layout.Groups)
        {
            if (Children.Count > 0)
            {
                var divider = IslandDashUi.Divider();
                divider.Margin = new Thickness(IslandDash.ColumnSpacing, 6, IslandDash.ColumnSpacing, 6);
                divider.Height = IslandDash.TileSize - 12;
                divider.VerticalAlignment = VerticalAlignment.Top;
                Children.Add(divider);
            }
            Children.Add(GroupView(group));
        }
    }

    private FrameworkElement GroupView(IReadOnlyList<IslandHomeColumn> group)
    {
        if (IslandHomeLayout.IsTile(group[0]))
        {
            var tiles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            foreach (var column in group)
            {
                var tile = Part(column);
                if (tiles.Children.Count > 0) tile.Margin = new Thickness(IslandDash.TileSpacing, 0, 0, 0);
                tiles.Children.Add(tile);
            }
            return tiles;
        }
        return Part(group[0]);
    }

    private FrameworkElement Part(IslandHomeColumn column)
    {
        FrameworkElement part = column switch
        {
            IslandHomeColumn.Media => new MediaDashboardColumn(),
            IslandHomeColumn.Calendar => new CalendarDashboardColumn(),
            IslandHomeColumn.Weather => new WeatherTile(),
            _ => new SystemRingsTile(),
        };
        if (part is IIslandContent content) parts.Add(content);
        return part;
    }

    /// <summary>Each part follows its own store; the companion's changes (the voice, the pointer) are none of theirs.</summary>
    public void Refresh() { }

    public void Tick()
    {
        foreach (var part in parts) part.Tick();
    }
}
