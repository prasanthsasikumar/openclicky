// Ports Dictation/UI/SettingsTour.swift: a one-minute tour of Settings. Clicky flies to the key
// settings in the real window, "clicks" each page in the sidebar (a ripple, then the page opens),
// and says what it is for in recorded narration, so it sounds the same with no key and no network.
// It plays once, the first time the window opens at General after the welcome tour, and General ›
// Startup › Settings tour plays it again. Esc, closing the window or a new question stops it.
//
// Rows tell SettingsTourTargets where they are on screen (the Windows rows register a provider of
// their screen rectangle); the tour asks the page to scroll a row into view first. The pointer's
// flight and the narration are the integration's: ISettingsTourPointer (the overlay) and
// ISettingsTourNarrator (RecordedVoice).

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Settings;

/// <summary>
/// The overlay pointer, as the tour drives it. Rectangles are in screen DIPs of the virtual desktop
/// (ScreenGeometry's convention: origin top-left of the primary monitor, y down).
/// Wired (CompanionManager.SettingsTourPointer, RecordedVoiceTourNarrator): CompanionManager.beginSettingsTourPointing / pointForSettingsTour(at:bubble:) /
/// showPointerClick / endSettingsTourPointing → the Overlay port.
/// </summary>
public interface ISettingsTourPointer
{
    /// <summary>The tour starts: the pointer comes out (released from the island) and stays on targets.</summary>
    void BeginTour();

    /// <summary>Flies to <see cref="SettingsTourTargets.PointerSpot"/> of <paramref name="target"/> with a bubble saying <paramref name="caption"/>.</summary>
    Task FlyTo(ScreenRect target, string caption);

    /// <summary>A click ripple where the pointer is (it is already over <paramref name="target"/>).</summary>
    Task Click(ScreenRect target);

    /// <summary>The tour ended or was stopped: the pointer goes back to how it was.</summary>
    void EndTour();
}

/// <summary>
/// The recorded narration. Wired (CompanionManager.SettingsTourPointer, RecordedVoiceTourNarrator): RecordedVoice.play(line) — the mp3s in
/// macos/OpenClicky/OpenClicky/RecordedVoice (settings-tour-*.mp3) shipped with the Windows app.
/// </summary>
public interface ISettingsTourNarrator
{
    /// <summary>Plays the line to its end. False when there is no recording for it (the tour then waits a moment instead).</summary>
    Task<bool> SayAsync(string line, CancellationToken cancellationToken);

    void Stop();
}

/// <summary>Where the tour's targets sit on screen, as the rows report it, and the row a page should scroll to.</summary>
public sealed class SettingsTourTargets
{
    public static SettingsTourTargets Shared { get; } = new();

    private readonly object registryLock = new();
    private readonly Dictionary<string, Func<ScreenRect?>> providers = new(StringComparer.Ordinal);

    /// <summary>The row the visible page should scroll into view (the tour asks before pointing).</summary>
    public event Action<string>? ScrollRequested;

    /// <summary>
    /// A row says where it is: <paramref name="screenRect"/> answers its rectangle on screen now, or
    /// null while it isn't laid out or is scrolled out of the window. Dispose to unregister.
    /// </summary>
    public IDisposable Register(string targetId, Func<ScreenRect?> screenRect)
    {
        lock (registryLock) providers[targetId] = screenRect;
        return new Registration(() =>
        {
            lock (registryLock)
            {
                if (providers.TryGetValue(targetId, out var current) && current == screenRect) providers.Remove(targetId);
            }
        });
    }

    /// <summary>The target's rectangle on screen, or null when it isn't laid out or has no size.</summary>
    public ScreenRect? ScreenRectFor(string targetId)
    {
        Func<ScreenRect?>? provider;
        lock (registryLock) providers.TryGetValue(targetId, out provider);
        var rect = provider?.Invoke();
        return rect is { IsEmpty: false } ? rect : null;
    }

    public void RequestScroll(string targetId) => ScrollRequested?.Invoke(targetId);

    /// <summary>"sidebar.page.voice", "sidebar.section.words".</summary>
    public static string SidebarTargetId(DictationDestination destination) =>
        destination.Page is { } page ? $"sidebar.page.{page.RawValue()}" : $"sidebar.section.{destination.Section!.Value.RawValue()}";

    /// <summary>Where on a row the pointer rests: a little in from its left edge (30 %, at most 120), at mid height.</summary>
    public static ScreenPoint PointerSpot(ScreenRect target) =>
        new(target.X + Math.Min(target.Width * 0.3, 120), target.MidY);

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;

        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }
}

public sealed record SettingsTourStep(DictationDestination Destination, string? TargetId, string Bubble, string Line)
{
    /// <summary>The page's name in the sidebar, for the bubble while the pointer goes to "click" it.</summary>
    public string DestinationTitle => Destination.Title;

