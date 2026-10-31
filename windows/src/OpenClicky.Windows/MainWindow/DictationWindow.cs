// Ports the window half of Dictation/UI/DictationWindow.swift: OpenClicky's one window. A Fluent
// NavigationView lists six pages — Dictation, Clicky, Island, Keys & Account, Privacy, General — with
// a search over every setting at the top and incognito and the account at the foot. Rows on a page
// open the pages under it (Your words, How it writes and The orb under Dictation; Calendars under
// Island); the back button above the page returns, and the button opposite it picks the appearance. Every way in
// (Ctrl+, the tray, the island, the orb's menu) goes through Open(section, settingsPage), which
// lands on the page it asks for. The window is reopened rather than recreated, and remembers where
// it was. Closing it stops the Settings tour.

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;
using WpfUi = Wpf.Ui.Controls;

namespace OpenClicky.Windows.AppWindow;

public sealed class DictationWindow : WpfUi.FluentWindow, IMainWindowActions
{
    /// <summary>Where the window was, in DIPs ("left,top,width,height"), the Mac's frame autosave name.</summary>
    public const string FramePreferenceKey = "openclicky.window.frame";

    public static readonly Size MinimumSize = new(860, 560);

    private readonly MainWindowServices services;
    private readonly SettingsCatalog catalog;
    private readonly SettingsRowFactory rowFactory;
    private readonly AppIconCache icons;
    private readonly MainWindowTheme theme;
    private readonly WpfUi.NavigationView navigation;
    private readonly WpfUi.TextBox searchBox;
    private readonly Dictionary<DictationDestination, WpfUi.NavigationViewItem> sidebarItems = [];
    private readonly ContentControl footerAccount = new() { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl contentHost = new() { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    /// <summary>The page with its toolbar above it: back on the left, appearance on the right.</summary>
    private readonly Grid pageArea = new();
    private readonly WpfUi.Button backButton;
    private readonly WpfUi.Button appearanceButton;
    private readonly WpfUi.ImageIcon titleBarIcon = new() { Width = 16, Height = 16 };
    private IReadOnlyList<string> shownItemIds = [];
    private string shownKey = "";

    public DictationWindowModel Model { get; } = new();

    public SettingsTourController Tour { get; }

    /// <summary>True: the close button hides the window (it is reopened later). The demo closes it for real.</summary>
    public bool HidesOnClose { get; set; } = true;

    public DictationWindow(MainWindowServices services)
    {
        this.services = services;
        catalog = new SettingsCatalog(services.Settings);
        icons = new AppIconCache(services.FrontmostApp);
        rowFactory = new SettingsRowFactory(services, this);
        Tour = new SettingsTourController(services.Preferences, services.TourPointer, services.TourNarrator);

        Title = "OpenClicky";
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WpfUi.WindowBackdropType.Mica;
        WindowCornerPreference = WpfUi.WindowCornerPreference.Round;
        MinWidth = MinimumSize.Width;
        MinHeight = MinimumSize.Height;
        theme = MainWindowTheme.Install(this, services.Settings);
        // The page sits in NavigationView's Frame, which does not inherit the window's resources.
        theme.AddScope(pageArea);
        RestoreFrame();

        var titleBar = new WpfUi.TitleBar
        {
            Title = "OpenClicky",
            Icon = titleBarIcon,
        };
        // The icon follows the colour picked in General (the taskbar shows the window's icon).
        void ShowAppIcon()
        {
            Icon = OpenClickyMarkDrawing.AppIconBitmap(64, services.Settings.AppIconColor);
            titleBarIcon.Source = OpenClickyMarkDrawing.AppIconImage(services.Settings.AppIconColor);
        }
        ShowAppIcon();
        services.Settings.PropertyChanged += (_, arguments) =>
        {
            if (arguments.PropertyName == nameof(DictationSettings.AppIconColor)) Dispatcher.BeginInvoke(ShowAppIcon);
        };
        searchBox = new WpfUi.TextBox
        {
            PlaceholderText = "Search settings",
            Icon = new WpfUi.SymbolIcon { Symbol = WpfUi.SymbolRegular.Search24 },
            IconPlacement = WpfUi.ElementPlacement.Left,
            ClearButtonEnabled = true,
            Margin = new Thickness(8, 4, 8, 8),
        };
        Ui.AutomationProperties(searchBox, "Search settings");
        searchBox.TextChanged += (_, _) => Model.SearchText = searchBox.Text;

        navigation = new WpfUi.NavigationView
        {
            PaneDisplayMode = WpfUi.NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = WpfUi.NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleVisible = false,
            OpenPaneLength = 272,
            PaneHeader = searchBox,
            PaneFooter = SidebarFooter(),
            FrameMargin = new Thickness(0),
            TransitionDuration = 0,
        };
        foreach (var destination in DictationDestination.SidebarOrder)
        {
            var item = SidebarItem(destination);
            sidebarItems[destination] = item;
            navigation.MenuItems.Add(item);
        }

        backButton = ToolbarButton("", "Back", () => Model.GoBack());
        backButton.HorizontalAlignment = HorizontalAlignment.Left;
        appearanceButton = ToolbarButton(AppearanceGlyph(services.Settings.Appearance), "Appearance", ShowAppearanceMenu);
        appearanceButton.HorizontalAlignment = HorizontalAlignment.Right;
        var toolbar = new Grid { Margin = new Thickness(28, 2, 28, 0) };
        toolbar.Children.Add(backButton);
        toolbar.Children.Add(appearanceButton);
        pageArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pageArea.Children.Add(toolbar);
        Grid.SetRow(contentHost, 1);
        pageArea.Children.Add(contentHost);
        UpdateToolbar();
        services.Settings.PropertyChanged += (_, arguments) =>
        {
            if (arguments.PropertyName == nameof(DictationSettings.Appearance)) Dispatcher.BeginInvoke(UpdateToolbar);
        };

        navigation.Loaded += (_, _) =>
        {
            navigation.ReplaceContent(pageArea, null);
            UpdateSidebarSelection();
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(titleBar);
        Grid.SetRow(navigation, 1);
        root.Children.Add(navigation);
        Content = root;

        Model.PropertyChanged += OnModelChanged;
        services.Settings.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshIfRowsChanged);
        void OnShellChanged() => Dispatcher.BeginInvoke(() => { RefreshIfRowsChanged(); RenderFooterAccount(); });
        OpenClickyConfiguration.SettingsChanged += OnShellChanged;
        ShellSettingsRevision.Changed += OnShellChanged;
        services.Auth.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RenderFooterAccount);
        Closed += (_, _) =>
        {
            OpenClickyConfiguration.SettingsChanged -= OnShellChanged;
            ShellSettingsRevision.Changed -= OnShellChanged;
        };

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        RenderFooterAccount();
        Render();
    }

    // MARK: - entry points

    /// <summary>Every way in: lands on the page asked for (a settings page wins), brings the window forward, and plays the tour the first time General opens.</summary>
    public void Open(DictationSection? section = null, DictationSettingsPage? settingsPage = null)
    {
        Model.Open(section, settingsPage);
        if (!IsVisible) base.Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // No field starts with the keyboard: a key field taking it would scroll a settings page.
        Keyboard.ClearFocus();
        if (Tour.ShouldStartOnItsOwn(Model, services.Companion.IsWelcomeTourDone)) StartSettingsTour();
    }

    public void ShowSettings(DictationSettingsPage page) => Open(settingsPage: page);

    public void ShowSection(DictationSection section) => Open(section: section);

    public void Navigate(DictationDestination destination) => Model.Push(destination);

    public void StartSettingsTour()
    {
        if (Tour.IsRunning) return;
        if (!IsVisible) base.Show();
        _ = Tour.Start(Model);
    }

    public Window OwnerWindow => this;

    // MARK: - drawing

    private void OnModelChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (arguments.PropertyName is nameof(DictationWindowModel.Destination) or nameof(DictationWindowModel.SearchText))
        {
            if (arguments.PropertyName == nameof(DictationWindowModel.Destination) || searchBox.Text != Model.SearchText)
            {
                if (searchBox.Text != Model.SearchText) searchBox.Text = Model.SearchText;
            }
            Render();
        }
        if (arguments.PropertyName is nameof(DictationWindowModel.CanGoBack) or nameof(DictationWindowModel.SearchText)) UpdateToolbar();
    }

