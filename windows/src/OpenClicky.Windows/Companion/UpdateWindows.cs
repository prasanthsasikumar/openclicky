//
//  UpdateWindows.cs
//  OpenClicky (Windows)
//
//  The update flow's windows (Core/Updates/UpdateFlow): the card that says a downloaded update is ready (Restart now /
//  Later, under the island), the update window that walks Download, Install and Open with a bar (in the middle of the
//  screen), and the note on the first start of a new version. All three are dark cards in the island's look, topmost and
//  never taking the keyboard. Each card is built by a static method too, so the update demo can draw them offscreen.
//

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenClicky.Core.Updates;
using OpenClicky.Windows.AppWindow;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Companion;

/// <summary>Shows and closes the update flow's windows, on the UI thread whatever thread asks.</summary>
public sealed class UpdateWindows(Dispatcher dispatcher)
{
    private Window? readyCard;
    private Window? progressWindow;
    private ContentControl? progressHost;
    private Window? updatedCard;
    private DispatcherTimer? updatedCardTimer;

    /// <summary>"OpenClicky 0.12.3 is ready": Restart now, Later, and What's new when there are notes to open.</summary>
    public void ShowReadyCard(string version, Action restart, Action later, Action? whatsNew) => dispatcher.BeginInvoke(() =>
    {
        CloseReadyCard();
        Window? card = null;
        card = Host(UpdateCards.Ready(version,
            () => { card?.Close(); restart(); },
            () => { card?.Close(); later(); },
            whatsNew), UpdateCards.ReadyWidth, below: true);
        readyCard = card;
        card.Closed += (_, _) => { if (ReferenceEquals(readyCard, card)) readyCard = null; };
        card.Show();
    });

    public void CloseReadyCard() => dispatcher.BeginInvoke(() => readyCard?.Close());

    /// <summary>The update window, opened at its first call and updated by the next ones.</summary>
    public void ShowProgress(UpdateProgress progress) => dispatcher.BeginInvoke(() =>
    {
        CloseReadyCard();
        if (progressWindow is null || progressHost is null)
        {
            progressHost = new ContentControl();
            progressWindow = Host(progressHost, UpdateCards.ProgressWidth, below: false);
            var shown = progressWindow;
            shown.Closed += (_, _) => { if (ReferenceEquals(progressWindow, shown)) { progressWindow = null; progressHost = null; } };
            progressHost.Content = UpdateCards.Progress(progress);
            shown.Show();
        }
        else progressHost.Content = UpdateCards.Progress(progress);
    });

    public void CloseProgress() => dispatcher.BeginInvoke(() => progressWindow?.Close());

    /// <summary>"OpenClicky was updated to 0.12.3", for a few seconds, with What's new.</summary>
    public void ShowUpdatedCard(string message, Action? whatsNew, TimeSpan showFor) => dispatcher.BeginInvoke(() =>
    {
        updatedCard?.Close();
        Window? card = null;
        card = Host(UpdateCards.Updated(message, () => card?.Close(), whatsNew is null ? null : () => { card?.Close(); whatsNew(); }),
            UpdateCards.ReadyWidth, below: true);
        updatedCard = card;
        updatedCardTimer?.Stop();
        updatedCardTimer = new DispatcherTimer(showFor, DispatcherPriority.Normal, (_, _) => { updatedCardTimer?.Stop(); card.Close(); }, dispatcher);
        card.Show();
    });

    /// <summary>A floating, topmost, non-activating window around <paramref name="content"/>: under the island's spot at the top
    /// of the main screen (<paramref name="below"/>), else in its middle.</summary>
    private static Window Host(UIElement content, double width, bool below)
    {
        var window = new Window
        {
            Title = "OpenClicky update",
            Content = new Border { Padding = new Thickness(UpdateCards.ShadowMargin), Child = content },
            SizeToContent = SizeToContent.Height,
            Width = width + UpdateCards.ShadowMargin * 2,
        };
        FloatingWindowSupport.Configure(window, clickThrough: false);
        var area = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = below ? area.Top + 64 : area.Top + area.Height * 0.32;
        return window;
    }
}