    /// <summary>About a minute in all: what someone needs to find their way, in sidebar order where it can be.</summary>
    public static IReadOnlyList<SettingsTourStep> All { get; } =
    [
        new(DictationDestination.Of(DictationSettingsPage.General), "general.region", "your region (esc stops the tour)", "settings-tour-intro"),
        new(DictationDestination.Of(DictationSettingsPage.Dictation), "keyboard.dictationKey", "the key you hold", "settings-tour-dictation"),
        new(DictationDestination.Of(DictationSettingsPage.Dictation), "dictation.language", "your language", "settings-tour-language"),
        new(DictationDestination.Of(DictationSettingsPage.Voice), "clicky.speechVoice", "how i sound", "settings-tour-voice"),
        new(DictationDestination.Of(DictationSettingsPage.Clicky), "clicky.pointerPresence", "when i show up", "settings-tour-pointer"),
        new(DictationDestination.Of(DictationSection.Words), null, "everything you said", "settings-tour-words"),
        new(DictationDestination.Of(DictationSettingsPage.Keyboard), "keyboard.talk", "every shortcut", "settings-tour-shortcuts"),
        new(DictationDestination.Of(DictationSettingsPage.Keys), "account.keys", "your own keys", "settings-tour-keys"),
        new(DictationDestination.Of(DictationSettingsPage.General), "general.settingsTour", "watch it again here", "settings-tour-done"),
    ];
}

/// <summary>Plays the tour in the window whose pages the model switches.</summary>
public sealed class SettingsTourController
{
    /// <summary>Set once the tour has played (or been stopped), so it doesn't start on its own again.</summary>
    public const string HasPlayedKey = "openclicky.settingsTourPlayed";

    private readonly Preferences preferences;
    private readonly ISettingsTourPointer pointer;
    private readonly ISettingsTourNarrator narrator;
    private readonly SettingsTourTargets targets;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private CancellationTokenSource? runCancellation;

    public SettingsTourController(Preferences preferences, ISettingsTourPointer pointer, ISettingsTourNarrator narrator,
        SettingsTourTargets? targets = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.preferences = preferences;
        this.pointer = pointer;
        this.narrator = narrator;
        this.targets = targets ?? SettingsTourTargets.Shared;
        this.delay = delay ?? Task.Delay;
    }

    public bool IsRunning { get; private set; }

    public bool HasPlayed => preferences.GetBool(HasPlayedKey);

    /// <summary>Raised when the tour finishes or is stopped.</summary>
    public event Action? Finished;

    /// <summary>The tour plays once on its own: the first time the window opens at General, after the welcome tour.</summary>
    public bool ShouldStartOnItsOwn(DictationWindowModel model, bool welcomeTourIsDone) =>
        !HasPlayed && !IsRunning && welcomeTourIsDone && model.Destination == DictationDestination.Of(DictationSettingsPage.General) && !model.IsSearching;

    /// <summary>Starts the tour; the returned task ends when it finishes or is stopped. Nothing happens while one runs.</summary>
    public Task Start(DictationWindowModel model)
    {
        if (IsRunning) return Task.CompletedTask;
        preferences.Set(HasPlayedKey, true);
        IsRunning = true;
        AppLog.Write("settings tour: started");
        var cancellation = new CancellationTokenSource();
        runCancellation = cancellation;
        return RunAndFinishAsync(model, cancellation.Token);
    }

    /// <summary>Esc, the window closing, or a new question.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        runCancellation?.Cancel();
        narrator.Stop();
    }

    private async Task RunAndFinishAsync(DictationWindowModel model, CancellationToken cancellationToken)
    {
        var stopped = false;
        try
        {
            await RunAsync(model, cancellationToken);
            stopped = cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        finally
        {
            IsRunning = false;
            runCancellation = null;
            pointer.EndTour();
            AppLog.Write(stopped ? "settings tour: stopped" : "settings tour: finished");
            Finished?.Invoke();
        }
    }

    private async Task RunAsync(DictationWindowModel model, CancellationToken cancellationToken)
    {
        pointer.BeginTour();
        // The window has just opened: let it lay out before the first flight.
        await PauseAsync(0.8, cancellationToken);
        foreach (var step in SettingsTourStep.All)
        {
            if (model.Destination != step.Destination || model.IsSearching)
            {
                var sidebarId = SettingsTourTargets.SidebarTargetId(step.Destination);
                if (targets.ScreenRectFor(sidebarId) is { } sidebarRect)
                {
                    Observe(pointer.FlyTo(sidebarRect, step.DestinationTitle));
                    await PauseAsync(1.0, cancellationToken);
                    await pointer.Click(sidebarRect);
                    await PauseAsync(0.25, cancellationToken);
                }
                model.Select(step.Destination);
                await PauseAsync(0.6, cancellationToken);
            }
            var targetId = step.TargetId ?? SettingsTourTargets.SidebarTargetId(step.Destination);
            if (step.TargetId is not null)
            {
                targets.RequestScroll(targetId);
                await PauseAsync(0.5, cancellationToken);
            }
            if (targets.ScreenRectFor(targetId) is { } targetRect)
            {
                Observe(pointer.FlyTo(targetRect, step.Bubble));
            }
            else
            {
                AppLog.Write($"settings tour: {targetId} not on screen, said without pointing");
            }
            var played = await narrator.SayAsync(step.Line, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // A missing recording reports nothing: give the bubble a moment instead.
            if (!played) await PauseAsync(2.5, cancellationToken);
            await PauseAsync(0.35, cancellationToken);
        }
    }

    private Task PauseAsync(double seconds, CancellationToken cancellationToken) =>
        delay(TimeSpan.FromSeconds(seconds), cancellationToken);

    private static void Observe(Task flight) =>
        flight.ContinueWith(task => AppLog.Write($"settings tour: pointer flight failed: {task.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
}
