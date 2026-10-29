//
//  IslandWindow.cs
//  OpenClicky (Windows port of NotchHUD.swift's NotchHUDWindow and NotchHUDView)
//
//  One monitor's island: a transparent, topmost, non-activating window sized to the island's state
//  (the transparent margin never blocks clicks elsewhere: fully transparent pixels of a layered
//  window let clicks through). HeyClicky's look: solid black, the top corners flaring out into the
//  edge it hangs from, rounded bottom corners, no border, no shadow. The black springs between
//  sizes (SwiftUI's spring(0.42, 0.84); the tour's settle 1.3 / 0.92) and the content cross-fades,
//  growing from 0.92 at the top. The composer and the tour's field take the keyboard; everything
//  else leaves it with the app in front.
//

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins;
using OpenClicky.Core.Plugins.Shelf;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Plugins.Shelf;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Island;

public sealed class IslandWindow : Window
{
    private readonly IslandModel model;
    private readonly IslandCompanionModel hub;
    private ScreenInfo screen;

    private readonly Canvas root = new() { ClipToBounds = false };
    /// <summary>Over the island's own rectangle, so a drag reaches it even collapsed (when nothing there is hit-testable):
    /// filled only while the Shelf is on, nearly transparent, since a layered window passes fully transparent pixels through.</summary>
    private readonly Rectangle dropCatcher = new();
    private static readonly Brush DropCatcherFill = MakeDropCatcherFill();
    private readonly Path islandShape = new() { Fill = DS.HUD.IslandBrush };
    private readonly Grid contentHost = new();
    private FrameworkElement? currentContentElement;
    private IIslandContent? currentContent;
    private readonly ScaleTransform contentScale = new(1, 1);
    private readonly Tween contentOpacity = new(1);
    private Spring contentScaleSpring = new(0.42, 0.84, 1);

    private Spring widthSpring = new(0.42, 0.84);
    private Spring heightSpring = new(0.42, 0.84);
    private readonly Tween bottomRadius = new(11);
    private readonly Tween shapeOpacity = new(0);
    private bool isSettleSpring;

    /// <summary>The window frame (this monitor's DIPs) actually applied; it lags the layout while shrinking.</summary>
    private ScreenRect appliedWindowFrame = ScreenRect.Empty;
    private int pendingShrinkGeneration;
    private string contentKey = "";
    private nint windowBeforeKeyboard;
    private bool isClosed;
    private double lastFrameTime;

    public IslandModel Model => model;
    public ScreenInfo Screen => screen;

    public IslandWindow(IslandModel model, IslandCompanionModel hub, ScreenInfo screen)
    {
        this.model = model;
        this.hub = hub;
        this.screen = screen;
        Title = "OpenClicky island";
        FloatingWindowSupport.Configure(this, clickThrough: false);
        Content = root;
        root.Children.Add(dropCatcher);
        root.Children.Add(islandShape);
        AllowDrop = true;
        DragEnter += OnDragOverIsland;
        DragOver += OnDragOverIsland;
        DragLeave += OnDragLeaveIsland;
        Drop += OnDropOnIsland;
        UpdateDropCatcher();
        contentHost.RenderTransform = contentScale;
        root.Children.Add(contentHost);
        islandShape.MouseLeftButtonUp += OnIslandClicked;
        contentHost.MouseLeftButtonUp += OnIslandClicked;

        var layout = model.Layout;
        widthSpring.Snap(layout.Width);
        heightSpring.Snap(layout.Height);
        Width = Math.Max(1, layout.WindowFrame.Width);
        Height = Math.Max(1, layout.WindowFrame.Height);

        SourceInitialized += (_, _) => ApplyWindowFrame(model.Layout.WindowFrame);
        DpiChanged += (_, _) => ApplyWindowFrame(appliedWindowFrame.IsEmpty ? model.Layout.WindowFrame : appliedWindowFrame);
        model.LayoutChanged += OnLayoutChanged;
        model.PropertyChanged += OnModelPropertyChanged;
        hub.PropertyChanged += OnHubPropertyChanged;
        Closed += (_, _) => TearDown();
        CompositionTarget.Rendering += OnRendering;
        lastFrameTime = MotionClock.Now;
        RebuildContentIfNeeded(force: true);
    }