/// <summary>The cards themselves, as elements (the windows host them; the demo draws them).</summary>
public static class UpdateCards
{
    public const double ReadyWidth = 380;
    public const double ProgressWidth = 400;
    public const double ShadowMargin = 22;

    private static readonly Brush CardBrush = DS.Brush(DS.Hex("#1E1F24"));
    private static readonly Brush EdgeBrush = DS.Brush(Colors.White, 0.08);
    private static readonly Brush Primary = Brushes.White;
    private static readonly Brush Secondary = DS.Brush(Colors.White, 0.66);
    private static readonly Brush Tertiary = DS.Brush(Colors.White, 0.38);
    private static readonly Color Accent = DS.Hex("#3D7BFF");

    public static FrameworkElement Ready(string version, Action restart, Action later, Action? whatsNew)
    {
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        if (whatsNew is not null)
        {
            var link = LinkButton("What's new", whatsNew);
            DockPanel.SetDock(link, Dock.Left);
            buttons.Children.Add(link);
        }
        var restartButton = Button(UpdateReadyCard.RestartTitle, restart, primary: true);
        DockPanel.SetDock(restartButton, Dock.Right);
        buttons.Children.Add(restartButton);
        var laterButton = Button(UpdateReadyCard.LaterTitle, later, primary: false);
        laterButton.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(laterButton, Dock.Right);
        buttons.Children.Add(laterButton);

        var words = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        words.Children.Add(Text(UpdateReadyCard.Title(version), 14, Primary, FontWeights.SemiBold));
        var detail = Text(UpdateReadyCard.Detail, 12, Secondary, wrapping: TextWrapping.Wrap);
        detail.Margin = new Thickness(0, 3, 0, 0);
        words.Children.Add(detail);
        words.Children.Add(buttons);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = AppIcon(40);
        icon.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(icon);
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        return Card(row, ReadyWidth, new Thickness(16, 14, 16, 14));
    }

