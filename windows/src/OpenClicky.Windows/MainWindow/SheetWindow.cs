// The Mac's sheets (AccountSheet, the take, term, snippet, style and app-picker sheets, the
// confirmation dialogs) as small Fluent dialog windows owned by the main window, each with the
// theme installed and the Cancel (Esc) / default (Enter) buttons at its foot. Also the mark
// (OpenClickyMark.swift), drawn for About, the generated tray icon and the window icon.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenClicky.Core.Dictation;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

internal class SheetWindow : WpfUi.FluentWindow
{
    private readonly DockPanel root = new() { LastChildFill = true };
    private readonly StackPanel footerLeft = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel footerRight = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border footerBar;

    public SheetWindow(Window? owner, DictationSettings settings, string title, double width)
    {
        Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = owner is null;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WpfUi.WindowBackdropType.Mica;
        WindowCornerPreference = WpfUi.WindowCornerPreference.Round;
        MainWindowTheme.Install(this, settings);

        var titleBar = new WpfUi.TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false, CanMaximize = false };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        var footer = new Grid();
        footer.Children.Add(footerLeft);
        footer.Children.Add(footerRight);
        footerBar = new Border { Padding = new Thickness(24, 14, 24, 18), Child = footer, BorderThickness = new Thickness(0, 1, 0, 0) };
        footerBar.SetResourceReference(Border.BorderBrushProperty, SettingsLook.Divider);
        footerBar.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        DockPanel.SetDock(footerBar, Dock.Bottom);
        root.Children.Add(footerBar);
        Content = root;
        PreviewKeyDown += (_, arguments) =>
        {
            if (arguments.Key == Key.Escape) Close();
        };
    }

    /// <summary>The sheet's body, scrolled when it is taller than the screen allows.</summary>
    public void SetBody(UIElement body, Thickness? padding = null)
    {
        var scroller = new ScrollViewer
        {
            Content = body,
            Padding = padding ?? new Thickness(24, 8, 24, 16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        if (root.Children.Count > 2) root.Children.RemoveAt(2);
        root.Children.Add(scroller);
    }

    public void HideFooter() => footerBar.Visibility = Visibility.Collapsed;

    /// <summary>A footer button; the default one answers Enter, the cancel one Esc.</summary>
    public WpfUi.Button AddFooterButton(string title, Action onClick, bool isDefault = false, bool isCancel = false,
        WpfUi.ControlAppearance? appearance = null, bool alignLeft = false)
    {
        var button = Ui.Button(title, onClick, appearance ?? (isDefault ? WpfUi.ControlAppearance.Primary : WpfUi.ControlAppearance.Secondary));
        button.IsDefault = isDefault;
        button.IsCancel = isCancel;
        button.MinWidth = 80;
        if (alignLeft)
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            footerLeft.Children.Add(button);
        }
        else
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            footerRight.Children.Add(button);
        }
        return button;
    }

    /// <summary>A yes/no question, as the Mac's confirmationDialog. Returns whether the destructive action was chosen.</summary>
    public static bool Confirm(Window? owner, DictationSettings settings, string title, string message, string actionTitle)
    {
        var confirmed = false;
        var sheet = new SheetWindow(owner, settings, "OpenClicky", 420);
        var body = Ui.VStack(8,
            Ui.Text(title, 16, SettingsLook.TextPrimary, FontWeights.SemiBold),
            Ui.Text(message, 13, SettingsLook.TextSecondary));
        sheet.SetBody(body, new Thickness(24, 4, 24, 18));
        sheet.AddFooterButton("Cancel", sheet.Close, isCancel: true);
        sheet.AddFooterButton(actionTitle, () => { confirmed = true; sheet.Close(); }, isDefault: true, appearance: WpfUi.ControlAppearance.Danger);
        sheet.ShowDialog();
        return confirmed;
    }
}

/// <summary>The mark: a ring whose top-right corner is square, with the pointer inside aiming up and to the left.</summary>
public static class OpenClickyMarkDrawing
{
    private const double InnerRadius = 0.4125;
    private static readonly Point ArrowTip = new(0.3125, 0.3125);
    private static readonly Point ArrowRight = new(0.7475, 0.475);
    private static readonly Point ArrowNotch = new(0.505, 0.52);
    private static readonly Point ArrowBottom = new(0.4775, 0.7525);

    /// <summary>The mark in a square of <paramref name="side"/> at (<paramref name="left"/>, <paramref name="top"/>), even-odd filled.</summary>
    public static Geometry Geometry(double left, double top, double side)
    {
        Point Scaled(Point unit) => new(left + unit.X * side, top + unit.Y * side);
        var center = new Point(left + side / 2, top + side / 2);
        var radius = side / 2;
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = geometry.Open())
        {
            // Three quarters of the circle, clockwise from the right to the top, then the square corner.
            context.BeginFigure(new Point(center.X + radius, center.Y), isFilled: true, isClosed: true);
            context.ArcTo(new Point(center.X, center.Y - radius), new Size(radius, radius), 0, isLargeArc: true, SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(left + side, top), true, false);
            // The hole.
            var inner = InnerRadius * side;
            context.BeginFigure(new Point(center.X + inner, center.Y), true, true);
            context.ArcTo(new Point(center.X - inner, center.Y), new Size(inner, inner), 0, false, SweepDirection.Clockwise, true, false);
            context.ArcTo(new Point(center.X + inner, center.Y), new Size(inner, inner), 0, false, SweepDirection.Clockwise, true, false);
            // The pointer.
            context.BeginFigure(Scaled(ArrowTip), true, true);
            context.LineTo(Scaled(ArrowRight), true, false);
            context.LineTo(Scaled(ArrowNotch), true, false);
            context.LineTo(Scaled(ArrowBottom), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The app icon: the white mark on an orange rounded square (scripts/make-app-icon.py's look).</summary>
    public static DrawingImage AppIconImage()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(SettingsLook.AccentLight), null, new RectangleGeometry(new Rect(0, 0, 64, 64), 14, 14)));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, Geometry(14, 14, 36)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>The app icon rendered to a bitmap of <paramref name="pixels"/> square.</summary>
    public static BitmapSource AppIconBitmap(int pixels)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(AppIconImage(), new Rect(0, 0, pixels, pixels));
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
