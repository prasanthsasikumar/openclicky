//
//  OrbWindow.cs
//  OpenClicky (Windows port of Dictation/OrbPanel.swift's OrbPanelManager and its SwiftUI views)
//
//  The orb: a small floating pill at the bottom centre of the primary monitor, just above the
//  taskbar, showing the take as it happens: resting as two dashes (or the mark, or the mark seen
//  through a pixel grid), bars while listening with the words as they come, a spinner while the
//  words are on their way, a check or a warning after; and the transcript box above it when the
//  words had nowhere to go. The window is sized to its content so the desktop around it stays
//  clickable, never takes the keyboard, can be dragged (its spot is remembered), and fades away
//  when idle if "hide when not in use" is on.
//

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Orb;

public sealed class OrbWindow : Window
{
    private readonly OrbModel model;
    private readonly OrbSettings settings;
    private readonly IScreens screens;
    private readonly Dispatcher dispatcher;

    private readonly StackPanel stack = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border pill = new();
    private readonly Grid pillContent = new();
    private readonly TextBlock hintText;
    private Border? transcriptBox;
    private OrbLevelBarsView? levelBars;
    private SpinnerView? workingSpinner;

    private bool isStarted;
    private ScreenInfo? currentScreen;
    private PixelRect currentFrame;
    private int hideGeneration;

    // Dragging the pill.
    private bool isPressOnPill;
    private bool didDrag;
    private PixelPoint dragStartCursor;
    private PixelRect dragStartFrame;

    public OrbWindow(OrbModel model, OrbSettings settings, IScreens screens)
    {
        this.model = model;
        this.settings = settings;
        this.screens = screens;
        dispatcher = Dispatcher.CurrentDispatcher;
        Title = "OpenClicky orb";
        FloatingWindowSupport.Configure(this, clickThrough: false);
        hintText = Text("", 10, Brushes.Gray, FontWeights.Medium);
        hintText.HorizontalAlignment = HorizontalAlignment.Center;
        hintText.Height = OrbMetrics.HintHeight - 6;
        hintText.Margin = new Thickness(0, 8, 0, 0);

        pill.Child = pillContent;
        pill.Cursor = Cursors.Hand;
        pill.HorizontalAlignment = HorizontalAlignment.Center;
        pill.MouseLeftButtonDown += OnPillMouseDown;
        pill.MouseMove += OnPillMouseMove;
        pill.MouseLeftButtonUp += OnPillMouseUp;
        stack.Children.Add(pill);
        stack.Children.Add(hintText);
        Content = new Border { Padding = new Thickness(OrbMetrics.Padding), Child = stack };

        DpiChanged += (_, _) => { if (!currentFrame.IsEmpty) FloatingWindowSupport.PlaceInPixels(this, currentFrame); };
        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    // MARK: - Starting

    public void Start()
    {
        if (isStarted) return;
        isStarted = true;
        settings.SettingsChanged += OnSettingsChanged;
        settings.PropertyChanged += OnSettingsPropertyChanged;
        model.PropertyChanged += OnModelPropertyChanged;
        screens.DisplaysChanged += OnDisplaysChanged;
        ApplySettings();
    }

    public void Stop()
    {
        if (!isStarted) return;
        isStarted = false;
        settings.SettingsChanged -= OnSettingsChanged;
        settings.PropertyChanged -= OnSettingsPropertyChanged;
        model.PropertyChanged -= OnModelPropertyChanged;
        screens.DisplaysChanged -= OnDisplaysChanged;
        Hide();
    }

    private void OnSettingsChanged() => dispatcher.BeginInvoke(settings.Reload);

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs) => ApplySettings();

