//
//  OverlayWindow.cs
//  OpenClicky (Windows port of OverlayWindow.swift: OverlayWindow + BlueCursorView)
//
//  A full-screen, transparent, click-through window per monitor hosting the pointer buddy. Each
//  monitor's overlay checks whether the mouse is on its monitor and only shows the buddy when it
//  is. The buddy follows the mouse (35, 25 DIPs off it, on SwiftUI's 0.2 s spring), flies along a
//  quadratic Bézier arc to what it points at (0.6–1.4 s, smoothstep, the tip turned along the
//  curve, swelling to 1.3× at the apex), shows a bubble that types itself out, holds 3 s and flies
//  back; it flies into the island to dock and back out, and across monitors in legs
//  (CursorFlightPlanner). While listening it becomes a waveform, while thinking a spinner. Beside
//  it: the typewriter caption (wraps at 300, flips left near the right edge), the first-launch
//  welcome, and the Settings tour's click ripple. Frames come from CompositionTarget.Rendering.
//
//  Positions inside the window are this monitor's DIPs; the model's global positions are pixels.
//
//  The window is the monitor's overlay, but its HWND is a viewport around the buddy (680 × 320 DIPs,
//  kept inside the monitor) that moves with it: a WPF transparent window is a layered window
//  rendered in software and copied whole every frame, which for a 3024 × 1964 full-screen window
//  cost half a core whenever the mouse moved; the viewport costs a tenth of that. The content is
//  laid out in full-monitor coordinates and shifted by the viewport's origin, so nothing else changes.
//

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Theme;

namespace OpenClicky.Windows.Overlay;

/// <summary>The buddy's behavioural mode.</summary>
internal enum BuddyNavigationMode
{
    /// <summary>Follows the mouse with the spring.</summary>
    FollowingCursor,
    /// <summary>Flying toward a target (or back to the mouse).</summary>
    NavigatingToTarget,
    /// <summary>Arrived, pointing with its bubble.</summary>
    PointingAtTarget,
    /// <summary>Flying into the island to dock; the island takes over when it lands.</summary>
    FlyingToDock,
}

public sealed class OverlayWindow : Window
{
    private const string FullWelcomeMessage = "Hi, I'm OpenClicky.";
    private static readonly string[] NavigationPointerPhrases = { "right here!", "this one!", "over here!", "click this!", "here it is!", "found it!" };
    private static readonly Random PhraseRandom = new();

    /// <summary>How far the viewport reaches around the buddy: the caption wraps at 300 and may flip to the left, the
    /// ripple and the glow reach above it, a caption hangs below it.</summary>
    private const double ViewportLeftReach = 340;
    private const double ViewportRightReach = 340;
    private const double ViewportTopReach = 60;
    private const double ViewportBottomReach = 260;

    /// <summary>The cursor-follow offset: the buddy sits this far right of and below the mouse.</summary>
    private const double FollowOffsetX = 35;
    private const double FollowOffsetY = 25;

    private readonly ScreenInfo screen;
    private readonly ScreenRect screenFrame;
    private readonly OverlayModel model;
    private readonly bool isFirstAppearance;
    private readonly double screenWidth;
    private readonly double screenHeight;

    // The views.
    private readonly Canvas canvas = new();
    /// <summary>The viewport's top-left in this monitor's DIPs (whole pixels); elements are placed relative to it.</summary>
    private Point viewportOrigin;
    private readonly double viewportWidth;
    private readonly double viewportHeight;
    private PixelPoint viewportOriginPixels = new(int.MinValue, int.MinValue);
    private readonly Border welcomeBubble;
    private readonly TextBlock welcomeTextBlock;
    private readonly Border captionBubble;
    private readonly TextBlock captionTextBlock;
    private readonly Border navigationBubble;
    private readonly TextBlock navigationTextBlock;
    private readonly ScaleTransform navigationBubbleScaleTransform = new(1, 1);
    private readonly DropShadowEffect navigationBubbleGlow;
    private readonly PointerTriangleView triangle = new();
    private readonly Ellipse clickRipple;
    private readonly ScaleTransform clickRippleScaleTransform = new(0.3, 0.3);
    private readonly WaveformView waveform = new();
    private readonly SpinnerView spinner = new();

    // State (BlueCursorView's @State).
    private Point cursorPosition;
    private bool isCursorOnThisScreen;
    private string welcomeText = "";
    private bool showWelcome = true;
    private BuddyNavigationMode buddyNavigationMode = BuddyNavigationMode.FollowingCursor;
    private double triangleRotationDegrees = -35;
    private readonly Tween triangleRotation = new(-35);
    private string navigationBubbleText = "";
    private Point cursorPositionWhenNavigationStarted;
    private int pointingGeneration;
    private double buddyFlightScale = 1.0;
    private bool isReturningToCursor;

