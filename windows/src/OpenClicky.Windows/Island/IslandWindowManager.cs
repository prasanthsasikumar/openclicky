//
//  IslandWindowManager.cs
//  OpenClicky (Windows port of NotchHUD.swift's NotchHUDManager)
//
//  One island per monitor, hanging from the top centre (below a taskbar docked at the top) or as a
//  drawer on a screen edge. The pointer comes out of the primary monitor's island. Hover is polled
//  from the cursor position every 50 ms (as on the Mac, where onHover is unreliable in a non-key
//  panel); a click anywhere outside closes a pinned panel or the composer. The composer opens on
//  the island under the pointer with the keyboard and hands it back to the previous window.
//

using System.ComponentModel;
using System.Windows.Threading;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Windows.Overlay;
using OpenClicky.Windows.Plugins.Media;

namespace OpenClicky.Windows.Island;

public sealed class IslandWindowManager : IDisposable
{
    private sealed record Instance(string DeviceName, IslandModel Model, IslandWindow Window);

    private readonly IslandCompanionModel hub;
    private readonly OverlayModel? overlayModel;
    private readonly IScreens screens;
    private readonly Preferences? preferences;
    private readonly Dispatcher dispatcher;
    private readonly List<Instance> instances = new();
    /// <summary>The island's default component, order and plugins (none without preferences: every plugin off).</summary>
    private readonly IslandComponentSettings? componentSettings;
    private readonly List<IDisposable> componentSettingsObservations = [];
    private readonly DispatcherTimer hoverPollTimer;
    private bool isShown;
    private bool wasMouseButtonDown;
    private HUDPlacement placement;

