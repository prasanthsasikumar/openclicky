//
//  OverlayWindowManager.cs
//  OpenClicky (Windows port of OverlayWindow.swift's OverlayWindowManager)
//
//  One overlay window per monitor, so the buddy follows the mouse across monitors. Shows and
//  hides them when the OverlayModel asks, fades them out in transient mode, and rebuilds them when
//  a monitor is plugged in, unplugged, moved or rescaled (they were sized for the old layout).
//

using System.Windows;
using System.Windows.Threading;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Windows.Overlay;

public sealed class OverlayWindowManager : IDisposable
{
    private readonly OverlayModel model;
    private readonly IScreens screens;
    private readonly Dispatcher dispatcher;
    private readonly List<OverlayWindow> overlayWindows = new();
    /// <summary>The screen frames the overlays were built for, so a display change can be told from the many
    /// notifications that change nothing.</summary>
    private List<ScreenRect> shownScreenFrames = new();

    public OverlayWindowManager(OverlayModel model, IScreens screens)
    {
        this.model = model;
        this.screens = screens;
        dispatcher = Dispatcher.CurrentDispatcher;
        model.OverlayShowRequested += ShowOverlay;
        model.OverlayHideRequested += OnHideRequested;
        screens.DisplaysChanged += OnDisplaysChanged;
        // Where things are on the desktop, for the model's flights.
        model.MouseLocation = FloatingWindowSupport.CursorScreenPoint;
        model.ScreenFrameAt = point =>
        {
            var screen = screens.ScreenAt(new PixelPoint((int)Math.Round(point.X), (int)Math.Round(point.Y)));
            return screen?.FrameInPixels() ?? ScreenRect.Empty;
        };
    }

    public IReadOnlyList<OverlayWindow> Windows => overlayWindows;

    /// <summary>Each monitor's frame in pixels and in its own DIPs, so a change of scale alone also rebuilds.</summary>
    private static List<ScreenRect> LayoutFrames(IReadOnlyList<ScreenInfo> allScreens) =>
        allScreens.Select(screen => screen.FrameInPixels()).Concat(allScreens.Select(screen => screen.BoundsInDipsRect())).ToList();

    private void OnDisplaysChanged() => dispatcher.BeginInvoke(RebuildIfScreensChanged);

    private void RebuildIfScreensChanged()
    {
        if (overlayWindows.Count == 0) return;
        var frames = LayoutFrames(screens.All());
        if (!OverlayScreenLayout.ScreensChanged(shownScreenFrames, frames)) return;
        AppLog.Write($"displays changed; rebuilding the pointer overlay for {frames.Count} screen(s)");
        model.HasShownOverlayBefore = true;
        ShowOverlay();
    }

    /// <summary>Hides any existing overlays and creates one per monitor (OverlayWindowManager.showOverlay).</summary>
    public void ShowOverlay()
    {
        HideOverlay();
        var allScreens = screens.All();
        shownScreenFrames = LayoutFrames(allScreens);
        // Only the very first overlay types the welcome bubble.
        var isFirstAppearance = !model.HasShownOverlayBefore;
        model.HasShownOverlayBefore = true;
        foreach (var screen in allScreens)
        {
            var window = new OverlayWindow(screen, model, isFirstAppearance);
            overlayWindows.Add(window);
            window.Show();
        }
    }

    public void HideOverlay()
    {
        foreach (var window in overlayWindows) window.Close();
        overlayWindows.Clear();
    }

    /// <summary>Fades the overlays out over <paramref name="duration"/> seconds (easeIn), then removes them.</summary>
    public void FadeOutAndHideOverlay(double duration = 0.4)
    {
        var windowsToFade = overlayWindows.ToList();
        overlayWindows.Clear();
        foreach (var window in windowsToFade) window.FadeOutAndClose(duration);
    }

    private void OnHideRequested(bool fade)
    {
        if (fade) FadeOutAndHideOverlay();
        else HideOverlay();
    }

    public void Dispose()
    {
        model.OverlayShowRequested -= ShowOverlay;
        model.OverlayHideRequested -= OnHideRequested;
        screens.DisplaysChanged -= OnDisplaysChanged;
        HideOverlay();
    }
}