    // MARK: - Model changes

    private void OnLayoutChanged()
    {
        if (isClosed) return;
        RebuildContentIfNeeded();
        ResizeWindow();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (isClosed) return;
        switch (eventArgs.PropertyName)
        {
            case nameof(IslandModel.CaptionText):
            case nameof(IslandModel.IsShowingCaption):
                RebuildContentIfNeeded();
                currentContent?.Refresh();
                break;
            case nameof(IslandModel.ConnectPrompt):
            case nameof(IslandModel.PermissionPrompt):
            case nameof(IslandModel.TourCard):
            case nameof(IslandModel.Expansion):
                RebuildContentIfNeeded();
                break;
            case nameof(IslandModel.Selection):
                // A plugin switched on or off changes the selection: the Shelf decides whether drags are caught.
                UpdateDropCatcher();
                RebuildContentIfNeeded();
                break;
            case nameof(IslandModel.IsSettlingGracefully):
                break;
        }
    }

    private void OnHubPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (isClosed) return;
        if (eventArgs.PropertyName is nameof(IslandCompanionModel.CurrentAudioPowerLevel)) return;
        currentContent?.Refresh();
    }

    /// <summary>What the content shows, as a key: a new key builds a new view (only the active layer lives in the tree).</summary>
    private string ContentKeyFor()
    {
        return model.Expansion switch
        {
            IslandExpansion.Compact => model.IsShowingCaption ? "caption" : "status",
            IslandExpansion.Connect => "connect:" + model.ConnectPrompt?.GetHashCode() + model.ConnectPrompt?.Stage,
            IslandExpansion.Permission => "permission:" + model.PermissionPrompt?.Stage + model.PermissionPrompt?.Status,
            IslandExpansion.Tour => "tour:" + model.TourCard?.GetHashCode() + model.TourCard?.Status + model.TourCard?.Field + model.TourCard?.PrimaryTitle,
            // The slot's component and the capsule's buttons are part of the panel.
            IslandExpansion.Full => "full:" + model.Placement + ":" + model.Selection.Active + ":" + string.Join(",", model.Selection.Available),
            _ => model.Expansion + ":" + model.Placement,
        };
    }

    private void RebuildContentIfNeeded(bool force = false)
    {
        var key = ContentKeyFor();
        if (!force && key == contentKey) return;
        contentKey = key;
        var layout = model.Layout;
        var topInset = layout.CardTopInset;
        FrameworkElement element = model.Expansion switch
        {
            IslandExpansion.Compact when model.IsShowingCaption => new CaptionStripView(hub, model),
            IslandExpansion.Compact => new CompactStatusView(hub),
            IslandExpansion.Composer => new ComposerView(hub),
            IslandExpansion.Connect when model.ConnectPrompt is { } connectPrompt => new ConnectCardView(hub, connectPrompt, topInset, IslandLayout.ConnectWidth),
            IslandExpansion.Permission when model.PermissionPrompt is { } permissionPrompt => new PermissionCardView(hub, permissionPrompt, topInset),
            IslandExpansion.Tour when model.TourCard is { } tourCard => new TourCardView(hub, tourCard, topInset),
            IslandExpansion.Full => new FullPanelView(hub, model),
            IslandExpansion.Peek => new PeekLineView(hub, model.Geometry.NotchHeight),
            _ => new CollapsedHandleView(model.Placement),
        };
        // The compact strip and the composer sit under the band (on Windows the band is 0 unless a taskbar is at the top).
        if (model.Expansion is IslandExpansion.Compact or IslandExpansion.Composer)
        {
            element = new Border { Padding = new Thickness(0, model.Geometry.NotchHeight, 0, 0), Child = element };
        }
        if (currentContentElement is not null) contentHost.Children.Remove(currentContentElement);
        currentContentElement = element;
        currentContent = element as IIslandContent ?? (element as Border)?.Child as IIslandContent;
        contentHost.Children.Add(element);

        // The new layer fades in, growing from 0.92 (0.96 for the panel) at the top.
        var isAppearing = model.Expansion != IslandExpansion.Collapsed;
        contentOpacity.Snap(isAppearing ? 0 : 1);
        contentOpacity.AnimateTo(1, isAppearing ? 0.25 : 0.2, Easing.EaseOut);
        var startScale = model.Expansion == IslandExpansion.Full ? 0.96 : model.Expansion == IslandExpansion.Collapsed ? 1.0 : 0.92;
        contentScaleSpring = model.IsSettlingGracefully ? new Spring(1.3, 0.92, startScale) : new Spring(0.42, 0.84, startScale);
        contentScaleSpring.Target = 1;

        if (model.Expansion == IslandExpansion.Composer) FocusComposerSoon();
    }

    // MARK: - Window frame

    private void ResizeWindow()
    {
        var frame = model.Layout.WindowFrame;
        var isGrowing = frame.Width >= appliedWindowFrame.Width && frame.Height >= appliedWindowFrame.Height;
        pendingShrinkGeneration++;
        if (isGrowing || appliedWindowFrame.IsEmpty)
        {
            ApplyWindowFrame(frame);
            return;
        }
        // Shrink after the closing animation so the content is not clipped mid-spring (the tour's slow settle is longer).
        var generation = pendingShrinkGeneration;
        var delaySeconds = model.IsSettlingGracefully ? 1.5 : 0.45;
        _ = ShrinkLaterAsync(generation, delaySeconds);
    }

    private async Task ShrinkLaterAsync(int generation, double delaySeconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        if (isClosed || generation != pendingShrinkGeneration) return;
        ApplyWindowFrame(model.Layout.WindowFrame);
    }

    private void ApplyWindowFrame(ScreenRect frameInDips)
    {
        appliedWindowFrame = frameInDips;
        FloatingWindowSupport.PlaceInPixels(this, screen.DipsToPixels(frameInDips));
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        // Hidden and idle, the island is nothing at all: no hover zone, no click target.
        FloatingWindowSupport.SetExtendedStyles(handle, clickThrough: model.IsInvisible, noActivate: windowBeforeKeyboard == 0);
    }

    /// <summary>The monitor changed (moved, rescaled, or the taskbar moved): re-place.</summary>
    public void UpdateScreen(ScreenInfo updatedScreen)
    {
        screen = updatedScreen;
        ApplyWindowFrame(model.Layout.WindowFrame);
    }

    /// <summary>The island's rectangle in physical pixels (for hover and click-outside).</summary>
    public PixelRect IslandRectInPixels(double margin) => screen.DipsToPixels(model.Layout.Island.Inset(-margin, -margin));

    // MARK: - Frames

    private void OnRendering(object? sender, EventArgs eventArgs)
    {
        if (isClosed) return;
        var now = MotionClock.Now;
        var deltaSeconds = Math.Clamp(now - lastFrameTime, 0, 0.1);
        lastFrameTime = now;
        var layout = model.Layout;

        if (model.IsSettlingGracefully != isSettleSpring)
        {
            isSettleSpring = model.IsSettlingGracefully;
            var (width, height) = (widthSpring.Value, heightSpring.Value);
            widthSpring = isSettleSpring ? new Spring(1.3, 0.92, width) : new Spring(0.42, 0.84, width);
            heightSpring = isSettleSpring ? new Spring(1.3, 0.92, height) : new Spring(0.42, 0.84, height);
        }
        widthSpring.Target = layout.Width;
        heightSpring.Target = layout.Height;
        widthSpring.Step(deltaSeconds);
        heightSpring.Step(deltaSeconds);
        contentScaleSpring.Step(deltaSeconds);

        var isCollapsed = model.Expansion == IslandExpansion.Collapsed;
        shapeOpacity.AnimateTo(isCollapsed ? 0 : 1, isCollapsed ? 0.25 : 0.12, Easing.EaseInOut);
        var targetRadius = model.Expansion switch
        {
            IslandExpansion.Collapsed => 11,
            IslandExpansion.Peek or IslandExpansion.Compact => 18,
            _ => DS.HUD.IslandRadius,
        };
        bottomRadius.AnimateTo(targetRadius, 0.3, Easing.EaseInOut);

        // Where the island hangs in the window: centred at the top, or against the drawer's screen edge.
        var island = layout.Island;
        var frame = appliedWindowFrame.IsEmpty ? layout.WindowFrame : appliedWindowFrame;
        var isSideDrawer = model.Placement.IsSideDrawer();
        var flare = isSideDrawer ? 0 : 6;
        var animatedWidth = Math.Max(0, widthSpring.Value);
        var animatedHeight = Math.Max(0, heightSpring.Value);
        double islandLeft = model.Placement switch
        {
            HUDPlacement.Left => island.Left - frame.Left,
            HUDPlacement.Right => island.Right - frame.Left - animatedWidth,
            _ => island.MidX - frame.Left - animatedWidth / 2,
        };
        var islandTop = island.Top - frame.Top;
        if (dropCatcher.Fill is not null)
        {
            dropCatcher.Width = Math.Max(1, animatedWidth);
            dropCatcher.Height = Math.Max(1, animatedHeight);
            Canvas.SetLeft(dropCatcher, islandLeft);
            Canvas.SetTop(dropCatcher, islandTop);
        }

        // The black: only while open on a display without a notch (collapsed, only the handle marks the spot).
        islandShape.Opacity = shapeOpacity.Value;
        if (islandShape.Opacity > 0.001)
        {
            var shapeRect = new Rect(islandLeft - flare, islandTop, animatedWidth + flare * 2, animatedHeight);
            islandShape.Data = isSideDrawer
                ? Shapes.Drawer(new Rect(islandLeft, islandTop, animatedWidth, animatedHeight), bottomRadius.Value, model.Placement == HUDPlacement.Left)
                : Shapes.Island(shapeRect, bottomRadius.Value, flare);
            islandShape.IsHitTestVisible = !isCollapsed;
        }

        // The content: the state's own size, at the top of the island.
        var contentWidth = layout.Width;
        var contentHeight = layout.Height;
        contentHost.Width = contentWidth;
        contentHost.Height = contentHeight;
        var contentLeft = model.Placement switch
        {
            HUDPlacement.Left => island.Left - frame.Left,
            HUDPlacement.Right => island.Right - frame.Left - contentWidth,
            _ => island.MidX - frame.Left - contentWidth / 2,
        };
        Canvas.SetLeft(contentHost, Math.Round(contentLeft));
        Canvas.SetTop(contentHost, Math.Round(islandTop));
        contentScale.CenterX = contentWidth / 2;
        contentScale.CenterY = 0;
        contentScale.ScaleX = contentScaleSpring.Value;
        contentScale.ScaleY = contentScaleSpring.Value;
        contentHost.Opacity = contentOpacity.Value;
        currentContent?.Tick();
    }

    // MARK: - Drops (the Shelf plugin; IslandDrop.swift)

    private bool AcceptsDrop(IDataObject data) =>
        ShelfDragPolicy.Accepts(ShelfIsOn ? ShelfOnly : NoComponents, ShelfDataObjects.Formats(data));

    private bool ShelfIsOn => model.IsComponentEnabled(IslandPluginRegistry.ShelfId);
    private static readonly IReadOnlySet<string> ShelfOnly = new HashSet<string> { IslandPluginRegistry.ShelfId };
    private static readonly IReadOnlySet<string> NoComponents = new HashSet<string>();

    private void UpdateDropCatcher() => dropCatcher.Fill = ShelfIsOn ? DropCatcherFill : null;

    /// <summary>Alpha 1/255: invisible, yet a pixel the window owns.</summary>
    private static Brush MakeDropCatcherFill()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    /// <summary>A drag from another app unfolds the island to the shelf; the island's own tiles and anything with the
    /// shelf off are refused.</summary>
    private void OnDragOverIsland(object sender, DragEventArgs eventArgs)
    {
        var accepts = AcceptsDrop(eventArgs.Data);
        eventArgs.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        eventArgs.Handled = true;
        if (accepts) model.SetDragHovering(true);
    }

    private void OnDragLeaveIsland(object sender, DragEventArgs eventArgs)
    {
        // WPF also raises a leave when the drag crosses from one element to another inside the window.
        if (new Rect(RenderSize).Contains(eventArgs.GetPosition(this))) return;
        model.SetDragHovering(false);
    }

    /// <summary>A drop adds what was dragged, and the island stays open a moment so the new tile is seen.</summary>
    private void OnDropOnIsland(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        var added = AcceptsDrop(eventArgs.Data) ? ShelfIslandView.Store.Add(ShelfDataObjects.Read(eventArgs.Data)) : [];
        model.SetDragHovering(false);
        eventArgs.Effects = added.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if (added.Count > 0) model.HoldOpenAfterDrop();
    }

    // MARK: - Clicks

    private void OnIslandClicked(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.Handled) return;
        var expansion = model.Expansion;
        if (expansion == IslandExpansion.Compact && model.IsSpeaking)
        {
            hub.StopSpeakingCommand.Execute(null);
            return;
        }
        if (expansion is not (IslandExpansion.Connect or IslandExpansion.Permission or IslandExpansion.Composer or IslandExpansion.Tour)) model.TogglePinned();
    }

    // MARK: - Keyboard (the composer and the tour's field)

    private async void FocusComposerSoon()
    {
        TakeKeyboard();
        // The field exists once the island has switched state; keep putting it in front until it takes.
        foreach (var delayMilliseconds in new[] { 50, 200, 350, 600 })
        {
            await Task.Delay(delayMilliseconds);
            if (isClosed || model.Expansion != IslandExpansion.Composer) return;
            if (currentContent is ComposerView composer && !IsKeyboardFocusWithin)
            {
                TakeKeyboard();
                composer.FocusField();
            }
            else if (currentContent is ComposerView firstTime && delayMilliseconds == 50)
            {
                firstTime.FocusField();
            }
        }
    }

    public async void FocusTourField()
    {
        TakeKeyboard();
        foreach (var delayMilliseconds in new[] { 50, 200, 350 })
        {
            await Task.Delay(delayMilliseconds);
            if (isClosed || model.Expansion != IslandExpansion.Tour) return;
            if (currentContent is TourCardView tourCard) tourCard.FocusField();
        }
    }

    private void TakeKeyboard()
    {
        var previous = FloatingWindowSupport.TakeKeyboardFocus(this);
        if (windowBeforeKeyboard == 0) windowBeforeKeyboard = previous;
    }

    /// <summary>Gives the keyboard back to the window that had it before the composer (SetForegroundWindow).</summary>
    public void ReturnKeyboard()
    {
        if (windowBeforeKeyboard == 0 && IsActive == false) return;
        Keyboard.ClearFocus();
        FloatingWindowSupport.ReturnKeyboardFocus(this, windowBeforeKeyboard);
        windowBeforeKeyboard = 0;
    }

    public bool HasKeyboard => windowBeforeKeyboard != 0;

    private void TearDown()
    {
        isClosed = true;
        CompositionTarget.Rendering -= OnRendering;
        model.LayoutChanged -= OnLayoutChanged;
        model.PropertyChanged -= OnModelPropertyChanged;
        hub.PropertyChanged -= OnHubPropertyChanged;
    }
}
