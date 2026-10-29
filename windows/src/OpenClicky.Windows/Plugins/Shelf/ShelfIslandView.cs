//
//  ShelfIslandView.cs
//  OpenClicky (Windows port of Plugins/Shelf/ShelfIslandView.swift)
//
//  The Shelf plugin's row: a tile for each thing dropped on the island. A tile drags out (an OLE drag,
//  so the shelf learns whether the drop landed and lets the tile go), a click copies it, hovering shows
//  a cross that removes it. While a drag hovers the island, a dashed tile shows where the drop lands.
//

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins.Shelf;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Plugins.Shelf;

internal sealed class ShelfIslandView : Grid, IIslandContent
{
    /// <summary>The app's one shelf (the Mac's NotchHUDManager.shelf).</summary>
    public static ShelfStore Store { get; } = new(ShelfStore.DefaultDirectory, ShelfDataObjects.FileIcon);

    private const double TileWidth = 76;
    private const double TileHeight = 70;
    private const double IconSize = 40;
    /// <summary>How far a press moves before it is a drag rather than a click.</summary>
    private const double DragThreshold = 4;

    private readonly IslandModel model;
    private readonly StackPanel tiles = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer scroller;
    private readonly TextBlock emptyMessage;
    private Guid? copiedId;

    public ShelfIslandView(IslandModel model)
    {
        this.model = model;
        emptyMessage = IslandUi.Text("drop files, text or links on the island to keep them handy", 12, DS.HUD.Text3Brush,
            wrapping: TextWrapping.Wrap);
        emptyMessage.TextAlignment = TextAlignment.Center;
        emptyMessage.HorizontalAlignment = HorizontalAlignment.Center;
        emptyMessage.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(emptyMessage);
        scroller = new ScrollViewer
        {
            Content = tiles,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        Children.Add(scroller);
        // A wheel turned over the row scrolls it sideways.
        scroller.PreviewMouseWheel += (_, eventArgs) =>
        {
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - eventArgs.Delta / 3.0);
            eventArgs.Handled = true;
        };
        Loaded += (_, _) =>
        {
            Store.Changed += Refresh;
            model.PropertyChanged += OnModelPropertyChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Store.Changed -= Refresh;
            model.PropertyChanged -= OnModelPropertyChanged;
        };
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(IslandModel.IsDragHovering)) Refresh();
    }

    public void Refresh()
    {
        var isEmpty = Store.Items.Count == 0 && !model.IsDragHovering;
        emptyMessage.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        scroller.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        tiles.Children.Clear();
        if (model.IsDragHovering) tiles.Children.Add(DropTarget());
        foreach (var item in Store.Items)
        {
            var tile = Tile(item);
            if (tiles.Children.Count > 0) tile.Margin = new Thickness(8, 0, 0, 0);
            tiles.Children.Add(tile);
        }
    }

    public void Tick() { }

    /// <summary>The leading tile while a drag hovers: where the drop will land.</summary>
    private static FrameworkElement DropTarget()
    {
        var grid = new Grid { Width = TileWidth, Height = TileHeight };
        grid.Children.Add(new Rectangle
        {
            Stroke = DS.HUD.Text3Brush,
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            RadiusX = DS.HUD.TileRadius,
            RadiusY = DS.HUD.TileRadius,
        });
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var arrow = IslandUi.Glyph("", 16, DS.HUD.Text2Brush);
        arrow.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(arrow);
        var label = IslandUi.Text("drop to keep", 11, DS.HUD.Text3Brush);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(label);
        grid.Children.Add(stack);
        return grid;
    }

    private FrameworkElement Tile(ShelfItem item)
    {
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var icon = TileIcon(item);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(icon);
        var isCopied = copiedId == item.Id;
        var title = IslandUi.Text(isCopied ? "copied" : item.Title, 11, isCopied ? DS.HUD.TextBrush : DS.HUD.Text2Brush,
            trimming: TextTrimming.CharacterEllipsis);
        title.MaxWidth = TileWidth - 4;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(title);

        var tile = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            CornerRadius = new CornerRadius(DS.HUD.TileRadius),
            Background = Brushes.Transparent,
            Child = body,
            ToolTip = item.Title,
            Cursor = Cursors.Hand,
        };
        AutomationProperties.SetName(tile, item.Title);

        var remove = IslandUi.Clickable(IslandUi.Glyph("", 9, DS.HUD.Text2Brush), () => Store.Remove(item.Id),
            "remove from the shelf", DS.HUD.IslandBrush, new CornerRadius(8));
        remove.Width = 16;
        remove.Height = 16;
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = new Thickness(0, -4, -4, 0);
        remove.Visibility = Visibility.Collapsed;

        var holder = new Grid();
        holder.Children.Add(tile);
        holder.Children.Add(remove);
        holder.MouseEnter += (_, _) =>
        {
            tile.Background = DS.HUD.SurfaceRaisedSoftBrush;
            remove.Visibility = Visibility.Visible;
        };
        holder.MouseLeave += (_, _) =>
        {
            tile.Background = Brushes.Transparent;
            remove.Visibility = Visibility.Collapsed;
        };

        // A press and 4 px of movement begin a drag; a release without movement is a click (copy).
        Point? pressPoint = null;
        tile.MouseLeftButtonDown += (_, eventArgs) =>
        {
            pressPoint = eventArgs.GetPosition(tile);
            eventArgs.Handled = true;
        };
        tile.MouseMove += (_, eventArgs) =>
        {
            if (pressPoint is not { } start || eventArgs.LeftButton != MouseButtonState.Pressed) return;
            var now = eventArgs.GetPosition(tile);
            if (Math.Abs(now.X - start.X) < DragThreshold && Math.Abs(now.Y - start.Y) < DragThreshold) return;
            pressPoint = null;
            DragOut(tile, item);
        };
        tile.MouseLeftButtonUp += (_, eventArgs) =>
        {
            // Handled: the island's own click (open / close the panel) must not run under a tile.
            eventArgs.Handled = true;
            if (pressPoint is null) return;
            pressPoint = null;
            Copy(item);
        };
        return holder;
    }

    private void DragOut(FrameworkElement tile, ShelfItem item)
    {
        // A file that went away since it was dropped has nothing to hand over: the tile goes.
        if (item.Kind == ShelfItemKind.File && !File.Exists(item.Value) && !Directory.Exists(item.Value))
        {
            Store.Remove(item.Id);
            return;
        }
        var allowed = ShelfDragPolicy.AllowsMove(item.Kind) ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy;
        model.SetDragOut(true);
        DragDropEffects result;
        try
        {
            result = DragDrop.DoDragDrop(tile, ShelfDataObjects.For(item, withMarker: true), allowed);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            result = DragDropEffects.None;
        }
        finally
        {
            model.SetDragOut(false);
        }
        if (ShelfDragPolicy.RemovesAfterDrag(result != DragDropEffects.None)) Store.Remove(item.Id);
    }

    private async void Copy(ShelfItem item)
    {
        try
        {
            Clipboard.SetDataObject(ShelfDataObjects.For(item, withMarker: false), copy: true);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            return;
        }
        copiedId = item.Id;
        Refresh();
        await Task.Delay(1200);
        if (copiedId != item.Id) return;
        copiedId = null;
        Refresh();
    }

    /// <summary>The tile's picture: the saved file icon or the image itself, a glyph for a link or a note.</summary>
    private static FrameworkElement TileIcon(ShelfItem item)
    {
        var path = item.Kind switch
        {
            ShelfItemKind.Image => item.Value,
            ShelfItemKind.File => item.IconPath,
            _ => null,
        };
        if (path is not null && LoadImage(path) is { } image)
        {
            var picture = new Image { Source = image, Width = IconSize, Height = IconSize, Stretch = item.Kind == ShelfItemKind.Image ? Stretch.UniformToFill : Stretch.Uniform };
            if (item.Kind != ShelfItemKind.Image) return picture;
            picture.Clip = new RectangleGeometry(new Rect(0, 0, IconSize, IconSize), 6, 6);
            return picture;
        }
        var glyph = item.Kind switch
        {
            ShelfItemKind.Image => "",
            ShelfItemKind.Link => "",
            ShelfItemKind.Text => "",
            _ => "",
        };
        var symbol = IslandUi.Glyph(glyph, 18, DS.HUD.Text2Brush);
        symbol.Width = IconSize;
        symbol.Height = IconSize;
        symbol.TextAlignment = TextAlignment.Center;
        symbol.Padding = new Thickness(0, 10, 0, 0);
        return symbol;
    }

    /// <summary>Loaded whole and closed at once, so the shelf never holds the file open.</summary>
    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 80;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
