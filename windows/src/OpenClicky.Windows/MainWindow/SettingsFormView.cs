// Ports SettingsFormView (Dictation/UI/SettingsPages.swift) and the .settingsTourTarget modifier
// (SettingsTour.swift): a page, or the search results, as a scrolling column of sections — a heading,
// its rows as cards, the notes under them. Every row reports where it is on screen to
// SettingsTourTargets under its item id, and the page scrolls a row the tour asks for into view.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Settings;

namespace OpenClicky.Windows.AppWindow;

internal static class TourTarget
{
    /// <summary>Reports <paramref name="element"/>'s place on screen to the Settings tour under <paramref name="targetId"/> while it is loaded.</summary>
    public static void Attach(FrameworkElement element, string targetId, SettingsTourTargets? targets = null)
    {
        targets ??= SettingsTourTargets.Shared;
        IDisposable? registration = null;
        element.Loaded += (_, _) =>
        {
            registration?.Dispose();
            registration = targets.Register(targetId, () => element.Dispatcher.CheckAccess()
                ? ScreenRectOf(element)
                : element.Dispatcher.Invoke(() => ScreenRectOf(element)));
        };
        element.Unloaded += (_, _) =>
        {
            registration?.Dispose();
            registration = null;
        };
    }

    /// <summary>
    /// The element's rectangle in screen DIPs, or null when it isn't shown or the spot the pointer
    /// would rest on is scrolled out of its page.
    /// </summary>
    public static ScreenRect? ScreenRectOf(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || PresentationSource.FromVisual(element) is null) return null;
        var rectInElement = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var spot = SettingsTourTargets.PointerSpot(new ScreenRect(0, 0, rectInElement.Width, rectInElement.Height));
        // The resting spot must be inside the scrolled page's viewport.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scroller)
            {
                var spotInScroller = element.TransformToAncestor(scroller).Transform(new Point(spot.X, spot.Y));
                if (spotInScroller.Y < 0 || spotInScroller.Y > scroller.ViewportHeight || spotInScroller.X < 0 || spotInScroller.X > scroller.ActualWidth) return null;
                break;
            }
        }
        var dpi = VisualTreeHelper.GetDpi(element);
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new ScreenRect(topLeft.X / dpi.DpiScaleX, topLeft.Y / dpi.DpiScaleY,
            (bottomRight.X - topLeft.X) / dpi.DpiScaleX, (bottomRight.Y - topLeft.Y) / dpi.DpiScaleY);
    }
}

internal sealed class SettingsFormView : ScrollViewer
{
    private readonly Dictionary<string, FrameworkElement> rowsById = new(StringComparer.Ordinal);
    /// <summary>For a row inside a folded group: opens that group (the tour asks for rows by id).</summary>
    private readonly Dictionary<string, Action> openFoldFor = new(StringComparer.Ordinal);

