//
//  OverlayModel.cs
//  OpenClicky (Windows port of the overlay half of CompanionManager.swift)
//
//  Everything the pointer overlay (one click-through window per monitor, Overlay/OverlayWindow.cs)
//  reads, and the overlay-side rules CompanionManager keeps on the Mac: pointing (pointAt, the
//  token that re-flies to the same spot, the Settings tour's click ripple), the typewriter caption
//  beside the pointer, docking into the island and back, the flight legs between displays
//  (CursorFlightPlanner), and transient cursor mode (the overlay fades out a second after an
//  interaction when "show openclicky" is off).
//
//  Coordinates: physical virtual-desktop pixels (top-left origin, y down), carried in the
//  ScreenPoint / ScreenRect records. Windows has no single DIP space across monitors of different
//  scales, so global positions stay in pixels and each overlay converts to its own DIPs.
//
//  Call everything on the UI thread: the typewriter and the timers resume on the context they were
//  started from (the WPF dispatcher in the app).
//

using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Companion;

public sealed partial class OverlayModel : ObservableObject
{
    // MARK: - Voice

    [ObservableProperty] private CompanionVoiceState voiceState = CompanionVoiceState.Idle;
    /// <summary>0…1 microphone level for the waveform.</summary>
    [ObservableProperty] private double currentAudioPowerLevel;

    // MARK: - Pointing

    /// <summary>Where the buddy should fly to and point (physical pixels). Set before the token is bumped.</summary>
    [ObservableProperty] private ScreenPoint? detectedElementScreenLocation;
    /// <summary>The monitor (physical pixels) the target is on, so only that overlay animates.</summary>
    [ObservableProperty] private ScreenRect? detectedElementDisplayFrame;
    /// <summary>The bubble's words when pointing; null = a random pointer phrase.</summary>
    [ObservableProperty] private string? detectedElementBubbleText;
    /// <summary>Bumped on every point so the overlay re-flies even to the same coordinates.</summary>
    [ObservableProperty] private int detectedElementPointToken;

    // MARK: - Caption (typewriter text beside the buddy)

    [ObservableProperty] private string cursorCaptionText = "";
    [ObservableProperty] private double cursorCaptionOpacity;
    [ObservableProperty] private bool isCursorCaptionVisible;

    // MARK: - Docking and flights

    /// <summary>True while the buddy lives in the island instead of following the mouse.</summary>
    [ObservableProperty] private bool isCursorDocked;
    /// <summary>Bumped when the user docks the buddy; the overlay showing the buddy starts the flight.</summary>
    [ObservableProperty] private int cursorDockRequestToken;
    /// <summary>The leg of a flight in progress; the overlay whose screen matches animates it.</summary>
    [ObservableProperty] private CursorFlightLeg? cursorFlightLeg;

    /// <summary>Where a freshly shown overlay starts the buddy (the island) before flying out. Consumed by the overlay.</summary>
    public ScreenPoint? CursorLaunchOriginScreenLocation { get; set; }

    public bool IsDockingInProgress { get; private set; }
    private int cursorFlightSequence;

    // MARK: - Presence, tour

    /// <summary>False while the pointer rests away (PointerPresence); flights and conversations ignore it.</summary>
    [ObservableProperty] private bool isPointerAwake = true;
    /// <summary>The Settings tour keeps the pointer on each row until the next step.</summary>
    [ObservableProperty] private bool pointerStaysOnTarget;
    /// <summary>Bumped for each simulated click; the overlay draws a ripple at the pointer's tip.</summary>
    [ObservableProperty] private int pointerClickToken;

    // MARK: - Visibility

    /// <summary>Whether the overlay windows are up.</summary>
    [ObservableProperty] private bool isOverlayVisible;
    /// <summary>"Show OpenClicky": off means transient mode (shown for an interaction, then faded out).</summary>
    [ObservableProperty] private bool isClickyCursorEnabled = true;

    /// <summary>False until the first overlay was shown: only that one types the welcome bubble.</summary>
    public bool HasShownOverlayBefore { get; set; }

