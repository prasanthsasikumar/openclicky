// Ports Dictation/UI/KeyboardSketch.swift: a laptop keyboard, sketched, with a fingertip pressing
// and holding the dictation key, because "right Ctrl" reads as the left keys to some people. The
// Mac drew a MacBook's bottom row; this is a Windows laptop's: Ctrl, Fn, Win, Alt, the space bar,
// Alt (AltGr on many layouts), Ctrl and the arrows, with Caps Lock and the Shift keys above.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Windows.AppWindow;

internal sealed class KeyboardSketch : FrameworkElement
{
    private const double RowWidthInKeys = 15;

    /// <summary>Key widths in key units; the caps lock and shift rows are labelled at their ends.</summary>
    private static readonly double[][] UpperRows =
    [
        [.. Enumerable.Repeat(1.0, 13), 2],
        [1.5, .. Enumerable.Repeat(1.0, 12), 1.5],
        [1.75, .. Enumerable.Repeat(1.0, 11), 2.25],
        [2.25, .. Enumerable.Repeat(1.0, 10), 2.75],
    ];

    private static readonly (double Width, string Label)[] BottomRow =
    [
        (1.25, "Ctrl"), (1, "Fn"), (1.25, "Win"), (1.25, "Alt"), (4.75, ""), (1.25, "Alt"), (1.25, "Ctrl"), (1, "◀"), (1, "▲▼"), (1, "▶"),
    ];

    private readonly DictationHotkey key;
    private readonly bool animates;
    private DateTime startedAt = DateTime.Now;

