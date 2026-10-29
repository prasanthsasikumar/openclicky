//
//  IslandViews.cs
//  OpenClicky (Windows port of the views in NotchHUD.swift, NotchHUDPanels.swift,
//  PermissionPrompt.swift, AppConnectPrompt.swift and FirstRunTour.swift)
//
//  What the island shows in each state, drawn with the HUD tokens: the collapsed handle, the busy
//  strip and the caption strip, the composer, the connect / permission / tour cards and the open
//  panel (the band with the pointer's status, the voice pill, Settings and quit; the body with
//  (i) and the 2 × 2 shortcut grid or the engine warning). Each view refreshes its words from
//  the models (Refresh) and animates its indicators every frame (Tick).
//

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Island;

internal interface IIslandContent
{
    /// <summary>Re-read the words and states from the models.</summary>
    void Refresh();

    /// <summary>One frame of the indicators (level bars, spinner, keycaps).</summary>
    void Tick();
}

// MARK: - Collapsed

/// <summary>The thin handle at the top edge (or a vertical one against a drawer's edge): a 44 × 6 dark capsule with a
/// 36 × 2 light line, so there is something to hover.</summary>
internal sealed class CollapsedHandleView : Grid, IIslandContent
{
    public CollapsedHandleView(HUDPlacement placement)
    {
        IsHitTestVisible = false;
        if (placement == HUDPlacement.Hidden) return;
        var isVertical = placement.IsSideDrawer();
        var outer = new Border
        {
            Width = isVertical ? 6 : 44,
            Height = isVertical ? 44 : 6,
            CornerRadius = new CornerRadius(3),
            Background = DS.Brush(Colors.Black, 0.7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = isVertical ? VerticalAlignment.Center : VerticalAlignment.Top,
            Margin = new Thickness(0, isVertical ? 0 : 3, 0, 0),
        };
        outer.Child = new Border
        {
            Width = isVertical ? 2 : 36,
            Height = isVertical ? 36 : 2,
            CornerRadius = new CornerRadius(1),
            Background = DS.Brush(Colors.White, 0.75),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(outer);
    }

    public void Refresh() { }
    public void Tick() { }
}

// MARK: - Peek

/// <summary>The hover tease (NotchPeekLine): the pointer's mark and status (or the playing track, with Now playing on),
/// then "· click to open", on one line under the band.</summary>
internal sealed class PeekLineView : Grid, IIslandContent
{
    private readonly IslandCompanionModel hub;
    private readonly PointerTriangleView mark = new(10, DS.HUD.Pointer, 3);
    private readonly TextBlock statusText;

    public PeekLineView(IslandCompanionModel hub, double topInset)
    {
        this.hub = hub;
        // The whole line is the island's click target: a click opens the panel.
        IsHitTestVisible = false;
        Loaded += (_, _) => Plugins.Media.MediaIslandView.Store.Changed += OnPluginLineChanged;
        Unloaded += (_, _) => Plugins.Media.MediaIslandView.Store.Changed -= OnPluginLineChanged;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, topInset, 10, 0),
        };
        var markHolder = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0) };
        markHolder.Children.Add(mark);
        row.Children.Add(markHolder);
        // A long track name is cut, so "click to open" always fits.
        statusText = Text("", 11, DS.HUD.Text2Brush, trimming: TextTrimming.CharacterEllipsis);
        statusText.MaxWidth = 170;
        row.Children.Add(statusText);
        row.Children.Add(Text(" · click to open", 11, DS.HUD.Text3Brush));
        Children.Add(row);
        // A coding session starting or stopping changes the line while it shows.
        Loaded += (_, _) => Plugins.CodingAgents.AgentsIslandView.Sessions.Changed += OnPluginLineChanged;
        Unloaded += (_, _) => Plugins.CodingAgents.AgentsIslandView.Sessions.Changed -= OnPluginLineChanged;
        Refresh();
    }

    /// <summary>A coding session or the track changed: the line follows while it shows.</summary>
    private void OnPluginLineChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    public void Refresh()
    {
        var sessions = Plugins.CodingAgents.AgentsIslandView.Sessions;
        // The watcher only runs while the Coding agents plugin is on, so its line is null otherwise.
        var agentLine = sessions.IsEnabled ? sessions.PeekLine : null;
        statusText.Text = IslandTexts.PeekText(IslandTexts.PeekStatus(hub.IsClickyCursorEnabled, hub.PointerPresence, hub.IsPointerAwake),
            agentLine, mediaLine: Plugins.Media.MediaIslandView.Store.NowPlayingLine);
    }

    public void Tick() { }
}

// MARK: - Badge

