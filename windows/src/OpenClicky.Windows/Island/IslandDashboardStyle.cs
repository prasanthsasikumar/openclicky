//
//  IslandDashboardStyle.cs
//  OpenClicky (Windows port of IslandDashboardStyle.swift)
//
//  The open island's dashboard look, shared by every column and tile on it: white at low opacity
//  on the black or see-through island, soft corners, small semibold type. The sizes are Core's
//  (OpenClicky.Core.Companion.IslandDash); the brushes and the controls are here: a tile's chrome,
//  a round icon button that lights up under the pointer, and a small pill button.
//

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenClicky.Core.Companion;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Island;

internal static class IslandDashUi
{
    public static readonly SolidColorBrush DividerBrush = DS.Brush(Colors.White, 0.08);
    public static readonly SolidColorBrush TileFillBrush = DS.Brush(Color.FromRgb(28, 28, 28));
    public static readonly SolidColorBrush TileBorderBrush = DS.Brush(Colors.White, 0.08);
    /// <summary>Round icon buttons (the band's tools, transport) are white 0.12 under the pointer.</summary>
    public static readonly SolidColorBrush HoverFillBrush = DS.Brush(Colors.White, 0.12);
    public static readonly SolidColorBrush PillFillBrush = DS.Brush(Colors.White, 0.16);
    public static readonly SolidColorBrush PillPressedBrush = DS.Brush(Colors.White, 0.25);
    public static readonly SolidColorBrush TrackBrush = DS.Brush(Colors.White, 0.18);
    public static readonly SolidColorBrush SecondaryTextBrush = DS.Brush(Colors.White, 0.55);
    /// <summary>Today's number in the calendar's week strip (the system red).</summary>
    public static readonly SolidColorBrush TodayBrush = DS.Brush(Color.FromRgb(255, 69, 58));
    /// <summary>Symbols that are pictures rather than icons (the weather's sky).</summary>
    public static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    /// <summary>A 1-wide hairline between home's columns, inset 6 top and bottom.</summary>
    public static Border Divider() => new() { Width = 1, Background = DividerBrush, Margin = new Thickness(0, 6, 0, 6) };

    /// <summary>The chrome every widget tile shares: the faint fill (or a background of its own, the weather's sky), the
    /// hairline border, the soft corners.</summary>
    public static Border Tile(UIElement content, Brush? background = null, double padding = 10) => new()
    {
        Width = IslandDash.TileSize,
        Height = IslandDash.TileSize,
        CornerRadius = new CornerRadius(IslandDash.TileRadius),
        Background = background ?? TileFillBrush,
        BorderBrush = TileBorderBrush,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(padding),
        Child = content,
        ClipToBounds = true,
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>A small pill button on the island ("Clear", "Allow calendar access").</summary>
    public static Border PillButton(string title, Action onClick, string? toolTip = null)
    {
        var label = IslandUi.Text(title, 11, DS.HUD.TextBrush, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var pill = IslandUi.Clickable(label, onClick, toolTip ?? title, PillFillBrush, new CornerRadius(11));
        pill.Padding = new Thickness(12, 4, 12, 5);
        pill.HorizontalAlignment = HorizontalAlignment.Center;
        pill.VerticalAlignment = VerticalAlignment.Center;
        pill.PreviewMouseLeftButtonDown += (_, _) => pill.Background = PillPressedBrush;
        pill.PreviewMouseLeftButtonUp += (_, _) => pill.Background = PillFillBrush;
        pill.MouseLeave += (_, _) => pill.Background = PillFillBrush;
        return pill;
    }
}

/// <summary>A round icon button on the island: the glyph in a circle that lights up under the pointer (or while it is the
/// active one) and shrinks a touch while pressed.</summary>
internal sealed class IslandIconButton : Border
{
    private readonly TextBlock glyph;
    private readonly ScaleTransform pressScale = new(1, 1);
    private bool isActive;
    private bool isHovered;
    private bool isPressed;
    private readonly Action onClick;

    /// <param name="size">The glyph's size; the circle is <paramref name="size"/> × <paramref name="frameScale"/>.</param>
    public IslandIconButton(string glyph, double size, string toolTip, Action onClick, double frameScale = 2, bool isActive = false,
                            FontFamily? font = null)
    {
        this.onClick = onClick;
        this.glyph = IslandUi.Glyph(glyph, size, DS.HUD.TextBrush);
        if (font is not null) this.glyph.FontFamily = font;
        Child = this.glyph;
        Width = Math.Round(size * frameScale);
        Height = Math.Round(size * frameScale);
        CornerRadius = new CornerRadius(Width / 2);
        Cursor = Cursors.Hand;
        ToolTip = toolTip;
        Focusable = false;
        SnapsToDevicePixels = true;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = pressScale;
        AutomationProperties.SetName(this, toolTip);
        IsActive = isActive;
        MouseEnter += (_, _) => { isHovered = true; Restyle(); };
        MouseLeave += (_, _) => { isHovered = false; isPressed = false; Restyle(); };
        MouseLeftButtonDown += (_, eventArgs) => { isPressed = true; eventArgs.Handled = true; Restyle(); };
        MouseLeftButtonUp += (_, eventArgs) =>
        {
            if (!isPressed) return;
            isPressed = false;
            eventArgs.Handled = true;
            Restyle();
            if (IsEnabled) this.onClick();
        };
        IsEnabledChanged += (_, _) => Restyle();
    }

    public bool IsActive
    {
        get => isActive;
        set
        {
            isActive = value;
            AutomationProperties.SetItemStatus(this, value ? "selected" : "");
            Restyle();
        }
    }

    public string Glyph
    {
        get => glyph.Text;
        set => glyph.Text = value;
    }

    public void SetToolTip(string toolTip)
    {
        ToolTip = toolTip;
        AutomationProperties.SetName(this, toolTip);
    }

    private void Restyle()
    {
        var lit = IsEnabled && (isActive || isHovered || isPressed);
        Background = lit ? IslandDashUi.HoverFillBrush : Brushes.Transparent;
        glyph.Opacity = !IsEnabled ? 0.4 : lit ? 1 : 0.85;
        var scale = isPressed ? 0.88 : 1;
        pressScale.ScaleX = scale;
        pressScale.ScaleY = scale;
    }
}
