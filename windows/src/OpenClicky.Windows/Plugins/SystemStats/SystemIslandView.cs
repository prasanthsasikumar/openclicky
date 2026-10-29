//
//  SystemIslandView.cs
//  OpenClicky (Windows port of Plugins/System/SystemIslandView.swift)
//
//  The System plugin's row: a ring per figure (CPU, memory, disk, battery) with its name and the
//  long figure under it, fed by the app's one SystemStatsStore, which samples only while a row is
//  on screen.
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenClicky.Core.Plugins.SystemStats;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Plugins.SystemStats;

internal sealed class SystemIslandView : Grid, IIslandContent
{
    /// <summary>The app's one store (the Mac's NotchHUDManager.systemStats).</summary>
    public static SystemStatsStore Store { get; } = new(new WindowsSystemStatsSampler());

    internal const double RingSize = 28;
    internal const double RingThickness = 3;

    private readonly StackPanel row = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private IReadOnlyList<SystemTile> shownTiles = [];

    public SystemIslandView()
    {
        Children.Add(row);
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
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        var tiles = SystemTile.For(Store.Snapshot);
        if (tiles.SequenceEqual(shownTiles)) return;
        shownTiles = tiles;
        row.Children.Clear();
        for (var index = 0; index < tiles.Count; index++)
        {
            var tileView = TileView(tiles[index]);
            if (index > 0) tileView.Margin = new Thickness(12, 0, 0, 0);
            row.Children.Add(tileView);
        }
    }

    public void Tick() { }

    /// <summary>One tile: the ring with its figure inside, the name, and the long figure under it.</summary>
    private static FrameworkElement TileView(SystemTile tile)
    {
        var stack = new StackPanel { Width = 72 };
        stack.Children.Add(Ring(tile));
        var caption = IslandUi.Text(tile.Caption, 10, DS.HUD.TextBrush, FontWeights.SemiBold);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(caption);
        var figure = IslandUi.Text(tile.Long, 10, DS.Brush(Colors.White, 0.8), trimming: TextTrimming.CharacterEllipsis);
        figure.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(figure);
        AutomationProperties.SetName(stack, $"{tile.Caption} {tile.Long}");
        return stack;
    }

    private static FrameworkElement Ring(SystemTile tile)
    {
        var ring = new Grid { Width = RingSize, Height = RingSize, HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(new Ellipse { Stroke = DS.Brush(Colors.White, 0.12), StrokeThickness = RingThickness });
        var fill = Math.Clamp(tile.Fraction, 0, 1);
        if (fill > 0)
        {
            ring.Children.Add(new Path
            {
                Data = Arc(fill),
                Stroke = tile.IsWarning ? DS.Brush(DS.Colors.Warning) : DS.HUD.TextBrush,
                StrokeThickness = RingThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
        var figure = IslandUi.Text(tile.Short, 8, DS.HUD.TextBrush, FontWeights.SemiBold);
        figure.HorizontalAlignment = HorizontalAlignment.Center;
        figure.VerticalAlignment = VerticalAlignment.Center;
        ring.Children.Add(figure);
        return ring;
    }

    /// <summary>The ring's filled part, clockwise from 12 o'clock, on the stroke's centre line.</summary>
    internal static Geometry Arc(double fraction)
    {
        var radius = (RingSize - RingThickness) / 2;
        var centre = new Point(RingSize / 2, RingSize / 2);
        if (fraction >= 0.999) return new EllipseGeometry(centre, radius, radius);
        var angle = fraction * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }
}