/// <summary>The voice-state indicator (NotchBuddyBadge): level bars while listening, a spinner while thinking, the
/// pointer otherwise.</summary>
internal sealed class BuddyBadgeView : Grid
{
    private readonly IslandCompanionModel hub;
    private readonly WaveformView waveform = new();
    private readonly SpinnerView spinner = new();
    private readonly PointerTriangleView pointerMark = new(12, DS.HUD.Pointer, 4);
    private readonly Grid markHolder = new() { Width = 14, Height = 14 };

    public BuddyBadgeView(IslandCompanionModel hub)
    {
        this.hub = hub;
        markHolder.Children.Add(pointerMark);
        markHolder.HorizontalAlignment = HorizontalAlignment.Center;
        markHolder.VerticalAlignment = VerticalAlignment.Center;
        waveform.HorizontalAlignment = HorizontalAlignment.Center;
        waveform.VerticalAlignment = VerticalAlignment.Center;
        spinner.HorizontalAlignment = HorizontalAlignment.Center;
        spinner.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(waveform);
        Children.Add(spinner);
        Children.Add(markHolder);
        Refresh();
    }

    public void Refresh()
    {
        waveform.Visibility = hub.VoiceState == CompanionVoiceState.Listening ? Visibility.Visible : Visibility.Collapsed;
        spinner.Visibility = hub.VoiceState == CompanionVoiceState.Processing ? Visibility.Visible : Visibility.Collapsed;
        var showsMark = hub.VoiceState is CompanionVoiceState.Idle or CompanionVoiceState.Responding;
        markHolder.Visibility = showsMark ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Tick()
    {
        if (waveform.Visibility == Visibility.Visible)
        {
            waveform.AudioPowerLevel = hub.CurrentAudioPowerLevel;
            waveform.Tick();
        }
        if (spinner.Visibility == Visibility.Visible) spinner.Tick();
    }
}

// MARK: - Compact

/// <summary>The busy strip (380 × 37): the indicator on the left, what is happening on the right, a stop sign while
/// speaking, and one caption line under it when there is something to read.</summary>
internal sealed class CompactStatusView : Grid, IIslandContent
{
    private readonly IslandCompanionModel hub;
    private readonly BuddyBadgeView badge;
    private readonly TextBlock statusText;
    private readonly Grid stopIcon;
    private readonly TextBlock detailText;
    private readonly Border stopOverlay;