    /// <summary>The overlay windows should be created on every monitor (the window manager listens).</summary>
    public event Action? OverlayShowRequested;
    /// <summary>The overlay windows should go; true = fade out over 0.4 s first (transient mode).</summary>
    public event Action<bool>? OverlayHideRequested;
    /// <summary>The buddy landed back at the mouse: the presence tracker should wake it (CompanionManager.wakePointer).</summary>
    public event Action? PointerWakeRequested;
    /// <summary>The first-launch welcome bubble finished (the Mac starts its onboarding video here).</summary>
    public event Action? WelcomeFinished;
    /// <summary>The island should drop a caption it is typing (undocking).</summary>
    public event Action? IslandCaptionClearRequested;

    // MARK: - Environment (set by the Windows side)

    /// <summary>The mouse in physical pixels.</summary>
    public Func<ScreenPoint> MouseLocation { get; set; } = () => ScreenPoint.Zero;
    /// <summary>The monitor frame (physical pixels) containing a point, else the nearest.</summary>
    public Func<ScreenPoint, ScreenRect> ScreenFrameAt { get; set; } = _ => ScreenRect.Empty;
    /// <summary>Where the docked buddy lives (the primary island's dock point) and that monitor's frame.</summary>
    public Func<(ScreenPoint DockPoint, ScreenRect DockScreenFrame)> DockTarget { get; set; } = () => (ScreenPoint.Zero, ScreenRect.Empty);
    /// <summary>An answer is being spoken (transient mode waits for it to finish).</summary>
    public Func<bool> IsAnswerAudioPlaying { get; set; } = () => false;

    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<double> secondsClock;
    private readonly Preferences? preferences;

    public const string DockedPreferenceKey = "isOpenClickyCursorDocked";
    public const string CursorEnabledPreferenceKey = "isClickyCursorEnabled";

    /// <param name="delay">The clock (Task.Delay); tests pass an immediate one.</param>
    /// <param name="preferences">Where the docked state is remembered (null: not remembered).</param>
    /// <param name="secondsClock">The clock <paramref name="delay"/> runs on, in seconds (the typewriter keeps pace with it).</param>
    public OverlayModel(Func<TimeSpan, CancellationToken, Task>? delay = null, Preferences? preferences = null, Func<double>? secondsClock = null)
    {
        this.delay = delay ?? Task.Delay;
        this.secondsClock = secondsClock ?? Typewriter.MonotonicSeconds;
        this.preferences = preferences;
        if (preferences is not null)
        {
            isClickyCursorEnabled = preferences.GetOptionalBool(CursorEnabledPreferenceKey) ?? true;
        }
    }

    // MARK: - Showing the overlay

    /// <summary>Puts the overlay on every display (CompanionManager.showOverlayOnAllScreens). Never the welcome: every
    /// caller shows the buddy for a reason of its own.</summary>
    public void ShowOverlayOnAllScreens()
    {
        HasShownOverlayBefore = true;
        IsOverlayVisible = true;
        OverlayShowRequested?.Invoke();
    }

    /// <summary>The first show at launch: the overlay may type its welcome bubble.</summary>
    public void ShowOverlayAtLaunch()
    {
        IsOverlayVisible = true;
        OverlayShowRequested?.Invoke();
    }

    public void HideOverlay()
    {
        IsOverlayVisible = false;
        OverlayHideRequested?.Invoke(false);
    }

    public void SetClickyCursorEnabled(bool enabled)
    {
        IsClickyCursorEnabled = enabled;
        preferences?.Set(CursorEnabledPreferenceKey, enabled);
        CancelTransientHide();
        if (enabled) ShowOverlayOnAllScreens();
        else HideOverlay();
    }

    // MARK: - Transient cursor mode

    private CancellationTokenSource? transientHideCancellation;

    public void CancelTransientHide()
    {
        transientHideCancellation?.Cancel();
        transientHideCancellation = null;
    }

