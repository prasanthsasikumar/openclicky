//
//  MediaDashboardColumn.cs
//  OpenClicky (Windows port of Plugins/Media/MediaDashboardColumn.swift)
//
//  The now-playing column of home: big artwork, the title and artist, a bar tinted by the artwork
//  that runs with the track (drag or click to seek), the times, and the transport. Fed by the same
//  MediaStore as the Now playing row. The Mac's speaker picker beside the transport (AirPlay) and
//  the player's icon on the artwork's corner have no Windows counterpart here.
//

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins.Media;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Plugins.Media;

internal sealed class MediaDashboardColumn : Grid, IIslandContent
{
    private const string MusicGlyph = "";
    private const string PreviousGlyph = "";
    private const string NextGlyph = "";
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private static MediaStore Store => MediaIslandView.Store;

    private readonly Border artworkFrame = new()
    {
        Width = IslandDash.ArtworkSize,
        Height = IslandDash.ArtworkSize,
        CornerRadius = new CornerRadius(IslandDash.ArtworkRadius),
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly StackPanel trackInfo = new() { Margin = new Thickness(14, 2, 0, 0) };
    private readonly StackPanel emptyInfo = new() { Margin = new Thickness(14, 8, 0, 0) };
    private readonly TextBlock title = Text("", 13, DS.HUD.TextBrush, FontWeights.Bold, trimming: TextTrimming.CharacterEllipsis);
    private readonly TextBlock artist = Text("", 12, DS.Brush(Colors.White, 0.6), trimming: TextTrimming.CharacterEllipsis);
    private readonly Grid bar = new() { Height = 10, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Border barTrack = new() { Height = 5, CornerRadius = new CornerRadius(2.5), Background = IslandDashUi.TrackBrush };
    private readonly Border barFill = new() { Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock elapsed = Text("", 10, IslandDashUi.SecondaryTextBrush, FontWeights.Medium);
    private readonly TextBlock remaining = Text("", 10, IslandDashUi.SecondaryTextBrush, FontWeights.Medium);
    private readonly IslandIconButton playPause;
    private string? shownArtworkKey;
    private Brush tint = DS.HUD.TextBrush;
    /// <summary>While dragging, where the thumb is (0…1); the bar follows the pointer, not the player.</summary>
    private double? dragFraction;

    public MediaDashboardColumn()
    {
        Width = IslandDash.MediaWidth;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        artworkFrame.Clip = new RectangleGeometry(new Rect(0, 0, IslandDash.ArtworkSize, IslandDash.ArtworkSize), IslandDash.ArtworkRadius, IslandDash.ArtworkRadius);
        AutomationProperties.SetName(artworkFrame, "artwork");
        Children.Add(artworkFrame);

        trackInfo.Children.Add(title);
        artist.Margin = new Thickness(0, 4, 0, 0);
        trackInfo.Children.Add(artist);
        trackInfo.Children.Add(Scrubber());
        var transport = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(new IslandIconButton(PreviousGlyph, 15, "previous", () => _ = Store.PreviousAsync()));
        playPause = new IslandIconButton(PlayGlyph, 20, "play", () => _ = Store.PlayPauseAsync(), frameScale: 1.7);
        buttons.Children.Add(playPause);
        buttons.Children.Add(new IslandIconButton(NextGlyph, 15, "next", () => _ = Store.NextAsync()));
        transport.Children.Add(buttons);
        trackInfo.Children.Add(transport);
        SetColumn(trackInfo, 1);
        Children.Add(trackInfo);

        emptyInfo.Children.Add(Text("Nothing playing", 14, DS.HUD.TextBrush, FontWeights.SemiBold));
        var hint = Text("Play something in Spotify, a browser or Media Player and it shows up here.", 12, IslandDashUi.SecondaryTextBrush,
            wrapping: TextWrapping.Wrap, trimming: TextTrimming.CharacterEllipsis);
        hint.MaxHeight = 50;
        hint.Margin = new Thickness(0, 4, 0, 0);
        emptyInfo.Children.Add(hint);
        SetColumn(emptyInfo, 1);
        Children.Add(emptyInfo);

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
        Refresh();
    }

    /// <summary>A 5-high track filled to the position in the artwork's tint, elapsed time on the left and what is left on
    /// the right. A click or a drag seeks; the island must not take the press as a click on itself.</summary>
    private StackPanel Scrubber()
    {
        bar.Children.Add(barTrack);
        bar.Children.Add(barFill);
        double FractionAt(MouseEventArgs eventArgs) => bar.ActualWidth > 0 ? Math.Clamp(eventArgs.GetPosition(bar).X / bar.ActualWidth, 0, 1) : 0;
        bar.MouseLeftButtonDown += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            dragFraction = FractionAt(eventArgs);
            bar.CaptureMouse();
            Tick();
        };
        bar.MouseMove += (_, eventArgs) =>
        {
            if (dragFraction is null) return;
            dragFraction = FractionAt(eventArgs);
            Tick();
        };
        bar.MouseLeftButtonUp += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            if (dragFraction is null) return;
            var target = FractionAt(eventArgs);
            dragFraction = null;
            bar.ReleaseMouseCapture();
            _ = Store.SeekAsync(target);
        };
        bar.LostMouseCapture += (_, _) => dragFraction = null;
        var times = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 3, 0, 0) };
        DockPanel.SetDock(elapsed, Dock.Left);
        DockPanel.SetDock(remaining, Dock.Right);
        times.Children.Add(elapsed);
        times.Children.Add(remaining);
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(bar);
        stack.Children.Add(times);
        return stack;
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        var snapshot = Store.Snapshot;
        trackInfo.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        emptyInfo.Visibility = snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        var artworkKey = (snapshot?.ArtworkKey ?? "") + (Store.Artwork is null ? "" : ":art");
        if (shownArtworkKey != artworkKey)
        {
            shownArtworkKey = artworkKey;
            var (picture, accent) = Artwork(Store.Artwork);
            artworkFrame.Child = picture;
            tint = accent;
            barFill.Background = tint;
        }
        if (snapshot is null) return;
        title.Text = snapshot.Title;
        artist.Text = snapshot.Artist.Length == 0 ? snapshot.PlayerName : snapshot.Artist;
        playPause.Glyph = snapshot.IsPlaying ? PauseGlyph : PlayGlyph;
        playPause.SetToolTip(snapshot.IsPlaying ? "pause" : "play");
        Tick();
    }

    /// <summary>The cover and the bar's tint (its average colour, lifted so a dark cover still shows); without one, a grey
    /// gradient with a note and a white bar.</summary>
    private static (UIElement Picture, Brush Tint) Artwork(byte[]? image)
    {
        if (image is not null)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 168;
                bitmap.StreamSource = new MemoryStream(image);
                bitmap.EndInit();
                bitmap.Freeze();
                return (new Image { Source = bitmap, Stretch = Stretch.UniformToFill }, DS.Brush(Accent(bitmap)));
            }
            catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or ArgumentException)
            {
                // A cover WPF cannot decode: the placeholder instead.
            }
        }
        var note = Glyph(MusicGlyph, IslandDash.ArtworkSize * 0.4, DS.Brush(Colors.White, 0.7));
        var placeholder = new Border
        {
            Background = new LinearGradientBrush(Color.FromRgb(128, 128, 128), Color.FromRgb(51, 51, 51), 90),
            Child = note,
        };
        return (placeholder, DS.HUD.TextBrush);
    }

    /// <summary>The average colour of a cover, keeping the hue, softening the saturation to at most 0.45 and raising the
    /// brightness to at least 0.75 so the bar shows on black.</summary>
    private static Color Accent(BitmapSource bitmap)
    {
        var small = new TransformedBitmap(bitmap, new ScaleTransform(8.0 / Math.Max(1, bitmap.PixelWidth), 8.0 / Math.Max(1, bitmap.PixelHeight)));
        var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        double red = 0, green = 0, blue = 0;
        var count = pixels.Length / 4;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            blue += pixels[index];
            green += pixels[index + 1];
            red += pixels[index + 2];
        }
        if (count == 0) return Colors.White;
        var (hue, saturation, value) = ToHsv(red / count / 255, green / count / 255, blue / count / 255);
        return FromHsv(hue, Math.Min(saturation, 0.45), Math.Max(value, 0.75));
    }

    private static (double Hue, double Saturation, double Value) ToHsv(double red, double green, double blue)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == red) hue = (green - blue) / delta % 6;
            else if (max == green) hue = (blue - red) / delta + 2;
            else hue = (red - green) / delta + 4;
            hue *= 60;
            if (hue < 0) hue += 360;
        }
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - chroma;
        var (red, green, blue) = (hue / 60) switch
        {
            < 1 => (chroma, x, 0.0),
            < 2 => (x, chroma, 0.0),
            < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma),
            < 5 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        byte Channel(double channel) => (byte)Math.Round(Math.Clamp(channel + m, 0, 1) * 255);
        return Color.FromRgb(Channel(red), Channel(green), Channel(blue));
    }

    /// <summary>The bar and the times run with the track while it plays (or follow a drag).</summary>
    public void Tick()
    {
        if (Store.Snapshot is not { } snapshot) return;
        var position = snapshot.PositionAt(DateTimeOffset.Now);
        var playing = snapshot.Duration > 0 ? position / snapshot.Duration : 0;
        var fraction = Math.Clamp(dragFraction ?? playing, 0, 1);
        if (bar.ActualWidth > 0) barFill.Width = Math.Max(5, bar.ActualWidth * fraction);
        var shownPosition = snapshot.Duration * fraction;
        var elapsedText = MediaFormatter.Clock(snapshot.Duration > 0 ? shownPosition : position);
        if (elapsed.Text != elapsedText)
        {
            elapsed.Text = elapsedText;
            remaining.Text = "-" + MediaFormatter.Clock(Math.Max(snapshot.Duration - shownPosition, 0));
            AutomationProperties.SetName(bar, $"{elapsedText} of {MediaFormatter.Clock(snapshot.Duration)}");
        }
    }
}