    public CompactStatusView(IslandCompanionModel hub)
    {
        this.hub = hub;
        Margin = new Thickness(14, 0, 14, 0);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(DS.HUD.StripHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topRow = new DockPanel { LastChildFill = true };
        badge = new BuddyBadgeView(hub) { Width = 20, Height = 18, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(badge, Dock.Left);
        topRow.Children.Add(badge);
        stopIcon = new Grid { Width = 15, Height = 15, Margin = new Thickness(12, 0, 0, 0) };
        stopIcon.Children.Add(new Ellipse { Fill = DS.HUD.TextBrush });
        stopIcon.Children.Add(new Rectangle { Width = 5.5, Height = 5.5, RadiusX = 1, RadiusY = 1, Fill = DS.HUD.IslandBrush });
        DockPanel.SetDock(stopIcon, Dock.Right);
        topRow.Children.Add(stopIcon);
        statusText = Text("", 12, DS.HUD.Text2Brush, trimming: TextTrimming.CharacterEllipsis);
        statusText.HorizontalAlignment = HorizontalAlignment.Right;
        topRow.Children.Add(statusText);
        SetRow(topRow, 0);
        Children.Add(topRow);

        detailText = Text("", 14, DS.HUD.TextBrush, trimming: TextTrimming.CharacterEllipsis);
        detailText.HorizontalAlignment = HorizontalAlignment.Left;
        SetRow(detailText, 1);
        Children.Add(detailText);

        // Only while speaking, so a click on the busy strip otherwise still opens the panel.
        stopOverlay = Clickable(new Grid(), () => hub.StopSpeakingCommand.Execute(null), IslandTexts.StopSpeakingToolTip);
        SetRowSpan(stopOverlay, 3);
        stopOverlay.Margin = new Thickness(-14, 0, -14, 0);
        Children.Add(stopOverlay);
        Refresh();
    }

    public void Refresh()
    {
        badge.Refresh();
        statusText.Text = hub.CompactStatusText;
        stopIcon.Visibility = hub.IsSpeaking ? Visibility.Visible : Visibility.Collapsed;
        stopOverlay.Visibility = hub.IsSpeaking ? Visibility.Visible : Visibility.Collapsed;
        var detailLine = hub.DetailLine;
        detailText.Text = detailLine ?? "";
        detailText.Visibility = detailLine is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Tick() => badge.Tick();
}

/// <summary>The caption strip (the goodbye): the badge with the text typing out.</summary>
internal sealed class CaptionStripView : DockPanel, IIslandContent
{
    private readonly IslandCompanionModel hub;
    private readonly IslandModel model;
    private readonly BuddyBadgeView badge;
    private readonly TextBlock captionText;

    public CaptionStripView(IslandCompanionModel hub, IslandModel model)
    {
        this.hub = hub;
        this.model = model;
        Margin = new Thickness(14, 10, 14, 10);
        LastChildFill = true;
        badge = new BuddyBadgeView(hub) { Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        SetDock(badge, Dock.Left);
        Children.Add(badge);
        captionText = Text("", 13, DS.HUD.TextBrush, wrapping: TextWrapping.Wrap, trimming: TextTrimming.CharacterEllipsis);
        captionText.VerticalAlignment = VerticalAlignment.Top;
        captionText.MaxHeight = 13 * 1.34 * 3;
        Children.Add(captionText);
        Refresh();
    }

    public void Refresh()
    {
        badge.Refresh();
        captionText.Text = model.CaptionText ?? "";
    }

    public void Tick() => badge.Tick();
}

// MARK: - Composer

/// <summary>The text composer (Ctrl tapped twice): one line, ↵ sends, esc closes. The request goes wherever a spoken one
/// would.</summary>
internal sealed class ComposerView : DockPanel, IIslandContent
{
    private readonly IslandCompanionModel hub;
    private readonly TextBox field;
    private readonly TextBlock placeholder;
    private readonly TextBlock hint;

    public ComposerView(IslandCompanionModel hub)
    {
        this.hub = hub;
        Margin = new Thickness(18, 0, 18, 0);
        LastChildFill = true;
        // SF Symbols' text.cursor: an I-beam (Segoe Fluent Icons has none).
        var cursorGlyph = new Path
        {
            Data = Geometry.Parse("M 1,0.8 Q 4.5,0.8 4.5,3.2 Q 4.5,0.8 8,0.8 M 4.5,3.2 L 4.5,9.8 M 1,12.2 Q 4.5,12.2 4.5,9.8 Q 4.5,12.2 8,12.2"),
            Stroke = DS.HUD.PointerBrush,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 9,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0),
        };
        SetDock(cursorGlyph, Dock.Left);
        Children.Add(cursorGlyph);

        hint = Text(IslandTexts.ComposerEscapeHint, 9.5, DS.Brush(Colors.White, 0.5), FontWeights.Medium, DS.Fonts.Mono);
        var hintBadge = new Border
        {
            Background = DS.Brush(Colors.White, 0.12),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 2, 5, 2),
            Child = hint,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        SetDock(hintBadge, Dock.Right);
        Children.Add(hintBadge);

        var fieldHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        placeholder = Text(IslandTexts.ComposerPlaceholder, 13, DS.Brush(Colors.White, 0.45));
        placeholder.Margin = new Thickness(2, 0, 0, 0);
        field = MakeField(13);
        field.TextChanged += (_, _) => Refresh();
        field.PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter)
            {
                eventArgs.Handled = true;
                var text = field.Text;
                hub.SubmitComposerCommand.Execute(text);
            }
            else if (eventArgs.Key == Key.Escape)
            {
                eventArgs.Handled = true;
                hub.CloseTextComposer();
            }
        };
        fieldHost.Children.Add(placeholder);
        fieldHost.Children.Add(field);
        Children.Add(fieldHost);
        Refresh();
    }

    /// <summary>A borderless one-line field in the island's white type.</summary>
    public static TextBox MakeField(double fontSize) => new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Brushes.White,
        CaretBrush = Brushes.White,
        SelectionBrush = DS.HUD.PointerBrush,
        FontFamily = DS.Fonts.Ui,
        FontSize = fontSize,
        VerticalAlignment = VerticalAlignment.Center,
        Padding = new Thickness(0),
        FocusVisualStyle = null,
    };

    public void FocusField()
    {
        field.Text = "";
        field.Focus();
        Keyboard.Focus(field);
    }

    public void Refresh()
    {
        placeholder.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        hint.Text = field.Text.Length == 0 ? IslandTexts.ComposerEscapeHint : IslandTexts.ComposerSendHint;
    }

    public void Tick() { }
}

// MARK: - Cards

/// <summary>The "!" disc behind a permission card's mark, or a tour symbol (56) / app icon tile (40).</summary>
internal static class CardArt
{
    public static Grid Disc(double size, UIElement content)
    {
        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse { Fill = DS.HUD.PointerTintBrush });
        grid.Children.Add(content);
        return grid;
    }
}