    /// <summary>In transient mode (show openclicky off): waits for the spoken answer, the pointing and the caption to end,
    /// then fades the overlay out after 1 s. Cancelled when another interaction starts (<see cref="CancelTransientHide"/>).</summary>
    public void ScheduleTransientHideIfNeeded()
    {
        if (IsClickyCursorEnabled || !IsOverlayVisible) return;
        CancelTransientHide();
        var cancellation = new CancellationTokenSource();
        transientHideCancellation = cancellation;
        _ = RunTransientHideAsync(cancellation.Token);
    }

    private async Task RunTransientHideAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pollInterval = TimeSpan.FromMilliseconds(200);
            while (IsAnswerAudioPlaying()) await delay(pollInterval, cancellationToken);
            while (DetectedElementScreenLocation is not null) await delay(pollInterval, cancellationToken);
            while (IsCursorCaptionVisible) await delay(pollInterval, cancellationToken);
            await delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            IsOverlayVisible = false;
            OverlayHideRequested?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // MARK: - Pointing

    /// <summary>Fly the buddy to <paramref name="location"/> on <paramref name="displayFrame"/> and show
    /// <paramref name="label"/> in its bubble. A second call while pointing retargets (CompanionManager.pointAt after
    /// the screenshot point was mapped to the screen and the system-prompt guard passed).</summary>
    public void PointAt(ScreenPoint location, ScreenRect displayFrame, string? label)
    {
        // The spinner (processing) hides the triangle, so the flight would be invisible.
        if (VoiceState == CompanionVoiceState.Processing) VoiceState = CompanionVoiceState.Idle;
        LaunchDockedCursorForPointing();
        var trimmedLabel = label?.Trim();
        DetectedElementBubbleText = string.IsNullOrEmpty(trimmedLabel) ? null : trimmedLabel;
        DetectedElementDisplayFrame = displayFrame;
        DetectedElementScreenLocation = location;
        DetectedElementPointToken = unchecked(DetectedElementPointToken + 1);
    }

    /// <summary>While docked, pointing needs the buddy on screen: it launches from the island and comes back after.</summary>
    private void LaunchDockedCursorForPointing()
    {
        if (!IsCursorDocked || IsOverlayVisible) return;
        CursorLaunchOriginScreenLocation = DockTarget().DockPoint;
        CursorFlightLeg = null;
        ShowOverlayOnAllScreens();
    }

    public void ClearDetectedElementLocation()
    {
        DetectedElementScreenLocation = null;
        DetectedElementDisplayFrame = null;
        DetectedElementBubbleText = null;
    }

    public void BeginSettingsTourPointing()
    {
        PointerStaysOnTarget = true;
        if (IsCursorDocked) CursorLaunchOriginScreenLocation = DockTarget().DockPoint;
        if (!IsOverlayVisible) ShowOverlayOnAllScreens();
    }

    /// <summary>The tour is over: pointing at the same spot again starts the normal hold, after which it flies back.</summary>
    public void EndSettingsTourPointing()
    {
        PointerStaysOnTarget = false;
        if (DetectedElementScreenLocation is not { } lastLocation) return;
        PointForSettingsTour(lastLocation, DetectedElementBubbleText ?? "");
    }

    public void PointForSettingsTour(ScreenPoint screenLocation, string bubble)
    {
        var displayFrame = ScreenFrameAt(screenLocation);
        if (!IsOverlayVisible) ShowOverlayOnAllScreens();
        DetectedElementBubbleText = string.IsNullOrEmpty(bubble) ? null : bubble;
        DetectedElementDisplayFrame = displayFrame;
        DetectedElementScreenLocation = screenLocation;
        DetectedElementPointToken = unchecked(DetectedElementPointToken + 1);
    }

    public void ShowPointerClick() => PointerClickToken = unchecked(PointerClickToken + 1);

    /// <summary>Show the pointer now; the presence tracker rests it away again as the setting says.</summary>
    public void WakePointer()
    {
        if (!IsPointerAwake) IsPointerAwake = true;
        PointerWakeRequested?.Invoke();
    }

