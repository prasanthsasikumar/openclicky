// Settings → Clicky → Pointer: the pointer's colour and shape picked from tiles instead of lists. Each tile is the
// pointer itself, glowing as it does on screen, on a dark face in both themes (a glow only reads on dark; a white
// pointer would vanish on a light tile): the colour row draws every colour in the chosen shape, the shape row
// every shape in the chosen colour, so the two rows always show what the pointer will look like. The chosen tile has
// the accent border and a check.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Settings;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.AppWindow;

internal static class PointerTiles
{
    private const double TileSize = 52;
    private const double PointerSize = 20;
    private static readonly Brush FaceBrush = DS.Brush(DS.Hex("#262A33"));
    private static readonly Brush FaceEdgeBrush = DS.Brush(DS.Hex("#3B404B"));

    /// <summary>The row for "clicky.pointerColor" or "clicky.pointerStyle": its title and copy, then its tiles.</summary>
    public static UIElement Row(SettingsItem item, string query, Preferences preferences)
    {
        var colorName = PointerLook.Colors.Any(color => color.Name == preferences.GetString(PointerLook.ColorPreferenceKey))
            ? preferences.GetString(PointerLook.ColorPreferenceKey)!
            : PointerLook.DefaultColor;
        var color = DS.Hex(PointerLook.HexFor(colorName));
        var style = PointerLook.StyleFrom(preferences.GetString(PointerLook.StylePreferenceKey));

        var tiles = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (item.Id == "clicky.pointerColor")
        {
            foreach (var (name, hex, title) in PointerLook.Colors)
            {
                tiles.Children.Add(Tile(PointerTriangleView.Preview(DS.Hex(hex), style, PointerSize, 8), title, caption: null,
                    isSelected: name == colorName, () => preferences.Set(PointerLook.ColorPreferenceKey, name)));
            }
        }
        else
        {
            foreach (var shape in PointerLook.Styles)
            {
                tiles.Children.Add(Tile(PointerTriangleView.Preview(color, shape, PointerSize, 8), shape.Title(), caption: shape.Title(),
                    isSelected: shape == style, () => preferences.Set(PointerLook.StylePreferenceKey, shape.RawValue())));
            }
        }
        return Ui.VStack(0, Ui.TitleAndDetail(item.Title, item.Detail, query), tiles);
    }

    private static Button Tile(UIElement pointer, string name, string? caption, bool isSelected, Action onSelect)
    {
        var face = new Border
        {
            Width = TileSize,
            Height = TileSize,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            Child = new Grid { Children = { Centered(pointer) } },
        };
        face.Background = FaceBrush;
        if (isSelected) face.SetResourceReference(Border.BorderBrushProperty, SettingsLook.Accent);
        else face.BorderBrush = FaceEdgeBrush;

        var stack = new StackPanel();
        var faceAndCheck = new Grid { Width = TileSize, Height = TileSize };
        faceAndCheck.Children.Add(face);
        if (isSelected) faceAndCheck.Children.Add(Check());
        stack.Children.Add(faceAndCheck);
        if (caption is not null)
        {
            var label = Ui.Text(caption, SettingsLook.CaptionSize, SettingsLook.TextSecondary);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(label);
        }

        var button = new Button
        {
            Content = stack,
            Cursor = Cursors.Hand,
            Template = Ui.TransparentButtonTemplate(),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = name,
        };
        Ui.AutomationProperties(button, $"{name}{(isSelected ? ", selected" : "")}");
        button.Click += (_, _) => { if (!isSelected) onSelect(); };
        return button;
    }

    private static FrameworkElement Centered(UIElement pointer)
    {
        if (pointer is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.VerticalAlignment = VerticalAlignment.Center;
        }
        return (FrameworkElement)pointer;
    }

    /// <summary>The chosen tile's check: an accent circle with a white tick, at its top right corner.</summary>
    private static FrameworkElement Check()
    {
        var circle = new Ellipse { Width = 18, Height = 18 };
        circle.SetResourceReference(Shape.FillProperty, SettingsLook.Accent);
        var tick = new Path
        {
            Data = Geometry.Parse("M 5,9.5 L 8,12.5 L 13,6.5"),
            Stroke = Brushes.White,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        return new Grid
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -5, -5, 0),
            Children = { circle, tick },
        };
    }
}