    public KeyboardSketch(DictationHotkey key, bool animates)
    {
        this.key = key;
        this.animates = animates;
        MaxWidth = 460;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Loaded += (_, _) =>
        {
            startedAt = DateTime.Now;
            if (this.animates) CompositionTarget.Rendering += OnRendering;
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
        System.Windows.Automation.AutomationProperties.SetName(this, $"Keyboard sketch: {Caption(key)}");
    }

    public static string Caption(DictationHotkey key) => key switch
    {
        DictationHotkey.RightControl => "Right Ctrl is right of the space bar, after Alt, by the arrows. Some laptops have none: then pick another key.",
        DictationHotkey.RightAlt => "Right Alt (AltGr on many keyboards) is the key just right of the space bar.",
        DictationHotkey.RightShift => "Right Shift is the long key above the arrows, under Enter.",
        _ => "Caps Lock sits on the left, above Shift.",
    };

    /// <summary>The sketch with its caption under it, for the "Where the key is" row.</summary>
    public static FrameworkElement WithCaption(DictationHotkey key, bool animates)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460, Margin = new Thickness(0, 6, 0, 4) };
        stack.Children.Add(new KeyboardSketch(key, animates));
        var caption = Ui.Text(Caption(key), SettingsLook.CaptionSize, SettingsLook.TextSecondary);
        caption.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(caption);
        return stack;
    }

    /// <summary>0 → 1 → hold → 0 over a 2.4 s loop: the fingertip comes down, holds the key like a take, and lifts.</summary>
    public static double PressAmount(double seconds)
    {
        var phase = seconds % 2.4;
        if (phase < 0.5) return 0;
        if (phase < 0.75) return (phase - 0.5) / 0.25;
        if (phase < 1.75) return 1;
        if (phase < 2.0) return 1 - (phase - 1.75) / 0.25;
        return 0;
    }

    private TimeSpan lastRender;

    private void OnRendering(object? sender, EventArgs arguments)
    {
        // About 30 frames a second is plenty for a fingertip.
        if (arguments is RenderingEventArgs rendering)
        {
            if ((rendering.RenderingTime - lastRender).TotalMilliseconds < 30) return;
            lastRender = rendering.RenderingTime;
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 460 : Math.Min(460, availableSize.Width);
        return new Size(width, width * 5.6 / 14.5);
    }

    private Color ResourceColor(string key, Color fallback) =>
        TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    private static Brush WithOpacity(Color color, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>Which key is pressed: (row index, key index), row 4 being the bottom row.</summary>
    private (int Row, int Index) PressedKey => key switch
    {
        DictationHotkey.RightControl => (4, 6),
        DictationHotkey.RightAlt => (4, 5),
        DictationHotkey.RightShift => (3, UpperRows[3].Length - 1),
        _ => (2, 0),
    };

    private static string UpperLabel(int row, int index, int count) => (row, index) switch
    {
        (2, 0) => "Caps",
        (3, 0) => "Shift",
        _ when row == 3 && index == count - 1 => "Shift",
        _ when row == 2 && index == count - 1 => "Enter",
        _ when row == 0 && index == count - 1 => "⌫",
        _ => "",
    };

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = RenderSize;
        if (size.Width <= 0) return;
        var height = size.Width * 5.6 / 14.5;
        var gap = Math.Max(2, size.Width * 0.006);
        const int rowCount = 5;
        var keyHeight = (height - gap * (rowCount - 1)) / rowCount;
        var ink = ResourceColor(SettingsLook.TextPrimary, Colors.Black);
        var accent = ResourceColor(SettingsLook.Accent, SettingsLook.AccentLight);
        var quietFill = WithOpacity(ink, 0.06);
        var quietStroke = new Pen(WithOpacity(ink, 0.16), 0.75);
        var labelBrush = WithOpacity(ink, 0.62);
        var press = animates ? PressAmount((DateTime.Now - startedAt).TotalSeconds) : 1;
        var pressed = PressedKey;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var boldTypeface = new Typeface(typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var iconTypeface = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double UnitWidth(int keys) => (size.Width - gap * (keys - 1)) / RowWidthInKeys;

        void DrawKey(Rect rect, string label, bool isPressed, bool isBottomRow)
        {
            var sink = isPressed ? press * keyHeight * 0.06 : 0;
            var keyRect = new Rect(rect.X, rect.Y + sink, rect.Width, rect.Height - sink);
            var radius = keyHeight * 0.18;
            if (isPressed)
            {
                drawingContext.DrawRoundedRectangle(WithOpacity(accent, 0.35 + 0.65 * press), new Pen(new SolidColorBrush(accent), 1.5), keyRect, radius, radius);
            }
            else
            {
                drawingContext.DrawRoundedRectangle(isBottomRow || label.Length > 0 ? WithOpacity(ink, 0.09) : quietFill, quietStroke, keyRect, radius, radius);
            }
            if (label.Length > 0)
            {
                var isIcon = label.Length == 1 && label[0] >= '';
                var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    isIcon ? iconTypeface : isPressed ? boldTypeface : typeface, keyHeight * (isIcon ? 0.36 : 0.3),
                    isPressed ? Brushes.White : labelBrush, pixelsPerDip);
                drawingContext.DrawText(text, new Point(keyRect.X + (keyRect.Width - text.Width) / 2, keyRect.Y + (keyRect.Height - text.Height) / 2));
            }
            if (isPressed)
            {
                // The fingertip: comes down onto the key and rests there while it is held.
                var lift = (1 - press) * keyHeight * 0.9;
                var diameter = keyHeight * 0.95;
                var center = new Point(keyRect.X + keyRect.Width / 2, keyRect.Y + keyRect.Height / 2 - lift - keyHeight * 0.15);
                drawingContext.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xD9, 0xED, 0xC7, 0xA8)), new Pen(new SolidColorBrush(Color.FromArgb(0x2E, 0, 0, 0)), 0.8),
                    center, diameter / 2, diameter / 2);
            }
        }

        for (var rowIndex = 0; rowIndex < UpperRows.Length; rowIndex++)
        {
            var row = UpperRows[rowIndex];
            var unit = UnitWidth(row.Length);
            var x = 0.0;
            var y = rowIndex * (keyHeight + gap);
            for (var index = 0; index < row.Length; index++)
            {
                var width = row[index] * unit;
                DrawKey(new Rect(x, y, width, keyHeight), UpperLabel(rowIndex, index, row.Length), pressed == (rowIndex, index), false);
                x += width + gap;
            }
        }
        var bottomUnit = UnitWidth(BottomRow.Length);
        var bottomX = 0.0;
        var bottomY = UpperRows.Length * (keyHeight + gap);
        for (var index = 0; index < BottomRow.Length; index++)
        {
            var width = BottomRow[index].Width * bottomUnit;
            var label = BottomRow[index].Label;
            if (index == 5 && key == DictationHotkey.RightAlt) label = "AltGr";
            DrawKey(new Rect(bottomX, bottomY, width, keyHeight), label, pressed == (4, index), true);
            bottomX += width + gap;
        }
    }
}