    /// <summary>Raised by the overlay when its welcome bubble is gone.</summary>
    public void NoteWelcomeFinished() => WelcomeFinished?.Invoke();

    // MARK: - Docking

    /// <summary>Dock the buddy in the island, or release it back to the mouse.</summary>
    public void SetCursorDocked(bool docked)
    {
        if (docked) DockCursor();
        else UndockCursor();
    }

    private void DockCursor()
    {
        if (IsCursorDocked || IsDockingInProgress) return;
        if (VoiceState != CompanionVoiceState.Idle) return;
        IsDockingInProgress = true;
        if (!IsOverlayVisible) ShowOverlayOnAllScreens();
        CancelTransientHide();
        ClearDetectedElementLocation();
        DismissCursorCaption();
        _ = BumpDockRequestAndWatchAsync();
    }

    private async Task BumpDockRequestAndWatchAsync()
    {
        // Bumped on the next turn so a freshly created overlay observes the change.
        await delay(TimeSpan.Zero, CancellationToken.None);
        CursorDockRequestToken = unchecked(CursorDockRequestToken + 1);
        // Both legs of the longest flight take under 3.5 s. If no overlay picked the request up (the pointer between
        // displays, say), dock without the flight rather than hang.
        await delay(TimeSpan.FromSeconds(4), CancellationToken.None);
        if (!IsDockingInProgress) return;
        AppLog.Write("pointer: docking without a flight (no overlay animated it)");
        FinishDockingCursor();
    }

    /// <summary>Called by the overlay once the flight into the island has finished.</summary>
    public void FinishDockingCursor()
    {
        IsDockingInProgress = false;
        IsCursorDocked = true;
        preferences?.Set(DockedPreferenceKey, true);
        HideOverlay();
        CursorFlightLeg = null;
        ClearDetectedElementLocation();
    }

    private void UndockCursor()
    {
        if (!IsCursorDocked) return;
        IsCursorDocked = false;
        preferences?.Set(DockedPreferenceKey, false);
        CursorFlightLeg = null;
        IslandCaptionClearRequested?.Invoke();
        // Re-create the overlay with the buddy starting at the island; it flies to the mouse.
        CursorLaunchOriginScreenLocation = DockTarget().DockPoint;
        ShowOverlayOnAllScreens();
    }

    /// <summary>Restores the docked state at launch without an animation: the buddy is simply home.</summary>
    public void RestoreDockedWithoutFlight() => IsCursorDocked = true;

    // MARK: - Flights between displays

    /// <summary>Starts a flight from the overlay on <paramref name="fromScreenFrame"/> to the island (ToDock) or to the
    /// mouse (ToMouse). A destination on another display gets two legs.</summary>
    public void BeginCursorFlight(CursorFlightPurpose purpose, ScreenRect fromScreenFrame)
    {
        if (CursorFlightLeg is not null) return;
        var (destination, destinationScreenFrame) = CursorFlightDestination(purpose, fromScreenFrame);
        cursorFlightSequence++;
        CursorFlightLeg = CursorFlightPlanner.FirstLeg(purpose, fromScreenFrame, destination, destinationScreenFrame, cursorFlightSequence);
    }

    /// <summary>Called by the overlay that finished a non-final leg: the destination display's overlay takes over.</summary>
    public void ContinueCursorFlight(CursorFlightLeg finishedLeg)
    {
        if (finishedLeg != CursorFlightLeg || finishedLeg.IsFinalLeg) return;
        var (destination, destinationScreenFrame) = CursorFlightDestination(finishedLeg.Purpose, finishedLeg.ScreenFrame);
        cursorFlightSequence++;
        CursorFlightLeg = CursorFlightPlanner.FinalLeg(finishedLeg.Purpose, finishedLeg.EndScreenLocation, destination, destinationScreenFrame, cursorFlightSequence);
    }

