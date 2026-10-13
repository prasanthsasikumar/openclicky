// Ports BuddyDictationManager.swift: the microphone-and-engine side of push to talk, shared by
// dictation (one manager per take controller, behind the DictationCapturing seam) and the
// companion's talk lane. Captures microphone audio (IAudioCapture), routes it into the active
// transcription provider, and hands the partial and final transcripts back to the caller's draft
// callbacks.
//
// Threading: like the Mac's @MainActor class, every public member is called on the main thread,
// and everything that arrives from elsewhere (session callbacks, audio frames, timers) is posted
// back to it through IMainThread. Every session callback carries the session's identity, so a late
// partial, final or error from one take never reaches the next; a start whose microphone fails
// tears the session down.

using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

/// <summary>The real IDictationCapturing (Core/Dictation's seam): the take controller drives one of these.</summary>
public sealed partial class BuddyDictationManager : ObservableObject, IDictationCapturing, IDisposable
{
    public static readonly TimeSpan DefaultFinalTranscriptFallbackDelay = TimeSpan.FromSeconds(2.4);
    public const string MicrophonePermissionRequiredSentence = "microphone permission is required for push to talk.";
    public const string SpeechRecognitionPermissionRequiredSentence = "speech recognition permission is required for push to talk.";

    [ObservableProperty] private bool isRecordingFromKeyboardShortcut;
    [ObservableProperty] private bool isKeyboardShortcutSessionActiveOrFinalizing;
    [ObservableProperty] private bool isFinalizingTranscript;
    [ObservableProperty] private bool isPreparingToRecord;
    [ObservableProperty] private double currentAudioPowerLevel;
    [ObservableProperty] private string transcriptionProviderDisplayName = "";
    [ObservableProperty] private string? lastErrorMessage;

    public event Action<double>? AudioPowerLevelChanged;
    public event Action<string?>? ErrorMessageChanged;
    public event Action<bool>? SessionActivityChanged;

    partial void OnCurrentAudioPowerLevelChanged(double value) => AudioPowerLevelChanged?.Invoke(value);
    partial void OnLastErrorMessageChanged(string? value) => ErrorMessageChanged?.Invoke(value);
    partial void OnIsKeyboardShortcutSessionActiveOrFinalizingChanged(bool value) => SessionActivityChanged?.Invoke(value);
    partial void OnIsPreparingToRecordChanged(bool value) => OnPropertyChanged(nameof(IsDictationInProgress));
    partial void OnIsRecordingFromKeyboardShortcutChanged(bool value) => OnPropertyChanged(nameof(IsDictationInProgress));
    partial void OnIsFinalizingTranscriptChanged(bool value) => OnPropertyChanged(nameof(IsDictationInProgress));

    public bool IsDictationInProgress => IsPreparingToRecord || IsRecordingFromKeyboardShortcut || IsFinalizingTranscript;

    private IBuddyTranscriptionProvider transcriptionProvider;
    private readonly IMainThread mainThread;
    private readonly Func<IAudioCapture> makeAudioCapture;
    private readonly IMicrophonePermission? microphonePermission;
    private IAudioCapture? audioCapture;
    private Action<AudioFrame>? installedFrameHandler;
    private IBuddyStreamingTranscriptionSession? activeTranscriptionSession;

    /// <summary>
    /// Which session the callbacks may speak for. Each session gets a new identity before it is
    /// opened, and it is cleared when the session ends, so a late partial, final or error from an
    /// old session (a slow upload, a socket closing) can never reach the take that came after.
    /// </summary>
    public Guid? ActiveSessionIdentity { get; private set; }
    /// <summary>True from a started push-to-talk session until it is finished, cancelled or reset.</summary>
    private bool isPushToTalkSessionOpen;
    private Action<string>? updateDraftTextCallback;
    private Action<string>? submitDraftTextCallback;
    private string draftTextBeforeCurrentDictation = "";
    private string latestRecognizedText = "";
    private bool shouldAutomaticallySubmitFinalDraft;
    private bool hasFinishedCurrentDictationSession;
    private CancellationTokenSource? finalizeFallback;
    private Guid pendingStartRequestIdentifier = Guid.NewGuid();
    private IReadOnlyList<string> contextualKeyterms = [];

    /// <summary>A manager around one provider; the dictation take controller picks its engine this way.</summary>
    public BuddyDictationManager(IBuddyTranscriptionProvider transcriptionProvider, IMainThread mainThread,
                                 Func<IAudioCapture> makeAudioCapture, IMicrophonePermission? microphonePermission = null)
    {
        this.transcriptionProvider = transcriptionProvider;
        this.mainThread = mainThread;
        this.makeAudioCapture = makeAudioCapture;
        this.microphonePermission = microphonePermission;
        transcriptionProviderDisplayName = transcriptionProvider.DisplayName;
    }

