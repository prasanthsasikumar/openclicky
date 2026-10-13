//
//  AgentResultCardWindow.cs
//  OpenClicky (Windows port of NotchHUDPanels.swift's AgentResultPanelManager and AgentResultView)
//
//  The floating card HeyClicky shows top-right when an agent finishes or one is opened: title,
//  status, the agent's summary, Copy, and a "Follow up with agent" field. It floats at the top
//  right of the work area of the monitor under the mouse; the field takes the keyboard when
//  clicked (the card is otherwise non-activating, like the Mac's nonactivating panel).
//

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Theme;
using static OpenClicky.Windows.Island.IslandUi;

namespace OpenClicky.Windows.Island;

public sealed class AgentResultCardWindow : Window
{
    private const double CardWidth = 400;
    /// <summary>Room around the card for its shadow.</summary>
    private const double ShadowMargin = 24;

    private readonly AgentResultCardModel model;
    private readonly IScreens screens;
    private readonly TextBlock titleText;
    private readonly Border statusBadge;
    private readonly TextBlock statusText;
    private readonly StackPanel loadingRow;
    private readonly ScrollViewer summaryScroller;
    private readonly TextBox summaryText;
    private readonly Border copyButton;
    private readonly TextBlock copyGlyph;
    private readonly TextBlock copyLabel;
    private readonly TextBox followUpField;
    private readonly TextBlock followUpPlaceholder;
    private readonly StackPanel activityRow;
    private readonly TextBlock activityText;
    private readonly SpinnerView activitySpinner = new(DS.Brush(Colors.White, 0.6).Color);
    private readonly SpinnerView loadingSpinner = new(Colors.White);
    private nint windowBeforeKeyboard;
    private ScreenInfo? shownScreen;

    public AgentResultCardWindow(AgentResultCardModel model, IScreens screens)
    {
        this.model = model;
        this.screens = screens;
        Title = "OpenClicky agent";
        FloatingWindowSupport.Configure(this, clickThrough: false);
        SizeToContent = SizeToContent.Height;
        Width = CardWidth + ShadowMargin * 2;

        var stack = new StackPanel();

        // Title, status, close.
        var header = new DockPanel { LastChildFill = true };
        var closeGlyph = Glyph(Icons.Close, 8, DS.Brush(Colors.White, 0.7));
        var close = Clickable(closeGlyph, model.Close, "close", DS.Brush(Colors.White, 0.12), new CornerRadius(9));
        close.Width = 18;
        close.Height = 18;
        close.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        statusText = Text("", 9, Brushes.White, FontWeights.SemiBold);
        statusBadge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 3, 7, 3), Child = statusText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(statusBadge, Dock.Right);
        header.Children.Add(statusBadge);
        titleText = Text("", 12, Brushes.White, FontWeights.Bold, trimming: TextTrimming.CharacterEllipsis);
        header.Children.Add(titleText);
        stack.Children.Add(header);

        // Loading…
        loadingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        loadingSpinner.Margin = new Thickness(0, 0, 8, 0);
        loadingRow.Children.Add(loadingSpinner);
        loadingRow.Children.Add(Text("Loading…", 11, DS.Brush(Colors.White, 0.6)));
        stack.Children.Add(loadingRow);

