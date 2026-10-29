//
//  CompanionManager.Pointing.cs
//  OpenClicky (Windows port of CompanionManager.swift: pointing)
//
//  One shared path for every point: the ask lane's [POINT] tag, the onboarding and tour demos,
//  the Realtime point_at tool and the Settings tour. A screenshot point is mapped onto the screen
//  (CompanionScreenCapture.GlobalPoint: clamp, scale, offset — Windows needs no y-flip), dropped when
//  it is on a system prompt (SystemPromptGuard), and handed to the overlay.
//

using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Settings;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>A point in a screenshot's pixels (origin top-left) → global physical pixels on the captured monitor.
    /// Out-of-range coordinates are clamped to the screenshot; NaN counts as the origin.</summary>
    public static ScreenPoint ScreenLocation(ScreenPoint screenshotPoint, CompanionScreenCapture capture)
    {
        var width = Math.Max(capture.ScreenshotWidthInPixels, 1);
        var height = Math.Max(capture.ScreenshotHeightInPixels, 1);
        var clampedX = double.IsNaN(screenshotPoint.X) ? 0 : Math.Max(0, Math.Min(screenshotPoint.X, width));
        var clampedY = double.IsNaN(screenshotPoint.Y) ? 0 : Math.Max(0, Math.Min(screenshotPoint.Y, height));
        var frame = capture.DisplayFrame;
        return new ScreenPoint(frame.Left + clampedX * frame.Width / width, frame.Top + clampedY * frame.Height / height);
    }

    private static ScreenRect FrameOf(CompanionScreenCapture capture) =>
        new(capture.DisplayFrame.Left, capture.DisplayFrame.Top, capture.DisplayFrame.Width, capture.DisplayFrame.Height);

    /// <summary>The prompt windows on screen now (consent, credentials, the lock screen), for the guard.</summary>
    private IEnumerable<PromptWindow> OnScreenPromptWindows() =>
        services.PromptWindows?.Invoke() ?? services.WindowList.VisibleWindows()
            .Select(window => new PromptWindow(window.ProcessName ?? "", new ScreenRect(window.Bounds.Left, window.Bounds.Top, window.Bounds.Width, window.Bounds.Height), window.ClassName));

    /// <summary>
    /// Fly the buddy to <paramref name="screenshotPoint"/> on the captured monitor with <paramref name="label"/> in
    /// its bubble. Safe while a reply is being spoken; a second call while pointing retargets it.
    /// </summary>
    public void PointAt(ScreenPoint screenshotPoint, string? label, CompanionScreenCapture capture)
    {
        var location = ScreenLocation(screenshotPoint, capture);
        if (!PointAt(location, FrameOf(capture), label)) return;
        // A guide checks the click that finishes this step against it (the Teacher's label stops are not steps).
        guidedStepPointedLocation = location;
        guidedStepPointedScale = capture.DisplayScale;
        AppLog.Write($"point: ({screenshotPoint.X:0}, {screenshotPoint.Y:0}) → \"{label?.Trim() ?? "element"}\"");
    }

    /// <summary>Fly the buddy to a place in physical screen pixels (the Teacher's labels, which it placed itself, have
    /// no screenshot point). False when the place is on a system prompt.</summary>
    public bool PointAt(ScreenPoint location, ScreenRect displayFrame, string? label)
    {
        // Granting a permission is the person's choice, never a step: no pointing at Windows' prompts.
        if (SystemPromptGuard.IsOnSystemPrompt(location, OnScreenPromptWindows))
        {
            AppLog.Write("point: not shown, it was on a windows permission prompt");
            return false;
        }
        // The spinner (processing) hides the triangle, so the flight would be invisible.
        if (VoiceState == CompanionVoiceState.Processing) VoiceState = CompanionVoiceState.Idle;
        Overlay.PointAt(location, displayFrame, label);
        return true;
    }

    /// <summary>Moves the pointer for the ask lane's [POINT:…] tag, on the screen it names (the cursor's when none).</summary>
    private void PointAtAnswerTag(string pointTag, IReadOnlyList<CompanionScreenCapture> screenCaptures)
    {
        var parseResult = PointTagParser.ParsePointingCoordinates(pointTag);
        if (parseResult.Coordinate is not { } pointCoordinate) return;
        CompanionScreenCapture? targetScreenCapture = parseResult.ScreenNumber is { } screenNumber && screenNumber >= 1 && screenNumber <= screenCaptures.Count
            ? screenCaptures[screenNumber - 1]
            : screenCaptures.FirstOrDefault(capture => capture.IsCursorScreen);
        if (targetScreenCapture is null) return;
        PointAt(pointCoordinate, parseResult.ElementLabel, targetScreenCapture);
    }

    // MARK: - The pointing demo (onboarding and the tour's "watch this")

    /// <summary>
    /// A screenshot of the cursor's screen only, Claude picks one specific thing and comments on it,
    /// and the buddy flies there with that comment in its bubble. Null when there was nothing to point at.
    /// </summary>
    private async Task<PointingParseResult?> PointAtSomethingOnScreenForDemoAsync(bool showingTheOverlayIfHidden)
    {
        var cursorScreenCapture = await services.ScreenCapture.CaptureCursorScreenAsync();
        if (cursorScreenCapture is null) return null;
        var (fullResponseText, _, _) = await claudeAPI.AnalyzeImageStreamingAsync(
            [ImageLabeledWithItsPixelSize(cursorScreenCapture)],
            AskLanePrompts.OnboardingDemoSystemPrompt,
            null,
            AskLanePrompts.OnboardingDemoUserPrompt,
            null,
            _ => { });
        var parseResult = PointTagParser.ParsePointingCoordinates(fullResponseText);
        if (parseResult.Coordinate is not { } pointCoordinate) return null;
        if (showingTheOverlayIfHidden && !Overlay.IsOverlayVisible) Overlay.ShowOverlayOnAllScreens();
        Overlay.PointAt(ScreenLocation(pointCoordinate, cursorScreenCapture), FrameOf(cursorScreenCapture), parseResult.SpokenText);
        return parseResult;
    }

    /// <summary>
    /// The Mac played an intro video here; OpenClicky runs the pointing demo once after the welcome
    /// bubble, then types the "press ctrl + win" prompt (kept under the Mac's name).
    /// </summary>
    public void SetupOnboardingVideo()
    {
        PerformOnboardingDemoInteraction();
        _ = StartOnboardingPromptLaterAsync();
    }

    private async Task StartOnboardingPromptLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (Overlay.IsCursorCaptionVisible) return;
        Overlay.StreamCursorCaption($"press {TalkKeysCaption} and introduce yourself");
    }

    /// <summary>Asks Claude for something to point at during onboarding; never over a voice answer in progress.</summary>
    public void PerformOnboardingDemoInteraction()
    {
        if (VoiceState is not (CompanionVoiceState.Idle or CompanionVoiceState.Responding)) return;
        _ = RunOnboardingDemoAsync();
    }

    private async Task RunOnboardingDemoAsync()
    {
        try
        {
            var parseResult = await PointAtSomethingOnScreenForDemoAsync(showingTheOverlayIfHidden: false);
            AppLog.Write(parseResult is null ? "onboarding demo: nothing to point at" : $"onboarding demo: pointing at \"{parseResult.ElementLabel ?? "element"}\"");
        }
        catch (Exception error)
        {
            AppLog.Write($"onboarding demo: failed ({error.GetType().Name})");
        }
    }

    // MARK: - The Settings tour's pointer

    /// <summary>The Settings tour drives the overlay through this (rectangles in virtual-desktop DIPs).</summary>
    public ISettingsTourPointer SettingsTourPointer => settingsTourPointer ??= new TourPointer(this);
    private ISettingsTourPointer? settingsTourPointer;

    private sealed class TourPointer(CompanionManager companion) : ISettingsTourPointer
    {
        /// <summary>A flight to the next row: long enough to land before the narration moves on.</summary>
        private static readonly TimeSpan FlightTime = TimeSpan.FromSeconds(1.1);

        public void BeginTour() => companion.Overlay.BeginSettingsTourPointing();

        public async Task FlyTo(ScreenRect target, string caption)
        {
            var spot = SettingsTourTargets.PointerSpot(target);
            companion.Overlay.PointForSettingsTour(companion.DipsToPixels(spot), caption);
            await Task.Delay(FlightTime);
        }

        public async Task Click(ScreenRect target)
        {
            companion.Overlay.ShowPointerClick();
            await Task.Delay(TimeSpan.FromSeconds(0.4));
        }

        public void EndTour() => companion.Overlay.EndSettingsTourPointing();
    }

    /// <summary>A point in virtual-desktop DIPs (each monitor's DIP bounds) → physical pixels.</summary>
    private ScreenPoint DipsToPixels(ScreenPoint pointInDips)
    {
        foreach (var screen in services.Screens.All())
        {
            var dips = screen.BoundsInDips;
            if (pointInDips.X >= dips.X && pointInDips.X < dips.Right && pointInDips.Y >= dips.Y && pointInDips.Y < dips.Bottom)
            {
                return new ScreenPoint(screen.Bounds.Left + (pointInDips.X - dips.X) * screen.Scale, screen.Bounds.Top + (pointInDips.Y - dips.Y) * screen.Scale);
            }
        }
        var primary = services.Screens.Primary();
        return primary is null ? pointInDips : new ScreenPoint(pointInDips.X * primary.Scale, pointInDips.Y * primary.Scale);
    }

    // MARK: - The welcome tour's pointing

    /// <summary>The tour's first moment: the pointer flies out of the island to the mouse and says hello.</summary>
    public void IntroducePointerForTour()
    {
        if (!Overlay.IsOverlayVisible)
        {
            Overlay.CursorLaunchOriginScreenLocation = Overlay.DockTarget().DockPoint;
            Overlay.ShowOverlayOnAllScreens();
        }
        _ = SayHelloLaterAsync();
    }

    private async Task SayHelloLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1.6));
        Overlay.StreamCursorCaption("hi! i’m clicky. i follow your pointer, and point at things when you ask.", holdSeconds: 6);
        // "Hi! I'm Clicky. Let me show you three quick things.", recorded and bundled.
        RecordedVoice.Play(Speech.RecordedVoice.Line.TourHello);
    }

    /// <summary>Flies the pointer to the tour card's main button with a short bubble, for someone who has not moved.</summary>
    public void PointAtTourPrimaryButton(string bubbleText)
    {
        if (host.TourPrimaryButtonLocation() is not { } target) return;
        if (!Overlay.IsOverlayVisible) Overlay.ShowOverlayOnAllScreens();
        Overlay.PointAt(target.Location, target.DisplayFrame, bubbleText);
    }

    /// <summary>
    /// The tour's "watch this" before asking: one thing on screen pointed at and a few words said out
    /// loud. <paramref name="onFinished"/> runs once the pointer and the voice are done (or it could not run).
    /// </summary>
    public void PerformTourDemo(Action onFinished) => _ = RunTourDemoAsync(onFinished);

    private async Task RunTourDemoAsync(Action onFinished)
    {
        try
        {
            if (await PointAtSomethingOnScreenForDemoAsync(showingTheOverlayIfHidden: true) is { } parseResult)
            {
                AppLog.Write($"tour demo: pointing at {parseResult.ElementLabel ?? "something"}");
                // Said in the answering voice; quietly skipped when no voice route is set up.
                try
                {
                    var audio = await answerVoicePlayer.FetchAudioAsync(parseResult.SpokenText);
                    await answerVoicePlayer.PlayToCompletionAsync(audio);
                }
                catch (Exception error)
                {
                    AppLog.Write($"tour demo: no voice ({error.GetType().Name})");
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Write($"tour demo: could not run ({error.GetType().Name})");
        }
        onFinished();
    }
}