    private readonly Tween cursorOpacity = new(0);
    private readonly Tween welcomeBubbleOpacity = new(1);
    private readonly Tween navigationBubbleOpacity = new(0);
    private readonly Spring navigationBubbleScale = new(0.4, 0.6, 1.0);
    private readonly Tween captionOpacity = new(0);
    private readonly Tween triangleGate = new(0);
    private readonly Tween waveformGate = new(0);
    private readonly Tween spinnerGate = new(0);
    private readonly Tween clickRippleScale = new(0.3);
    private readonly Tween clickRippleOpacity = new(0);
    private readonly Tween dockScale = new(1);
    private bool? lastPresenceAllowsPointer;

    /// <summary>The triangle follows on the spring only while following the mouse; the bubbles always do.</summary>
    private readonly Spring2D triangleSpring = new(0.2, 0.6);
    private readonly Spring2D bubbleSpring = new(0.2, 0.6);

    private BezierFlight? currentFlight;
    private double lastFrameTime;
    private bool isClosed;

    private sealed record BezierFlight(Point Start, Point Control, Point End, double DurationSeconds, double StartTime, Action OnComplete);

    public ScreenRect ScreenFrame => screenFrame;

    public OverlayWindow(ScreenInfo screen, OverlayModel model, bool isFirstAppearance)
    {
        this.screen = screen;
        this.model = model;
        this.isFirstAppearance = isFirstAppearance;
        screenFrame = screen.FrameInPixels();
        screenWidth = screen.BoundsInDips.Width;
        screenHeight = screen.BoundsInDips.Height;
        Title = "OpenClicky pointer";
        FloatingWindowSupport.Configure(this, clickThrough: true);
        viewportWidth = Math.Min(ViewportLeftReach + ViewportRightReach, screenWidth);
        viewportHeight = Math.Min(ViewportTopReach + ViewportBottomReach, screenHeight);
        Width = viewportWidth;
        Height = viewportHeight;
        Left = screen.BoundsInDips.X;
        Top = screen.BoundsInDips.Y;

        canvas.Width = viewportWidth;
        canvas.Height = viewportHeight;
        canvas.IsHitTestVisible = false;
        canvas.Background = Brushes.Transparent;
        canvas.ClipToBounds = true;
        Content = canvas;

        (welcomeBubble, welcomeTextBlock) = MakeBubble(wraps: false, verticalPadding: 4);
        (captionBubble, captionTextBlock) = MakeBubble(wraps: true, verticalPadding: 5);
        (navigationBubble, navigationTextBlock) = MakeBubble(wraps: false, verticalPadding: 4);
        navigationBubbleGlow = (DropShadowEffect)navigationBubble.Effect;
        navigationBubble.RenderTransformOrigin = new Point(0.5, 0.5);
        navigationBubble.RenderTransform = navigationBubbleScaleTransform;
        // Room for the caption bubble's padding inside the 300 frame.
        captionBubble.MaxWidth = 300;

        clickRipple = new Ellipse
        {
            Width = 34,
            Height = 34,
            Stroke = DS.Colors.OverlayCursorBrush,
            StrokeThickness = 2.5,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = clickRippleScaleTransform,
            Opacity = 0,
        };

        // Z order as in the SwiftUI ZStack.
        foreach (var element in new UIElement[] { welcomeBubble, captionBubble, navigationBubble, triangle, clickRipple, waveform, spinner })
        {
            element.Opacity = 0;
            canvas.Children.Add(element);
        }

        // Seed the position from the mouse so the buddy doesn't flash at the corner before the first frame.
        var mouse = MouseInLocalDips();
        cursorPosition = new Point(mouse.X + FollowOffsetX, mouse.Y + FollowOffsetY);
        isCursorOnThisScreen = IsMouseOnThisScreen();
        triangleSpring.Snap(cursorPosition.X, cursorPosition.Y);
        bubbleSpring.Snap(cursorPosition.X, cursorPosition.Y);

        SourceInitialized += (_, _) => MoveViewport(cursorPosition, force: true);
        // Moved onto a monitor of another scale, WPF re-sizes the window for the new DPI; put it back on its monitor.
        DpiChanged += (_, _) => MoveViewport(cursorPosition, force: true);
        Loaded += (_, _) => OnAppear();
        Closed += (_, _) => TearDown();
        model.PropertyChanged += OnModelPropertyChanged;
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
        lastFrameTime = MotionClock.Now;
    }