    private void OnDisplaysChanged() => dispatcher.BeginInvoke(LayoutWindow);

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            // Only what changes the geometry rebuilds; the level bars redraw inside the pill on their own.
            case nameof(OrbModel.Phase):
            case nameof(OrbModel.IsBoxOpen):
            case nameof(OrbModel.BoxText):
            case nameof(OrbModel.Hint):
            case nameof(OrbModel.QuickRewrites):
            case nameof(OrbModel.IsRewriting):
            case nameof(OrbModel.BoxReason):
                RebuildContent();
                LayoutWindow();
                break;
            case nameof(OrbModel.LiveTranscript):
                RebuildContent();
                break;
        }
    }

    private void ApplySettings()
    {
        if (settings.OrbVisible)
        {
            RebuildContent();
            LayoutWindow();
        }
        else
        {
            Hide();
        }
    }

    // MARK: - Theme

    private Color Fill => settings.OrbTheme switch
    {
        OrbThemeChoice.Coral => Color.FromRgb(227, 84, 54),
        OrbThemeChoice.Mist => Color.FromRgb(237, 237, 237),
        _ => Color.FromRgb(41, 41, 41),
    };

    private Brush Ink => settings.OrbTheme == OrbThemeChoice.Mist ? DS.Brush(Color.FromRgb(51, 51, 51)) : DS.Brush(Colors.White, 0.92);

    /// <summary>The box and the hint follow the dictation appearance (system = Windows' app mode).</summary>
    private bool IsDarkAppearance => settings.Appearance switch
    {
        DictationAppearanceChoice.Dark => true,
        DictationAppearanceChoice.Light => false,
        _ => SystemUsesDarkApps(),
    };

    private static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    // MARK: - Content

    private (double Width, double Height) PillSize => OrbMetrics.PillSize(settings.OrbSize);

    private void RebuildContent()
    {
        var size = PillSize;
        var isExpanded = model.Phase.Kind != OrbPhaseKind.Idle;
        var width = isExpanded ? Math.Max(size.Width, OrbMetrics.ExpandedPillWidth) : size.Width;
        var height = isExpanded ? Math.Max(size.Height, 30) : size.Height;
        pill.Width = width;
        pill.Height = height;
        pill.CornerRadius = new CornerRadius(height / 2);
        pill.Background = DS.Brush(Fill);
        pill.BorderBrush = DS.Brush(Colors.White, settings.OrbTheme == OrbThemeChoice.Mist ? 0.9 : 0.08);
        pill.BorderThickness = new Thickness(1);
        pill.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.22, BlurRadius = 16, ShadowDepth = 3, Direction = 270 };
        pill.ToolTip = OrbPhaseTexts.PillToolTip(settings.DictationKey);
        pillContent.Children.Clear();
        levelBars = null;
        workingSpinner = null;
        var ink = Ink;

        switch (model.Phase.Kind)
        {
            case OrbPhaseKind.Idle:
                pillContent.Children.Add(IdleContent(size, ink));
                break;
            case OrbPhaseKind.Listening:
            case OrbPhaseKind.EditListening:
            {
                levelBars = new OrbLevelBarsView(ink) { Width = 34, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
                var words = model.LiveTranscript.Length == 0
                    ? (model.Phase.Kind == OrbPhaseKind.EditListening ? OrbPhaseTexts.SayYourEdit : OrbPhaseTexts.Listening)
                    : model.LiveTranscript;
                var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var label = Text(TrimHead(words, 160, 12, DS.Fonts.Ui, FontWeights.Medium, pixelsPerDip), 12, ink, FontWeights.Medium);
                label.MaxWidth = 160;
                pillContent.Children.Add(PillRow(levelBars, label));
                break;
            }
            case OrbPhaseKind.Working:
            {
                var spinnerColor = settings.OrbTheme == OrbThemeChoice.Mist ? Color.FromRgb(51, 51, 51) : Colors.White;
                workingSpinner = new SpinnerView(spinnerColor) { Margin = new Thickness(0, 0, 8, 0), Effect = null };
                pillContent.Children.Add(PillRow(workingSpinner, Text(model.Phase.Message, 12, ink, FontWeights.Medium, trimming: TextTrimming.CharacterEllipsis)));
                break;
            }
            case OrbPhaseKind.Done:
            case OrbPhaseKind.Failed:
            {
                var glyph = Glyph(model.Phase.Kind == OrbPhaseKind.Done ? Icons.CheckMark : Icons.Error, 11, ink);
                glyph.FontWeight = FontWeights.Bold;
                glyph.Margin = new Thickness(0, 0, 6, 0);
                pillContent.Children.Add(PillRow(glyph, Text(model.Phase.Message, 12, ink, FontWeights.Medium, trimming: TextTrimming.CharacterEllipsis)));
                break;
            }
        }

        // The hint under the pill, when tooltips are on.
        var showsHint = settings.Tooltips && model.Hint is not null;
        hintText.Text = model.Hint ?? "";
        hintText.Foreground = IsDarkAppearance ? DS.Brush(Colors.White, 0.55) : DS.Brush(Colors.Black, 0.5);
        hintText.Visibility = showsHint ? Visibility.Visible : Visibility.Collapsed;

        // The transcript box above the pill.
        if (transcriptBox is not null) stack.Children.Remove(transcriptBox);
        transcriptBox = null;
        if (model.IsBoxOpen && model.BoxText is { } boxText)
        {
            transcriptBox = TranscriptBox(boxText);
            stack.Children.Insert(0, transcriptBox);
        }
    }

    private FrameworkElement IdleContent((double Width, double Height) size, Brush ink)
    {
        switch (settings.OrbLook)
        {
            case OrbLookChoice.Classic:
            {
                var side = size.Height * 0.5;
                return new Path { Data = Shapes.OrbMark(new Rect(0, 0, side, side)), Fill = ink, Width = side, Height = side, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            case OrbLookChoice.Pixel:
            {
                var side = size.Height * 0.55;
                var rect = new Rect(0, 0, side, side);
                var masked = Geometry.Combine(Shapes.OrbMark(rect), Shapes.PixelGrid(rect), GeometryCombineMode.Intersect, null);
                return new Path { Data = masked, Fill = ink, Width = side, Height = side, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            default:
            {
                var dashWidth = settings.OrbSize == OrbSizeChoice.Mini ? 6 : 9;
                Border Dash() => new() { Width = dashWidth, Height = 2.5, CornerRadius = new CornerRadius(1.25), Background = ink, Opacity = 0.9 };
                var dashes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var first = Dash();
                first.Margin = new Thickness(0, 0, 6, 0);
                dashes.Children.Add(first);
                dashes.Children.Add(Dash());
                return dashes;
            }
        }
    }

    private static StackPanel PillRow(FrameworkElement indicator, TextBlock label)
    {
        indicator.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(indicator);
        label.MaxWidth = Math.Min(label.MaxWidth > 0 ? label.MaxWidth : double.PositiveInfinity, OrbMetrics.ExpandedPillWidth - 24 - 20);
        row.Children.Add(label);
        return row;
    }

    /// <summary>The box: the take's text (selectable, scrolls), the rewrite chips, then why it is open and paste / copy / ✕.</summary>
    private Border TranscriptBox(string text)
    {
        var isDark = IsDarkAppearance;
        var primary = isDark ? Colors.White : Colors.Black;
        var primaryBrush = DS.Brush(primary, 0.88);
        var secondaryBrush = DS.Brush(primary, isDark ? 0.55 : 0.5);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = primaryBrush,
            FontFamily = DS.Fonts.Ui,
            FontSize = 13,
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FocusVisualStyle = null,
        };
        grid.Children.Add(body);

        var rewrites = model.IsRewriting ? Array.Empty<string>() : model.QuickRewrites;
        if (rewrites.Count > 0)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var rewrite in rewrites)
            {
                var chipLabel = Text(rewrite, 10, primaryBrush, FontWeights.Medium);
                var chip = Clickable(chipLabel, () => model.OnQuickRewrite?.Invoke(rewrite), background: DS.Brush(primary, 0.08), cornerRadius: new CornerRadius(9));
                chip.Padding = new Thickness(8, 3, 8, 3);
                chip.Margin = new Thickness(0, 0, 6, 0);
                chips.Children.Add(chip);
            }
            Grid.SetRow(chips, 1);
            grid.Children.Add(chips);
        }

        var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        Border SmallButton(UIElement content, Action onClick)
        {
            var button = Clickable(content, onClick, background: DS.Brush(primary, 0.1), cornerRadius: new CornerRadius(5));
            button.Padding = new Thickness(8, 2, 8, 3);
            button.Margin = new Thickness(6, 0, 0, 0);
            return button;
        }
        var close = SmallButton(Glyph(Icons.Close, 8, primaryBrush), () => model.IsBoxOpen = false);
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var copy = SmallButton(Text("copy", 11, primaryBrush), () =>
        {
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.COMException) { return; }
            if (!settings.OrbRestsExpanded) model.IsBoxOpen = false;
        });
        DockPanel.SetDock(copy, Dock.Right);
        footer.Children.Add(copy);
        if (model.OnPasteFromBox is { } paste)
        {
            var pasteButton = SmallButton(Text("paste", 11, primaryBrush), paste);
            DockPanel.SetDock(pasteButton, Dock.Right);
            footer.Children.Add(pasteButton);
        }
        var reason = Text(model.IsRewriting ? OrbPhaseTexts.Rewriting : model.BoxReason, 10, secondaryBrush, trimming: TextTrimming.CharacterEllipsis);
        footer.Children.Add(reason);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        return new Border
        {
            Width = OrbMetrics.BoxWidth,
            Height = OrbMetrics.BoxHeight,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(14),
            Background = isDark ? DS.Brush(Color.FromRgb(44, 44, 46), 0.97) : DS.Brush(Color.FromRgb(246, 246, 248), 0.97),
            BorderBrush = DS.Brush(primary, 0.08),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.18, BlurRadius = 24, ShadowDepth = 4, Direction = 270 },
            Child = grid,
        };
    }

    private void OnRendering(object? sender, EventArgs eventArgs)
    {
        if (!IsVisible) return;
        if (levelBars is not null)
        {
            levelBars.Level = model.AudioLevel;
            levelBars.Tick();
        }
        workingSpinner?.Tick();
    }

    // MARK: - Layout

    /// <summary>Sizes the window to what is showing and keeps the pill where it rests.</summary>
    private void LayoutWindow()
    {
        if (!isStarted || !settings.OrbVisible) return;
        var pillSize = PillSize;
        var showsBox = model.IsBoxOpen && model.BoxText is not null;
        var showsHint = model.Hint is not null && settings.Tooltips;
        var width = OrbMetrics.ContentWidth(model.Phase.Kind, settings.OrbSize, showsBox) + OrbMetrics.Padding * 2;
        var height = Math.Max(pillSize.Height, model.Phase.Kind == OrbPhaseKind.Idle ? pillSize.Height : 30) + OrbMetrics.Padding * 2;
        if (showsBox) height += OrbMetrics.BoxHeight + 8;
        if (showsHint) height += OrbMetrics.HintHeight;

        var (anchor, screen) = RestingAnchor();
        currentScreen = screen;
        // The anchor is the pill's bottom centre; the hint hangs below it, the box stacks above.
        var hintSpace = showsHint ? OrbMetrics.HintHeight : 0;
        var scale = screen.Scale;
        var bottom = anchor.Y + (OrbMetrics.Padding + hintSpace) * scale;
        currentFrame = PixelRect.FromEdges(
            (int)Math.Round(anchor.X - width * scale / 2),
            (int)Math.Round(bottom - height * scale),
            (int)Math.Round(anchor.X + width * scale / 2),
            (int)Math.Round(bottom));
        Width = width;
        Height = height;
        if (!IsVisible)
        {
            // Hidden-when-idle starts out of sight; it fades in with the first take.
            if (settings.OrbHidesWhenIdle && !model.IsBusy && !model.IsBoxOpen) Opacity = 0;
            Show();
        }
        FloatingWindowSupport.PlaceInPixels(this, currentFrame);
        ScheduleHideIfIdle();
    }

    /// <summary>The pill's bottom centre in pixels: the remembered drag (if it is still on a monitor), else the primary
    /// monitor's work area, centred, 18 above the taskbar.</summary>
    private (PixelPoint Anchor, ScreenInfo Screen) RestingAnchor()
    {
        var allScreens = screens.All();
        if (settings.OrbPosition is { } remembered)
        {
            var point = new PixelPoint((int)Math.Round(remembered.X), (int)Math.Round(remembered.Y));
            var holder = allScreens.FirstOrDefault(screen => screen.Bounds.Inflated((int)(20 * screen.Scale)).Contains(point));
            if (holder is not null) return (point, holder);
        }
        var primary = screens.Primary() ?? allScreens.First();
        var workArea = primary.WorkArea;
        return (new PixelPoint(workArea.Left + workArea.Width / 2, workArea.Bottom - (int)Math.Round(OrbMetrics.RestingBottomInset * primary.Scale)), primary);
    }

    private void RememberPosition()
    {
        if (currentScreen is not { } screen) return;
        var showsHint = model.Hint is not null && settings.Tooltips;
        var hintSpace = showsHint ? OrbMetrics.HintHeight : 0;
        settings.OrbPosition = (currentFrame.Left + currentFrame.Width / 2.0, currentFrame.Bottom - (OrbMetrics.Padding + hintSpace) * screen.Scale);
    }

    /// <summary>"hide when not in use": the orb fades out a moment after a take finishes.</summary>
    private async void ScheduleHideIfIdle()
    {
        hideGeneration++;
        var generation = hideGeneration;
        if (!settings.OrbHidesWhenIdle)
        {
            FadeTo(1, 0);
            return;
        }
        if (model.IsBusy || model.IsBoxOpen)
        {
            FadeTo(1, settings.ReduceAnimation ? 0 : 0.15);
            return;
        }
        FadeTo(1, 0);
        await Task.Delay(1600);
        if (generation != hideGeneration || model.IsBusy || model.IsBoxOpen) return;
        FadeTo(0, settings.ReduceAnimation ? 0 : 0.35);
    }

    private void FadeTo(double opacity, double seconds)
    {
        if (seconds <= 0)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = opacity;
            return;
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromSeconds(seconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    // MARK: - Dragging and clicking the pill

    private void OnPillMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        isPressOnPill = true;
        didDrag = false;
        dragStartCursor = FloatingWindowSupport.CursorPixel();
        dragStartFrame = currentFrame;
        pill.CaptureMouse();
        eventArgs.Handled = true;
    }

    private void OnPillMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (!isPressOnPill || !settings.OrbIsDraggable) return;
        var now = FloatingWindowSupport.CursorPixel();
        var deltaX = now.X - dragStartCursor.X;
        var deltaY = now.Y - dragStartCursor.Y;
        var scale = currentScreen?.Scale ?? 1;
        if (!didDrag && Math.Sqrt(deltaX * deltaX + deltaY * deltaY) < 3 * scale) return;
        didDrag = true;
        currentFrame = new PixelRect(dragStartFrame.Left + deltaX, dragStartFrame.Top + deltaY, dragStartFrame.Width, dragStartFrame.Height);
        FloatingWindowSupport.PlaceInPixels(this, currentFrame);
    }

    private void OnPillMouseUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!isPressOnPill) return;
        isPressOnPill = false;
        pill.ReleaseMouseCapture();
        eventArgs.Handled = true;
        if (didDrag)
        {
            currentScreen = screens.ScreenAt(new PixelPoint(currentFrame.Left + currentFrame.Width / 2, currentFrame.Bottom)) ?? currentScreen;
            RememberPosition();
        }
        else
        {
            model.NotePillClicked();
        }
    }
}

/// <summary>Five bars that follow the microphone level (OrbLevelBars): 3 wide, 3 apart, each wobbling on its own phase.</summary>
internal sealed class OrbLevelBarsView : Canvas
{
    private readonly Rectangle[] bars = new Rectangle[5];

    public double Level { get; set; }

    public OrbLevelBarsView(Brush color)
    {
        IsHitTestVisible = false;
        for (var index = 0; index < bars.Length; index++)
        {
            bars[index] = new Rectangle { Width = 3, Fill = color, RadiusX = 1.5, RadiusY = 1.5 };
            SetLeft(bars[index], 2 + index * 6);
            Children.Add(bars[index]);
        }
    }

    public void Tick()
    {
        var time = MotionClock.Now;
        var height = double.IsNaN(Height) ? 16 : Height;
        for (var index = 0; index < bars.Length; index++)
        {
            var wobble = (Math.Sin(time * 9 + index * 1.3) + 1) / 2;
            var barHeight = Math.Max(3, 3 + Level * 12 * (0.5 + 0.5 * wobble));
            bars[index].Height = barHeight;
            SetTop(bars[index], (height - barHeight) / 2);
        }
    }
}
