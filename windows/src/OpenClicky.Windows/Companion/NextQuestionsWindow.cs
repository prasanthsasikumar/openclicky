//
//  NextQuestionsWindow.cs
//  OpenClicky (Windows)
//
//  Next questions (Core/Answers/NextQuestions) as buttons: a small dark card under the island, "You could ask" and up to
//  three questions, each a rounded button that asks it when tapped. It never takes the keyboard, goes when tapped, closed,
//  withdrawn (a new question, esc) or after NextQuestions.ShownFor. The card is also built on its own for the demo.
//

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenClicky.Core.Answers;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Companion;

public sealed class NextQuestionsWindow(Dispatcher dispatcher)
{
    private const double CardWidth = 420;
    private const double ShadowMargin = 20;

    private Window? window;
    private DispatcherTimer? hideTimer;

    /// <summary>Shows <paramref name="questions"/>; <paramref name="ask"/> hears the one tapped.</summary>
    public void Show(IReadOnlyList<string> questions, Action<string> ask) => dispatcher.BeginInvoke(() =>
    {
        Close();
        var shown = new Window
        {
            Title = "OpenClicky next questions",
            SizeToContent = SizeToContent.Height,
            Width = CardWidth + ShadowMargin * 2,
        };
        shown.Content = new Border
        {
            Padding = new Thickness(ShadowMargin),
            Child = Card(questions, question => { Close(); ask(question); }, Close),
        };
        FloatingWindowSupport.Configure(shown, clickThrough: false);
        var area = SystemParameters.WorkArea;
        shown.WindowStartupLocation = WindowStartupLocation.Manual;
        shown.Left = area.Left + (area.Width - shown.Width) / 2;
        shown.Top = area.Top + 64;
        window = shown;
        shown.Closed += (_, _) => { if (ReferenceEquals(window, shown)) window = null; };
        hideTimer = new DispatcherTimer(NextQuestions.ShownFor, DispatcherPriority.Normal, (_, _) => Close(), dispatcher);
        shown.Show();
    });

    public void Hide() => dispatcher.BeginInvoke(Close);

    private void Close()
    {
        hideTimer?.Stop();
        hideTimer = null;
        window?.Close();
        window = null;
    }

    /// <summary>The card: the heading and a close button, then a button for each question.</summary>
    public static FrameworkElement Card(IReadOnlyList<string> questions, Action<string> ask, Action close)
    {
        var secondary = DS.Brush(Colors.White, 0.62);
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 0, 8) };
        var closeButton = Clickable(Glyph("", 9, secondary), close, "close", DS.Brush(Colors.White, 0.1), new CornerRadius(9));
        closeButton.Width = closeButton.Height = 18;
        DockPanel.SetDock(closeButton, Dock.Right);
        head.Children.Add(closeButton);
        head.Children.Add(Text(NextQuestions.Heading, 11, secondary, FontWeights.SemiBold));

        var buttons = new WrapPanel();
        foreach (var question in questions)
        {
            var label = Text(question, 13, Brushes.White, FontWeights.Medium, wrapping: TextWrapping.Wrap);
            label.MaxWidth = CardWidth - 60;
            var button = Clickable(label, () => ask(question), null, DS.Brush(DS.Hex("#3D7BFF"), 0.22), new CornerRadius(14));
            button.Padding = new Thickness(12, 6, 12, 7);
            button.Margin = new Thickness(0, 0, 6, 6);
            button.BorderBrush = DS.Brush(DS.Hex("#6C9BFF"), 0.5);
            button.BorderThickness = new Thickness(1);
            buttons.Children.Add(button);
        }

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(buttons);
        return new Border
        {
            Width = CardWidth,
            Background = DS.Brush(DS.Hex("#1E1F24")),
            BorderBrush = DS.Brush(Colors.White, 0.08),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 10, 10, 8),
            Child = stack,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 4, Opacity = 0.4, RenderingBias = RenderingBias.Performance },
        };
    }

    /// <summary>The demo: the card for <paramref name="questions"/>, drawn offscreen to a PNG.</summary>
    public static string SaveDemo(string path, IReadOnlyList<string> questions)
    {
        var frame = new Border { Padding = new Thickness(ShadowMargin), Background = DS.Brush(DS.Hex("#C9CED6")), Child = Card(questions, _ => { }, () => { }) };
        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        frame.Arrange(new Rect(frame.DesiredSize));
        frame.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth * 2), (int)Math.Ceiling(frame.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }
}
