//
//  CompanionManager.Captions.cs
//  OpenClicky (Windows port of CompanionManager.swift: captions, notices, pointer presence,
//  the window's entry points and quitting)
//

using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Settings;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    // MARK: - Captions

    /// <summary>One quiet caption (the running-low notice, "click into a text box"), typed out like the (i).</summary>
    public void ShowQuietNotice(string message)
    {
        Overlay.ShowCaption(message, 8);
        if (!IsCursorDocked) Overlay.ScheduleTransientHideIfNeeded();
    }

    /// <summary>The island's (i): the buddy types out what OpenClicky does next to itself (beside the mouse while docked).</summary>
    public void ExplainWhatOpenClickyDoes()
    {
        Overlay.CancelTransientHide();
        Overlay.ShowCaption(IslandTexts.ExplainMessage(DictationSettings.DictationKey), 8);
        Overlay.ScheduleTransientHideIfNeeded();
    }

    // MARK: - Pointer presence

    private PointerPresence pointerPresence;
    private PointerPresenceTracker pointerPresenceTracker;
    private Timer? pointerPresenceTimer;

    /// <summary>When the pointer is on screen while it follows the mouse.</summary>
    public PointerPresence PointerPresence => pointerPresence;

    public void SetPointerPresence(PointerPresence presence)
    {
        SetProperty(ref pointerPresence, presence, nameof(PointerPresence));
        preferences.Set(PointerPresenceKey, presence.RawValue());
        Island.PointerPresence = presence;
        pointerPresenceTracker = new PointerPresenceTracker(presence);
        Overlay.IsPointerAwake = pointerPresenceTracker.IsAwake;
        StartPointerPresenceMonitor();
    }

    /// <summary>Show the pointer now; it rests away again as the presence setting says.</summary>
    public void WakePointer()
    {
        pointerPresenceTracker.Wake(MonotonicSeconds());
        if (!Overlay.IsPointerAwake) Overlay.IsPointerAwake = true;
    }

    /// <summary>Samples the mouse at 30 Hz while a presence other than "always" is chosen.</summary>
    private void StartPointerPresenceMonitor()
    {
        pointerPresenceTimer?.Dispose();
        pointerPresenceTimer = null;
        if (pointerPresence == PointerPresence.Always)
        {
            Overlay.IsPointerAwake = true;
            return;
        }
        var samplePending = 0;
        pointerPresenceTimer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref samplePending, 1) == 1) return;
            services.MainThread.Post(() =>
            {
                Interlocked.Exchange(ref samplePending, 0);
                var mouse = services.Screens.CursorPosition();
                var awake = pointerPresenceTracker.Update(new ScreenPoint(mouse.X, mouse.Y), MonotonicSeconds());
                if (awake != Overlay.IsPointerAwake) Overlay.IsPointerAwake = awake;
                // In the shake mode a shake means "come here": a docked pointer leaves the island too.
                if (pointerPresenceTracker.ShookOnLastUpdate && IsCursorDocked && VoiceState == CompanionVoiceState.Idle) SetCursorDocked(false);
            });
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1.0 / 30.0));
    }

    /// <summary>After the first few uses the pointer waits for a shake, unless the person picked a setting. Said once.</summary>
    private void HidePointerUntilShakeOnceItIsFamiliar()
    {
        var useCount = preferences.GetInt(FirstRunTourController.UseCountKey);
        var personChoseOne = preferences.GetString(PointerPresenceKey) is not null;
        if (PointerPresenceDefault.PresenceToSwitchTo(useCount, personChoseOne, pointerPresence) is not { } newPresence) return;
        _ = SwitchPointerPresenceLaterAsync(newPresence, useCount);
    }

    private async Task SwitchPointerPresenceLaterAsync(PointerPresence newPresence, int useCount)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (VoiceState != CompanionVoiceState.Idle || pointerPresence != PointerPresence.Always || preferences.GetString(PointerPresenceKey) is not null) return;
        AppLog.Write($"pointer: hides until a shake now, after {useCount} uses");
        SetPointerPresence(newPresence);
        ShowQuietNotice("from now on i'll stay out of the way. shake the mouse when you want me. you can change this in settings, clicky.");
    }

    // MARK: - The window

    /// <summary>OpenClicky's one window at a section or a settings page (a settings page wins).</summary>
    public void ShowDictationWindow(DictationSection? section = null, DictationSettingsPage? settingsPage = null)
    {
        // The island would otherwise stay open over the window, since the pointer is still on it.
        Island.DismissPanels();
        host.ShowDictationWindow(section, settingsPage);
    }

    /// <summary>The window at a settings page (General when none is named): Ctrl+,, "Settings…", the island.</summary>
    public void ShowSettings(DictationSettingsPage page = DictationSettingsPage.General) => ShowDictationWindow(settingsPage: page);

    /// <summary>Opens the window at General and plays the Settings tour (General › Startup › Settings tour).</summary>
    public void StartSettingsTour()
    {
        InterruptCurrentAnswer(includingAgentWork: false);
        Island.DismissPanels();
        host.StartSettingsTour();
    }

    // MARK: - Quitting

    private bool isQuitting;

    /// <summary>
    /// Quits with a goodbye: a short chime and a line typed into the island that tells how to bring
    /// OpenClicky back (no voice). A second quit while the goodbye shows quits at once.
    /// </summary>
    public void QuitWithFarewell()
    {
        if (isQuitting)
        {
            host.Quit();
            return;
        }
        isQuitting = true;
        var farewell = Farewell.Random();
        Island.CloseTextComposer();
        Island.ShowGoodbye(farewell.Text);
        StopSystemVoice();
        services.Earcons.Play(DictationEarcon.Goodbye);
        // Long enough to type the line (30 ms a character) and read it, never long enough to feel stuck.
        var typingSeconds = farewell.Text.Length * 0.03;
        var lingerSeconds = Math.Min(Math.Max(typingSeconds + 1.6, 2.6), 4.5);
        _ = QuitLaterAsync(lingerSeconds);
    }

    private async Task QuitLaterAsync(double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        host.Quit();
    }
}
