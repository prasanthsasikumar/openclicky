//
//  DS.cs
//  OpenClicky (Windows port of DesignSystem.swift)
//
//  The shared tokens the companion UI draws with: the overlay pointer and agent card colours, the
//  island's always-dark palette and sizes, and the fonts (Segoe UI Variable for SF Pro, a
//  monospaced face for SF Mono, Segoe Fluent Icons for SF Symbols). The same tokens are in
//  OpenClickyTheme.xaml for XAML; this class is what code-built views use.
//

using System.Windows;
using System.Windows.Media;

namespace OpenClicky.Windows.Theme;

public static class DS
{
    public static Color Hex(string hex)
    {
        var sanitized = hex.Trim().TrimStart('#');
        var value = Convert.ToUInt32(sanitized, 16);
        return Color.FromRgb((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
    }

    public static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * opacity), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static class Colors
    {
        /// <summary>The agent result card's status dot once the agent has answered (Tailwind Blue 600).</summary>
        public static readonly Color Accent = Hex("#2563eb");
        /// <summary>The agent result card's status dot while the companion is still working (Radix Amber 9).</summary>
        public static readonly Color Warning = Hex("#FFB224");
        /// <summary>The pointer and its bubbles: red-orange like the current HeyClicky cursor.</summary>
        public static readonly Color OverlayCursor = Hex("#F0452B");

        public static readonly SolidColorBrush OverlayCursorBrush = Brush(OverlayCursor);
    }

    /// <summary>The island, always dark: it is part of the top of the screen, not the app's appearance.</summary>
    public static class HUD
    {
        public static readonly Color Island = Hex("#000000");
        /// <summary>Pills and the gear in the band, a step lighter than the island.</summary>
        public static readonly Color BandControl = Hex("#1A1A1A");
        public static readonly Color SurfaceRaised = Hex("#262626");
        /// <summary>The (i) button and other secondary squares on a surface.</summary>
        public static readonly Color SurfaceRaisedSoft = Hex("#2A2A2A");
        public static readonly Color Text = Hex("#FFFFFF");
        public static readonly Color Text2 = Hex("#A3A3A3");
        public static readonly Color Text3 = Hex("#8A8A8A");
        public static readonly Color Pointer = Hex("#FF5A3C");
        /// <summary>The "!" disc behind a permission card's mark.</summary>
        public static readonly Color PointerTint = Hex("#2A1A14");
        public static readonly Color Live = Hex("#34C77B");
        /// <summary>The tour's "heard you" line.</summary>
        public static readonly Color Good = Color.FromRgb(102, 217, 128);
        /// <summary>The connect card's example chips.</summary>
        public static readonly Color ChipText = Hex("#D4D4D4");

        public static readonly SolidColorBrush IslandBrush = Brush(Island);
        public static readonly SolidColorBrush BandControlBrush = Brush(BandControl);
        public static readonly SolidColorBrush SurfaceRaisedBrush = Brush(SurfaceRaised);
        public static readonly SolidColorBrush SurfaceRaisedSoftBrush = Brush(SurfaceRaisedSoft);
        public static readonly SolidColorBrush TextBrush = Brush(Text);
        public static readonly SolidColorBrush Text2Brush = Brush(Text2);
        public static readonly SolidColorBrush Text3Brush = Brush(Text3);
        public static readonly SolidColorBrush PointerBrush = Brush(Pointer);
        public static readonly SolidColorBrush PointerTintBrush = Brush(PointerTint);
        public static readonly SolidColorBrush LiveBrush = Brush(Live);
        public static readonly SolidColorBrush GoodBrush = Brush(Good);
        public static readonly SolidColorBrush ChipTextBrush = Brush(ChipText);

        public const double PillHeight = 24;
        public const double TileSize = 40;
        public const double MinimumHitSize = 22;
        public const double TileRadius = 10;
        public const double IslandRadius = 22;
        public const double BodySidePadding = 16;
        public const double StripWidth = 380;
        public const double StripHeight = 37;
    }

    public static class Fonts
    {
        /// <summary>SF Pro → Segoe UI Variable (Text optical size for UI text), Segoe UI before Windows 11.</summary>
        public static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
        /// <summary>Headlines (15 pt semibold on the cards).</summary>
        public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
        /// <summary>SF Mono → Cascadia Mono, Consolas.</summary>
        public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
        /// <summary>SF Symbols → Segoe Fluent Icons (Windows 11), Segoe MDL2 Assets (Windows 10).</summary>
        public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
        /// <summary>The (i)'s serif italic.</summary>
        public static readonly FontFamily Serif = new("Georgia, Cambria, Times New Roman");

        public static readonly FontWeight Medium = FontWeights.Medium;
        public static readonly FontWeight Semibold = FontWeights.SemiBold;
    }

    /// <summary>The shared resource dictionary (OpenClickyTheme.xaml), for windows that use XAML resources.</summary>
    public static ResourceDictionary LoadResources() =>
        new() { Source = new Uri("pack://application:,,,/OpenClicky;component/Theme/OpenClickyTheme.xaml", UriKind.Absolute) };
}