/// <summary>The permission card: the orange "!" disc, the headline and one line of copy with the progress dashes under
/// them, grant / later on the right, and the gear top-right (PermissionPromptView).</summary>
internal sealed class PermissionCardView : Grid, IIslandContent
{
    public PermissionCardView(IslandCompanionModel hub, PermissionPrompt prompt, double topInset)
    {
        Margin = new Thickness(DS.HUD.BodySidePadding, topInset + 12, DS.HUD.BodySidePadding, 16);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        VerticalAlignment = VerticalAlignment.Top;

        var bang = Text("!", 15, DS.HUD.PointerBrush, FontWeights.Bold);
        bang.HorizontalAlignment = HorizontalAlignment.Center;
        var disc = CardArt.Disc(DS.HUD.TileSize, bang);
        disc.Margin = new Thickness(0, 0, 14, 0);
        SetColumn(disc, 0);
        Children.Add(disc);

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Text(prompt.Headline, 15, DS.HUD.TextBrush, FontWeights.SemiBold, DS.Fonts.Display, TextTrimming.CharacterEllipsis));
        var detail = Text(prompt.Detail, 12, DS.HUD.Text2Brush, wrapping: TextWrapping.Wrap);
        detail.Margin = new Thickness(0, 2, 0, 0);
        detail.LineHeight = 16;
        copy.Children.Add(detail);
        var dashes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var step in PermissionSteps.Relevant(prompt.Status))
        {
            var fill = prompt.Status.IsGranted(step) ? DS.HUD.LiveBrush : step == prompt.Step ? DS.HUD.PointerBrush : DS.HUD.SurfaceRaisedBrush;
            dashes.Children.Add(Dash(16, fill));
        }
        System.Windows.Automation.AutomationProperties.SetName(dashes, prompt.ProgressLabel);
        copy.Children.Add(dashes);
        SetColumn(copy, 1);
        Children.Add(copy);

        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        buttons.Children.Add(PrimaryButton(prompt.ButtonTitle, () => hub.GrantPermissionCommand.Execute(null)));
        var later = QuietButton("later", () => hub.PermissionLaterCommand.Execute(null), IslandTexts.PermissionLaterToolTip);
        later.Margin = new Thickness(0, 6, 0, 0);
        buttons.Children.Add(later);
        SetColumn(buttons, 2);
        Children.Add(buttons);

        // Settings, while nothing else on the island works.
        var gear = Clickable(Glyph(Icons.Settings, 10, DS.HUD.Text3Brush), () => hub.OpenPermissionSettingsCommand.Execute(null), IslandTexts.SettingsToolTip);
        gear.Width = DS.HUD.MinimumHitSize;
        gear.Height = DS.HUD.MinimumHitSize;
        gear.HorizontalAlignment = HorizontalAlignment.Right;
        gear.VerticalAlignment = VerticalAlignment.Top;
        gear.Margin = new Thickness(0, -16, -12, 0);
        SetColumn(gear, 2);
        Children.Add(gear);
    }

    public void Refresh() { }
    public void Tick() { }
}

/// <summary>The connect card: the 40 app icon with the question and one line of what it means, the example prompts as
/// whole chips, then "never for &lt;app&gt;" on the left and not now / connect on the right (AppConnectPromptView).</summary>
internal sealed class ConnectCardView : StackPanel, IIslandContent
{
    public ConnectCardView(IslandCompanionModel hub, AppConnectPrompt prompt, double topInset, double cardWidth)
    {
        Margin = new Thickness(DS.HUD.BodySidePadding, topInset + 12, DS.HUD.BodySidePadding, 16);
        VerticalAlignment = VerticalAlignment.Top;

        var header = new DockPanel { LastChildFill = true };
        var icon = AppIcon(prompt.FrontProcessPath);
        icon.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Text(prompt.Headline, 15, DS.HUD.TextBrush, FontWeights.SemiBold, DS.Fonts.Display, TextTrimming.CharacterEllipsis));
        var detail = Text(prompt.Detail, 12, DS.HUD.Text2Brush, trimming: TextTrimming.CharacterEllipsis);
        detail.Margin = new Thickness(0, 2, 0, 0);
        copy.Children.Add(detail);
        header.Children.Add(copy);
        Children.Add(header);