    /// <summary>Called by the overlay when the flight it was animating ended (or was cancelled).</summary>
    public void FinishCursorFlight(ScreenRect screenFrame)
    {
        if (CursorFlightLeg is not { } leg || leg.ScreenFrame != screenFrame) return;
        CursorFlightLeg = null;
    }

    private (ScreenPoint Destination, ScreenRect DestinationScreenFrame) CursorFlightDestination(CursorFlightPurpose purpose, ScreenRect fallbackScreenFrame)
    {
        if (purpose == CursorFlightPurpose.ToDock)
        {
            var (dockPoint, dockScreenFrame) = DockTarget();
            return (dockPoint, dockScreenFrame.IsEmpty ? fallbackScreenFrame : dockScreenFrame);
        }
        var mouseLocation = MouseLocation();
        var mouseScreenFrame = ScreenFrameAt(mouseLocation);
        return (mouseLocation, mouseScreenFrame.IsEmpty ? fallbackScreenFrame : mouseScreenFrame);
    }

    // MARK: - Caption

    private CancellationTokenSource? captionCancellation;

    /// <summary>Types <paramref name="message"/> beside the buddy one character every 30 ms (the onboarding prompt, the
    /// island's (i), hands-free feedback), holds it <paramref name="holdSeconds"/>, then fades it out.</summary>
    public void StreamCursorCaption(string message, double holdSeconds = 10)
    {
        captionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        captionCancellation = cancellation;
        CursorCaptionText = "";
        IsCursorCaptionVisible = true;
        // The overlay fades the caption in over 0.4 s (easeIn) when the opacity goes to 1.
        CursorCaptionOpacity = 1.0;
        _ = TypeCaptionAsync(message, holdSeconds, cancellation.Token);
    }

    private async Task TypeCaptionAsync(string message, double holdSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await Typewriter.TypeAsync(message, typed => CursorCaptionText = typed, delay, cancellationToken, secondsClock);
            await delay(TimeSpan.FromSeconds(holdSeconds), cancellationToken);
            DismissCursorCaption();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A caption by the pointer; beside the mouse when the buddy is docked (the overlay is shown for the caption
    /// alone, the buddy stays in the island).</summary>
    public void ShowCaption(string message, double holdSeconds)
    {
        if (IsCursorDocked && !IsOverlayVisible)
        {
            CancelTransientHide();
            ShowOverlayOnAllScreens();
        }
        StreamCursorCaption(message, holdSeconds);
    }

    /// <summary>Text shown at once, no typewriter (the question as it is heard, many times a second).</summary>
    public void ShowLiveCaption(string text)
    {
        var heardSoFar = text.Trim();
        if (heardSoFar.Length == 0) return;
        if (IsCursorDocked && !IsOverlayVisible)
        {
            CancelTransientHide();
            ShowOverlayOnAllScreens();
        }
        captionCancellation?.Cancel();
        captionCancellation = null;
        CursorCaptionText = heardSoFar;
        CursorCaptionOpacity = 1.0;
        IsCursorCaptionVisible = true;
    }

    public void DismissCursorCaption()
    {
        captionCancellation?.Cancel();
        captionCancellation = null;
        if (!IsCursorCaptionVisible) return;
        // The overlay fades it out over 0.3 s; it is gone 0.35 s later unless a new caption started.
        CursorCaptionOpacity = 0.0;
        _ = ClearCaptionAfterFadeAsync();
    }

    private async Task ClearCaptionAfterFadeAsync()
    {
        await delay(TimeSpan.FromMilliseconds(350), CancellationToken.None);
        if (CursorCaptionOpacity != 0.0) return;
        IsCursorCaptionVisible = false;
        CursorCaptionText = "";
        HideCaptionOnlyOverlayIfDocked();
    }

    /// <summary>The caption-only overlay goes away with its caption, unless the docked buddy has since launched to point.</summary>
    private void HideCaptionOnlyOverlayIfDocked()
    {
        if (!IsCursorDocked || !IsOverlayVisible || IsDockingInProgress || DetectedElementScreenLocation is not null || CursorFlightLeg is not null) return;
        HideOverlay();
    }
}