    public IslandWindowManager(IslandCompanionModel hub, OverlayModel? overlayModel, IScreens screens, Preferences? preferences = null)
    {
        this.hub = hub;
        this.overlayModel = overlayModel;
        this.screens = screens;
        this.preferences = preferences;
        dispatcher = Dispatcher.CurrentDispatcher;
        placement = HUDPlacements.FromRawValue(preferences?.GetString(HUDPlacements.DefaultsKey));
        hub.Placement = placement;
        hoverPollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) => PollHover(), dispatcher);
        hoverPollTimer.Stop();

        hub.PropertyChanged += OnHubPropertyChanged;
        hub.OpenComposerRequested += OpenTextComposer;
        hub.CloseComposerRequested += CloseTextComposer;
        hub.OpenPanelRequested += OpenPanel;
        hub.DismissPanelsRequested += DismissPanels;
        hub.FocusTourFieldRequested += FocusTourField;
        hub.PlacementChangeRequested += SetPlacement;
        screens.DisplaysChanged += OnDisplaysChanged;
        // A plugin switched on, or the default or order changed in Settings: every island picks again.
        if (preferences is not null)
        {
            componentSettings = new IslandComponentSettings(preferences);
            foreach (var key in IslandComponentSettings.Keys)
            {
                componentSettingsObservations.Add(preferences.Observe(key, () => dispatcher.BeginInvoke(() =>
                {
                    foreach (var instance in instances) instance.Model.RefreshComponentSettings();
                    SyncOptionalComponents();
                })));
            }
        }
        // The playing track is a moment of its own (Now playing on and something plays).
        MediaIslandView.Store.Changed += OnMediaChanged;
        SyncOptionalComponents();
        if (overlayModel is not null)
        {
            overlayModel.DockTarget = () => (DockPoint, DockScreenFrame);
        }
    }

    public IReadOnlyList<IslandWindow> Windows => instances.Select(instance => instance.Window).ToList();

    // MARK: - Geometry

    /// <summary>The island's geometry on a monitor, in that monitor's DIPs. At the top it hangs from the work area's top
    /// edge, so a taskbar docked at the top stays visible above it (the Mac covers its menu bar band instead).</summary>
    public static IslandGeometry GeometryFor(ScreenInfo screen, HUDPlacement placement)
    {
        var bounds = screen.BoundsInDipsRect();
        var workArea = screen.WorkAreaInDipsRect();
        if (placement.IsSideDrawer()) return IslandGeometry.ForScreen(bounds, workArea, placement);
        var top = Math.Max(bounds.Top, workArea.Top);
        var hangingFrame = new ScreenRect(bounds.Left, top, bounds.Width, bounds.Bottom - top);
        return IslandGeometry.VirtualNotch(hangingFrame, workArea);
    }

    private Instance? PrimaryInstance
    {
        get
        {
            var primary = screens.Primary();
            return instances.FirstOrDefault(instance => instance.DeviceName == primary?.DeviceName) ?? instances.FirstOrDefault();
        }
    }

    /// <summary>Where the pointer comes out of the island (the primary island), physical pixels.</summary>
    public ScreenPoint DockPoint
    {
        get
        {
            if (PrimaryInstance is { } primaryInstance)
            {
                return primaryInstance.Window.Screen.DipsToPixels(primaryInstance.Model.Layout.DockPoint);
            }
            var primary = screens.Primary();
            if (primary is null) return ScreenPoint.Zero;
            return primary.DipsToPixels(new IslandLayout(GeometryFor(primary, placement), placement).DockPoint);
        }
    }

    /// <summary>Where the tour card's main button sits (NotchHUDManager.tourPrimaryButtonLocation): the card's
    /// button column is at its right edge, the main button above the quiet one. Physical pixels; null without a card.</summary>
    public (ScreenPoint Location, ScreenRect DisplayFrame)? TourPrimaryButtonLocation()
    {
        if (PrimaryInstance is not { } primaryInstance || primaryInstance.Model.Expansion != IslandExpansion.Tour) return null;
        var island = primaryInstance.Window.IslandRectInPixels(0);
        var scale = primaryInstance.Window.Screen.Scale;
        var location = new ScreenPoint(island.Right - (24 + 43) * scale, island.Top + island.Height * 0.42);
        return (location, primaryInstance.Window.Screen.FrameInPixels());
    }

    /// <summary>The monitor the pointer comes out on, physical pixels.</summary>
    public ScreenRect DockScreenFrame => (PrimaryInstance?.Window.Screen ?? screens.Primary())?.FrameInPixels() ?? ScreenRect.Empty;

    private Instance? InstanceUnderPointer
    {
        get
        {
            var cursor = FloatingWindowSupport.CursorPixel();
            return instances.FirstOrDefault(instance => instance.Window.Screen.Bounds.Contains(cursor)) ?? PrimaryInstance;
        }
    }

    // MARK: - Showing

    public void Show()
    {
        isShown = true;
        SyncInstancesWithScreens();
        foreach (var instance in instances)
        {
            if (!instance.Window.IsVisible) instance.Window.Show();
        }
        hoverPollTimer.Start();
    }

    public void Hide()
    {
        isShown = false;
        hoverPollTimer.Stop();
        foreach (var instance in instances) instance.Window.Hide();
    }

    /// <summary>Opens the full panel on the island under the pointer and keeps it open.</summary>
    public void OpenPanel() => InstanceUnderPointer?.Model.OpenPinned();

    /// <summary>Closes every island (the demo; the tray's other items).</summary>
    public void CloseAll()
    {
        foreach (var instance in instances) instance.Model.Close();
    }

    private void DismissPanels()
    {
        foreach (var instance in instances.Where(instance => instance.Model.Expansion == IslandExpansion.Full)) instance.Model.Dismiss();
    }

    private void SetPlacement(HUDPlacement newPlacement)
    {
        if (placement == newPlacement) return;
        placement = newPlacement;
        preferences?.Set(HUDPlacements.DefaultsKey, newPlacement.RawValue());
        foreach (var instance in instances)
        {
            instance.Model.Close();
            instance.Model.Placement = newPlacement;
            instance.Model.Geometry = GeometryFor(instance.Window.Screen, newPlacement);
            instance.Window.UpdateScreen(instance.Window.Screen);
        }
    }

    // MARK: - Screens

    private void OnDisplaysChanged() => dispatcher.BeginInvoke(SyncInstancesWithScreens);

    /// <summary>Creates an island for every monitor and drops the ones whose monitor went away.</summary>
    private void SyncInstancesWithScreens()
    {
        var allScreens = screens.All();
        var liveNames = allScreens.Select(screen => screen.DeviceName).ToHashSet();
        foreach (var gone in instances.Where(instance => !liveNames.Contains(instance.DeviceName)).ToList())
        {
            gone.Window.Close();
            instances.Remove(gone);
        }
        var primary = screens.Primary();
        foreach (var screen in allScreens)
        {
            var geometry = GeometryFor(screen, placement);
            if (instances.FirstOrDefault(instance => instance.DeviceName == screen.DeviceName) is { } existing)
            {
                if (existing.Model.Geometry != geometry) existing.Model.Geometry = geometry;
                existing.Window.UpdateScreen(screen);
                continue;
            }
            var model = new IslandModel(geometry, placement, componentSettings: componentSettings);
            model.SetBusy(hub.IsBusy);
            model.SetSpeaking(hub.IsSpeaking);
            model.SetCompactDetailLine(hub.HasCompactDetailLine);
            model.SetConnectPrompt(hub.ConnectPrompt);
            model.SetPermissionPrompt(hub.PermissionPrompt);
            model.SetTourCard(hub.TourCard);
            model.SetCaption(hub.CaptionText);
            model.SetContext(CurrentContext());
            var window = new IslandWindow(model, hub, screen);
            instances.Add(new Instance(screen.DeviceName, model, window));
            if (isShown) window.Show();
        }
    }

    // MARK: - Mirroring the shared state

    private void OnHubPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(IslandCompanionModel.VoiceState):
            case nameof(IslandCompanionModel.AgentActivityText):
            case nameof(IslandCompanionModel.LastTranscript):
                foreach (var instance in instances)
                {
                    instance.Model.SetBusy(hub.IsBusy);
                    instance.Model.SetSpeaking(hub.IsSpeaking);
                    instance.Model.SetCompactDetailLine(hub.HasCompactDetailLine);
                }
                break;
            case nameof(IslandCompanionModel.IslandContext):
                ApplyIslandContext();
                break;
            case nameof(IslandCompanionModel.ConnectPrompt):
                foreach (var instance in instances) instance.Model.SetConnectPrompt(hub.ConnectPrompt);
                break;
            case nameof(IslandCompanionModel.PermissionPrompt):
                foreach (var instance in instances) instance.Model.SetPermissionPrompt(hub.PermissionPrompt);
                break;
            case nameof(IslandCompanionModel.TourCard):
                foreach (var instance in instances) instance.Model.SetTourCard(hub.TourCard);
                break;
            case nameof(IslandCompanionModel.CaptionText):
                foreach (var instance in instances) instance.Model.SetCaption(hub.CaptionText);
                break;
            case nameof(IslandCompanionModel.IsSayingGoodbye):
                if (hub.IsSayingGoodbye) foreach (var instance in instances) instance.Model.BeginGoodbye();
                break;
        }
    }

    // MARK: - Hover and clicks

    private void PollHover()
    {
        var cursor = FloatingWindowSupport.CursorPixel();
        var isInsideAnyIsland = false;
        foreach (var instance in instances)
        {
            var model = instance.Model;
            if (model.IsInvisible)
            {
                model.SetHovering(false);
                continue;
            }
            // A drag that ended without telling the island (a cancelled drag, a torn-down destination) must not leave it
            // open: no button down means no drag.
            if ((model.IsDragHovering || model.IsDraggingOut) && !FloatingWindowSupport.IsAnyMouseButtonDown())
            {
                model.SetDragHovering(false);
                model.SetDragOut(false);
            }
            var hoverMargin = model.Expansion is IslandExpansion.Collapsed or IslandExpansion.Peek ? 6 : 14;
            var isHovering = instance.Window.IslandRectInPixels(hoverMargin).Contains(cursor);
            model.SetHovering(isHovering);
            if (instance.Window.IslandRectInPixels(0).Contains(cursor)) isInsideAnyIsland = true;
        }

        // A click anywhere outside the islands closes a pinned-open panel or the composer.
        var isMouseButtonDown = FloatingWindowSupport.IsAnyMouseButtonDown();
        if (isMouseButtonDown && !wasMouseButtonDown && !isInsideAnyIsland)
        {
            foreach (var instance in instances.Where(instance => instance.Model.Expansion == IslandExpansion.Full)) instance.Model.Close();
            CloseTextComposer();
        }
        wasMouseButtonDown = isMouseButtonDown;
    }

    // MARK: - Composer (Ctrl tapped twice)

    private void OpenTextComposer()
    {
        if (InstanceUnderPointer is not { } instance) return;
        if (instances.Any(candidate => candidate.Model.IsComposerOpen)) return;
        // The window's content switches to the composer and takes the keyboard (IslandWindow.FocusComposerSoon).
        instance.Model.OpenComposer();
    }

    /// <summary>Closes the composer everywhere and hands the keyboard back to the window that had it.</summary>
    private void CloseTextComposer()
    {
        if (!instances.Any(instance => instance.Model.IsComposerOpen)) return;
        foreach (var instance in instances)
        {
            var hadKeyboard = instance.Window.HasKeyboard;
            instance.Model.CloseComposer();
            if (hadKeyboard) instance.Window.ReturnKeyboard();
        }
    }

    private void FocusTourField()
    {
        if (PrimaryInstance is { } primaryInstance && primaryInstance.Model.Expansion == IslandExpansion.Tour) primaryInstance.Window.FocusTourField();
    }

    // MARK: - The slot's moment

    /// <summary>Ticks once a second while a landed take's transcript window runs, so it ends on time.</summary>
    private DispatcherTimer? landedTranscriptTimer;

    /// <summary>The plugins that read something in the background follow their switches: Now playing asks the media
    /// sessions only while it is on (NotchHUDManager.syncOptionalComponents).</summary>
    private void SyncOptionalComponents() =>
        MediaIslandView.Store.SetEnabled(componentSettings?.EnabledPlugins.Contains(Core.Plugins.IslandPluginRegistry.MediaId) ?? false);

    private bool isMediaPlaying;

    private void OnMediaChanged()
    {
        if (MediaIslandView.Store.IsPlaying == isMediaPlaying) return;
        isMediaPlaying = MediaIslandView.Store.IsPlaying;
        ApplyIslandContext();
    }

    /// <summary>The companion's moment, with the time now and whether music plays.</summary>
    private IslandContext CurrentContext() => hub.IslandContext with { Now = DateTimeOffset.Now, IsMediaPlaying = isMediaPlaying };

    /// <summary>Every island gets the moment, with the time now; a landed take keeps the clock running until its window ends.</summary>
    private void ApplyIslandContext()
    {
        var context = CurrentContext();
        foreach (var instance in instances) instance.Model.SetContext(context);
        var isInLandedWindow = !context.IsTakeLive && context.TakeLandedAt is { } landedAt && context.Now - landedAt < IslandContext.LandedTranscriptWindow;
        if (isInLandedWindow && landedTranscriptTimer is null)
        {
            landedTranscriptTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ApplyIslandContext(), dispatcher);
            landedTranscriptTimer.Start();
        }
        else if (!isInLandedWindow && landedTranscriptTimer is not null)
        {
            landedTranscriptTimer.Stop();
            landedTranscriptTimer = null;
        }
    }

    public void Dispose()
    {
        hoverPollTimer.Stop();
        landedTranscriptTimer?.Stop();
        MediaIslandView.Store.Changed -= OnMediaChanged;
        MediaIslandView.Store.SetEnabled(false);
        foreach (var observation in componentSettingsObservations) observation.Dispose();
        componentSettingsObservations.Clear();
        hub.PropertyChanged -= OnHubPropertyChanged;
        hub.OpenComposerRequested -= OpenTextComposer;
        hub.CloseComposerRequested -= CloseTextComposer;
        hub.OpenPanelRequested -= OpenPanel;
        hub.DismissPanelsRequested -= DismissPanels;
        hub.FocusTourFieldRequested -= FocusTourField;
        hub.PlacementChangeRequested -= SetPlacement;
        screens.DisplaysChanged -= OnDisplaysChanged;
        foreach (var instance in instances) instance.Window.Close();
        instances.Clear();
    }
}