        if (prompt.ExamplePrompts.Count > 0)
        {
            // As many whole chips as fit on one line (WholeItemsRowLayout): a chip is never cut in half.
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Height = 26, Margin = new Thickness(0, 12, 0, 0), ClipToBounds = true };
            var available = cardWidth - DS.HUD.BodySidePadding * 2;
            var used = 0.0;
            foreach (var example in prompt.ExamplePrompts.Take(4))
            {
                var chip = new Border
                {
                    Background = DS.HUD.BandControlBrush,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(9, 5, 9, 5),
                    Child = Text($"“{example}”", 12, DS.HUD.ChipTextBrush),
                    Margin = new Thickness(0, 0, 6, 0),
                };
                chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (used + chip.DesiredSize.Width - 6 > available + 0.5) break;
                used += chip.DesiredSize.Width;
                chips.Children.Add(chip);
            }
            Children.Add(chips);
        }

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        if (prompt.Stage == AppConnectPromptStage.Ask)
        {
            var never = Clickable(Text(prompt.NeverTitle, 13, DS.HUD.Text2Brush), () => hub.AnswerConnectCommand.Execute(AppConnectAnswer.No), prompt.NeverToolTip);
            never.Height = 30;
            never.Padding = new Thickness(10, 0, 10, 0);
            DockPanel.SetDock(never, Dock.Left);
            buttons.Children.Add(never);
            var connect = AnswerButton("connect", isPrimary: true, () => hub.AnswerConnectCommand.Execute(AppConnectAnswer.Yes));
            DockPanel.SetDock(connect, Dock.Right);
            buttons.Children.Add(connect);
            var notNow = AnswerButton("not now", isPrimary: false, () => hub.AnswerConnectCommand.Execute(AppConnectAnswer.NotNow));
            notNow.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(notNow, Dock.Right);
            buttons.Children.Add(notNow);
        }
        else
        {
            var openSettings = AnswerButton("open shell.json", isPrimary: true, () => hub.AnswerConnectCommand.Execute(AppConnectAnswer.OpenSettings));
            DockPanel.SetDock(openSettings, Dock.Right);
            buttons.Children.Add(openSettings);
            var ok = AnswerButton("ok", isPrimary: false, () => hub.AnswerConnectCommand.Execute(AppConnectAnswer.DismissNotice));
            ok.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(ok, Dock.Right);
            buttons.Children.Add(ok);
        }
        Children.Add(buttons);
    }

    private static Border AnswerButton(string title, bool isPrimary, Action onClick)
    {
        var label = Text(title, 13, isPrimary ? Brushes.Black : DS.HUD.TextBrush, isPrimary ? FontWeights.SemiBold : FontWeights.Normal);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var button = Clickable(label, onClick, background: isPrimary ? DS.HUD.TextBrush : DS.HUD.SurfaceRaisedBrush, cornerRadius: new CornerRadius(9));
        button.Height = 30;
        button.Padding = new Thickness(14, 0, 14, 0);
        return button;
    }

    /// <summary>The front app's icon, else a globe on a raised tile.</summary>
    private static FrameworkElement AppIcon(string? processPath)
    {
        var size = DS.HUD.TileSize;
        if (processPath is not null && System.IO.File.Exists(processPath))
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    return new Image { Source = source, Width = size, Height = size };
                }
            }
            catch (Exception exception) when (exception is ArgumentException or System.ComponentModel.Win32Exception)
            {
            }
        }
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(DS.HUD.TileRadius),
            Background = DS.HUD.SurfaceRaisedSoftBrush,
            Child = Glyph(Icons.Globe, 18, DS.HUD.Text2Brush),
        };
    }

    public void Refresh() { }
    public void Tick() { }
}

/// <summary>The step's keys as keycaps, pressed down together and held, then let go, over and over (TourKeycaps).</summary>
internal sealed class TourKeycapsView : StackPanel
{
    private readonly List<(Border Face, Border Edge, TextBlock Label, TranslateTransform Offset)> keycaps = new();

    public TourKeycapsView(IReadOnlyList<string> keys)
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        foreach (var key in keys)
        {
            var label = Text(key, key.Length > 3 ? 13 : 15, DS.HUD.TextBrush, FontWeights.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var width = keys.Count > 1 ? 36 : 48;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Max(width, (int)Math.Ceiling(label.DesiredSize.Width) + 14);
            var face = new Border
            {
                Width = width,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = DS.HUD.SurfaceRaisedBrush,
                BorderBrush = DS.Brush(Colors.White, 0.18),
                BorderThickness = new Thickness(1),
                Child = label,
            };
            // The hard shadow under the key that the press closes (shadow radius 0, y (1 − press) · 3).
            var edge = new Border { Width = width, Height = 36, CornerRadius = new CornerRadius(8), Background = DS.Brush(Colors.Black, 0.5) };
            var offset = new TranslateTransform();
            var holder = new Grid { Width = width, Height = 42, Margin = new Thickness(0, 0, 5, 0), RenderTransform = offset };
            edge.VerticalAlignment = VerticalAlignment.Top;
            face.VerticalAlignment = VerticalAlignment.Top;
            holder.Children.Add(edge);
            holder.Children.Add(face);
            Children.Add(holder);
            keycaps.Add((face, edge, label, offset));
        }
    }

