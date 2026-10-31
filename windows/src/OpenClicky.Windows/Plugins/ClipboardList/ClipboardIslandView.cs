//
//  ClipboardIslandView.cs
//  OpenClicky (Windows port of Plugins/Clipboard/ClipboardIslandView.swift)
//
//  The Clipboard plugin's view: the last things copied, newest first. A click puts one back on the
//  clipboard ("Copied" for a moment); Clear forgets them all. The app's one store polls only while
//  the plugin is on (IslandWindowManager switches it with the plugin), so copies are caught from
//  launch, not only once the list was first opened.
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OpenClicky.Core.Plugins.ClipboardList;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Plugins.ClipboardList;

internal sealed class ClipboardIslandView : Grid, IIslandContent
{
    /// <summary>The app's one store (the Mac's ClipboardStore.shared).</summary>
    public static ClipboardStore Store { get; } = new(new WindowsClipboardListSource());

    private const string CheckGlyph = "";
    private static readonly SolidColorBrush RowFill = DS.Brush(Colors.White, 0.07);
    private static readonly SolidColorBrush RowHover = DS.Brush(Colors.White, 0.12);

    private readonly TextBlock heading = Text("Clipboard", 12, IslandDashUi.SecondaryTextBrush, FontWeights.SemiBold);
    private readonly Border clear;
    private readonly StackPanel rows = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly ScrollViewer scroller;
    private readonly TextBlock emptyMessage = Text("Copy something and it shows up here.", 12, DS.Brush(Colors.White, 0.45));
    private Guid? copiedId;
    private int copiedGeneration;

    public ClipboardIslandView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(heading, Dock.Left);
        header.Children.Add(heading);
        clear = IslandDashUi.PillButton("Clear", Store.Clear, "forget everything in this list");
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        Children.Add(header);

        scroller = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        SetRow(scroller, 1);
        Children.Add(scroller);
        emptyMessage.HorizontalAlignment = HorizontalAlignment.Center;
        SetRow(emptyMessage, 1);
        Children.Add(emptyMessage);

        Loaded += (_, _) =>
        {
            Store.Changed += OnStoreChanged;
            Refresh();
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
        var entries = Store.History.Entries;
        var isEmpty = entries.Count == 0;
        heading.Text = isEmpty ? "Clipboard" : "Copied lately";
        clear.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        emptyMessage.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        scroller.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        rows.Children.Clear();
        foreach (var entry in entries)
        {
            var row = Row(entry);
            if (rows.Children.Count > 0) row.Margin = new Thickness(0, 4, 0, 0);
            rows.Children.Add(row);
        }
    }

    public void Tick() { }

    private Border Row(ClipboardEntry entry)
    {
        var isCopied = copiedId == entry.Id;
        var line = new DockPanel { LastChildFill = true };
        var glyph = Glyph(isCopied ? CheckGlyph : entry.Content.Glyph, 11, DS.Brush(Colors.White, 0.7));
        glyph.Width = 16;
        glyph.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(glyph, Dock.Left);
        line.Children.Add(glyph);
        line.Children.Add(Text(isCopied ? "Copied" : entry.Content.Label, 11, DS.Brush(Colors.White, isCopied ? 1 : 0.9), FontWeights.Medium,
            trimming: TextTrimming.CharacterEllipsis));
        var row = Clickable(line, () => Copy(entry), entry.Content.Detail, RowFill, new CornerRadius(7));
        row.Padding = new Thickness(8, 5, 8, 5);
        row.MouseEnter += (_, _) => row.Background = RowHover;
        row.MouseLeave += (_, _) => row.Background = RowFill;
        AutomationProperties.SetName(row, entry.Content.Label);
        AutomationProperties.SetHelpText(row, "copies it again");
        return row;
    }

    private async void Copy(ClipboardEntry entry)
    {
        Store.CopyBack(entry);
        copiedId = entry.Id;
        var generation = ++copiedGeneration;
        Refresh();
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        if (generation != copiedGeneration) return;
        copiedId = null;
        Refresh();
    }
}