    /// <param name="page">The page drawn, for its header card; null for search results, which show <paramref name="title"/> and never fold.</param>
    public SettingsFormView(string title, IReadOnlyList<SettingsSectionGroup> groups, Func<SettingsItem, UIElement> buildRow, string? emptySentence = null,
        DictationSettingsPage? page = null)
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        var column = page is { } shownPage ? PageColumn(PageHeader(shownPage)) : PageColumn(title);
        if (groups.Count == 0 && emptySentence is not null)
        {
            var empty = Ui.Text(emptySentence, SettingsLook.RowTitleSize, SettingsLook.TextSecondary);
            empty.Margin = new Thickness(0, 40, 0, 0);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(empty);
        }
        foreach (var group in groups)
        {
            var rows = column;
            if (group.IsAdvanced)
            {
                rows = new StackPanel { Visibility = Visibility.Collapsed };
                column.Children.Add(new Border { Height = 20 });
                column.Children.Add(FoldRow(group, rows, out var open));
                column.Children.Add(rows);
                foreach (var item in group.Items) openFoldFor[item.Id] = open;
            }
            else if (group.Title.Length > 0) column.Children.Add(SectionHeader(group.Title));
            else if (column.Children.Count > 1) column.Children.Add(new Border { Height = 20 });
            foreach (var item in group.Items)
            {
                var row = buildRow(item);
                if (row is FrameworkElement rowElement)
                {
                    TourTarget.Attach(rowElement, item.Id);
                    rowsById[item.Id] = rowElement;
                }
                rows.Children.Add(row);
            }
            foreach (var footer in group.Footers) rows.Children.Add(buildRow(footer));
        }
        Content = column;
        void OnScrollRequested(string targetId) => Dispatcher.BeginInvoke(() => ScrollIntoView(targetId));
        Loaded += (_, _) => SettingsTourTargets.Shared.ScrollRequested += OnScrollRequested;
        Unloaded += (_, _) => SettingsTourTargets.Shared.ScrollRequested -= OnScrollRequested;
    }

    /// <summary>The page's column: its title, then its sections, no wider than reads well.</summary>
    public static StackPanel PageColumn(string title)
    {
        var heading = Ui.Text(title, SettingsLook.PageTitleSize, SettingsLook.TextPrimary, FontWeights.SemiBold, wrap: false);
        heading.Margin = new Thickness(0, 0, 0, 10);
        return PageColumn(heading);
    }

    private static StackPanel PageColumn(UIElement heading)
    {
        var column = new StackPanel { Margin = new Thickness(36, 8, 36, 40), MaxWidth = 1000 };
        column.Children.Add(heading);
        return column;
    }

    /// <summary>The card at the top of a page, like Windows Settings' own: the page's icon large in its coloured tile, its name, and a line on what it holds.</summary>
    public static UIElement PageHeader(DictationSettingsPage page)
    {
        var icon = new Image { Source = DictationWindow.SidebarIcon(page.Glyph(), page.TintHex()), Width = 40, Height = 40 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(page.Title(), SettingsLook.PageTitleSize, SettingsLook.TextPrimary, FontWeights.SemiBold, wrap: false));
        text.Children.Add(Ui.Text(page.Subtitle(), SettingsLook.CaptionSize, SettingsLook.TextSecondary));
        var header = Ui.HStack(14, icon, text);
        header.Margin = new Thickness(0, 0, 0, 10);
        return header;
    }

    /// <summary>
    /// A page's folded group ("More"): one card, closed by default with "N settings" at its right;
    /// a click anywhere on it opens the rows under it, and again closes them.
    /// </summary>
    private static Button FoldRow(SettingsSectionGroup group, StackPanel rows, out Action open)
    {
        var chevron = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0),
            Margin = new Thickness(0, 0, 10, 0),
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, SettingsLook.TextSecondary);
        var title = Ui.Text(group.Title, SettingsLook.RowTitleSize, SettingsLook.TextPrimary, wrap: false);
        title.VerticalAlignment = VerticalAlignment.Center;
        var count = Ui.Text($"{group.Items.Count} settings", SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false);
        count.VerticalAlignment = VerticalAlignment.Center;
        var line = new DockPanel();
        DockPanel.SetDock(count, Dock.Right);
        line.Children.Add(count);
        DockPanel.SetDock(chevron, Dock.Left);
        line.Children.Add(chevron);
        line.Children.Add(title);
        var button = new Button
        {
            Content = Ui.Card(line),
            Template = Ui.TransparentButtonTemplate(),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        void Show(bool isOpen)
        {
            rows.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            ((RotateTransform)chevron.RenderTransform).Angle = isOpen ? 90 : 0;
            count.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
            Ui.AutomationProperties(button, group.Title, isOpen ? "Open" : "Closed");
        }
        Show(false);
        button.Click += (_, _) => Show(rows.Visibility != Visibility.Visible);
        open = () => Show(true);
        return button;
    }

    public static TextBlock SectionHeader(string title)
    {
        var header = Ui.Text(title, SettingsLook.SectionTitleSize, SettingsLook.TextPrimary, FontWeights.SemiBold, wrap: false);
        header.Margin = new Thickness(2, 22, 0, 8);
        return header;
    }

    /// <summary>Scrolls a row to the middle of the page (the tour asks before pointing at it).</summary>
    public void ScrollIntoView(string targetId)
    {
        if (openFoldFor.TryGetValue(targetId, out var openFold) && rowsById.TryGetValue(targetId, out var folded) && !folded.IsVisible)
        {
            openFold();
            UpdateLayout();
        }
        if (!rowsById.TryGetValue(targetId, out var row) || !row.IsVisible) return;
        var rowTop = row.TransformToAncestor(this).Transform(new Point(0, 0)).Y + VerticalOffset;
        ScrollToVerticalOffset(Math.Max(0, rowTop - (ViewportHeight - row.ActualHeight) / 2));
    }
}
