//
//  IslandUi.cs
//  OpenClicky (Windows)
//
//  Small builders for the island's hand-drawn controls (the Mac draws every control by hand too:
//  the island never becomes key, and system controls grey out in a panel that isn't). Every
//  clickable thing shows the hand cursor (the Mac's pointerCursor()) and dims a touch on hover.
//

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Island;

internal static class IslandUi
{
    public static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null, FontFamily? family = null,
        TextTrimming trimming = TextTrimming.None, TextWrapping wrapping = TextWrapping.NoWrap)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = family ?? DS.Fonts.Ui,
            TextTrimming = trimming,
            TextWrapping = wrapping,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    public static TextBlock Glyph(string glyph, double size, Brush brush) => new()
    {
        Text = glyph,
        FontFamily = DS.Fonts.Icons,
        FontSize = size,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    /// <summary>A plain clickable surface: hand cursor, a tooltip, dims on hover, raises <paramref name="onClick"/> on
    /// release over it.</summary>
    public static Border Clickable(UIElement content, Action onClick, string? toolTip = null, Brush? background = null,
        CornerRadius? cornerRadius = null, bool isEnabled = true)
    {
        var border = new Border
        {
            Child = content,
            Background = background ?? Brushes.Transparent,
            CornerRadius = cornerRadius ?? new CornerRadius(0),
            Cursor = isEnabled ? Cursors.Hand : Cursors.Arrow,
            ToolTip = toolTip,
            SnapsToDevicePixels = true,
            Opacity = isEnabled ? 1 : 0.5,
            Focusable = false,
        };
        if (toolTip is not null) AutomationPropertiesName(border, toolTip);
        if (!isEnabled) return border;
        var isPressed = false;
        border.MouseEnter += (_, _) => border.Opacity = 0.85;
        border.MouseLeave += (_, _) => { border.Opacity = 1; isPressed = false; };
        border.MouseLeftButtonDown += (_, eventArgs) => { isPressed = true; eventArgs.Handled = true; };
        border.MouseLeftButtonUp += (_, eventArgs) =>
        {
            if (!isPressed) return;
            isPressed = false;
            eventArgs.Handled = true;
            onClick();
        };
        return border;
    }

    private static void AutomationPropertiesName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    /// <summary>A round band button (24, the band-control fill) with a glyph.</summary>
    public static Border BandIconButton(string glyph, string toolTip, Action onClick)
    {
        var grid = new Grid { Width = DS.HUD.PillHeight, Height = DS.HUD.PillHeight };
        grid.Children.Add(new Ellipse { Fill = DS.HUD.BandControlBrush });
        grid.Children.Add(Glyph(glyph, 11, DS.HUD.Text2Brush));
        return Clickable(grid, onClick, toolTip, cornerRadius: new CornerRadius(DS.HUD.PillHeight / 2));
    }

    /// <summary>A key as the island draws it: monospaced on a raised 5-radius keycap (NotchKeycap).</summary>
    public static Border Keycap(string text, bool isSmall)
    {
        return new Border
        {
            Background = DS.HUD.SurfaceRaisedBrush,
            CornerRadius = new CornerRadius(5),
            Padding = isSmall ? new Thickness(4, 0, 4, 1) : new Thickness(6, 2, 6, 2),
            Child = Text(text, isSmall ? 9.5 : 11, DS.HUD.TextBrush, family: DS.Fonts.Mono),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>The cards' primary button (30 tall, 9-radius, black text on the pointer colour or white).</summary>
    public static Border PrimaryButton(string title, Action onClick, Brush? fill = null, double minWidth = 72)
    {
        var label = Text(title, 13, Brushes.Black, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var border = Clickable(label, onClick, background: fill ?? DS.HUD.PointerBrush, cornerRadius: new CornerRadius(9));
        border.Height = 30;
        border.MinWidth = minWidth;
        border.Padding = new Thickness(14, 0, 14, 0);
        return border;
    }

    /// <summary>A quiet text button ("later", "skip"): 24 tall, text2.</summary>
    public static Border QuietButton(string title, Action onClick, string? toolTip = null, double minWidth = 72)
    {
        var label = Text(title, 12, DS.HUD.Text2Brush);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var border = Clickable(label, onClick, toolTip);
        border.Height = 24;
        border.MinWidth = minWidth;
        return border;
    }

    /// <summary>A progress dash.</summary>
    public static Border Dash(double width, Brush fill) => new()
    {
        Width = width,
        Height = 3,
        CornerRadius = new CornerRadius(1.5),
        Background = fill,
        Margin = new Thickness(0, 0, 5, 0),
    };

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var index = 0; index < children.Length; index++)
        {
            if (children[index] is FrameworkElement element && index < children.Length - 1)
            {
                element.Margin = new Thickness(element.Margin.Left, element.Margin.Top, element.Margin.Right + spacing, element.Margin.Bottom);
            }
            panel.Children.Add(children[index]);
        }
        return panel;
    }

    /// <summary>The tail of <paramref name="text"/> that fits <paramref name="maxWidth"/>, with a leading ellipsis
    /// (SwiftUI's truncationMode(.head)); WPF only trims at the end.</summary>
    public static string TrimHead(string text, double maxWidth, double fontSize, FontFamily family, FontWeight weight, double pixelsPerDip)
    {
        double Measure(string candidate) =>
            new FormattedText(candidate, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), fontSize, Brushes.White, pixelsPerDip).WidthIncludingTrailingWhitespace;
        if (Measure(text) <= maxWidth) return text;
        var start = 1;
        while (start < text.Length && Measure("…" + text[start..]) > maxWidth) start++;
        return "…" + text[start..];
    }
}