    private static (Border Bubble, TextBlock Text) MakeBubble(bool wraps, double verticalPadding)
    {
        var text = new TextBlock
        {
            FontFamily = DS.Fonts.Ui,
            FontSize = 11,
            FontWeight = DS.Fonts.Medium,
            Foreground = Brushes.White,
            TextWrapping = wraps ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
        };
        var bubble = new Border
        {
            Background = DS.Colors.OverlayCursorBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, verticalPadding, 8, verticalPadding),
            Child = text,
            Effect = new DropShadowEffect { Color = DS.Colors.OverlayCursor, ShadowDepth = 0, BlurRadius = 12, Opacity = 0.5, RenderingBias = RenderingBias.Performance },
            IsHitTestVisible = false,
        };
        return (bubble, text);
    }

    // MARK: - Coordinates

    /// <summary>A global pixel point → this overlay's DIPs (convertScreenPointToSwiftUICoordinates).</summary>
    private Point ToLocal(ScreenPoint pixelPoint) =>
        new((pixelPoint.X - screen.Bounds.Left) / screen.Scale, (pixelPoint.Y - screen.Bounds.Top) / screen.Scale);

    private Point MouseInLocalDips() => ToLocal(FloatingWindowSupport.CursorScreenPoint());

    private bool IsMouseOnThisScreen() => screen.Bounds.Contains(FloatingWindowSupport.CursorPixel());

    // MARK: - Appearing

    private async void OnAppear()
    {
        isCursorOnThisScreen = IsMouseOnThisScreen();
        var mouse = MouseInLocalDips();
        cursorPosition = new Point(mouse.X + FollowOffsetX, mouse.Y + FollowOffsetY);
        triangleSpring.Snap(cursorPosition.X, cursorPosition.Y);
        bubbleSpring.Snap(cursorPosition.X, cursorPosition.Y);
        UpdateCaptionOpacityTarget();

        // Leaving the island (undock, or launching from the dock to point): start at the island and fly out.
        if (model.CursorLaunchOriginScreenLocation is { } launchOrigin && screenFrame.Contains(launchOrigin))
        {
            cursorPosition = ToLocal(launchOrigin);
            triangleSpring.Snap(cursorPosition.X, cursorPosition.Y);
            cursorOpacity.Snap(1.0);
            model.CursorLaunchOriginScreenLocation = null;
            showWelcome = false;
            if (model.DetectedElementScreenLocation is null)
            {
                StartFlyingBackToCursor();
            }
            else
            {
                // The point arrived before this overlay existed: fly to it now.
                OnDetectedElementPointTokenChanged();
            }
            return;
        }

        if (isFirstAppearance && isCursorOnThisScreen)
        {
            cursorOpacity.AnimateTo(1.0, 2.0, Easing.EaseIn);
            await Task.Delay(2000);
            if (isClosed) return;
            welcomeBubbleOpacity.Snap(0.0);
            StartWelcomeAnimation();
        }
        else
        {
            showWelcome = false;
            cursorOpacity.Snap(1.0);
            if (model.DetectedElementScreenLocation is not null) OnDetectedElementPointTokenChanged();
        }
    }

    private async void StartWelcomeAnimation()
    {
        welcomeBubbleOpacity.AnimateTo(1.0, 0.4, Easing.EaseIn);
        await Typewriter.TypeAsync(FullWelcomeMessage, typed =>
        {
            if (isClosed) return;
            welcomeText = typed;
            welcomeTextBlock.Text = typed;
        }, Task.Delay, CancellationToken.None);
        if (isClosed) return;
        // Hold the text for 2 seconds, then fade it out.
        await Task.Delay(2000);
        if (isClosed) return;
        welcomeBubbleOpacity.AnimateTo(0.0, 0.5, Easing.EaseOut);
        await Task.Delay(500);
        if (isClosed) return;
        showWelcome = false;
        model.NoteWelcomeFinished();
    }

    // MARK: - Model changes

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (isClosed) return;
        switch (eventArgs.PropertyName)
        {
            case nameof(OverlayModel.DetectedElementPointToken):
                OnDetectedElementPointTokenChanged();
                break;
            case nameof(OverlayModel.PointerClickToken):
                clickRippleScale.Snap(0.3);
                clickRippleOpacity.Snap(0.9);
                clickRippleScale.AnimateTo(1.4, 0.45, Easing.EaseOut);
                clickRippleOpacity.AnimateTo(0, 0.45, Easing.EaseOut);
                break;
            case nameof(OverlayModel.CursorDockRequestToken):
                // The overlay that is showing the buddy starts the flight; the model splits it at display edges.
                if (BuddyIsVisibleOnThisScreen) model.BeginCursorFlight(CursorFlightPurpose.ToDock, screenFrame);
                break;
            case nameof(OverlayModel.CursorFlightLeg):
                if (model.CursorFlightLeg is { } flightLeg && flightLeg.ScreenFrame == screenFrame) PerformFlightLeg(flightLeg);
                break;
            case nameof(OverlayModel.CursorCaptionOpacity):
            case nameof(OverlayModel.IsCursorCaptionVisible):
                UpdateCaptionOpacityTarget();
                break;
        }
    }

    private void UpdateCaptionOpacityTarget()
    {
        var target = model.IsCursorCaptionVisible ? model.CursorCaptionOpacity : 0;
        // The caption fades in with the manager's easeIn 0.4 s and out with its easeOut 0.3 s.
        if (target > captionOpacity.Target) captionOpacity.AnimateTo(target, 0.4, Easing.EaseIn);
        else captionOpacity.AnimateTo(target, 0.3, Easing.EaseOut);
    }

    private void OnDetectedElementPointTokenChanged()
    {
        // Every point bumps the token, so repeated coordinates re-fly too; the location is set before the token.
        if (model.DetectedElementScreenLocation is not { } screenLocation || model.DetectedElementDisplayFrame is not { } displayFrame) return;
        // Only navigate if the target is on THIS screen.
        if (!screenFrame.Contains(displayFrame.Center) && displayFrame != screenFrame) return;
        StartNavigatingToElement(screenLocation);
    }

    /// <summary>The presence setting only governs plain following: flying, pointing or talking always shows.</summary>
    private bool PresenceAllowsPointer =>
        buddyNavigationMode != BuddyNavigationMode.FollowingCursor || model.VoiceState != CompanionVoiceState.Idle || model.IsPointerAwake;

    /// <summary>Whether the buddy should be visible on this screen: only one buddy is ever visible at a time.</summary>
    private bool BuddyIsVisibleOnThisScreen
    {
        get
        {
            switch (buddyNavigationMode)
            {
                case BuddyNavigationMode.FollowingCursor:
                    // Another screen's overlay is navigating to an element: no duplicate here.
                    if (model.DetectedElementScreenLocation is not null) return false;
                    // Likewise while another screen's overlay is flying the buddy.
                    if (model.CursorFlightLeg is { } flightLeg && flightLeg.ScreenFrame != screenFrame) return false;
                    // Docked, the buddy lives in the island; an overlay shown then is for a caption beside the mouse.
                    if (model.IsCursorDocked) return false;
                    return isCursorOnThisScreen;
                default:
                    return true;
            }
        }
    }

    // MARK: - Frames

    private void OnRendering(object? sender, EventArgs eventArgs)
    {
        if (isClosed) return;
        var now = MotionClock.Now;
        var deltaSeconds = Math.Clamp(now - lastFrameTime, 0, 0.1);
        lastFrameTime = now;

        TrackCursor();
        AdvanceFlight(now);

        // During following: the fast spring. During navigation the arc drives the position directly.
        if (buddyNavigationMode == BuddyNavigationMode.FollowingCursor)
        {
            triangleSpring.SetTarget(cursorPosition.X, cursorPosition.Y);
            triangleSpring.Step(deltaSeconds);
        }
        else
        {
            triangleSpring.Snap(cursorPosition.X, cursorPosition.Y);
        }
        bubbleSpring.SetTarget(cursorPosition.X, cursorPosition.Y);
        bubbleSpring.Step(deltaSeconds);
        navigationBubbleScale.Step(deltaSeconds);

        Render();
    }

    /// <summary>The Mac's 16 ms tracking timer.</summary>
    private void TrackCursor()
    {
        var mouseOnThisScreen = IsMouseOnThisScreen();
        isCursorOnThisScreen = mouseOnThisScreen;

        // During the forward flight or pointing the buddy is not interrupted by the mouse; only the RETURN flight may be
        // cancelled by moving it (the buddy then snaps to following).
        if (buddyNavigationMode == BuddyNavigationMode.NavigatingToTarget && isReturningToCursor)
        {
            var mouse = MouseInLocalDips();
            var distanceFromNavigationStart = Math.Sqrt(Math.Pow(mouse.X - cursorPositionWhenNavigationStarted.X, 2) + Math.Pow(mouse.Y - cursorPositionWhenNavigationStarted.Y, 2));
            if (distanceFromNavigationStart > 100) CancelNavigationAndResumeFollowing();
            return;
        }
        if (buddyNavigationMode != BuddyNavigationMode.FollowingCursor) return;
        var mouseInDips = MouseInLocalDips();
        cursorPosition = new Point(mouseInDips.X + FollowOffsetX, mouseInDips.Y + FollowOffsetY);
    }

    /// <summary>Keeps the viewport around <paramref name="anchor"/> (the buddy), inside the monitor, on whole pixels. While
    /// the buddy only follows the mouse its place in the viewport stays the same, so the window moves and nothing redraws.</summary>
    private void MoveViewport(Point anchor, bool force = false)
    {
        var scale = screen.Scale;
        var anchorPixelsX = Math.Round(anchor.X * scale);
        var anchorPixelsY = Math.Round(anchor.Y * scale);
        var originPixelsX = Math.Clamp(anchorPixelsX - Math.Round(ViewportLeftReach * scale), 0, Math.Max(0, Math.Round((screenWidth - viewportWidth) * scale)));
        var originPixelsY = Math.Clamp(anchorPixelsY - Math.Round(ViewportTopReach * scale), 0, Math.Max(0, Math.Round((screenHeight - viewportHeight) * scale)));
        var originPixels = new PixelPoint((int)originPixelsX, (int)originPixelsY);
        if (!force && originPixels == viewportOriginPixels) return;
        viewportOriginPixels = originPixels;
        viewportOrigin = new Point(originPixels.X / scale, originPixels.Y / scale);
        var left = screen.Bounds.Left + originPixels.X;
        var top = screen.Bounds.Top + originPixels.Y;
        if (force)
        {
            FloatingWindowSupport.PlaceInPixels(this, new PixelRect(left, top, (int)Math.Round(viewportWidth * screen.Scale), (int)Math.Round(viewportHeight * screen.Scale)));
        }
        else
        {
            FloatingWindowSupport.MoveInPixels(this, left, top);
        }
    }

    private void Render()
    {
        MoveViewport(new Point(triangleSpring.X, triangleSpring.Y));
        var buddyVisible = BuddyIsVisibleOnThisScreen;
        var voiceState = model.VoiceState;

        // Gates fade like the SwiftUI view's implicit animations: the voice state eases in over 0.25 s (0.15 s for the
        // waveform and spinner); the presence setting over 0.2 s waking, 0.6 s resting.
        var presenceAllowsPointer = PresenceAllowsPointer;
        var triangleShows = buddyVisible && presenceAllowsPointer && voiceState is CompanionVoiceState.Idle or CompanionVoiceState.Responding;
        if (lastPresenceAllowsPointer is { } previousPresence && previousPresence != presenceAllowsPointer)
            triangleGate.AnimateTo(triangleShows ? 1 : 0, model.IsPointerAwake ? 0.2 : 0.6, Easing.EaseInOut);
        else
            triangleGate.AnimateTo(triangleShows ? 1 : 0, 0.25, Easing.EaseIn);
        lastPresenceAllowsPointer = presenceAllowsPointer;
        waveformGate.AnimateTo(buddyVisible && voiceState == CompanionVoiceState.Listening ? 1 : 0, 0.15, Easing.EaseIn);
        spinnerGate.AnimateTo(buddyVisible && voiceState == CompanionVoiceState.Processing ? 1 : 0, 0.15, Easing.EaseIn);

        var cursorOpacityValue = cursorOpacity.Value;
        var trianglePosition = new Point(triangleSpring.X, triangleSpring.Y);
        var bubblePosition = new Point(bubbleSpring.X, bubbleSpring.Y);

        // The triangle.
        triangle.Opacity = triangleGate.Value * cursorOpacityValue;
        if (triangle.Opacity > 0.001)
        {
            triangle.RotationDegrees = buddyNavigationMode == BuddyNavigationMode.NavigatingToTarget ? triangleRotationDegrees : RotationTweened();
            triangle.FlightScale = buddyFlightScale * dockScale.Value;
            Place(triangle, trianglePosition.X - 8, trianglePosition.Y - 8);
        }

        // The click ripple: 8 left of and 12 above the pointer, where its tip touches the target.
        clickRipple.Opacity = buddyVisible ? clickRippleOpacity.Value : 0;
        if (clickRipple.Opacity > 0.001)
        {
            clickRippleScaleTransform.ScaleX = clickRippleScale.Value;
            clickRippleScaleTransform.ScaleY = clickRippleScale.Value;
            Place(clickRipple, cursorPosition.X - 8 - 17, cursorPosition.Y - 12 - 17);
        }

        // The waveform and the spinner take the triangle's place.
        waveform.Opacity = waveformGate.Value * cursorOpacityValue;
        if (waveform.Opacity > 0.001)
        {
            waveform.AudioPowerLevel = model.CurrentAudioPowerLevel;
            waveform.Tick();
            Place(waveform, bubblePosition.X - waveform.Width / 2, bubblePosition.Y - waveform.Height / 2);
        }
        spinner.Opacity = spinnerGate.Value * cursorOpacityValue;
        if (spinner.Opacity > 0.001)
        {
            spinner.Tick();
            Place(spinner, bubblePosition.X - 7, bubblePosition.Y - 7);
        }

        // The welcome bubble (first launch only).
        var welcomeShows = isCursorOnThisScreen && showWelcome && welcomeText.Length > 0;
        welcomeBubble.Opacity = welcomeShows ? welcomeBubbleOpacity.Value : 0;
        if (welcomeShows)
        {
            var size = Measure(welcomeBubble);
            Place(welcomeBubble, bubblePosition.X + 10, bubblePosition.Y + 18 - size.Height / 2);
        }

        // The caption: wraps at 300 and flips to the buddy's left when it would run off the screen.
        var captionText = model.CursorCaptionText;
        var captionShows = isCursorOnThisScreen && model.IsCursorCaptionVisible && captionText.Length > 0;
        captionBubble.Opacity = captionShows ? captionOpacity.Value : 0;
        if (captionShows)
        {
            if (captionTextBlock.Text != captionText) captionTextBlock.Text = captionText;
            var size = Measure(captionBubble);
            var fitsToTheRight = bubblePosition.X + 10 + size.Width <= screenWidth - 12;
            var left = fitsToTheRight ? bubblePosition.X + 10 : bubblePosition.X - 10 - size.Width;
            Place(captionBubble, left, bubblePosition.Y + 12);
        }

        // The pointing bubble: pops in 0.5 → 1 with a glow that settles.
        var navigationShows = buddyNavigationMode == BuddyNavigationMode.PointingAtTarget && navigationBubbleText.Length > 0;
        navigationBubble.Opacity = navigationShows ? navigationBubbleOpacity.Value : 0;
        if (navigationShows)
        {
            if (navigationTextBlock.Text != navigationBubbleText) navigationTextBlock.Text = navigationBubbleText;
            var scale = navigationBubbleScale.Value;
            navigationBubbleScaleTransform.ScaleX = scale;
            navigationBubbleScaleTransform.ScaleY = scale;
            navigationBubbleGlow.Opacity = Math.Clamp(0.5 + (1.0 - scale) * 1.0, 0, 1);
            navigationBubbleGlow.BlurRadius = (6 + (1.0 - scale) * 16) * 2;
            var size = Measure(navigationBubble);
            Place(navigationBubble, bubblePosition.X + 10, bubblePosition.Y + 18 - size.Height / 2);
        }
    }

    /// <summary>Outside the arc the rotation eases over 0.3 s (back to -35° on landing).</summary>
    private double RotationTweened()
    {
        if (triangleRotation.Target != triangleRotationDegrees) triangleRotation.AnimateTo(triangleRotationDegrees, 0.3, Easing.EaseInOut);
        return triangleRotation.Value;
    }

    private static Size Measure(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize;
    }

    /// <summary>Puts an element at a monitor position, relative to the viewport, on whole device pixels (an unchanged
    /// position leaves the element alone, so following the mouse redraws nothing).</summary>
    private void Place(UIElement element, double left, double top)
    {
        var scale = screen.Scale;
        Canvas.SetLeft(element, Math.Round((left - viewportOrigin.X) * scale) / scale);
        Canvas.SetTop(element, Math.Round((top - viewportOrigin.Y) * scale) / scale);
    }

    // MARK: - Element navigation

    private void StartNavigatingToElement(ScreenPoint screenLocation)
    {
        // Don't interrupt the welcome animation.
        if (showWelcome && welcomeText.Length > 0) return;

        var targetLocal = ToLocal(screenLocation);
        // Beside the element rather than on top of it: 8 right, 12 below.
        var offsetTarget = new Point(targetLocal.X + 8, targetLocal.Y + 12);
        var clampedTarget = new Point(
            Math.Max(20, Math.Min(offsetTarget.X, screenWidth - 20)),
            Math.Max(20, Math.Min(offsetTarget.Y, screenHeight - 20)));

        cursorPositionWhenNavigationStarted = MouseInLocalDips();

        // Retarget while already pointing (the next step of a walkthrough): drop the bubble and invalidate the pending
        // fly-back, then fly on from where the buddy is now.
        if (buddyNavigationMode == BuddyNavigationMode.PointingAtTarget)
        {
            pointingGeneration++;
            navigationBubbleText = "";
            navigationBubbleOpacity.Snap(0);
            navigationBubbleScale.Snap(1.0);
        }

        cursorOpacity.Snap(Math.Max(cursorOpacity.Value, 1.0));
        buddyNavigationMode = BuddyNavigationMode.NavigatingToTarget;
        isReturningToCursor = false;
        AnimateBezierFlightArc(clampedTarget, () =>
        {
            if (buddyNavigationMode != BuddyNavigationMode.NavigatingToTarget) return;
            StartPointingAtElement();
        });
    }

    /// <summary>A quadratic Bézier arc from where the buddy is to <paramref name="destination"/>: the tip turns along the
    /// curve, the buddy swells at the midpoint, the glow brightens with it.</summary>
    private void AnimateBezierFlightArc(Point destination, Action onComplete)
    {
        var startPosition = cursorPosition;
        var deltaX = destination.X - startPosition.X;
        var deltaY = destination.Y - startPosition.Y;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        // Short hops are quick, long flights more dramatic: 0.6 s – 1.4 s.
        var flightDurationSeconds = Math.Min(Math.Max(distance / 800.0, 0.6), 1.4);
        // The Mac counts whole 60 fps frames; the same length here, on the clock.
        var totalFrames = (int)(flightDurationSeconds / (1.0 / 60.0));
        var midPoint = new Point((startPosition.X + destination.X) / 2.0, (startPosition.Y + destination.Y) / 2.0);
        // The arc rises (negative y) by a fifth of the distance, at most 80.
        var arcHeight = Math.Min(distance * 0.2, 80.0);
        var controlPoint = new Point(midPoint.X, midPoint.Y - arcHeight);
        currentFlight = new BezierFlight(startPosition, controlPoint, destination, Math.Max(totalFrames, 1) / 60.0, MotionClock.Now, onComplete);
    }

    private void AdvanceFlight(double now)
    {
        if (currentFlight is not { } flight) return;
        var linearProgress = (now - flight.StartTime) / flight.DurationSeconds;
        if (linearProgress >= 1)
        {
            currentFlight = null;
            cursorPosition = flight.End;
            buddyFlightScale = 1.0;
            flight.OnComplete();
            return;
        }
        linearProgress = Math.Max(linearProgress, 0);
        // Smoothstep easeInOut: 3t² − 2t³.
        var t = linearProgress * linearProgress * (3.0 - 2.0 * linearProgress);
        // B(t) = (1−t)²·P0 + 2(1−t)t·P1 + t²·P2
        var oneMinusT = 1.0 - t;
        var bezierX = oneMinusT * oneMinusT * flight.Start.X + 2.0 * oneMinusT * t * flight.Control.X + t * t * flight.End.X;
        var bezierY = oneMinusT * oneMinusT * flight.Start.Y + 2.0 * oneMinusT * t * flight.Control.Y + t * t * flight.End.Y;
        cursorPosition = new Point(bezierX, bezierY);
        // Face the direction of travel: B'(t) = 2(1−t)(P1−P0) + 2t(P2−P1); +90° because the tip points up at 0°.
        var tangentX = 2.0 * oneMinusT * (flight.Control.X - flight.Start.X) + 2.0 * t * (flight.End.X - flight.Control.X);
        var tangentY = 2.0 * oneMinusT * (flight.Control.Y - flight.Start.Y) + 2.0 * t * (flight.End.Y - flight.Control.Y);
        triangleRotationDegrees = Math.Atan2(tangentY, tangentX) * (180.0 / Math.PI) + 90.0;
        // The swoop: sin peaks at the midpoint, 1.0 → 1.3 → 1.0.
        var scalePulse = Math.Sin(linearProgress * Math.PI);
        buddyFlightScale = 1.0 + scalePulse * 0.3;
        // During the arc there is no implicit rotation animation.
        triangleRotation.Snap(triangleRotationDegrees);
    }

    /// <summary>Arrived: the bubble pops in and types itself out (30–60 ms a character), holds 3 s, then the buddy flies
    /// back, unless something retargeted it since.</summary>
    private async void StartPointingAtElement()
    {
        buddyNavigationMode = BuddyNavigationMode.PointingAtTarget;
        pointingGeneration++;
        var generation = pointingGeneration;
        triangleRotationDegrees = -35.0;
        navigationBubbleText = "";
        navigationBubbleOpacity.Snap(1.0);
        navigationBubbleScale.Snap(0.5);

        var pointerPhrase = model.DetectedElementBubbleText ?? NavigationPointerPhrases[PhraseRandom.Next(NavigationPointerPhrases.Length)];
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(pointerPhrase);
        var isFirstCharacter = true;
        while (enumerator.MoveNext())
        {
            if (isClosed || buddyNavigationMode != BuddyNavigationMode.PointingAtTarget || pointingGeneration != generation) return;
            navigationBubbleText += enumerator.GetTextElement();
            if (isFirstCharacter)
            {
                // The scale-bounce entrance.
                navigationBubbleScale.Target = 1.0;
                isFirstCharacter = false;
            }
            await Task.Delay(TimeSpan.FromSeconds(0.03 + PhraseRandom.NextDouble() * 0.03));
        }

        // Hold 3 s, then fly back (unless retargeted since).
        await Task.Delay(3000);
        if (isClosed || buddyNavigationMode != BuddyNavigationMode.PointingAtTarget || pointingGeneration != generation) return;
        // The Settings tour keeps the pointer on each row while its line is said.
        if (model.PointerStaysOnTarget) return;
        navigationBubbleOpacity.AnimateTo(0.0, 0.5, Easing.EaseOut);
        await Task.Delay(500);
        if (isClosed || buddyNavigationMode != BuddyNavigationMode.PointingAtTarget || pointingGeneration != generation) return;
        StartFlyingBackToCursor();
    }

    /// <summary>Back to the mouse after pointing, or back into the island when the buddy is docked there.</summary>
    private void StartFlyingBackToCursor()
    {
        if (model.IsCursorDocked)
        {
            navigationBubbleText = "";
            navigationBubbleOpacity.Snap(0.0);
            model.BeginCursorFlight(CursorFlightPurpose.ToDock, screenFrame);
            return;
        }
        if (!IsMouseOnThisScreen())
        {
            // The mouse is on another display: fly to this display's edge, then across that one.
            navigationBubbleText = "";
            navigationBubbleOpacity.Snap(0.0);
            model.BeginCursorFlight(CursorFlightPurpose.ToMouse, screenFrame);
            return;
        }
        var mouse = MouseInLocalDips();
        var cursorWithTrackingOffset = new Point(mouse.X + FollowOffsetX, mouse.Y + FollowOffsetY);
        cursorPositionWhenNavigationStarted = mouse;
        buddyNavigationMode = BuddyNavigationMode.NavigatingToTarget;
        isReturningToCursor = true;
        AnimateBezierFlightArc(cursorWithTrackingOffset, FinishNavigationAndResumeFollowing);
    }

    // MARK: - Docking

    /// <summary>One leg of a flight on this display: into the island, or back to a mouse on another display. A non-final
    /// leg ends at this display's edge and hands over to the destination display's overlay.</summary>
    private void PerformFlightLeg(CursorFlightLeg flightLeg)
    {
        if (showWelcome && welcomeText.Length > 0) return;
        currentFlight = null;
        pointingGeneration++;
        navigationBubbleText = "";
        navigationBubbleOpacity.Snap(0.0);
        navigationBubbleScale.Snap(1.0);
        showWelcome = false;

        if (flightLeg.StartScreenLocation is { } startScreenLocation)
        {
            // Continuing a flight from another display: appear at the edge it left from.
            cursorPosition = ToLocal(startScreenLocation);
            triangleSpring.Snap(cursorPosition.X, cursorPosition.Y);
        }
        cursorOpacity.Snap(1.0);
        dockScale.Snap(1.0);

        var destination = ToLocal(flightLeg.EndScreenLocation);
        if (flightLeg.Purpose == CursorFlightPurpose.ToDock)
        {
            buddyNavigationMode = BuddyNavigationMode.FlyingToDock;
            isReturningToCursor = false;
        }
        else
        {
            if (flightLeg.IsFinalLeg)
            {
                // Land where cursor following would put the buddy; a big mouse move cancels it.
                destination = new Point(destination.X + FollowOffsetX, destination.Y + FollowOffsetY);
                cursorPositionWhenNavigationStarted = MouseInLocalDips();
            }
            buddyNavigationMode = BuddyNavigationMode.NavigatingToTarget;
            isReturningToCursor = flightLeg.IsFinalLeg;
        }

        AnimateBezierFlightArc(destination, () =>
        {
            if (model.CursorFlightLeg != flightLeg) return;
            if (!flightLeg.IsFinalLeg)
            {
                // Slip off this display's edge; the destination display's overlay continues.
                if (flightLeg.Purpose == CursorFlightPurpose.ToDock)
                {
                    cursorOpacity.Snap(0.0);
                }
                else
                {
                    buddyNavigationMode = BuddyNavigationMode.FollowingCursor;
                    isReturningToCursor = false;
                    triangleRotationDegrees = -35.0;
                }
                model.ContinueCursorFlight(flightLeg);
                return;
            }
            if (flightLeg.Purpose == CursorFlightPurpose.ToDock) SettleIntoDock();
            else FinishNavigationAndResumeFollowing();
        });
    }

    /// <summary>The end of the docking flight: tip up under the island, slip inside, hand over to the island.</summary>
    private async void SettleIntoDock()
    {
        if (buddyNavigationMode != BuddyNavigationMode.FlyingToDock) return;
        triangleRotationDegrees = 0;
        triangleRotation.Snap(0);
        cursorOpacity.AnimateTo(0.0, 0.22, Easing.EaseIn);
        dockScale.AnimateTo(0.6, 0.22, Easing.EaseIn);
        await Task.Delay(250);
        if (isClosed) return;
        model.FinishDockingCursor();
    }

    private void CancelNavigationAndResumeFollowing()
    {
        currentFlight = null;
        navigationBubbleText = "";
        navigationBubbleOpacity.Snap(0.0);
        navigationBubbleScale.Snap(1.0);
        buddyFlightScale = 1.0;
        FinishNavigationAndResumeFollowing();
    }

    private void FinishNavigationAndResumeFollowing()
    {
        currentFlight = null;
        buddyNavigationMode = BuddyNavigationMode.FollowingCursor;
        isReturningToCursor = false;
        triangleRotationDegrees = -35.0;
        buddyFlightScale = 1.0;
        navigationBubbleText = "";
        navigationBubbleOpacity.Snap(0.0);
        navigationBubbleScale.Snap(1.0);
        model.ClearDetectedElementLocation();
        model.FinishCursorFlight(screenFrame);
        // Back at the mouse: stay a moment so the landing is seen, then rest away as usual.
        model.WakePointer();
    }

    // MARK: - Teardown

    /// <summary>Fades the whole window out over <paramref name="durationSeconds"/> (easeIn), then closes it.</summary>
    public async void FadeOutAndClose(double durationSeconds = 0.4)
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromSeconds(durationSeconds))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
        };
        BeginAnimation(OpacityProperty, fade);
        await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
        Close();
    }

    private void TearDown()
    {
        isClosed = true;
        System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        model.PropertyChanged -= OnModelPropertyChanged;
    }
}