    /// <summary>Swaps the engine between takes (Settings → Dictation). Ignored while a take is in progress.</summary>
    public void ReplaceTranscriptionProvider(IBuddyTranscriptionProvider provider)
    {
        if (IsDictationInProgress) return;
        transcriptionProvider = provider;
        TranscriptionProviderDisplayName = provider.DisplayName;
    }

    public IBuddyTranscriptionProvider TranscriptionProvider => transcriptionProvider;

    public string? PreferredMicrophoneId { get; set; }

    public Action<AudioFrame>? AudioRetentionSink { get; set; }

    public void UpdateContextualKeyterms(IReadOnlyList<string> contextualKeyterms) => this.contextualKeyterms = contextualKeyterms;

    public async Task StartPushToTalkFromKeyboardShortcutAsync(string currentDraftText, Action<string> updateDraftText, Action<string> submitDraftText,
                                                               CancellationToken cancellationToken = default)
    {
        if (IsDictationInProgress) return;

        var startRequestIdentifier = Guid.NewGuid();
        pendingStartRequestIdentifier = startRequestIdentifier;

        LastErrorMessage = null;
        IsPreparingToRecord = true;

        if (!CheckMicrophoneAndSpeechPermissions())
        {
            AppLog.Write($"dictation: not started ({transcriptionProvider.DisplayName}): {LastErrorMessage ?? "permissions missing or denied"}");
            IsPreparingToRecord = false;
            return;
        }
        if (cancellationToken.IsCancellationRequested || pendingStartRequestIdentifier != startRequestIdentifier)
        {
            IsPreparingToRecord = false;
            return;
        }

        draftTextBeforeCurrentDictation = currentDraftText;
        latestRecognizedText = "";
        updateDraftTextCallback = updateDraftText;
        submitDraftTextCallback = submitDraftText;
        isPushToTalkSessionOpen = true;
        // With words already in the caller's draft the dictation is added to them, not sent.
        shouldAutomaticallySubmitFinalDraft = currentDraftText.Trim().Length == 0;
        hasFinishedCurrentDictationSession = false;
        IsFinalizingTranscript = false;
        IsRecordingFromKeyboardShortcut = true;
        IsKeyboardShortcutSessionActiveOrFinalizing = true;
        CurrentAudioPowerLevel = 0;

        try
        {
            await StartRecognitionSessionAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                // The shortcut was released while the session was starting.
                TearDownRecordingAndSession();
                ResetSessionState();
                return;
            }
            IsPreparingToRecord = false;
        }
        catch (Exception error)
        {
            IsPreparingToRecord = false;
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                TearDownRecordingAndSession();
                ResetSessionState();
                return;
            }
            LastErrorMessage = UserFacingErrorMessage(error, "couldn't start voice input. try again.");
            AppLog.Write($"dictation: failed to start recognition session ({transcriptionProvider.DisplayName}): {error.GetType().Name}: {error.Message}");
            // The provider may have opened its session before the microphone refused to start: both
            // go, as on a cancelled start, so nothing keeps feeding (or billing) a session no take
            // is listening to.
            TearDownRecordingAndSession();
            ResetSessionState();
        }
    }

    public void StopPushToTalkFromKeyboardShortcut()
    {
        pendingStartRequestIdentifier = Guid.NewGuid();

        if (!isPushToTalkSessionOpen)
        {
            IsPreparingToRecord = false;
            return;
        }
        if (IsFinalizingTranscript) return;

        IsRecordingFromKeyboardShortcut = false;
        IsFinalizingTranscript = true;

        var finalTranscriptFallbackDelay = activeTranscriptionSession?.FinalTranscriptFallbackDelay ?? DefaultFinalTranscriptFallbackDelay;

        StopCapture();
        activeTranscriptionSession?.RequestFinalTranscript();

        finalizeFallback?.Cancel();
        var shouldSubmitFinalDraftWhenFallbackTriggers = shouldAutomaticallySubmitFinalDraft;
        var fallback = new CancellationTokenSource();
        finalizeFallback = fallback;
        _ = Task.Delay(finalTranscriptFallbackDelay, fallback.Token).ContinueWith(delay =>
        {
            if (delay.IsCanceled) return;
            mainThread.Post(() =>
            {
                if (fallback.IsCancellationRequested) return;
                FinishCurrentDictationSessionIfNeeded(shouldSubmitFinalDraftWhenFallbackTriggers);
            });
        }, TaskScheduler.Default);
    }

    public void CancelCurrentDictation(bool preserveDraftText = true)
    {
        pendingStartRequestIdentifier = Guid.NewGuid();

        if (!IsDictationInProgress) return;

        finalizeFallback?.Cancel();
        finalizeFallback = null;

        if (preserveDraftText)
            updateDraftTextCallback?.Invoke(ComposeDraftText(latestRecognizedText));

        TearDownRecordingAndSession();
        ResetSessionState();
    }

    /// <summary>Stops the microphone, unhooks the frames and cancels the open transcription session.</summary>
    private void TearDownRecordingAndSession()
    {
        StopCapture();
        activeTranscriptionSession?.Cancel();
    }

    private void StopCapture()
    {
        if (audioCapture is null) return;
        if (installedFrameHandler is not null)
        {
            audioCapture.FramesAvailable -= installedFrameHandler;
            installedFrameHandler = null;
        }
        try
        {
            if (audioCapture.IsCapturing) audioCapture.Stop();
        }
        catch (Exception error)
        {
            AppLog.Write($"dictation: microphone stop failed: {error.Message}");
        }
    }

    /// <summary>Gives the session about to open a new identity; callbacks made for an earlier one go quiet.</summary>
    public Guid BeginNewSessionIdentity()
    {
        var sessionIdentity = Guid.NewGuid();
        ActiveSessionIdentity = sessionIdentity;
        return sessionIdentity;
    }

    /// <summary>
    /// The callbacks for the session with <paramref name="sessionIdentity"/>. Each hops to the main
    /// thread and checks that its session is still the active one, dropping what it carries otherwise.
    /// </summary>
    public BuddyTranscriptionSessionCallbacks MakeTranscriptionCallbacks(Guid sessionIdentity) => new(
        OnTranscriptUpdate: transcriptText => mainThread.Post(() =>
        {
            if (ActiveSessionIdentity != sessionIdentity) return;
            latestRecognizedText = transcriptText;
            // Live words for the orb; the companion's callers ignore partials.
            updateDraftTextCallback?.Invoke(ComposeDraftText(transcriptText));
        }),
        OnFinalTranscriptReady: transcriptText => mainThread.Post(() =>
        {
            if (ActiveSessionIdentity != sessionIdentity) return;
            latestRecognizedText = transcriptText;
            if (IsFinalizingTranscript) FinishCurrentDictationSessionIfNeeded(shouldAutomaticallySubmitFinalDraft);
        }),
        OnError: error => mainThread.Post(() =>
        {
            if (ActiveSessionIdentity != sessionIdentity) return;
            HandleRecognitionError(error);
        }));

    private async Task StartRecognitionSessionAsync(CancellationToken cancellationToken)
    {
        activeTranscriptionSession?.Cancel();
        activeTranscriptionSession = null;

        var callbacks = MakeTranscriptionCallbacks(BeginNewSessionIdentity());
        var session = await transcriptionProvider.StartStreamingSessionAsync(
            BuildTranscriptionKeyterms(contextualKeyterms),
            callbacks.OnTranscriptUpdate,
            callbacks.OnFinalTranscriptReady,
            callbacks.OnError,
            cancellationToken);
        activeTranscriptionSession = session;

        audioCapture ??= makeAudioCapture();
        StopCapture();
        // The handler feeds the session it was installed for, and nothing else, for as long as it is
        // installed: it never reads `activeTranscriptionSession`, which the main thread reassigns.
        var audioRetentionSink = AudioRetentionSink;
        Action<AudioFrame> frameHandler = frame =>
        {
            session.AppendAudio(frame);
            audioRetentionSink?.Invoke(frame);
            UpdateAudioPowerLevel(frame.Level);
        };
        installedFrameHandler = frameHandler;
        audioCapture.FramesAvailable += frameHandler;
        // Settings → microphone: a chosen input device, else the default.
        await audioCapture.StartAsync(new AudioCaptureOptions
        {
            DeviceId = PreferredMicrophoneId,
            SampleRate = 16_000,
            Format = AudioSampleFormat.Pcm16,
        }, cancellationToken);
    }

    private void HandleRecognitionError(Exception error)
    {
        if (hasFinishedCurrentDictationSession) return;

        if (IsFinalizingTranscript && latestRecognizedText.Trim().Length > 0)
        {
            FinishCurrentDictationSessionIfNeeded(shouldAutomaticallySubmitFinalDraft);
        }
        else
        {
            AppLog.Write($"dictation: recognition error ({transcriptionProvider.DisplayName}): {error.GetType().Name}: {error.Message}");
            LastErrorMessage = UserFacingErrorMessage(error, "couldn't transcribe that. try again.");
            CancelCurrentDictation(preserveDraftText: false);
        }
    }

    private void FinishCurrentDictationSessionIfNeeded(bool shouldSubmitFinalDraft)
    {
        if (hasFinishedCurrentDictationSession) return;
        hasFinishedCurrentDictationSession = true;

        finalizeFallback?.Cancel();
        finalizeFallback = null;

        var finalDraftText = ComposeDraftText(latestRecognizedText);
        var finalTranscriptText = latestRecognizedText.Trim();
        var updateDraftText = updateDraftTextCallback;
        var submitDraftText = submitDraftTextCallback;

        if (!shouldSubmitFinalDraft && finalDraftText.Length > 0) updateDraftText?.Invoke(finalDraftText);

        TearDownRecordingAndSession();
        ResetSessionState();

        if (!shouldSubmitFinalDraft || finalTranscriptText.Length == 0) return;
        submitDraftText?.Invoke(finalDraftText);
    }

    private string ComposeDraftText(string transcribedText)
    {
        var trimmedTranscriptText = transcribedText.Trim();
        if (trimmedTranscriptText.Length == 0) return draftTextBeforeCurrentDictation;
        if (draftTextBeforeCurrentDictation.Trim().Length == 0) return trimmedTranscriptText;
        if (draftTextBeforeCurrentDictation.EndsWith(' ') || draftTextBeforeCurrentDictation.EndsWith('\n'))
            return draftTextBeforeCurrentDictation + trimmedTranscriptText;
        return draftTextBeforeCurrentDictation + " " + trimmedTranscriptText;
    }

    private void ResetSessionState()
    {
        pendingStartRequestIdentifier = Guid.NewGuid();
        activeTranscriptionSession = null;
        ActiveSessionIdentity = null;
        updateDraftTextCallback = null;
        submitDraftTextCallback = null;
        isPushToTalkSessionOpen = false;
        draftTextBeforeCurrentDictation = "";
        latestRecognizedText = "";
        shouldAutomaticallySubmitFinalDraft = false;
        hasFinishedCurrentDictationSession = false;
        IsPreparingToRecord = false;
        IsRecordingFromKeyboardShortcut = false;
        IsKeyboardShortcutSessionActiveOrFinalizing = false;
        IsFinalizingTranscript = false;
        CurrentAudioPowerLevel = 0;
    }

    /// <summary>The words every session is told to expect, then the caller's, trimmed and de-duplicated without case.</summary>
    public static IReadOnlyList<string> BuildTranscriptionKeyterms(IReadOnlyList<string> contextualKeyterms)
    {
        string[] baseKeyterms =
        [
            "OpenClicky", "Codex", "Claude", "Anthropic", "OpenAI", "SwiftUI", "Xcode", "Vercel", "Next.js", "localhost",
        ];
        var uniqueNormalizedKeyterms = new HashSet<string>(StringComparer.Ordinal);
        var orderedKeyterms = new List<string>();
        foreach (var keyterm in baseKeyterms.Concat(contextualKeyterms))
        {
            var trimmedKeyterm = keyterm.Trim();
            if (trimmedKeyterm.Length == 0) continue;
            if (!uniqueNormalizedKeyterms.Add(trimmedKeyterm.ToLowerInvariant())) continue;
            orderedKeyterms.Add(trimmedKeyterm);
        }
        return orderedKeyterms;
    }

    /// <summary>Called on the capture thread: reads nothing off this object before hopping to the main thread.</summary>
    private void UpdateAudioPowerLevel(float boostedLevel) => mainThread.Post(() =>
    {
        CurrentAudioPowerLevel = Math.Max(boostedLevel, CurrentAudioPowerLevel * 0.72);
    });

    /// <summary>
    /// Windows has no permission prompt for desktop apps: the microphone switches in Settings →
    /// Privacy &amp; security are read, and a refusal says why (a silent false left the take waiting for
    /// a session that never opened). The Windows speech recogniser's own privacy switch surfaces as
    /// its start error.
    /// </summary>
    private bool CheckMicrophoneAndSpeechPermissions()
    {
        var consent = microphonePermission?.ReadConsent() ?? MicrophoneConsent.Unknown;
        if (consent is MicrophoneConsent.DeniedForDevice or MicrophoneConsent.DeniedForAllApps or MicrophoneConsent.DeniedForDesktopApps)
        {
            LastErrorMessage = MicrophonePermissionRequiredSentence;
            return false;
        }
        return true;
    }

    private static string UserFacingErrorMessage(Exception error, string fallback)
    {
        if (error is OfflineSpeechException offline && offline.UserFacingSentence.Trim().Length > 0) return offline.UserFacingSentence.Trim();
        var description = error.Message.Trim();
        if (description.Length > 0 && !description.StartsWith("Exception of type", StringComparison.Ordinal)) return description;
        return fallback;
    }

    public void Dispose()
    {
        finalizeFallback?.Cancel();
        TearDownRecordingAndSession();
        audioCapture?.Dispose();
        audioCapture = null;
    }
}