    // MARK: - toolbar

    private static WpfUi.Button ToolbarButton(string glyph, string label, Action onClick)
    {
        var button = new WpfUi.Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 },
            Appearance = WpfUi.ControlAppearance.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(9, 6, 9, 6),
            Cursor = Cursors.Hand,
            ToolTip = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Ui.AutomationProperties(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static string AppearanceGlyph(DictationAppearance appearance) => appearance switch
    {
        DictationAppearance.Light => "",
        DictationAppearance.Dark => "",
        _ => "",
    };

    /// <summary>Back shows only when a row opened this page; the appearance button shows the current choice.</summary>
    private void UpdateToolbar()
    {
        var canGoBack = Model.CanGoBack && !Model.IsSearching;
        backButton.Visibility = canGoBack ? Visibility.Visible : Visibility.Hidden;
        ((TextBlock)appearanceButton.Content).Text = AppearanceGlyph(services.Settings.Appearance);
        appearanceButton.ToolTip = $"Appearance: {AppearanceTitle(services.Settings.Appearance)}";
    }

    private static string AppearanceTitle(DictationAppearance appearance) => appearance switch
    {
        DictationAppearance.Light => "Light",
        DictationAppearance.Dark => "Dark",
        _ => "Auto",
    };

    /// <summary>Light, dark or auto (the Mac's AppearanceToolbarMenu): this window, the orb and its transcript box follow it; the island stays black.</summary>
    private void ShowAppearanceMenu()
    {
        var menu = new ContextMenu { PlacementTarget = appearanceButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var appearance in new[] { DictationAppearance.Light, DictationAppearance.Dark, DictationAppearance.System })
        {
            var item = new MenuItem
            {
                Header = AppearanceTitle(appearance),
                IsCheckable = true,
                IsChecked = services.Settings.Appearance == appearance,
                Icon = new TextBlock { Text = AppearanceGlyph(appearance), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") },
            };
            var chosen = appearance;
            item.Click += (_, _) => services.Settings.Appearance = chosen;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>The ids of the rows the current view would show, to tell whether a settings change added or removed one.</summary>
    private IReadOnlyList<string> CurrentItemIds()
    {
        if (Model.IsSearching) return catalog.SearchMatches(Model.TrimmedSearchText).Select(item => item.Id).ToList();
        return Model.Destination.Page is { } page ? catalog.PageItems(page).Select(item => item.Id).ToList() : [];
    }

    /// <summary>A setting changed which rows apply (the region, the engine, a key): redraw, keeping the scroll position.</summary>
    private void RefreshIfRowsChanged()
    {
        if (Model.Destination.Section is not null && !Model.IsSearching) return;
        if (CurrentItemIds().SequenceEqual(shownItemIds)) return;
        var offset = (contentHost.Content as ScrollViewer)?.VerticalOffset ?? 0;
        Render(force: true);
        if (contentHost.Content is ScrollViewer scroller)
        {
            scroller.Dispatcher.BeginInvoke(() => scroller.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void Render(bool force = false)
    {
        var key = Model.IsSearching ? "search:" + Model.TrimmedSearchText : Model.Destination.ToString();
        if (!force && key == shownKey && contentHost.Content is not null) return;
        shownKey = key;
        shownItemIds = CurrentItemIds();
        if (Model.IsSearching)
        {
            var query = Model.TrimmedSearchText;
            var matches = catalog.SearchMatches(query);
            contentHost.Content = new SettingsFormView("Search Results", SettingsSectionGroup.SearchResultGroups(matches, query),
                item => rowFactory.Build(item, query), $"No settings match “{query}”.");
        }
        else if (Model.Destination.Page is { } page)
        {
            contentHost.Content = new SettingsFormView(page.Title(), SettingsSectionGroup.Groups(catalog.PageItems(page), foldsAdvanced: true),
                item => rowFactory.Build(item, ""), page: page);
        }
        else if (Model.Destination.Section == DictationSection.Words)
        {
            contentHost.Content = new WordsPageView(services, Model, this, icons);
        }
        else
        {
            contentHost.Content = new WritingPageView(services, this, icons);
        }
        UpdateSidebarSelection();
    }

    private void UpdateSidebarSelection()
    {
        var matches = Model.IsSearching ? catalog.SearchMatches(Model.TrimmedSearchText) : [];
        foreach (var (destination, item) in sidebarItems)
        {
            // A page opened from a row keeps its parent selected (The orb keeps Dictation).
            var isCurrent = !Model.IsSearching && destination.SidebarPage == Model.Destination.SidebarPage;
            if (isCurrent) item.Activate(navigation);
            else item.Deactivate(navigation);
            // While searching, the pages with no match on them or the pages under them fade back.
            var hasMatch = matches.Any(match => match.Page.SidebarPage() == destination.SidebarPage);
            item.Opacity = Model.IsSearching && !hasMatch ? 0.35 : 1;
        }
    }

    // MARK: - sidebar

    private WpfUi.NavigationViewItem SidebarItem(DictationDestination destination)
    {
        var page = destination.SidebarPage;
        var item = new WpfUi.NavigationViewItem
        {
            Content = destination.Title,
            Icon = new WpfUi.ImageIcon { Source = SidebarIcon(page.Glyph(), page.TintHex()), Width = 20, Height = 20 },
            Cursor = Cursors.Hand,
        };
        Ui.AutomationProperties(item, destination.Title);
        item.Click += (_, _) => Model.Select(destination);
        TourTarget.Attach(item, SettingsTourTargets.SidebarTargetId(destination));
        return item;
    }

    /// <summary>A sidebar icon like System Settings': a white glyph in a coloured rounded square (the orb's own capsule for The Orb).</summary>
    public static ImageSource SidebarIcon(string glyph, string tintHex)
    {
        var tint = (Color)ColorConverter.ConvertFromString(tintHex);
        var lighter = Color.FromRgb((byte)Math.Min(255, tint.R + 28), (byte)Math.Min(255, tint.G + 28), (byte)Math.Min(255, tint.B + 28));
        var group = new DrawingGroup();
        var gradient = new LinearGradientBrush(lighter, tint, 90);
        group.Children.Add(new GeometryDrawing(gradient, null, new RectangleGeometry(new Rect(0, 0, 20, 20), 5, 5)));
        Geometry symbol;
        if (glyph.Length == 0)
        {
            symbol = new RectangleGeometry(new Rect(4.5, 7, 11, 6), 3, 3);
        }
        else
        {
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                11.5, Brushes.White, 2.0);
            var geometry = text.BuildGeometry(new Point(0, 0));
            var bounds = geometry.Bounds;
            geometry.Transform = new TranslateTransform(10 - bounds.X - bounds.Width / 2, 10 - bounds.Y - bounds.Height / 2);
            symbol = geometry;
        }
        group.Children.Add(new GeometryDrawing(Brushes.White, null, symbol));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>The sidebar's foot: incognito, and who is signed in (a click opens Account).</summary>
    private StackPanel SidebarFooter()
    {
        var footer = new StackPanel { Margin = new Thickness(4, 0, 4, 4) };
        var divider = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        divider.SetResourceReference(Border.BackgroundProperty, SettingsLook.Divider);
        footer.Children.Add(divider);
        var incognitoRow = new LiveContent(() =>
        {
            var row = new DockPanel { Margin = new Thickness(12, 4, 8, 4) };
            var toggle = Ui.Toggle(services.Settings.Incognito, isOn => services.Settings.Incognito = isOn);
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            var label = Ui.Text("Incognito", 14, SettingsLook.TextPrimary, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            row.ToolTip = services.Settings.Incognito ? "Incognito is on: takes paste but are not saved." : "Incognito is off: takes are saved to history on this PC.";
            return row;
        }, Changes.Of(services.Settings, nameof(DictationSettings.Incognito)));
        footer.Children.Add(incognitoRow);
        var secondDivider = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        secondDivider.SetResourceReference(Border.BackgroundProperty, SettingsLook.Divider);
        footer.Children.Add(secondDivider);
        footer.Children.Add(footerAccount);
        return footer;
    }

    private void RenderFooterAccount()
    {
        var auth = services.Auth;
        var name = AccountRows.AccountName(auth.AccountEmail, WindowsUserName.FullName);
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(name, 14, SettingsLook.TextPrimary, FontWeights.Medium, wrap: false));
        text.Children.Add(Ui.Text(AccountRows.AccountStanding(auth.AccountEmail is not null, auth.IsGuestWithoutEmail), SettingsLook.CaptionSize, SettingsLook.TextSecondary, wrap: false));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 6) };
        row.Children.Add(AccountProfileRow.Avatar(AccountRows.AccountInitial(name), auth.AccountEmail is not null, 30));
        row.Children.Add(text);
        var button = new WpfUi.Button
        {
            Content = row,
            Appearance = WpfUi.ControlAppearance.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "Account",
        };
        button.Click += (_, _) => Model.Select(DictationSettingsPage.Account);
        footerAccount.Content = button;
    }

    // MARK: - keys, closing, frame

    private void OnPreviewKeyDown(object sender, KeyEventArgs arguments)
    {
        // Ctrl+, opens General, wherever the focus is.
        if (arguments.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Model.Select(DictationSettingsPage.General);
            arguments.Handled = true;
        }
        else if (arguments.Key == Key.Escape && Tour.IsRunning)
        {
            Tour.Stop();
            arguments.Handled = true;
        }
        else if (arguments.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            searchBox.Focus();
            arguments.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs arguments)
    {
        Tour.Stop();
        SaveFrame();
        if (HidesOnClose)
        {
            arguments.Cancel = true;
            Hide();
        }
    }

    /// <summary>Tall enough that the whole sidebar shows without scrolling, no wider than a page needs; smaller on a smaller screen.</summary>
    public static Size StartingSize
    {
        get
        {
            var room = SystemParameters.WorkArea;
            return new Size(Math.Min(1040, room.Width - 40), Math.Min(880, room.Height - 40));
        }
    }

    private void RestoreFrame()
    {
        var starting = StartingSize;
        Width = starting.Width;
        Height = starting.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var stored = services.Preferences.GetString(FramePreferenceKey)?.Split(',');
        if (stored is not { Length: 4 }) return;
        var numbers = stored.Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN).ToArray();
        if (numbers.Any(double.IsNaN)) return;
        var frame = new Rect(numbers[0], numbers[1], Math.Max(MinimumSize.Width, numbers[2]), Math.Max(MinimumSize.Height, numbers[3]));
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // A frame left on a monitor that is gone would open off screen: start centred instead.
        var visiblePart = Rect.Intersect(frame, desktop);
        if (visiblePart.IsEmpty || visiblePart.Width < 200 || visiblePart.Height < 120) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = frame.X;
        Top = frame.Y;
        Width = frame.Width;
        Height = frame.Height;
    }

    private void SaveFrame()
    {
        var frame = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (frame.IsEmpty || double.IsInfinity(frame.Width)) return;
        services.Preferences.Set(FramePreferenceKey, string.Join(",", new[] { frame.X, frame.Y, frame.Width, frame.Height }
            .Select(number => Math.Round(number).ToString(CultureInfo.InvariantCulture))));
    }
}