        // The summary (selectable, at most 90 tall) and Copy.
        summaryText = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            FontFamily = DS.Fonts.Ui,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0),
            SelectionBrush = DS.Colors.OverlayCursorBrush,
            FocusVisualStyle = null,
        };
        summaryScroller = new ScrollViewer
        {
            Content = summaryText,
            MaxHeight = 90,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Margin = new Thickness(0, 10, 0, 0),
        };
        stack.Children.Add(summaryScroller);
        copyGlyph = Glyph(Icons.Copy, 10, Brushes.White);
        copyGlyph.Margin = new Thickness(0, 0, 5, 0);
        copyLabel = Text("Copy", 11, Brushes.White, FontWeights.SemiBold);
        copyButton = Clickable(Row(0, copyGlyph, copyLabel), CopySummary, background: DS.Brush(Colors.White, 0.14), cornerRadius: new CornerRadius(12));
        copyButton.Padding = new Thickness(10, 5, 10, 5);
        copyButton.HorizontalAlignment = HorizontalAlignment.Left;
        copyButton.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(copyButton);

        // Follow up with agent.
        var followUp = new DockPanel { LastChildFill = true };
        var micGlyph = Glyph(Icons.Microphone, 11, DS.Brush(Colors.White, 0.7));
        micGlyph.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(micGlyph, Dock.Left);
        followUp.Children.Add(micGlyph);
        var keyboardGlyph = Glyph(Icons.Keyboard, 11, DS.Brush(Colors.White, 0.35));
        keyboardGlyph.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(keyboardGlyph, Dock.Right);
        followUp.Children.Add(keyboardGlyph);
        var fieldHost = new Grid();
        followUpPlaceholder = Text("Follow up with agent", 12, DS.Brush(Colors.White, 0.45), FontWeights.Medium);
        followUpField = ComposerView.MakeField(12);
        followUpField.FontWeight = FontWeights.Medium;
        followUpField.TextChanged += (_, _) => followUpPlaceholder.Visibility = followUpField.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        followUpField.PreviewMouseLeftButtonDown += (_, _) => TakeKeyboard();
        followUpField.PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter)
            {
                eventArgs.Handled = true;
                var text = followUpField.Text;
                followUpField.Text = "";
                model.SendFollowUp(text);
                GiveKeyboardBack();
            }
            else if (eventArgs.Key == Key.Escape)
            {
                eventArgs.Handled = true;
                GiveKeyboardBack();
            }
        };
        fieldHost.Children.Add(followUpPlaceholder);
        fieldHost.Children.Add(followUpField);
        followUp.Children.Add(fieldHost);
        stack.Children.Add(new Border
        {
            Background = DS.Brush(Colors.White, 0.10),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 10, 0, 0),
            Child = followUp,
        });

        // The running turn's latest milestone.
        activityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        activitySpinner.Margin = new Thickness(0, 0, 6, 0);
        activitySpinner.LayoutTransform = new ScaleTransform(0.8, 0.8);
        activityRow.Children.Add(activitySpinner);
        activityText = Text("", 10, DS.Brush(Colors.White, 0.6), trimming: TextTrimming.CharacterEllipsis);
        activityText.MaxWidth = 340;
        activityRow.Children.Add(activityText);
        stack.Children.Add(activityRow);

        Content = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(ShadowMargin),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(16),
            Background = DS.Brush(DS.Hex("#2B2D31"), 0.96),
            BorderBrush = DS.Brush(Colors.White, 0.12),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.35 },
            Child = stack,
        };

        model.PropertyChanged += OnModelPropertyChanged;
        CompositionTarget.Rendering += OnRendering;
        SizeChanged += (_, _) => PlaceTopRight();
        Closed += (_, _) =>
        {
            model.PropertyChanged -= OnModelPropertyChanged;
            CompositionTarget.Rendering -= OnRendering;
        };
        Refresh();
    }

    private void OnRendering(object? sender, EventArgs eventArgs)
    {
        if (!IsVisible) return;
        if (loadingRow.Visibility == Visibility.Visible) loadingSpinner.Tick();
        if (activityRow.Visibility == Visibility.Visible) activitySpinner.Tick();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(AgentResultCardModel.IsVisible))
        {
            if (model.IsVisible) ShowCard();
            else
            {
                GiveKeyboardBack();
                Hide();
            }
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        titleText.Text = model.Title;
        statusText.Text = model.StatusLabel;
        var statusColor = model.StatusKind switch
        {
            AgentResultStatusKind.Working => DS.Colors.Warning,
            AgentResultStatusKind.Error => DS.Colors.OverlayCursor,
            _ => DS.Colors.Accent,
        };
        statusBadge.Background = DS.Brush(statusColor, 0.8);
        statusBadge.Visibility = model.StatusLabel.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        loadingRow.Visibility = model.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        summaryScroller.Visibility = model.IsLoading ? Visibility.Collapsed : Visibility.Visible;
        copyButton.Visibility = model.IsLoading ? Visibility.Collapsed : Visibility.Visible;
        summaryText.Text = model.LiveSummary;
        followUpField.IsEnabled = model.CanFollowUp;
        var showsActivity = model.IsWorking && model.AgentActivityText is not null;
        activityRow.Visibility = showsActivity ? Visibility.Visible : Visibility.Collapsed;
        activityText.Text = model.AgentActivityText ?? "";
    }

    private void ShowCard()
    {
        shownScreen = screens.CursorScreen() ?? screens.Primary();
        if (!IsVisible) Show();
        PlaceTopRight();
        FloatingWindowSupport.BringToTopWithoutActivating(this);
    }

    /// <summary>20 from the right and 12 from the top of the work area, as the Mac places it under the menu bar.</summary>
    private void PlaceTopRight()
    {
        if (shownScreen is not { } screen) return;
        var workArea = screen.WorkAreaInDipsRect();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 260;
        var frame = new ScreenRect(workArea.Right - (width - ShadowMargin) - 20, workArea.Top + 12 - ShadowMargin, width, height);
        FloatingWindowSupport.PlaceInPixels(this, screen.DipsToPixels(frame));
    }

    private async void CopySummary()
    {
        try
        {
            Clipboard.SetText(model.LiveSummary);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }
        copyGlyph.Text = Icons.CheckMark;
        copyLabel.Text = "Copied";
        await Task.Delay(1500);
        copyGlyph.Text = Icons.Copy;
        copyLabel.Text = "Copy";
    }

    private void TakeKeyboard()
    {
        if (windowBeforeKeyboard != 0) return;
        windowBeforeKeyboard = FloatingWindowSupport.TakeKeyboardFocus(this);
        followUpField.Focus();
    }

    private void GiveKeyboardBack()
    {
        if (windowBeforeKeyboard == 0) return;
        Keyboard.ClearFocus();
        FloatingWindowSupport.ReturnKeyboardFocus(this, windowBeforeKeyboard);
        windowBeforeKeyboard = 0;
    }
}
