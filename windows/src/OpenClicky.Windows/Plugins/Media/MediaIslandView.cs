//
//  MediaIslandView.cs
//  OpenClicky (Windows port of Plugins/Media/MediaIslandView.swift)
//
//  The Now playing plugin's row: artwork, title and artist, a bar that runs with the track (a click
//  seeks), and previous / play-pause / next. Fed by the app's one MediaStore, which the island
//  windows switch on with the plugin (IslandWindowManager) and which reads every 2 s while this row
//  is on screen.
//

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenClicky.Core.Plugins.Media;
using OpenClicky.Windows.Island;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Plugins.Media;

internal sealed class MediaIslandView : Grid, IIslandContent
{
    /// <summary>The app's one store (the Mac's NotchHUDManager.media).</summary>
    public static MediaStore Store { get; } = new(new WindowsMediaSource());

    private const string MusicGlyph = "";
    private const string PreviousGlyph = "";
    private const string NextGlyph = "";
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private readonly Grid trackRow = new();
    private readonly StackPanel emptyState = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border artworkFrame = new() { Width = 56, Height = 56, CornerRadius = new CornerRadius(8), ClipToBounds = true };
    private readonly TextBlock title = Text("", 13, DS.HUD.TextBrush, FontWeights.SemiBold, trimming: TextTrimming.CharacterEllipsis);
    private readonly TextBlock artist = Text("", 11, DS.HUD.Text2Brush, trimming: TextTrimming.CharacterEllipsis);
    private readonly TextBlock elapsed = Text("", 9, DS.HUD.Text3Brush);
    private readonly TextBlock total = Text("", 9, DS.HUD.Text3Brush);
    private readonly Grid bar = new() { Height = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), Cursor = Cursors.Hand };
    private readonly Border barFill = new() { Background = DS.HUD.TextBrush, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock playPauseGlyph = Glyph(PlayGlyph, 11, DS.HUD.TextBrush);
    private readonly Border playPause;
    private string? shownArtworkKey;

    public MediaIslandView()
    {
        // The empty state: nothing playing, and where music would come from.
        emptyState.Children.Add(Text("nothing playing", 13, DS.HUD.TextBrush));
        emptyState.Children.Add(Text("play something in spotify, a browser or media player", 11, DS.HUD.Text3Brush, trimming: TextTrimming.CharacterEllipsis));
        Children.Add(emptyState);

        trackRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trackRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        trackRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trackRow.VerticalAlignment = VerticalAlignment.Center;
        artworkFrame.VerticalAlignment = VerticalAlignment.Center;
        trackRow.Children.Add(artworkFrame);

        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        middle.Children.Add(title);
        artist.Margin = new Thickness(0, 3, 0, 3);
        middle.Children.Add(artist);
        var progress = new DockPanel { LastChildFill = true, Height = 12 };
        DockPanel.SetDock(elapsed, Dock.Left);
        DockPanel.SetDock(total, Dock.Right);
        progress.Children.Add(elapsed);
        progress.Children.Add(total);
        bar.Children.Add(new Border { Background = DS.Brush(Colors.White, 0.12), CornerRadius = new CornerRadius(1.5) });
        bar.Children.Add(barFill);
        // A click on the bar seeks to that point of the track; the island must not take it as a click on itself.
        bar.Background = Brushes.Transparent;
        bar.MouseLeftButtonDown += (_, eventArgs) => eventArgs.Handled = true;
        bar.MouseLeftButtonUp += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            if (bar.ActualWidth <= 0) return;
            _ = Store.SeekAsync(eventArgs.GetPosition(bar).X / bar.ActualWidth);
        };
        progress.Children.Add(bar);
        middle.Children.Add(progress);
        SetColumn(middle, 1);
        trackRow.Children.Add(middle);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(Control(Glyph(PreviousGlyph, 11, DS.HUD.TextBrush), "previous track", () => _ = Store.PreviousAsync()));
        playPause = Control(playPauseGlyph, "play", () => _ = Store.PlayPauseAsync());
        playPause.Margin = new Thickness(6, 0, 6, 0);
        controls.Children.Add(playPause);
        controls.Children.Add(Control(Glyph(NextGlyph, 11, DS.HUD.TextBrush), "next track", () => _ = Store.NextAsync()));
        SetColumn(controls, 2);
        trackRow.Children.Add(controls);
        Children.Add(trackRow);

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

    private static Border Control(TextBlock glyph, string label, Action onClick)
    {
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var button = Clickable(glyph, onClick, label, DS.HUD.BandControlBrush, new CornerRadius(DS.HUD.PillHeight / 2));
        button.Width = DS.HUD.PillHeight;
        button.Height = DS.HUD.PillHeight;
        return button;
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        var snapshot = Store.Snapshot;
        emptyState.Visibility = snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        trackRow.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        if (snapshot is null) return;
        title.Text = snapshot.Title;
        artist.Text = snapshot.Artist.Length == 0 ? snapshot.PlayerName : snapshot.Artist;
        total.Text = MediaFormatter.Clock(snapshot.Duration);
        playPauseGlyph.Text = snapshot.IsPlaying ? PauseGlyph : PlayGlyph;
        var playPauseLabel = snapshot.IsPlaying ? "pause" : "play";
        playPause.ToolTip = playPauseLabel;
        AutomationProperties.SetName(playPause, playPauseLabel);
        if (shownArtworkKey != snapshot.ArtworkKey + (Store.Artwork is null ? "" : ":art"))
        {
            shownArtworkKey = snapshot.ArtworkKey + (Store.Artwork is null ? "" : ":art");
            artworkFrame.Child = Artwork(Store.Artwork);
        }
        Tick();
    }

    /// <summary>The cover, or a note on the band-control grey while it loads or when the player has none.</summary>
    private static UIElement Artwork(byte[]? image)
    {
        if (image is not null)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 112;
                bitmap.StreamSource = new MemoryStream(image);
                bitmap.EndInit();
                bitmap.Freeze();
                return new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
            }
            catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException)
            {
                // A cover WPF cannot decode: the note instead.
            }
        }
        var note = Glyph(MusicGlyph, 20, DS.HUD.Text2Brush);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Background = DS.HUD.BandControlBrush, Child = note };
    }

    /// <summary>The bar and the clock run with the track while it plays.</summary>
    public void Tick()
    {
        if (Store.Snapshot is not { } snapshot) return;
        var position = snapshot.PositionAt(DateTimeOffset.Now);
        var text = MediaFormatter.Clock(position);
        if (elapsed.Text != text)
        {
            elapsed.Text = text;
            AutomationProperties.SetName(bar, $"{text} of {MediaFormatter.Clock(snapshot.Duration)}");
        }
        var fraction = snapshot.Duration > 0 ? Math.Clamp(position / snapshot.Duration, 0, 1) : 0;
        if (bar.ActualWidth > 0) barFill.Width = Math.Max(3, bar.ActualWidth * fraction);
    }
}