    public void Tick()
    {
        var press = FirstRunTourCards.PressAmount(MotionClock.Now);
        foreach (var (face, edge, label, offset) in keycaps)
        {
            var isDown = press > 0.5;
            face.Background = isDown ? DS.HUD.PointerBrush : DS.HUD.SurfaceRaisedBrush;
            label.Foreground = isDown ? Brushes.Black : DS.HUD.TextBrush;
            edge.Margin = new Thickness(0, (1 - press) * 3, 0, 0);
            offset.Y = press * 3;
        }
    }
}

/// <summary>A tour card: the illustration (animated keycaps or a symbol on the tinted disc), the title and copy, the
/// field / status / microphone lines, the progress dashes, and the buttons (FirstRunTourCardView).</summary>
internal sealed class TourCardView : Grid, IIslandContent
{
    private readonly IslandCompanionModel hub;
    private readonly TourKeycapsView? keycaps;
    private readonly TextBox? field;

    public TourCardView(IslandCompanionModel hub, FirstRunTourCard card, double topInset)
    {
        this.hub = hub;
        Margin = new Thickness(DS.HUD.BodySidePadding, topInset + 12, DS.HUD.BodySidePadding, 14);
        // The Mac's card is sized for three lines of copy in SF; Segoe wraps the same words in fewer lines, so the
        // content is centred in the card instead of leaving the room empty at the bottom.
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement illustration;
        if (card.Keys.Count > 0)
        {
            keycaps = new TourKeycapsView(card.Keys);
            illustration = keycaps;
        }
        else
        {
            illustration = CardArt.Disc(56, Glyph(Icons.ForTourSymbol(card.Symbol), 24, DS.HUD.PointerBrush));
            illustration.HorizontalAlignment = HorizontalAlignment.Center;
        }
        var illustrationHost = new Grid { Width = 84, Height = 56, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        illustrationHost.Children.Add(illustration);
        SetColumn(illustrationHost, 0);
        Children.Add(illustrationHost);
        ColumnDefinitions[0].Width = new GridLength(84 + 14);

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Text(card.Title, 15, DS.HUD.TextBrush, FontWeights.SemiBold, DS.Fonts.Display, TextTrimming.CharacterEllipsis));
        var detail = Text(card.Detail, 12, DS.HUD.Text2Brush, wrapping: TextWrapping.Wrap, trimming: TextTrimming.CharacterEllipsis);
        detail.Margin = new Thickness(0, 3, 0, 0);
        detail.LineHeight = 16;
        detail.MaxHeight = 16 * 3 + 1;
        copy.Children.Add(detail);
        if (card.Field is { } fieldKind)
        {
            var fieldHost = new Grid();
            var placeholder = Text(fieldKind == FirstRunTourFieldKind.Email ? "you@example.com" : "6-digit code", 13, DS.Brush(Colors.White, 0.45));
            field = ComposerView.MakeField(13);
            field.Text = hub.TourFieldText;
            placeholder.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            field.TextChanged += (_, _) =>
            {
                hub.TourFieldText = field.Text;
                placeholder.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            };
            field.PreviewKeyDown += (_, eventArgs) =>
            {
                if (eventArgs.Key != Key.Enter) return;
                eventArgs.Handled = true;
                hub.SubmitTourFieldCommand.Execute(null);
            };
            fieldHost.Children.Add(placeholder);
            fieldHost.Children.Add(field);
            copy.Children.Add(new Border
            {
                Background = DS.HUD.SurfaceRaisedBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 4, 10, 4),
                Height = 30,
                Margin = new Thickness(0, 4, 0, 0),
                Child = fieldHost,
            });
        }
        if (card.Status is { } status)
        {
            var statusLine = Text(status, 12, card.StatusIsGood ? DS.HUD.GoodBrush : DS.HUD.PointerBrush, FontWeights.Medium, wrapping: TextWrapping.Wrap);
            statusLine.Margin = new Thickness(0, 2, 0, 0);
            statusLine.MaxHeight = 34;
            copy.Children.Add(statusLine);
        }
        if (card.MicrophoneName is { } microphoneName)
        {
            var weight = card.MicrophoneNeedsAttention ? FontWeights.SemiBold : FontWeights.Normal;
            var brush = card.MicrophoneNeedsAttention ? DS.HUD.TextBrush : DS.HUD.Text3Brush;
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var micGlyph = Glyph(Icons.Microphone, 10, brush);
            micGlyph.Margin = new Thickness(0, 0, 4, 0);
            line.Children.Add(micGlyph);
            var name = Text($"listening through {microphoneName} · ", 12, brush, weight, trimming: TextTrimming.CharacterEllipsis);
            name.MaxWidth = 230;
            line.Children.Add(name);
            var change = Text("change", 12, brush, weight);
            change.TextDecorations = TextDecorations.Underline;
            line.Children.Add(change);
            var button = Clickable(line, () => hub.TourChangeMicrophoneCommand.Execute(null));
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 2, 0, 0);
            copy.Children.Add(button);
        }
        if (card.PracticeIndex is { } practiceIndex)
        {
            var dashes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            for (var index = 0; index < 3; index++) dashes.Children.Add(Dash(18, index <= practiceIndex ? DS.HUD.PointerBrush : DS.HUD.SurfaceRaisedBrush));
            copy.Children.Add(dashes);
        }
        SetColumn(copy, 1);
        Children.Add(copy);

        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        if (card.PrimaryTitle is { } primaryTitle)
        {
            var primary = PrimaryButton(primaryTitle, () => hub.TourPrimaryCommand.Execute(null));
            primary.Margin = new Thickness(0, 0, 0, 6);
            buttons.Children.Add(primary);
        }
        buttons.Children.Add(QuietButton(card.SecondaryTitle, () => hub.TourSecondaryCommand.Execute(null)));
        SetColumn(buttons, 2);
        Children.Add(buttons);
    }

    public void FocusField()
    {
        if (field is null) return;
        field.Focus();
        Keyboard.Focus(field);
    }

    public void Refresh() { }

    public void Tick() => keycaps?.Tick();
}