    public static FrameworkElement Updated(string message, Action dismiss, Action? whatsNew)
    {
        var words = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text(message, 14, Primary, FontWeights.SemiBold, trimming: TextTrimming.CharacterEllipsis));
        if (whatsNew is not null)
        {
            var link = LinkButton("See what's new", whatsNew);
            link.HorizontalAlignment = HorizontalAlignment.Left;
            link.Margin = new Thickness(0, 4, 0, 0);
            words.Children.Add(link);
        }
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(AppIcon(32));
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        var close = Clickable(Glyph("", 10, Secondary), dismiss, "close", DS.Brush(Colors.White, 0.1), new CornerRadius(10));
        close.Width = close.Height = 20;
        close.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(close, 2);
        row.Children.Add(close);
        return Card(row, ReadyWidth, new Thickness(14, 12, 12, 12));
    }

    public static FrameworkElement Progress(UpdateProgress progress)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var icon = AppIcon(52);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(icon);
        var title = Text(progress.Title, 16, Primary, FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(title);
        var bar = Bar(progress.Fraction, progress.Step == UpdateStep.Done);
        bar.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(bar);
        var detail = Text(progress.Detail, 12, Secondary);
        detail.HorizontalAlignment = HorizontalAlignment.Center;
        detail.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(detail);
        var steps = Steps(progress);
        steps.Margin = new Thickness(0, 18, 0, 0);
        stack.Children.Add(steps);
        return Card(stack, ProgressWidth, new Thickness(28, 22, 28, 18));
    }

    /// <summary>Download › Install › Open: a tick for a step behind, a filled dot for the one under way, a ring for the rest.</summary>
    private static FrameworkElement Steps(UpdateProgress progress)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var first = true;
        foreach (var (step, name) in UpdateFlow.Steps)
        {
            if (!first) row.Children.Add(new TextBlock { Text = "›", Foreground = Tertiary, FontSize = 14, Margin = new Thickness(12, -2, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            first = false;
            var done = progress.HasDone(step);
            var current = !done && step == progress.Step;
            FrameworkElement mark;
            if (done)
            {
                var tick = new Grid { Width = 14, Height = 14 };
                tick.Children.Add(new Ellipse { Fill = new SolidColorBrush(Accent) });
                tick.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M 3.6,7.2 L 6,9.6 L 10.4,4.8"), Stroke = Brushes.White, StrokeThickness = 1.6, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round });
                mark = tick;
            }
            else mark = new Ellipse
            {
                Width = 10, Height = 10, Margin = new Thickness(2),
                Fill = current ? new SolidColorBrush(Accent) : Brushes.Transparent,
                Stroke = current ? null : Tertiary, StrokeThickness = 1.4,
            };
            mark.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(mark);
            var label = Text(name, 12, done || current ? Primary : Tertiary, current ? FontWeights.SemiBold : FontWeights.Normal);
            label.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(label);
        }
        return row;
    }

    /// <summary>The bar: filled to <paramref name="fraction"/>, or a moving segment when it can't tell.</summary>
    private static FrameworkElement Bar(double? fraction, bool full)
    {
        const double height = 6;
        var track = new Grid { Height = height, ClipToBounds = true };
        track.Children.Add(new Border { Background = DS.Brush(Colors.White, 0.12), CornerRadius = new CornerRadius(height / 2) });
        var fill = new Border { Background = new SolidColorBrush(Accent), CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(fill);
        if (full || fraction is not null)
        {
            var share = full ? 1 : fraction!.Value;
            track.SizeChanged += (_, _) => fill.Width = Math.Max(height, track.ActualWidth * share);
            fill.Width = Math.Max(height, (ProgressWidth - 56) * share);
        }
        else
        {
            var width = (ProgressWidth - 56) * 0.3;
            fill.Width = width;
            var slide = new TranslateTransform();
            fill.RenderTransform = slide;
            fill.Loaded += (_, _) => slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-width, ProgressWidth, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }
        return track;
    }

    private static Border Card(UIElement content, double width, Thickness padding) => new()
    {
        Width = width,
        Background = CardBrush,
        BorderBrush = EdgeBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Padding = padding,
        Child = content,
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Opacity = 0.4, RenderingBias = RenderingBias.Performance },
    };

    private static FrameworkElement AppIcon(double side) => new Image
    {
        Source = OpenClickyMarkDrawing.AppIconImage(),
        Width = side,
        Height = side,
    };

    private static Border Button(string title, Action onClick, bool primary)
    {
        var label = Text(title, 12, Brushes.White, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var button = Clickable(label, onClick, null, primary ? new SolidColorBrush(Accent) : DS.Brush(Colors.White, 0.12), new CornerRadius(8));
        button.Padding = new Thickness(14, 6, 14, 6);
        return button;
    }

    private static Border LinkButton(string title, Action onClick)
    {
        var label = Text(title, 12, new SolidColorBrush(DS.Hex("#8DB4FF")), FontWeights.SemiBold);
        var link = Clickable(label, onClick, null, Brushes.Transparent, new CornerRadius(6));
        link.Padding = new Thickness(0, 6, 0, 6);
        link.VerticalAlignment = VerticalAlignment.Center;
        return link;
    }

    /// <summary>The update demo: every card, drawn offscreen to PNGs in <paramref name="folder"/>.</summary>
    public static IReadOnlyList<string> SaveDemo(string folder, string version)
    {
        Directory.CreateDirectory(folder);
        var saved = new List<string>();
        void Save(string name, FrameworkElement card)
        {
            var frame = new Border { Padding = new Thickness(ShadowMargin), Background = DS.Brush(DS.Hex("#C9CED6")), Child = card };
            frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            frame.Arrange(new Rect(frame.DesiredSize));
            frame.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth * 2), (int)Math.Ceiling(frame.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = System.IO.Path.Combine(folder, $"update-{name}.png");
            using (var file = File.Create(path)) encoder.Save(file);
            saved.Add(path);
        }
        Save("ready", Ready(version, () => { }, () => { }, () => { }));
        Save("1-download", Progress(UpdateProgress.Downloading(version, 0.42)));
        Save("2-install", Progress(UpdateProgress.Installing(version)));
        Save("3-open", Progress(UpdateProgress.Opening(version)));
        Save("4-done", Progress(UpdateProgress.Finished(version)));
        Save("updated", Updated(UpdatedNotice.Message("0.0.0", version)!, () => { }, () => { }));
        return saved;
    }
}
