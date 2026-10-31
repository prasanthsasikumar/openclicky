//
//  SystemRingsTile.cs
//  OpenClicky (Windows port of Plugins/System/SystemRingsTile.swift)
//
//  The System tile on home: CPU and memory as two rings side by side, each with its figure and
//  name under it. It shares the System row's one SystemStatsStore, which samples only while one of
//  them is on screen.
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

internal sealed class SystemRingsTile : Border, IIslandContent
{
    private const double RingSize = 40;
    private const double RingThickness = 4.5;
    private static readonly Color CpuColor = Color.FromRgb(52, 199, 89);
    private static readonly Color MemoryColor = Color.FromRgb(10, 132, 255);

    private readonly RingGauge cpu = new("CPU", CpuColor);
    private readonly RingGauge memory = new("Memory", MemoryColor);

    public SystemRingsTile()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cpu.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(cpu);
        row.Children.Add(memory);
        var tile = IslandDashUi.Tile(row);
        Child = tile;
        Loaded += (_, _) =>
        {
            SystemIslandView.Store.Changed += OnStoreChanged;
            SystemIslandView.Store.BeginWatching();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            SystemIslandView.Store.Changed -= OnStoreChanged;
            SystemIslandView.Store.EndWatching();
        };
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        var snapshot = SystemIslandView.Store.Snapshot;
        cpu.Show(snapshot.Cpu);
        memory.Show(snapshot.MemoryTotal > 0 ? (double)snapshot.MemoryUsed / snapshot.MemoryTotal : 0);
    }

    public void Tick() { }

    /// <summary>One ring with the figure and name under it.</summary>
    private sealed class RingGauge : StackPanel
    {
        private readonly string label;
        private readonly Path fill;
        private readonly TextBlock figure;

        public RingGauge(string label, Color color)
        {
            this.label = label;
            Width = 46;
            var ring = new Grid { Width = RingSize, Height = RingSize, HorizontalAlignment = HorizontalAlignment.Center };
            ring.Children.Add(new Ellipse { Stroke = DS.Brush(color, 0.22), StrokeThickness = RingThickness });
            fill = new Path
            {
                Stroke = DS.Brush(color),
                StrokeThickness = RingThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            ring.Children.Add(fill);
            Children.Add(ring);
            figure = IslandUi.Text("0%", 13, DS.HUD.TextBrush, FontWeights.SemiBold);
            figure.HorizontalAlignment = HorizontalAlignment.Center;
            figure.Margin = new Thickness(0, 4, 0, 0);
            Children.Add(figure);
            var name = IslandUi.Text(label, 9, IslandDashUi.SecondaryTextBrush, FontWeights.SemiBold);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            Children.Add(name);
        }

        /// <summary>A sliver always shows, so an idle PC still reads as a ring that is measuring, not an empty track.</summary>
        public void Show(double fraction)
        {
            fill.Data = Arc(Math.Max(Math.Min(fraction, 1), 0.01));
            figure.Text = SystemStatsFormatter.Percent(fraction);
            AutomationProperties.SetName(this, $"{label} {figure.Text}");
        }

        /// <summary>The ring's filled part, clockwise from 12 o'clock, on the stroke's centre line.</summary>
        private static Geometry Arc(double fraction)
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
}
