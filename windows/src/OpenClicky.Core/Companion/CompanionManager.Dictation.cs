//
//  CompanionManager.Dictation.cs
//  OpenClicky (Windows port of CompanionManager.swift's dictation wiring)
//
//  Dictation: the orb, the engines, the formatting, history, owned here so the shortcut hook, the
//  Realtime microphone and the window all meet in one place. The take controller itself is
//  Core/Dictation/DictationTakeController; this file only connects it.
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Shortcuts;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    private DictationTakeController? dictationTakeController;

    /// <summary>The take controller (built at Start; null before).</summary>
    public DictationTakeController? DictationTakeController => dictationTakeController;

    /// <summary>A take with words finished (raised on the UI thread): the welcome tour and the window watch it.</summary>
    public event Action<TakeRecord>? TakeFinished;

    /// <summary>The take controller's state changed (Idle / Starting / Listening / Finishing).</summary>
    public event Action<DictationTakeController.TakeState>? TakeStateChanged;

    public bool IsTakeInProgress => dictationTakeController?.IsTakeInProgress == true;

    /// <summary>Starts the orb's controller and keeps the hook's dictation key in step with Settings.</summary>
    private void StartDictation()
    {
        var controller = services.CreateDictationTakeController(new OrbModelDictationAdapter(Orb));
        dictationTakeController = controller;
        controller.SpaceStore.StartWatching();
        services.Shortcuts.DictationKey = DictationSettings.DictationKey;
        UpdateRightAltAdoption();
        DictationSettings.PropertyChanged += OnDictationSettingsChanged;
        Orb.Clicked += controller.OrbClicked;
        Orb.Hint = controller.IdleHint;

        controller.BeforeMicrophoneOpens = async () =>
        {
            IsDictatingToFrontApp = true;
            // The Realtime engine's input must be gone first, or the dictation engine records silence.
            // Only when a session is open: releasing an idle engine would just delay the first words.
            if (RealtimeVoiceClient.IsConnected) await RealtimeVoiceClient.ReleaseMicrophoneForDictationAsync();
        };
        controller.AfterMicrophoneCloses = () =>
        {
            IsDictatingToFrontApp = false;
            RealtimeVoiceClient.ResumeListeningAfterDictation();
        };
        controller.OnNoTextBoxAtPress = message =>
        {
            // The pointer may be resting out of sight ("after a shake"): it is the one speaking.
            AppLog.Write($"pointer: asked for a text box (overlay {(Overlay.IsOverlayVisible ? "shown" : "hidden")}, presence {pointerPresence.RawValue()}, awake {Overlay.IsPointerAwake}, voice {VoiceState})");
            WakePointer();
            ShowQuietNotice(message);
        };
        controller.OnTakeFinished = take =>
        {
            // A dictation that produced words counts as a use.
            if (take.Status != TakeStatus.Failed && take.DisplayText.Length > 0) NoteUse();
            TakeFinished?.Invoke(take);
        };
        controller.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(DictationTakeController.State)) TakeStateChanged?.Invoke(controller.State);
        };

        // The microphone chosen in Settings is the one every way in hears with: dictation, the ask
        // shortcut and Realtime (the ask used the default input and heard nothing through an unplugged jack).
        DictationSettings.PreferredMicrophoneUIDDidChange = ApplyPreferredMicrophone;
        ApplyPreferredMicrophone(DictationSettings.PreferredMicrophoneUID);
        Island.DictationEngineName = controller.EngineDisplayName;
        RefreshEngineWarning();
    }

    private void ApplyPreferredMicrophone(string? microphoneId)
    {
        if (dictationTakeController is not null) dictationTakeController.Capture.PreferredMicrophoneId = microphoneId;
        BuddyDictationManager.PreferredMicrophoneId = microphoneId;
        RealtimeVoiceClient.PreferredMicrophoneId = microphoneId;
    }

    private void OnDictationSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => services.MainThread.Post(() =>
    {
        services.Shortcuts.DictationKey = DictationSettings.DictationKey;
        UpdateRightAltAdoption();
        Island.DictationKey = DictationSettings.DictationKey;
        if (dictationTakeController is { } controller)
        {
            Orb.Hint = controller.IdleHint;
            Island.DictationEngineName = controller.EngineDisplayName;
        }
        RefreshEngineWarning();
    });

    /// <summary>The island's engine warning and the voice pill's call to action (NotchVoiceDestination).</summary>
    private void RefreshEngineWarning()
    {
        var engine = DictationSettings.Engine;
        var reason = DictationEngineResolver.UnavailableReason(engine);
        Island.EngineUnavailableReason = reason;
        Island.EngineBlockedPillLabel = IslandVoiceDestination.BlockedPillLabel(engine, reason);
        Island.EngineWarning = IslandEngineWarning.Make(engine.DisplayName(), reason, IslandVoiceDestination.ButtonTitle(engine, reason));
    }

    private void StopDictation()
    {
        DictationSettings.PropertyChanged -= OnDictationSettingsChanged;
        if (dictationTakeController is { } controller)
        {
            if (controller.IsTakeInProgress) controller.CancelTake("cancelled");
            controller.Dispose();
        }
        dictationTakeController = null;
    }

    /// <summary>The dictation events of the shortcut recogniser go to the take controller.</summary>
    private void HandleDictationShortcut(CompanionShortcutEvent shortcutEvent) => dictationTakeController?.Handle(shortcutEvent);

    /// <summary>The dictate step's microphone change: a take still listening keeps the microphone it opened; end it.</summary>
    public void CancelTakeInProgress()
    {
        if (dictationTakeController is { IsTakeInProgress: true } controller) controller.CancelTake("cancelled");
    }

    /// <summary>The welcome tour's dictate step keeps takes on the island.</summary>
    public void SetHoldsTakesOnTheIsland(bool holds)
    {
        if (dictationTakeController is not null) dictationTakeController.HoldsTakesOnTheIsland = holds;
    }
}