// MARK: - Full panel

/// <summary>The open island, one short panel: the band (the pointer's status; the voice pill, Settings, quit) over one
/// row ((i), the four shortcuts or what the dictation engine is missing). A side drawer stacks them.</summary>
internal sealed class FullPanelView : Grid, IIslandContent
{
    private readonly IslandCompanionModel hub;
    private readonly IslandModel model;
    private readonly PointerTriangleView statusMark = new(12, DS.HUD.Pointer, 4);
    private readonly TextBlock statusText;
    private readonly Grid voicePillHost = new();

    private readonly ContentControl shortcutsOrWarning = new() { Focusable = false };

    public FullPanelView(IslandCompanionModel hub, IslandModel model)
    {
        this.hub = hub;
        this.model = model;
        var layout = model.Layout;
        var isSideDrawer = model.Placement.IsSideDrawer();
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.BandRowHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // The band.
        var band = new DockPanel { LastChildFill = true, Margin = new Thickness(16, 6, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(voicePillHost);
        var gear = BandIconButton(Icons.Settings, IslandTexts.SettingsToolTip, () =>
        {
            model.Dismiss();
            hub.OpenSettingsCommand.Execute(IslandSettingsDestination.General);
        });
        gear.Margin = new Thickness(6, 0, 0, 0);
        controls.Children.Add(gear);
        var power = BandIconButton(Icons.Power, IslandTexts.QuitToolTip, () => hub.QuitCommand.Execute(null));
        power.Margin = new Thickness(6, 0, 0, 0);
        controls.Children.Add(power);
        DockPanel.SetDock(controls, Dock.Right);
        band.Children.Add(controls);
        var markHolder = new Grid { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0) };
        markHolder.Children.Add(statusMark);
        statusText = Text("", 12, DS.HUD.Text2Brush, trimming: TextTrimming.CharacterEllipsis);
        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        status.Children.Add(markHolder);
        status.Children.Add(statusText);
        band.Children.Add(status);
        SetRow(band, 0);
        Children.Add(band);

        // The body.

        var info = Text("i", 17, DS.HUD.TextBrush, family: DS.Fonts.Serif);
        info.FontStyle = FontStyles.Italic;
        info.HorizontalAlignment = HorizontalAlignment.Center;
        var infoButton = Clickable(info, () =>
        {
            model.Close();
            hub.ExplainCommand.Execute(null);
        }, IslandTexts.InfoToolTip, DS.HUD.SurfaceRaisedSoftBrush, new CornerRadius(DS.HUD.TileRadius));
        infoButton.Width = 30;
        infoButton.Height = 30;

        FrameworkElement body;
        if (isSideDrawer)
        {
            var stack = new StackPanel();
            infoButton.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(infoButton);
            shortcutsOrWarning.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(shortcutsOrWarning);
            body = stack;
        }
        else
        {
            var row = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Top };

            infoButton.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(infoButton, Dock.Left);
            row.Children.Add(infoButton);
            row.Children.Add(shortcutsOrWarning);
            body = row;
        }
        body.Margin = new Thickness(DS.HUD.BodySidePadding, isSideDrawer ? 6 : 2, DS.HUD.BodySidePadding, 0);
        // The slot: the shortcuts row above, or the component the island picked (a plugin) in its place.
        if (IslandComponentViews.Make(model.ActiveComponent, hub, model) is { } componentView)
        {
            slotContent = componentView as IIslandContent;
            body = componentView;
            body.Margin = new Thickness(DS.HUD.BodySidePadding, 0, DS.HUD.BodySidePadding, 6);
        }
        SetRow(body, 1);
        Children.Add(body);
        // More than one component to show: the capsule takes the status line's place in the band.
        if (model.ShowsCapsule)
        {
            status.Children.Remove(statusText);
            status.Children.Add(IslandComponentViews.Capsule(model));
        }
        Refresh();
    }

