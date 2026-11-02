//
//  ScreenMarkerWindow.cs
//  OpenClicky (Windows)
//
//  Core's ScreenMarker draws with this: a window over the monitor under the pointer, dimmed a
//  little while the person draws a loop with a yellow marker round what they want to ask about
//  (Esc or a right click gives up). When the marker lifts, the mark stays on the screen, the dim
//  goes and the window lets every click through, until the question about it has been answered.
//  Ctrl+Alt+M starts a mark from anywhere, like the island's marker button. The window is left out
//  of screenshots like OpenClicky's other windows, so the model sees the screen and a close-up of
//  the marked part, never the marker over it.
//

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Overlay;

public sealed class ScreenMarkerWindow : IScreenMarkerSurface, IDisposable
{
    private const double MarkerWidth = 16;
    /// <summary>A mark thinner than this (an underline) is widened to it, so its close-up shows the lines round it.</summary>
    private const double SmallestSide = 56;
    private static readonly Color MarkerColor = Color.FromRgb(0xFF, 0xD8, 0x3B);

    private readonly IScreens screens;
    private Window? window;
    private Canvas? canvas;
    private Polyline? stroke;
    private Border? hint;
    private Action<PixelRect?>? done;
    private nint previousForeground;
    private HwndSource? hotkeyWindow;

    public ScreenMarkerWindow(IScreens screens)
    {
        this.screens = screens;
        RegisterHotkey();
    }

    // MARK: - IScreenMarkerSurface

    public void BeginMarking(Action<PixelRect?> whenDone)
    {
        Close(reportGiveUp: true);
        done = whenDone;
        var screen = screens.CursorScreen();
        if (screen is null)
        {
            Finish(null);
            return;
        }
        canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0x22, 0x10, 0x14, 0x20)), Cursor = Cursors.Pen };
        stroke = new Polyline
        {
            Stroke = new SolidColorBrush(MarkerColor), StrokeThickness = MarkerWidth, Opacity = 0.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        canvas.Children.Add(stroke);
        hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x22, 0x30)), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 8, 16, 8), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Draw round what you want to ask about  ·  Esc to cancel", Foreground = Brushes.White, FontSize = 15,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            },
        };
        canvas.Children.Add(hint);
        window = new Window { Content = canvas, Title = "OpenClicky marker" };
        FloatingWindowSupport.Configure(window, clickThrough: false, noActivate: false);
        window.Left = screen.BoundsInDips.X;
        window.Top = screen.BoundsInDips.Y;
        window.Width = screen.BoundsInDips.Width;
        window.Height = screen.BoundsInDips.Height;
        window.SourceInitialized += (_, _) => FloatingWindowSupport.PlaceInPixels(window, screen.Bounds);
        window.Loaded += (_, _) =>
        {
            Canvas.SetLeft(hint, Math.Max(0, (canvas.ActualWidth - hint.ActualWidth) / 2));
            Canvas.SetTop(hint, 28);
        };
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            stroke.Points.Clear();
            stroke.Points.Add(e.GetPosition(canvas));
            canvas.CaptureMouse();
            hint.Visibility = Visibility.Collapsed;
        };
        canvas.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || stroke.Points.Count == 0) return;
            var at = e.GetPosition(canvas);
            var last = stroke.Points[^1];
            if (Math.Abs(at.X - last.X) + Math.Abs(at.Y - last.Y) >= 2) stroke.Points.Add(at);
        };
        canvas.MouseLeftButtonUp += (_, _) =>
        {
            canvas.ReleaseMouseCapture();
            Finish(MarkBounds());
        };
        canvas.MouseRightButtonUp += (_, _) => Finish(null);
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        window.Show();
        previousForeground = FloatingWindowSupport.TakeKeyboardFocus(window);
    }

    public void HideMark() => Close(reportGiveUp: false);

    /// <summary>The smoke command's hand: draws <paramref name="points"/> (in the window's DIPs) as one stroke and lifts the marker.</summary>
    internal void DrawForSmoke(IEnumerable<Point> points)
    {
        if (stroke is null || canvas is null) return;
        if (hint is not null) hint.Visibility = Visibility.Collapsed;
        foreach (var point in points) stroke.Points.Add(point);
        Finish(MarkBounds());
    }

    // MARK: - The mark

    /// <summary>The stroke's bounds (with the marker's width, at least <see cref="SmallestSide"/> each way) in physical pixels.</summary>
    private PixelRect? MarkBounds()
    {
        if (stroke is null || canvas is null || stroke.Points.Count < 2) return null;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var p in stroke.Points)
        {
            left = Math.Min(left, p.X); top = Math.Min(top, p.Y);
            right = Math.Max(right, p.X); bottom = Math.Max(bottom, p.Y);
        }
        if (right - left < 6 && bottom - top < 6) return null;
        void Widen(ref double low, ref double high)
        {
            var grow = Math.Max(0, SmallestSide - (high - low)) / 2 + MarkerWidth / 2;
            low -= grow;
            high += grow;
        }
        Widen(ref left, ref right);
        Widen(ref top, ref bottom);
        var topLeft = canvas.PointToScreen(new Point(left, top));
        var bottomRight = canvas.PointToScreen(new Point(right, bottom));
        return new PixelRect((int)topLeft.X, (int)topLeft.Y, (int)(bottomRight.X - topLeft.X), (int)(bottomRight.Y - topLeft.Y));
    }

    private void Finish(PixelRect? bounds)
    {
        var report = done;
        done = null;
        if (window is not null) FloatingWindowSupport.ReturnKeyboardFocus(window, previousForeground);
        previousForeground = 0;
        if (bounds is null || window is null || canvas is null)
        {
            Close(reportGiveUp: false);
        }
        else
        {
            // The mark stays up, the dim goes, and every click goes through to the apps below.
            canvas.Background = Brushes.Transparent;
            canvas.Cursor = null;
            var handle = new WindowInteropHelper(window).Handle;
            FloatingWindowSupport.SetExtendedStyles(handle, clickThrough: true, noActivate: true);
        }
        report?.Invoke(bounds);
    }

    private void Close(bool reportGiveUp)
    {
        var report = reportGiveUp ? done : null;
        done = null;
        window?.Close();
        window = null;
        canvas = null;
        stroke = null;
        hint = null;
        report?.Invoke(null);
    }

    // MARK: - Ctrl+Alt+M

    private const int HotkeyId = 0x4D4B;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint VK_M = 0x4D;

    private void RegisterHotkey()
    {
        // A message-only window to receive WM_HOTKEY on the UI thread.
        hotkeyWindow = new HwndSource(new HwndSourceParameters("OpenClicky marker hotkey") { ParentWindow = new nint(-3), WindowStyle = 0 });
        hotkeyWindow.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == WM_HOTKEY && wParam == HotkeyId)
            {
                handled = true;
                ScreenMarker.Shared.Begin();
            }
            return 0;
        });
        if (!RegisterHotKey(hotkeyWindow.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_M))
        {
            AppLog.Write($"marker: Ctrl+Alt+M is taken by another app (error {Marshal.GetLastWin32Error()}); the island's marker button still works");
        }
    }

    public void Dispose()
    {
        Close(reportGiveUp: false);
        if (hotkeyWindow is not null)
        {
            UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
            hotkeyWindow.Dispose();
            hotkeyWindow = null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