/// <summary>What the Your words page asks of the take controller.</summary>
public sealed class DictationHistoryActions : IDictationHistoryActions
{
    private readonly Func<DictationTakeController?> controller;
    private readonly IMainThreadPoster post;

    public interface IMainThreadPoster
    {
        void Post(Action action);
    }

    public DictationHistoryActions(CompanionManager companion)
    {
        controller = () => companion.DictationTakeController;
        post = new Poster(companion.Services.MainThread);
        companion.TakeFinished += take => TakeFinished?.Invoke(take);
        var subscribed = false;
        void Subscribe()
        {
            if (subscribed || controller() is not { } current) return;
            subscribed = true;
            current.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(DictationTakeController.HistoryVersion)) post.Post(() => HistoryChanged?.Invoke());
            };
        }
        Subscribe();
        companion.PropertyChanged += (_, _) => Subscribe();
        companion.TakeFinished += _ => Subscribe();
        subscribeLater = Subscribe;
    }

    private readonly Action subscribeLater;

    private sealed class Poster(Platform.IMainThread mainThread) : IMainThreadPoster
    {
        public void Post(Action action) => mainThread.Post(action);
    }

    public event Action? HistoryChanged;
    public event Action<TakeRecord>? TakeFinished;

    public bool CanRetry(Guid takeId) => controller()?.CanRetry(takeId) == true;

    public Task RetryAsync(Guid takeId) => controller()?.RetryAsync(takeId) ?? Task.CompletedTask;

    public IReadOnlyList<TakeRecord> HistoryMatches(string question) => controller()?.HistoryMatches(question) ?? [];

    public Task<string> AskHistoryAsync(string question) =>
        controller()?.AskHistoryAsync(question) ?? Task.FromResult("dictation isn't running yet.");

    public void PasteLast() => controller()?.PasteLast();

    public void HistoryDidChange()
    {
        subscribeLater();
        if (controller() is { } current) current.HistoryDidChange();
        else HistoryChanged?.Invoke();
    }
}