    /// <summary>The component in the slot when it is not the shortcuts row.</summary>
    private readonly IIslandContent? slotContent;

    public void Refresh()
    {
        statusText.Text = hub.PointerStatusText;
        voicePillHost.Children.Clear();
        voicePillHost.Children.Add(VoicePill());
        shortcutsOrWarning.Content = hub.EngineWarning is { } warning ? EngineWarningView(warning) : ShortcutGrid();
        slotContent?.Refresh();
    }

    /// <summary>Where dictated words go: a green microphone while the engine can run, an orange "!" when it can't.</summary>
    private Border VoicePill()
    {
        var isBlocked = hub.EngineBlockedPillLabel is not null;
        var glyph = isBlocked ? Text("!", 11, Brushes.Black, FontWeights.Bold) : Glyph(Icons.Microphone, 10, DS.HUD.LiveBrush);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var pill = Clickable(glyph, () => hub.OpenEngineSetupCommand.Execute(null), hub.VoicePillToolTip,
            isBlocked ? DS.HUD.PointerBrush : DS.HUD.BandControlBrush, new CornerRadius(DS.HUD.PillHeight / 2));
        pill.MinWidth = DS.HUD.PillHeight;
        pill.Height = DS.HUD.PillHeight;
        System.Windows.Automation.AutomationProperties.SetName(pill, hub.EngineBlockedPillLabel ?? $"dictation is heard by {hub.DictationEngineName}");
        return pill;
    }

    /// <summary>The four shortcuts as a 2 × 2 grid with no heading (NotchShortcutGrid); auto-sized columns 12 apart.</summary>
    private Grid ShortcutGrid()
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        // The left column takes what talk / dictate need, the right one the rest: "R Ctrl" + "Ctrl" make hands-free the
        // widest cell, where the Mac's single-glyph keys left two equal columns room enough.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        var shortcuts = hub.ShortcutGrid;
        for (var index = 0; index < shortcuts.Count; index++)
        {
            var cell = ShortcutCell(shortcuts[index]);
            SetRow(cell, index / 2 * 2);
            SetColumn(cell, index % 2 * 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static DockPanel ShortcutCell(IslandShortcut shortcut)
    {
        var cell = new DockPanel { LastChildFill = false, Height = 14 };
        var title = Text(shortcut.Title, 12, DS.HUD.TextBrush);
        DockPanel.SetDock(title, Dock.Left);
        cell.Children.Add(title);
        if (shortcut.IsLive)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = DS.HUD.LiveBrush, Margin = new Thickness(4, 0, 0, 0), ToolTip = "hands-free is on" };
            DockPanel.SetDock(dot, Dock.Left);
            cell.Children.Add(dot);
        }
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var key in shortcut.Keys)
        {
            var keycap = Keycap(key, isSmall: true);
            keycap.Margin = new Thickness(0, 0, 3, 0);
            keys.Children.Add(keycap);
        }
        keys.Children.Add(Text(shortcut.Gesture, 10, DS.HUD.Text3Brush, family: DS.Fonts.Mono));
        DockPanel.SetDock(keys, Dock.Right);
        cell.Children.Add(keys);
        System.Windows.Automation.AutomationProperties.SetName(cell, $"{shortcut.Title}: {string.Join(" + ", shortcut.Keys)} {shortcut.Gesture}");
        return cell;
    }

    /// <summary>Fits the body's one row: the message in two lines at most, the way to fix it beside it.</summary>
    private FrameworkElement EngineWarningView(IslandEngineWarning warning)
    {
        var row = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        var label = Text(warning.ButtonTitle, 12, DS.HUD.TextBrush, FontWeights.Medium);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var button = Clickable(label, () => hub.OpenEngineSetupCommand.Execute(null), background: DS.HUD.SurfaceRaisedBrush, cornerRadius: new CornerRadius(8));
        button.Height = 26;
        button.Padding = new Thickness(10, 0, 10, 0);
        button.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        var message = Text(warning.Message, 11, DS.HUD.Text2Brush, wrapping: TextWrapping.Wrap, trimming: TextTrimming.CharacterEllipsis);
        message.MaxHeight = 30;
        message.ToolTip = warning.Message;
        row.Children.Add(message);
        System.Windows.Automation.AutomationProperties.SetName(row, warning.Heading);
        return row;
    }

    public void Tick() => slotContent?.Tick();
}
