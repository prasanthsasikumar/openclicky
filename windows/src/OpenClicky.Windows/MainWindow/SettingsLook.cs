// Ports SettingsLook (Dictation/UI/SettingsPages.swift) and NSWindow.followAppearanceSetting
// (DictationWindow.swift): the window's colours and type, and the Fluent theme each of its windows
// installs. The Mac used the system's own tokens; here they are WPF-UI's Windows 11 tokens, with
// the proposal's orange accent (#D24E2A light, #E8653F dark) in place of the system accent.
//
// The theme dictionaries are loaded into each window's own Resources, so these windows look right
// without App.xaml. Integration: merge <ui:ThemesDictionary/> and <ui:ControlsDictionary/> into
// App.xaml's resources as well, so context menus and popups outside a window's tree are styled too.

using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

namespace OpenClicky.Windows.AppWindow;

public static class SettingsLook
{
    public static readonly Color AccentLight = Color.FromRgb(0xD2, 0x4E, 0x2A);
    public static readonly Color AccentDark = Color.FromRgb(0xE8, 0x65, 0x3F);
    public static readonly Color Highlighter = Color.FromArgb(0x5A, 0xFF, 0xD6, 0x0A);

    public const double RowTitleSize = 14;
    public const double CaptionSize = 12;
    public const double MicroSize = 11;
    public const double PageTitleSize = 28;
    public const double SectionTitleSize = 14;

    // WPF-UI's Windows 11 resource keys, looked up dynamically so a theme change repaints.
    public const string TextPrimary = "TextFillColorPrimaryBrush";
    public const string TextSecondary = "TextFillColorSecondaryBrush";
    public const string TextTertiary = "TextFillColorTertiaryBrush";
    public const string CardBackground = "CardBackgroundFillColorDefaultBrush";
    public const string CardStroke = "CardStrokeColorDefaultBrush";
    public const string Divider = "DividerStrokeColorDefaultBrush";
    public const string ControlFill = "ControlFillColorDefaultBrush";
    public const string ControlStroke = "ControlStrokeColorDefaultBrush";
    public const string SubtleFill = "SubtleFillColorSecondaryBrush";
    public const string Danger = "SystemFillColorCriticalBrush";
    public const string Success = "SystemFillColorSuccessBrush";
    public const string Caution = "SystemFillColorCautionBrush";
    /// <summary>Our own key: the orange accent for the current theme.</summary>
    public const string Accent = "OpenClickyAccentBrush";
    /// <summary>Our own key: the accent at 12 % (a selected take).</summary>
    public const string Selection = "OpenClickySelectionBrush";
}

/// <summary>Installs and follows the Fluent theme for one window (light, dark, or Windows' own).</summary>
public sealed class MainWindowTheme : IDisposable
{
    private readonly Window window;
    private readonly IDisposable appearanceSubscription;
    /// <summary>
    /// Where the theme is loaded: the window, and any part of it that does not inherit the window's
    /// resources (NavigationView shows its page in a Frame, whose content is a tree of its own).
    /// </summary>
    private readonly List<(ResourceDictionary Resources, ThemesDictionary Themes)> scopes = [];
    private ApplicationTheme installedTheme = ApplicationTheme.Light;
    private DictationAppearance appearance;

    public bool IsDark { get; private set; }

    /// <summary>Raised after the theme switched (the keyboard sketch and orb previews redraw).</summary>
    public event Action? Changed;

    private MainWindowTheme(Window window, DictationSettings settings)
    {
        this.window = window;
        AddScope(window.Resources);
        appearanceSubscription = WindowAppearance.Follow(settings, newAppearance =>
        {
            appearance = newAppearance;
            if (window.Dispatcher.CheckAccess()) Apply();
            else window.Dispatcher.BeginInvoke(Apply);
        });
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        window.SourceInitialized += (_, _) => Apply();
        window.Closed += (_, _) => Dispose();
    }

    /// <summary>Loads the theme into <paramref name="window"/> and keeps it following the Appearance setting.</summary>
    public static MainWindowTheme Install(Window window, DictationSettings settings) => new(window, settings);

    /// <summary>Loads the theme into another element's resources too (content hosted in a Frame).</summary>
    public void AddScope(FrameworkElement element) => AddScope(element.Resources);

    private void AddScope(ResourceDictionary resources)
    {
        var themes = new ThemesDictionary { Theme = installedTheme };
        resources.MergedDictionaries.Add(themes);
        resources.MergedDictionaries.Add(new ControlsDictionary());
        scopes.Add((resources, themes));
        ApplyAccent(resources, IsDark);
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int usesLight && usesLight == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs arguments)
    {
        if (arguments.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            window.Dispatcher.BeginInvoke(Apply);
    }

    private void Apply()
    {
        var isDark = WindowAppearance.IsDark(appearance, SystemUsesDarkTheme());
        var theme = isDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        for (var index = 0; index < scopes.Count; index++)
        {
            var (resources, themes) = scopes[index];
            if (installedTheme != theme)
            {
                var replacement = new ThemesDictionary { Theme = theme };
                var position = resources.MergedDictionaries.IndexOf(themes);
                if (position >= 0) resources.MergedDictionaries[position] = replacement;
                else resources.MergedDictionaries.Insert(0, replacement);
                scopes[index] = (resources, replacement);
            }
            ApplyAccent(resources, isDark);
        }
        installedTheme = theme;
        IsDark = isDark;
        if (window is FluentWindow fluentWindow && new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            WindowBackgroundManager.UpdateBackground(window, theme, fluentWindow.WindowBackdropType);
        }
        Changed?.Invoke();
    }

    /// <summary>The orange accent in place of the system's, in this window only.</summary>
    private static void ApplyAccent(ResourceDictionary resources, bool isDark)
    {
        var accent = isDark ? SettingsLook.AccentDark : SettingsLook.AccentLight;
        var lighter = Blend(accent, Colors.White, 0.25);
        var darker = Blend(accent, Colors.Black, 0.2);
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorPrimary"] = isDark ? lighter : accent;
        resources["SystemAccentColorSecondary"] = accent;
        resources["SystemAccentColorTertiary"] = isDark ? darker : lighter;
        var accentBrush = Frozen(accent);
        foreach (var key in new[]
                 {
                     "SystemAccentBrush", "SystemAccentColorPrimaryBrush", "SystemAccentColorSecondaryBrush", "SystemAccentColorTertiaryBrush",
                     "AccentFillColorDefaultBrush", "AccentControlElevationBorderBrush",
                 })
        {
            resources[key] = accentBrush;
        }
        resources["AccentFillColorSecondaryBrush"] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
        resources["AccentFillColorTertiaryBrush"] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        resources["AccentTextFillColorPrimaryBrush"] = Frozen(isDark ? lighter : darker);
        resources["AccentTextFillColorSecondaryBrush"] = Frozen(isDark ? lighter : darker);
        resources["AccentTextFillColorTertiaryBrush"] = accentBrush;
        resources["AccentFillColorSelectedTextBackgroundBrush"] = accentBrush;
        resources[SettingsLook.Accent] = accentBrush;
        resources[SettingsLook.Selection] = Frozen(Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount), (byte)(from.G + (to.G - from.G) * amount), (byte)(from.B + (to.B - from.B) * amount));

    public void Dispose()
    {
        appearanceSubscription.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
