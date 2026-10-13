// Ports Dictation/DictationTakeController.swift (DictationEngineResolver and HeyClickyEditor are in
// DictationEngineResolver.cs).
//
// One take at a time: the dictation key goes down, the microphone opens, words arrive, the key
// comes up, the words are formatted in the front app's style and pasted where the cursor was, and
// the take is written to history. Holding the key is one take; a tap starts a take that the next
// tap ends; two quick taps or esc discard it; Ctrl joining the held key turns the take into a
// Hey Clicky edit of the selection. The orb shows every step.
//
// The take is a small state machine (idle → starting → listening → finishing → idle) and every
// asynchronous continuation carries the take's id: a take that was cancelled, or that ended while
// a model was still polishing it, can never paste into the one that came after.
//
// Threading: like the Mac's @MainActor class, every member is called on the main thread (the WPF
// dispatcher, whose synchronization context the awaits come back to); the capture side's events and
// every timer are posted back to it through IMainThread. Delays and the take's clock read the
// injected TimeProvider. Windows differs from the Mac in one way that shapes this file: UI
// Automation reads are asynchronous (bounded, off the caller's thread), so reading the focused
// element at press, before each live-typed phrase and at release is awaited, and live typing's
// steps run one at a time behind a gate so a phrase is still only ever typed whole and in order.

using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Shortcuts;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Dictation;

public sealed class DictationTakeController : ObservableObject, IDisposable
{
    public enum TakeMode { Dictate, Edit }

    /// <summary>Where the take is.</summary>
    public enum TakeState
    {
        Idle,
        /// <summary>The key is down; the microphone is being opened.</summary>
        Starting,
        Listening,
        /// <summary>The key came up; the engine, the formatter and the paste are at work.</summary>
        Finishing,
    }

    /// <summary>What happened when a live phrase was to be typed.</summary>
    public enum LivePhraseTypingResult { Typed, DeferredForHeldModifier, Stopped }

    // MARK: what the orb and the pointer say

    /// <summary>What the orb says when a Hey Clicky edit is asked for with the clean-up switch off.</summary>
    public const string HeyClickyNeedsCleanUpMessage = "hey clicky needs “clean up my words with ai” on (styles)";

    /// <summary>What the pointer says when a take starts with no text box focused.</summary>
    public const string NoTextBoxPointerMessage = "you're not in a text box. click into one and your words will go there.";
    public const string NoTextBoxOrbHint = "click into a text box for your words";

    /// <summary>Below this the whole take was room noise: a microphone that hears nothing, whose silence the offline recogniser wrote down as "no".</summary>
    public const double SilentTakeLevel = 0.03;

    /// <summary>The box stays this long after a take whose words had nowhere to go; a click on it or a new take ends that early.</summary>
    public static readonly TimeSpan BoxLingerTime = TimeSpan.FromSeconds(8);

    /// <summary>The rewrites the box offers: Kivi's casual and formal keys, as chips, plus shorter.</summary>
    public static IReadOnlyList<(string Label, string Instruction)> QuickRewrites { get; } =
    [
        ("formal", "Rewrite it formally: full sentences, full forms, no slang, polite."),
        ("casual", "Rewrite it casually: lowercase, shorthand welcome, light punctuation, warm."),
        ("shorter", "Make it shorter: the fewest words that still say it, nothing added."),
    ];

    /// <summary>
    /// UI Automation can read the focused element and it is not a text box (a list, a button, a
    /// page's text): the words would have nowhere to go. Unknown (an app that exposes nothing) is
    /// not "no text box": the paste is tried then, as before.
    /// </summary>
    public static bool HasNoTextBoxToPasteInto(FocusedFieldSnapshot field, bool isAccessibilityTrusted) =>
        isAccessibilityTrusted && field.Role is not null && !field.IsEditable && !field.IsSecure;

    // MARK: state the windows read

    private TakeState state = TakeState.Idle;
    public TakeState State { get => state; private set => SetProperty(ref state, value); }

    private TakeRecord? lastTake;
    public TakeRecord? LastTake { get => lastTake; private set => SetProperty(ref lastTake, value); }

    private int historyVersion;
    /// <summary>Bumped when history changes, so the window's pages reload.</summary>
    public int HistoryVersion { get => historyVersion; private set => SetProperty(ref historyVersion, value); }

    private Guid? retryableTakeId;
    /// <summary>A failed take whose audio is on disk and can be heard again (history's "retry").</summary>
    public Guid? RetryableTakeId { get => retryableTakeId; private set => SetProperty(ref retryableTakeId, value); }

    private bool isRetrying;
    public bool IsRetrying { get => isRetrying; private set => SetProperty(ref isRetrying, value); }

    public bool IsTakeInProgress => State != TakeState.Idle;

    public DictationSettings Settings { get; }
    public DictationSpaceStore SpaceStore { get; }
    public TakeStore? TakeStore { get; }
    public IDictationOrb Orb { get; }
    public TakeAudioStore AudioStore { get; }
    public IDictationCapturing Capture { get; }

    private readonly DictationHost host;
    private readonly IDictationProviderFactory providerFactory;
    private readonly IEarconPlayer? earcons;
    private readonly IMainThread mainThread;
    private readonly TimeProvider timeProvider;

    // MARK: what the app sets

    /// <summary>How cautiously live typing commits words. The defaults are the live values; tests pin their own.</summary>
    public StableWordCommitter.Timing LiveTypingTiming { get; set; } = new();
    /// <summary>Called before the microphone opens (the Realtime engine must let go of it).</summary>
    public Func<Task>? BeforeMicrophoneOpens { get; set; }
    /// <summary>Called after a take, when the microphone is free again.</summary>
    public Action? AfterMicrophoneCloses { get; set; }
    /// <summary>A take's words when the window wants them (the onboarding's first take, the record page box).</summary>
    public Action<TakeRecord>? OnTakeFinished { get; set; }
    /// <summary>
    /// The key went down with no text box focused: the pointer says so beside the mouse, so the
    /// person knows to click into one instead of finding out from "copied, press Ctrl+V" at the end.
    /// TODO(integration): the companion shows it as its quiet notice, waking a resting pointer.
    /// </summary>
    public Action<string>? OnNoTextBoxAtPress { get; set; }
    /// <summary>
    /// How long a take that began outside any text box, and still has none when the key is let go,
    /// waits for one to be clicked into: people let go first and click after.
    /// </summary>
    public TimeSpan TimeToWaitForATextBoxAfterRelease { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>The first-run tour's practice: takes are heard and recorded but never typed or pasted.</summary>
    public bool HoldsTakesOnTheIsland { get; set; }
    /// <summary>Hides OpenClicky's own windows before "paste the last take" (the Mac's NSApp.hide). TODO(integration): the main window.</summary>
    public Action? HideOwnWindows { get; set; }
    /// <summary>How fast a retried recording is fed to the engine (a retry goes faster than real time).</summary>
    public TimeSpan RetryPauseBetweenSlices { get; set; } = TimeSpan.FromMilliseconds(60);

    // MARK: the take in progress

    private DictationEngineChoice currentEngine;
    /// <summary>The language the engine in use was built for.</summary>
    private string currentLanguageCode;
    private Task? pendingStartTask;
    private CancellationTokenSource? pendingStartCancellation;
    /// <summary>
    /// A Hey Clicky press (Ctrl already down) waits out the tap window before it opens the
    /// microphone: a quick tap of the key with Ctrl is the hands-free gesture, not an edit.
    /// </summary>
    private CancellationTokenSource? pendingEditStart;
    private ITimer? inactivityTimer;
    private DateTimeOffset lastLoudMomentAt;
    /// <summary>The loudest level the microphone reported during this take; null until it reports any.</summary>
    private double? loudestLevelThisTake;
    /// <summary>How many levels the microphone reported this take: a real one reports many a second.</summary>
    private int levelSamplesThisTake;
    /// <summary>Closes the orb's transcript box a moment after it opened for a take.</summary>
    private CancellationTokenSource? boxCloseCancellation;
    private CancellationTokenSource? liveTypingPauseCancellation;
    /// <summary>One live typing step at a time (each awaits a UI Automation read before it types).</summary>
    private readonly SemaphoreSlim liveTypingGate = new(1, 1);

    private Guid takeId = Guid.NewGuid();
    private TakeMode takeMode = TakeMode.Dictate;
    private DateTimeOffset takeStartedAt;
    private bool takeIsToggle;
    private bool takeReceivedFinalTranscript;
    private FocusedFieldSnapshot fieldAtPress = FocusedFieldSnapshot.Unknown;
    /// <summary>The focused element itself at press: the paste goes only there, not into another field of the same app that has focus by release.</summary>
    private FocusedTextTarget? focusedTargetAtPress;
    private IReadOnlyList<string> nearbyTermsAtPress = [];
    /// <summary>Set when this take's words are typed as they settle (no model polish will run); null otherwise.</summary>
    private LiveTypingSession? liveTyping;
    /// <summary>The model that may polish this take, chosen once when the key goes down.</summary>
    private ITakePolisher? polisherForThisTake;
    /// <summary>A history write that failed is logged every time but said on the orb only once per run.</summary>
    private bool hasShownHistoryWriteFailure;
    private bool isDisposed;

    public DictationTakeController(
        DictationSettings settings,
        DictationSpaceStore spaceStore,
        TakeStore? takeStore,
        IDictationOrb orb,
        IDictationCapturing capture,
        DictationHost host,
        IDictationProviderFactory providerFactory,
        IMainThread mainThread,
        IEarconPlayer? earcons = null,
        TakeAudioStore? audioStore = null,
        TimeProvider? timeProvider = null)
    {
        Settings = settings;
        SpaceStore = spaceStore;
        TakeStore = takeStore;
        Orb = orb;
        Capture = capture;
        this.host = host;
        this.providerFactory = providerFactory;
        this.mainThread = mainThread;
        this.earcons = earcons;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        AudioStore = audioStore ?? new TakeAudioStore();
        currentEngine = settings.Engine;
        currentLanguageCode = settings.LanguageCode;
        lastLoudMomentAt = Now;
        takeStartedAt = Now;
        var retainedAudio = AudioStore;
        capture.AudioRetentionSink = frame => retainedAudio.Append(frame);
        orb.OnQuickRewrite = label => _ = RewriteLastTakeAsync(label);
        orb.OnPasteFromBox = PasteFromBox;
        Bind();
    }

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    private static string Short(Guid id) => id.ToString("D").ToUpperInvariant()[..8];

    private static readonly string[] EngineSettingNames = [nameof(DictationSettings.Engine), nameof(DictationSettings.LanguageCode)];

    private void Bind()
    {
        // A change made while a take listens is picked up by the next take.
        Settings.PropertyChanged += OnSettingsChanged;
        Capture.AudioPowerLevelChanged += OnAudioPowerLevel;
        Capture.ErrorMessageChanged += OnErrorMessage;
        Capture.SessionActivityChanged += OnSessionActivity;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
    {
        if (change.PropertyName is null || !EngineSettingNames.Contains(change.PropertyName)) return;
        mainThread.Post(RebuildEngineIfSettingsChanged);
    }

    private void OnAudioPowerLevel(double level) => mainThread.Post(() =>
    {
        Orb.AudioLevel = level;
        if (level > 0.08) lastLoudMomentAt = Now;
        if (IsTakeInProgress)
        {
            loudestLevelThisTake = Math.Max(loudestLevelThisTake ?? 0, level);
            levelSamplesThisTake += 1;
        }
    });

    private void OnErrorMessage(string? message)
    {
        if (message is null) return;
        mainThread.Post(() =>
        {
            // An error belongs to the take whose session is open; one that lands after the engine
            // already delivered its words (a late socket close) is noise.
            if (!IsTakeInProgress || takeReceivedFinalTranscript) return;
            FailTake(message, takeId);
        });
    }

    private void OnSessionActivity(bool active) => mainThread.Post(() =>
    {
        // A session that ended without a transcript (nothing said, permission denied) leaves the
        // controller thinking a take is open; the manager's state says otherwise.
        if (!IsTakeInProgress || active || pendingStartTask is not null) return;
        var endedTakeId = takeId;
        // Give the final transcript callback a tick to land first.
        After(TimeSpan.FromSeconds(0.2), () =>
        {
            if (!IsTakeInProgress || takeId != endedTakeId || takeReceivedFinalTranscript || Capture.IsDictationInProgress) return;
            FailTake("didn't catch that", endedTakeId);
        });
    });

    /// <summary>
    /// Builds the engine again when the settings name another engine or language than the one in
    /// use. Never under a take; BeginTake checks again, so a change made during one is not lost.
    /// </summary>
    private void RebuildEngineIfSettingsChanged()
    {
        if (IsTakeInProgress) return;
        if (Settings.Engine == currentEngine && Settings.LanguageCode == currentLanguageCode) return;
        currentEngine = Settings.Engine;
        currentLanguageCode = Settings.LanguageCode;
        Capture.ReplaceTranscriptionProvider(DictationEngineResolver.MakeProvider(Settings.Engine, Settings, providerFactory));
        AppLog.Write($"dictation engine → {Settings.Engine.Id()}, language {Settings.LanguageCode}");
    }

    public string EngineDisplayName => Capture.TranscriptionProviderDisplayName;

    /// <summary>The hint pill under a resting orb ("tooltips" in Settings → the orb).</summary>
    public string IdleHint => $"tap / hold {Settings.DictationKey.KeycapLabel()} to talk";

    // MARK: keyboard

    /// <summary>Feeds a shortcut event; true when it was one of dictation's.</summary>
    public bool Handle(CompanionShortcutEvent shortcutEvent)
    {
        switch (shortcutEvent.Kind)
        {
            case CompanionShortcutEventKind.DictationPressed:
                if (State == TakeState.Idle) BeginTake(TakeMode.Dictate);
                // A second press while a tapped take listens: this press ends it.
                else if (State == TakeState.Listening && takeIsToggle) StopTake();
                return true;

            case CompanionShortcutEventKind.DictationEditPressed:
            {
                // Ctrl was already down when the key went down: a Hey Clicky press, unless it turns
                // out to be a tap (the hands-free gesture), which the tap window decides.
                if (State != TakeState.Idle) return true;
                pendingEditStart?.Cancel();
                var pending = new CancellationTokenSource();
                pendingEditStart = pending;
                After(TimeSpan.FromSeconds(CompanionShortcutRecognizer.TapMaxHoldSeconds + 0.05), () =>
                {
                    if (State != TakeState.Idle || !ReferenceEquals(pendingEditStart, pending)) return;
                    pendingEditStart = null;
                    BeginTake(TakeMode.Edit);
                }, pending.Token);
                return true;
            }

            case CompanionShortcutEventKind.DictationEditModifierJoined:
                if (State is TakeState.Starting or TakeState.Listening && takeMode == TakeMode.Dictate)
                {
                    if (liveTyping is { HasTyped: true } session)
                    {
                        // Words already went into the field: an edit now would rewrite a selection the
                        // person never meant, so the take stays a dictation — but typing stops here,
                        // since what is said from now on is an instruction and must never be typed.
                        // What was heard before Ctrl and not typed yet goes to the clipboard at release.
                        MarkLiveTypingStopped(session, "too late for an edit", takeId);
                        session.EditKeyJoinedAfterTyping = true;
                        Orb.Hint = "already typing, stopped here. let go, then start an edit";
                        return true;
                    }
                    takeMode = TakeMode.Edit;
                    // An edit's words are an instruction, not text: nothing of it may be typed.
                    StopLiveTyping("the take became a hey clicky edit", takeId);
                    Orb.Phase = DictationOrbPhase.EditListening;
                    // With the clean-up off the edit is refused when the key comes up; say so now.
                    Orb.Hint = DictationEngineResolver.WantsModelPolish(Settings) ? "say your edit, then let go" : HeyClickyNeedsCleanUpMessage;
                }
                return true;

            case CompanionShortcutEventKind.DictationReleased:
                if (pendingEditStart is { } pendingEdit)
                {
                    // Released inside the tap window: not an edit, nothing was opened.
                    pendingEdit.Cancel();
                    pendingEditStart = null;
                    return true;
                }
                if (State is TakeState.Starting or TakeState.Listening && !takeIsToggle)
                {
                    if (shortcutEvent.KeyWasPressedDuringHold)
                    {
                        // The key was held for a shortcut (right Ctrl + C), not to talk: finishing
                        // would paste room noise or end in "didn't catch that".
                        CancelTake("cancelled, that was a shortcut");
                    }
                    else if (shortcutEvent.WasTap)
                    {
                        // A tap: keep listening until the next tap (or a double tap cancels).
                        takeIsToggle = true;
                        Orb.Hint = $"say it, then tap {Settings.DictationKey.KeycapLabel()}";
                    }
                    else
                    {
                        StopTake();
                    }
                }
                return true;

            case CompanionShortcutEventKind.DictationDoubleTapped:
                if (IsTakeInProgress) CancelTake("cancelled");
                return true;

            case CompanionShortcutEventKind.EscapePressed:
                if (!IsTakeInProgress) return false;
                CancelTake("cancelled");
                return true;

            case CompanionShortcutEventKind.HandsFreeToggleRequested:
                // The gesture's taps never opened a take; the companion toggles always-on listening.
                pendingEditStart?.Cancel();
                pendingEditStart = null;
                return false;

            default:
                return false;
        }
    }

    /// <summary>The orb was clicked: like a tap of the key.</summary>
    public void OrbClicked()
    {
        switch (State)
        {
            case TakeState.Idle:
                BeginTake(TakeMode.Dictate);
                if (State == TakeState.Idle) return;
                takeIsToggle = true;
                Orb.Hint = "say it, then tap the orb";
                break;
            case TakeState.Starting or TakeState.Listening:
                StopTake();
                break;
        }
    }

    // MARK: the take

    private void BeginTake(TakeMode mode)
    {
        if (State != TakeState.Idle || Capture.IsDictationInProgress) return;
        if (host.EngineUnavailableReason(Settings.Engine) is { } reason)
        {
            Play(DictationEarcon.Blocked);
            Orb.Phase = DictationOrbPhase.Failed(reason);
            ScheduleIdle(TimeSpan.FromSeconds(2.5));
            return;
        }
        if (mode == TakeMode.Edit && !DictationEngineResolver.WantsModelPolish(Settings))
        {
            // An edit is the selection sent to a model; with the clean-up off nothing may leave.
            Play(DictationEarcon.Blocked);
            Orb.Phase = DictationOrbPhase.Failed(HeyClickyNeedsCleanUpMessage);
            ScheduleIdle(TimeSpan.FromSeconds(2.5));
            return;
        }
        // An engine or language chosen while the last take listened takes effect now.
        RebuildEngineIfSettingsChanged();
        var thisTakeId = Guid.NewGuid();
        takeId = thisTakeId;
        State = TakeState.Starting;
        takeMode = mode;
        takeStartedAt = Now;
        takeIsToggle = false;
        takeReceivedFinalTranscript = false;
        lastLoudMomentAt = Now;
        loudestLevelThisTake = null;
        levelSamplesThisTake = 0;
        fieldAtPress = FocusedFieldSnapshot.Unknown;
        focusedTargetAtPress = null;
        nearbyTermsAtPress = [];
        polisherForThisTake = null;
        liveTyping = null;
        AudioStore.BeginCapture();
        boxCloseCancellation?.Cancel();
        Capture.UpdateContextualKeyterms(SpaceStore.Space.Dictionary.Select(term => term.Written).ToList());
        Orb.LiveTranscript = "";
        Orb.IsBoxOpen = Settings.OrbRestsExpanded && Orb.BoxText is not null;
        Orb.Phase = mode == TakeMode.Edit ? DictationOrbPhase.EditListening : DictationOrbPhase.Listening;
        Orb.Hint = mode == TakeMode.Edit ? "say your edit, then let go" : "tap / release to transcribe";
        Play(DictationEarcon.Start);
        StartInactivityTimer();

        pendingStartCancellation?.Cancel();
        var startCancellation = new CancellationTokenSource();
        pendingStartCancellation = startCancellation;
        pendingStartTask = RunTakeStartAsync(thisTakeId, mode, startCancellation.Token);
    }

    private bool IsStarting(Guid id) => takeId == id && State == TakeState.Starting;

    private async Task RunTakeStartAsync(Guid thisTakeId, TakeMode mode, CancellationToken cancellationToken)
    {
        try
        {
            // The focused element is read now (a few UI Automation calls); the window's captions,
            // which can take hundreds of milliseconds in a browser, are read once the microphone is open.
            var field = await host.ReadFocusedFieldAsync(cancellationToken);
            var target = await host.ReadFocusedTextTargetAsync(cancellationToken);
            if (!IsStarting(thisTakeId) || cancellationToken.IsCancellationRequested) return;
            fieldAtPress = field;
            focusedTargetAtPress = target;
            polisherForThisTake = host.MakePolisher();
            var session = await MakeLiveTypingSessionIfWantedAsync(mode, target, cancellationToken);
            if (!IsStarting(thisTakeId) || cancellationToken.IsCancellationRequested) return;
            liveTyping = session;
            // An edit works on the selection or the last take, and the tour's practice is never
            // pasted: only a dictation needs somewhere to put its words.
            if (mode == TakeMode.Dictate && !HoldsTakesOnTheIsland && HasNoTextBoxToPasteInto(fieldAtPress, host.IsAccessibilityTrusted()))
            {
                Orb.Hint = NoTextBoxOrbHint;
                OnNoTextBoxAtPress?.Invoke(NoTextBoxPointerMessage);
                AppLog.Write($"take {Short(thisTakeId)}: no text box at press; the pointer asked for one");
            }
            AppLog.Write($"take {Short(thisTakeId)} started ({mode.ToString().ToLowerInvariant()}) in {fieldAtPress.AppName ?? "?"} field={fieldAtPress.Role ?? "none"} editable={fieldAtPress.IsEditable} live={liveTyping is not null}");

            // The rest runs a moment later, as the Mac's start task does.
            await Task.Yield();
            if (BeforeMicrophoneOpens is { } beforeMicrophoneOpens) await beforeMicrophoneOpens();
            if (cancellationToken.IsCancellationRequested || !IsStarting(thisTakeId)) return;
            await Capture.StartPushToTalkFromKeyboardShortcutAsync(
                "",
                partial =>
                {
                    if (takeId != thisTakeId || !IsTakeInProgress) return;
                    HandlePartialTranscript(partial, thisTakeId);
                },
                final =>
                {
                    if (takeId != thisTakeId) return;
                    takeReceivedFinalTranscript = true;
                    Observe(FinishTakeAsync(final, thisTakeId));
                });
            if (takeId != thisTakeId) return;
            pendingStartTask = null;
            if (State == TakeState.Starting) State = TakeState.Listening;
            // The start came back without opening a session (microphone denied, a refused engine):
            // nothing will ever end this take, and presses are ignored while it lasts. The manager's
            // own reason usually arrives first through the error stream; this catches the rest.
            if (!Capture.IsDictationInProgress && !takeReceivedFinalTranscript)
            {
                After(TimeSpan.FromSeconds(0.2), () =>
                {
                    if (takeId != thisTakeId || !IsTakeInProgress || takeReceivedFinalTranscript || Capture.IsDictationInProgress) return;
                    FailTake("couldn't open the microphone", thisTakeId);
                });
                return;
            }
            if (Settings.ReadNearbyText)
            {
                var terms = await host.ReadNearbyTermsAsync(CancellationToken.None);
                if (takeId == thisTakeId) nearbyTermsAtPress = terms;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            AppLog.Write($"take {Short(thisTakeId)} could not start: {error.GetType().Name}");
            if (takeId == thisTakeId && IsTakeInProgress) FailTake("couldn't open the microphone", thisTakeId);
        }
    }

    private void StopTake()
    {
        if (State is not (TakeState.Starting or TakeState.Listening)) return;
        var thisTakeId = takeId;
        StopInactivityTimer();
        var heldFor = (Now - takeStartedAt).TotalSeconds;
        AppLog.Write($"take {Short(thisTakeId)} stopped after {heldFor.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s");
        Play(DictationEarcon.Stop);
        Orb.Phase = DictationOrbPhase.Working(takeMode == TakeMode.Edit ? "finishing your edit" : "moving your words");
        Orb.Hint = null;
        if (State == TakeState.Starting && pendingStartTask is not null)
        {
            // Released before the microphone opened: nothing was said.
            pendingStartCancellation?.Cancel();
            pendingStartTask = null;
            Capture.CancelCurrentDictation(preserveDraftText: false);
            State = TakeState.Finishing;
            FailTake("didn't catch that", thisTakeId);
            return;
        }
        State = TakeState.Finishing;
        Capture.StopPushToTalkFromKeyboardShortcut();
    }

    public void CancelTake(string reason)
    {
        if (!IsTakeInProgress) return;
        var thisTakeId = takeId;
        StopInactivityTimer();
        pendingStartCancellation?.Cancel();
        pendingStartTask = null;
        Capture.CancelCurrentDictation(preserveDraftText: false);
        Play(DictationEarcon.Blocked);
        Orb.Phase = DictationOrbPhase.Failed(reason);
        Orb.LiveTranscript = "";
        Orb.Hint = null;
        AppLog.Write($"take {Short(thisTakeId)} cancelled: {reason}");
        _ = AudioStore.EndCapture();
        // Words already typed live stay in the app (deleting in a terminal is unsafe); history
        // keeps what reached it.
        if (liveTyping is { HasTyped: true } session)
        {
            LogLiveTypedTotal(session, thisTakeId);
            if (RecordInterruptedLiveTypedTake(session, thisTakeId, TakeStatus.Cancelled, reason) is { } historyNote)
            {
                Orb.Phase = DictationOrbPhase.Failed($"{reason} · {historyNote}");
            }
        }
        EndTake();
        ScheduleIdle(TimeSpan.FromSeconds(1.4));
    }

    private void FailTake(string message, Guid failedTakeId)
    {
        if (!IsTakeInProgress || takeId != failedTakeId) return;
        StopInactivityTimer();
        Play(DictationEarcon.Error);
        Orb.Phase = DictationOrbPhase.Failed(message);
        var heardSoFar = Orb.LiveTranscript;
        Orb.LiveTranscript = "";
        AppLog.Write($"take {Short(failedTakeId)} failed: {message}");
        var pcm = AudioStore.EndCapture();
        if (liveTyping is { HasTyped: true } session)
        {
            // The typed words stay in the app; the take is recorded with them, and what was heard
            // but not typed yet goes to the clipboard, as when typing stops.
            LogLiveTypedTotal(session, failedTakeId);
            var historyNote = RecordInterruptedLiveTypedTake(session, failedTakeId, TakeStatus.Failed, message);
            var failureLine = message;
            var heardButUntyped = HeardButUntypedText(session);
            if (heardButUntyped.Length > 0)
            {
                host.CopyToClipboard(heardButUntyped);
                failureLine = $"{message}. the rest is copied, press Ctrl+V";
            }
            if (historyNote is not null) failureLine += $" · {historyNote}";
            Orb.Phase = DictationOrbPhase.Failed(failureLine);
        }
        else if (!Settings.Incognito && !message.StartsWith("didn't catch", StringComparison.Ordinal))
        {
            var record = MakeRecord(failedTakeId, takeStartedAt, takeMode == TakeMode.Edit ? Dictation.TakeMode.Edit : Dictation.TakeMode.Dictate, TakeStatus.Failed,
                heardSoFar, "", fieldAtPress.AppProcessName, fieldAtPress.AppName, (Now - takeStartedAt).TotalSeconds, failureReason: message);
            if (WriteToHistory("failed take", failedTakeId, store => store.Insert(record)) is { } historyNote)
            {
                Orb.Phase = DictationOrbPhase.Failed($"{message} · {historyNote}");
            }
            HistoryVersion += 1;
            // The words are still in the audio: keep it so the take can be heard again once the key,
            // the credits or the network are back. Only a network engine's take is kept (the rule in
            // TakeAudioStore's header): an engine on this PC has none of those to wait for, and a
            // retry would hear the same audio the same way.
            if (Settings.RetainFailedTakeAudio && !Settings.Engine.IsOnDevice() && pcm.Length > 16_000)
            {
                try
                {
                    AudioStore.Retain(failedTakeId, pcm);
                    RetryableTakeId = failedTakeId;
                    Orb.Phase = DictationOrbPhase.Failed($"{message} · take saved, retry from history");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    AppLog.Write($"take {Short(failedTakeId)} audio not retained: {error.Message}");
                }
            }
        }
        EndTake();
        ScheduleIdle(TimeSpan.FromSeconds(2.5));
    }

    private void EndTake()
    {
        State = TakeState.Idle;
        takeIsToggle = false;
        if (liveTyping is { } session) session.IsAbandoned = true;
        liveTyping = null;
        pendingStartTask = null;
        liveTypingPauseCancellation?.Cancel();
        polisherForThisTake = null;
        AfterMicrophoneCloses?.Invoke();
    }

    /// <summary>Still this take, still wanted: false once it was cancelled or another take began.</summary>
    private bool IsCurrent(Guid id) => takeId == id && State == TakeState.Finishing;

    private async Task FinishTakeAsync(string rawText, Guid thisTakeId)
    {
        if (takeId != thisTakeId || State == TakeState.Idle) return;
        State = TakeState.Finishing;
        // The live session closes here: a partial that lands from now on is never typed, so nothing
        // can be typed during finishing and then typed or pasted again. A typing step already under
        // way finishes first, so its words count as typed.
        LiveTypingSession? liveTypingAtFinish;
        await liveTypingGate.WaitAsync();
        try
        {
            liveTypingAtFinish = liveTyping;
            liveTyping = null;
        }
        finally
        {
            liveTypingGate.Release();
        }
        liveTypingPauseCancellation?.Cancel();
        if (!IsCurrent(thisTakeId)) return;
        _ = AudioStore.EndCapture();
        var raw = rawText.Trim();
        if (raw.Length == 0 && liveTypingAtFinish is { HasTyped: true } typedSession)
        {
            // The engine's final came back empty but words were already typed: they are the take.
            raw = string.Join(" ", typedSession.TypedRawWords);
        }
        if (raw.Length == 0)
        {
            FailTake("didn't catch that", thisTakeId);
            return;
        }
        // Words from a take the microphone never heard a voice in are the engine's guess at
        // silence, not what was said: nothing is pasted.
        if (loudestLevelThisTake is { } loudest && levelSamplesThisTake >= 10 && loudest < SilentTakeLevel && liveTypingAtFinish?.HasTyped != true)
        {
            AppLog.Write($"take {Short(thisTakeId)} heard only silence (loudest {loudest.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}); dropped {raw.Length} characters");
            FailTake("didn't catch that. check the microphone", thisTakeId);
            return;
        }
        var duration = (Now - takeStartedAt).TotalSeconds;
        var context = MakeFormattingContext(fieldAtPress.AppProcessName, fieldAtPress.AppName, nearbyTermsAtPress);
        // Read into locals: a new take can begin while a model is at work below.
        var polisher = polisherForThisTake;
        var mode = takeMode;
        var field = fieldAtPress;

        if (mode == TakeMode.Dictate && liveTypingAtFinish is { HasTyped: true } liveSession)
        {
            await FinishLiveTypedTakeAsync(raw, liveSession, duration, thisTakeId);
            return;
        }

        string outputText;
        var formattingDegraded = false;
        if (mode == TakeMode.Edit)
        {
            if (await ApplyHeyClickyEditAsync(raw, field, context, polisher, thisTakeId) is not { } editedText) return;
            outputText = editedText;
        }
        else
        {
            var formatted = await TakeFormatter.FormatAsync(raw, context, polisher, DictationEngineResolver.WantsModelPolish(Settings));
            outputText = formatted.Text;
            formattingDegraded = formatted.FormattingDegraded;
        }
        // Cancelled, or another take began, while the model was at work: these words are dropped.
        if (!IsCurrent(thisTakeId))
        {
            AppLog.Write($"take {Short(thisTakeId)} finished after it was cancelled; words dropped");
            return;
        }

        var (pasteOutcome, deliveryMessage) = await DeliverWhereTheTakeBeganAsync(outputText, field, thisTakeId);
        var pasteMessage = deliveryMessage;
        if (takeId != thisTakeId) return;
        // Words with nowhere to go are put on the clipboard and the orb says so in one line; the big
        // box only opens when the person asked for it ("rest with the box open").
        if (pasteOutcome == TakePasteOutcome.LeftOnPasteboard) host.CopyToClipboard(outputText);

        var record = MakeRecord(thisTakeId, takeStartedAt, mode == TakeMode.Edit ? Dictation.TakeMode.Edit : Dictation.TakeMode.Dictate, TakeStatus.Complete,
            raw, outputText, field.AppProcessName, field.AppName, duration, pasteOutcome);
        LastTake = record;
        string? historyNote = null;
        if (!Settings.Incognito)
        {
            historyNote = WriteToHistory("take", thisTakeId, store => store.Insert(record));
            HistoryVersion += 1;
        }
        OnTakeFinished?.Invoke(record);

        var landed = pasteOutcome is TakePasteOutcome.Verified or TakePasteOutcome.Posted;
        Orb.BoxText = outputText;
        Orb.LiveTranscript = "";
        var showBox = (pasteOutcome == TakePasteOutcome.Posted && Settings.OrbOpensBoxWhenPasteUnverified) || Settings.OrbRestsExpanded;
        Orb.BoxReason = landed ? "your last take" : pasteMessage;
        Orb.QuickRewrites = QuickRewriteLabels(polisher);
        Orb.IsBoxOpen = showBox;
        ScheduleBoxClose();
        Play(landed ? DictationEarcon.Complete : DictationEarcon.Notify);
        if (formattingDegraded && landed) pasteMessage = "moved to text box · cleaned up locally";
        if (historyNote is not null) pasteMessage += $" · {historyNote}";
        Orb.Phase = DictationOrbPhase.Done(pasteMessage);
        AppLog.Write($"take {Short(thisTakeId)} done: {outputText.Length} chars, paste={pasteOutcome}, engine={Settings.Engine.Id()}, degraded={formattingDegraded}");
        EndTake();
        ScheduleIdle(TimeSpan.FromSeconds(1.8));
    }

    /// <summary>
    /// A Hey Clicky take's words are an instruction for the selection (or the last take): the model
    /// rewrites that text. Null when the edit could not be made; the take has already failed then,
    /// with the reason on the orb.
    /// </summary>
    private async Task<string?> ApplyHeyClickyEditAsync(string instruction, FocusedFieldSnapshot field, TakeFormattingContext context,
                                                        ITakePolisher? polisher, Guid thisTakeId)
    {
        Orb.Phase = DictationOrbPhase.Working("finishing your edit");
        // The selection would go to a model: only with "clean up my words with ai" on, the switch
        // the privacy page names as what keeps words on this PC.
        if (!DictationEngineResolver.WantsModelPolish(Settings))
        {
            FailTake(HeyClickyNeedsCleanUpMessage, thisTakeId);
            return null;
        }
        if (polisher is null)
        {
            FailTake($"hey clicky needs a model: {DictationEngineResolver.HowToGetAModelSentence}", thisTakeId);
            return null;
        }
        var selection = field.SelectedText ?? LastTake?.DisplayText ?? "";
        if (selection.Length == 0)
        {
            FailTake("select some text first, or dictate something to edit", thisTakeId);
            return null;
        }
        try
        {
            return await HeyClickyEditor.EditAsync(selection, instruction, context, polisher);
        }
        catch (Exception error)
        {
            if (IsCurrent(thisTakeId)) FailTake($"couldn't finish your edit: {error.Message}", thisTakeId);
            return null;
        }
    }

    /// <summary>
    /// Where a finished take's words go, and the orb's line about it. They are pasted where the
    /// cursor was when the key went down — and only there: never into a password field, another
    /// app, or another element of the same app; otherwise they are left on the clipboard (the caller
    /// puts them there) or, for the tour's practice and a password field, only in the orb. The one
    /// exception: a take that began outside any text box goes to the text box focused now.
    /// </summary>
    private async Task<(TakePasteOutcome Outcome, string Message)> DeliverWhereTheTakeBeganAsync(string outputText, FocusedFieldSnapshot fieldAtPress, Guid thisTakeId)
    {
        const string pastedMessage = "moved to text box";
        if (HoldsTakesOnTheIsland)
        {
            // The tour's practice take: shown on the island, never typed into the app in front
            // (a new user's first words went into their terminal).
            return (TakePasteOutcome.LeftInOrb, "practice take, kept here");
        }
        // The take began outside any text box and the pointer asked for one: a text box focused now
        // is the one the person clicked into for these words, so they go there. Nothing at press was
        // a place to protect. Every check below runs on that field instead.
        var field = fieldAtPress;
        var followsTheTextBoxClickedDuringTheTake = false;
        if (HasNoTextBoxToPasteInto(fieldAtPress, host.IsAccessibilityTrusted()))
        {
            var fieldNow = await host.ReadFocusedFieldAsync(CancellationToken.None);
            if (!fieldNow.IsEditable && !fieldNow.IsSecure && TimeToWaitForATextBoxAfterRelease > TimeSpan.Zero && takeId == thisTakeId)
            {
                // Still nowhere to put them: the words wait a few seconds for a click into a box.
                Orb.Phase = DictationOrbPhase.Working(NoTextBoxOrbHint);
                AppLog.Write($"take {Short(thisTakeId)}: no text box at release; waiting {(int)TimeToWaitForATextBoxAfterRelease.TotalSeconds} s for one");
                var deadline = Now + TimeToWaitForATextBoxAfterRelease;
                while (Now < deadline && takeId == thisTakeId)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider);
                    fieldNow = await host.ReadFocusedFieldAsync(CancellationToken.None);
                    if (fieldNow.IsEditable || fieldNow.IsSecure) break;
                }
            }
            if (fieldNow.IsEditable || fieldNow.IsSecure)
            {
                field = fieldNow;
                followsTheTextBoxClickedDuringTheTake = true;
                AppLog.Write($"take {Short(thisTakeId)}: no text box at press; pasting into the {fieldNow.Role ?? "?"} clicked since, in {fieldNow.AppName ?? "?"}");
            }
        }
        if (field.IsSecure || await host.IsSecureInputOnAsync(CancellationToken.None))
        {
            // Not copied either: a password should not sit on the clipboard (Windows' clipboard
            // history and sync can keep it). It stays in history only.
            return (TakePasteOutcome.LeftInOrb, "not pasting into a password field");
        }
        if (field.AppProcessName is { } appAtPress && host.FrontAppProcessName() is { } appInFrontNow
            && !string.Equals(appAtPress, appInFrontNow, StringComparison.OrdinalIgnoreCase))
        {
            return (TakePasteOutcome.LeftOnPasteboard, "the app changed. copied, press Ctrl+V");
        }
        if (!followsTheTextBoxClickedDuringTheTake && await FocusMovedSincePressAsync())
        {
            return (TakePasteOutcome.LeftOnPasteboard, "the focus moved. copied, press Ctrl+V");
        }
        // Editable, or unknown (the app exposes nothing): try the paste.
        if (!(field.IsEditable || !host.IsAccessibilityTrusted() || field.Role is null))
        {
            return (TakePasteOutcome.LeftOnPasteboard, "no text box. copied, press Ctrl+V");
        }
        if (host.Paste(outputText) == TextInsertionOutcome.LeftOnClipboard)
        {
            // Windows drops keystrokes sent to an app that runs as administrator.
            return (TakePasteOutcome.LeftOnPasteboard, "copied, press Ctrl+V (the app in front didn't take the paste)");
        }
        return await host.VerifyPasteAsync(outputText, field) switch
        {
            PasteLandingResult.Verified => (TakePasteOutcome.Verified, pastedMessage),
            PasteLandingResult.Posted => (TakePasteOutcome.Posted, pastedMessage),
            PasteLandingResult.NoTarget => (TakePasteOutcome.LeftOnPasteboard, "couldn't see it land. copied, press Ctrl+V"),
            _ => (TakePasteOutcome.LeftOnPasteboard, pastedMessage),
        };
    }

    // MARK: a take's context, record and rewrite chips

    /// <summary>The formatting context for words going into an app: its style, the dictionary, the spoken shortcuts, the language and script; captions read near the cursor when there are any.</summary>
    private TakeFormattingContext MakeFormattingContext(string? appProcessName, string? appName, IReadOnlyList<string>? nearbyTerms = null)
    {
        var space = SpaceStore.Space;
        return new TakeFormattingContext
        {
            Style = space.Style(appProcessName),
            Dictionary = space.Dictionary,
            Shortcuts = space.Shortcuts,
            AppName = appName,
            Language = Settings.Language,
            Script = Settings.Script,
            NearbyTerms = nearbyTerms ?? [],
        };
    }

    /// <summary>
    /// A history row, stamped with the language and engine the settings name, so every kind of take
    /// (finished, failed, live-typed, interrupted, retried) records them the same way.
    /// </summary>
    private TakeRecord MakeRecord(Guid recordTakeId, DateTimeOffset createdAt, Dictation.TakeMode mode, TakeStatus status,
                                  string rawText, string formattedText, string? appProcessName, string? appName,
                                  double durationSeconds, TakePasteOutcome pasteOutcome = TakePasteOutcome.None, string? failureReason = null) =>
        new()
        {
            Id = recordTakeId,
            CreatedAt = createdAt.ToLocalTime(),
            Mode = mode,
            Status = status,
            RawText = rawText,
            FormattedText = formattedText,
            AppProcessName = appProcessName,
            AppName = appName,
            Language = Settings.Language.BareCode,
            Engine = Settings.Engine.Id(),
            DurationSeconds = durationSeconds,
            PasteOutcome = pasteOutcome,
            FailureReason = failureReason,
        };

    /// <summary>The rewrite chips under the box: only with the AI clean-up on (off means nothing goes online) and a model to rewrite with.</summary>
    private IReadOnlyList<string> QuickRewriteLabels(ITakePolisher? polisher) =>
        polisher is not null && DictationEngineResolver.WantsModelPolish(Settings) ? QuickRewrites.Select(rewrite => rewrite.Label).ToList() : [];

    // MARK: live typing

    /// <summary>
    /// A take's words are typed as they settle only when: it is a dictation (not a Hey Clicky edit),
    /// no model will polish it (the same decision TakeFormatter.FormatAsync makes), the focused
    /// element is a real text field (not a web page, where letters are shortcuts) and not a password
    /// field, it is not a terminal or OpenClicky's own window (LiveTypingExclusions), and the
    /// focused element is not a password box.
    /// </summary>
    private async Task<LiveTypingSession?> MakeLiveTypingSessionIfWantedAsync(TakeMode mode, FocusedTextTarget? focusedTarget, CancellationToken cancellationToken)
    {
        if (mode != TakeMode.Dictate || HoldsTakesOnTheIsland || fieldAtPress.IsSecure || focusedTarget is null || !focusedTarget.AcceptsLiveTyping) return null;
        if (await host.IsSecureInputOnAsync(cancellationToken)) return null;
        if (LiveTypingExclusions.ReasonNotToType(focusedTarget, fieldAtPress.AppProcessName, host.OwnProcessId, host.OwnProcessName) is { } reason)
        {
            AppLog.Write($"take {Short(takeId)} not typed live: {reason}; the words paste at release");
            return null;
        }
        var space = SpaceStore.Space;
        var context = MakeFormattingContext(fieldAtPress.AppProcessName, fieldAtPress.AppName);
        var modelWillPolish = TakeFormatter.ModelPassWillRun(context, polisherForThisTake, DictationEngineResolver.WantsModelPolish(Settings));
        if (modelWillPolish) return null;
        static IReadOnlyList<string> SplitIntoNormalizedWords(string phrase) =>
            TakeFormatter.NormalizedForMatching(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var dictionaryAliases = space.Dictionary.SelectMany(term => new[] { term.Written }.Concat(term.HeardAs)).Select(SplitIntoNormalizedWords).ToList();
        var shortcutTriggers = space.Shortcuts.Select(shortcut => SplitIntoNormalizedWords(shortcut.Trigger)).ToList();
        return new LiveTypingSession(new StableWordCommitter(dictionaryAliases, shortcutTriggers, LiveTypingTiming), focusedTarget, context);
    }

    /// <summary>
    /// Another element of the same app has focus than at press (the search field instead of the
    /// message box): the paste would land there. Only a confident "different": when either read
    /// fails (an app that answers nothing, no runtime id) the paste goes ahead as before.
    /// </summary>
    private async Task<bool> FocusMovedSincePressAsync()
    {
        if (focusedTargetAtPress is not { RuntimeId: not null } targetAtPress) return false;
        if (await host.ReadFocusedTextTargetAsync(CancellationToken.None) is not { RuntimeId: not null } targetNow) return false;
        return !targetNow.IsSameElement(targetAtPress);
    }

    /// <summary>
    /// A partial transcript: settled words are typed (when this take types live and the key is still
    /// held or the tapped take still listens) and the orb shows what has not been typed yet.
    /// </summary>
    private void HandlePartialTranscript(string partial, Guid thisTakeId)
    {
        if (liveTyping is not { } session)
        {
            Orb.LiveTranscript = partial;
            return;
        }
        // After a late Ctrl the words are an edit instruction: the take keeps what was heard before it.
        if (!session.EditKeyJoinedAfterTyping) session.LatestPartial = partial;
        var takeIsStillListening = State is TakeState.Starting or TakeState.Listening;
        if (session.StoppedReason is null && takeIsStillListening)
        {
            Observe(RunLiveTypingStepAsync(session, thisTakeId, () => CommitAndTypeSettledWordsAsync(partial, personPaused: false, session, thisTakeId)));
            if (session.StoppedReason is null) ScheduleLiveTypingPauseCheck(partial, thisTakeId);
        }
        ShowUntypedWords(session);
    }

    /// <summary>The words are already appearing in the app: the orb shows only the listening pill. Once typing stopped, the untyped words show so nothing is lost.</summary>
    private void ShowUntypedWords(LiveTypingSession session)
    {
        if (!ReferenceEquals(liveTyping, session)) return;
        Orb.LiveTranscript = session.StoppedReason is null ? "" : string.Join(" ", UntypedWords(session.LatestPartial, session));
    }

    /// <summary>One typing step at a time, for the session still in use by this take.</summary>
    private async Task RunLiveTypingStepAsync(LiveTypingSession session, Guid thisTakeId, Func<Task> step)
    {
        await liveTypingGate.WaitAsync();
        try
        {
            if (takeId != thisTakeId || session.IsAbandoned || !ReferenceEquals(liveTyping, session)) return;
            await step();
        }
        finally
        {
            liveTypingGate.Release();
        }
        ShowUntypedWords(session);
    }

    /// <summary>
    /// When the person stops talking no new partial comes, so the last word would wait for the key's
    /// release. After a pause with the partial unchanged, it is typed too.
    /// </summary>
    private void ScheduleLiveTypingPauseCheck(string partial, Guid thisTakeId)
    {
        liveTypingPauseCancellation?.Cancel();
        var pauseCancellation = new CancellationTokenSource();
        liveTypingPauseCancellation = pauseCancellation;
        After(TimeSpan.FromSeconds(Math.Max(0, LiveTypingTiming.PauseSeconds)), () =>
        {
            if (takeId != thisTakeId || State is not (TakeState.Starting or TakeState.Listening)) return;
            if (liveTyping is not { } session || session.StoppedReason is not null || session.LatestPartial != partial) return;
            Observe(RunLiveTypingStepAsync(session, thisTakeId, () => CommitAndTypeSettledWordsAsync(partial, personPaused: true, session, thisTakeId)));
        }, pauseCancellation.Token);
    }

    /// <summary>
    /// Shows the partial to the committer, then types every word that is committed but not typed
    /// yet: the words that settled now, plus any that waited for a held modifier to be let go.
    /// </summary>
    private async Task CommitAndTypeSettledWordsAsync(string partial, bool personPaused, LiveTypingSession session, Guid thisTakeId)
    {
        if (session.StoppedReason is not null) return;
        _ = session.Committer.Observe(partial, host.Now(), personPaused);
        var committedButUntypedWords = session.Committer.CommittedWords.Skip(session.TypedRawWords.Count).ToList();
        if (committedButUntypedWords.Count > 0) await TypeLivePhraseAsync(committedButUntypedWords, session, thisTakeId);
    }

    private static IReadOnlyList<string> UntypedWords(string transcript, LiveTypingSession session)
    {
        var transcriptWords = StableWordCommitter.Words(transcript);
        return TranscriptAlignment.Align(transcriptWords, session.TypedRawWords).RemainingWords(transcriptWords);
    }

    /// <summary>What the take heard that never reached the field, formatted, for the clipboard.</summary>
    private static string HeardButUntypedText(LiveTypingSession session) =>
        LivePhraseFormatter.TextToType(UntypedWords(session.LatestPartial, session), session.FormattingContext, session.TypedText).Trim();

    /// <summary>
    /// Why typing must stop, checked before every typing step: keystrokes go to whatever has focus,
    /// so it must still be the very element the take began in (UI Automation's runtime id and pid —
    /// the foreground window lags right after Alt-Tab), still a text field, and not a password box.
    /// </summary>
    private async Task<string?> LiveTypingStopReasonAsync(LiveTypingSession session)
    {
        if (await host.ReadFocusedTextTargetAsync(CancellationToken.None) is not { } focusedNow) return "the text field lost focus";
        if (focusedNow.ProcessId != session.FocusedTargetAtPress.ProcessId) return "the app changed";
        if (!focusedNow.IsSameElement(session.FocusedTargetAtPress) || !focusedNow.AcceptsLiveTyping) return "the focus moved";
        if (await host.IsSecureInputOnAsync(CancellationToken.None)) return "secure input came on";
        return null;
    }

    /// <summary>Formats and types one phrase. A phrase is only ever typed whole: when typing stops, or waits for a held modifier, none of it was posted.</summary>
    private async Task<LivePhraseTypingResult> TypeLivePhraseAsync(IReadOnlyList<string> phraseWords, LiveTypingSession session, Guid thisTakeId)
    {
        if (await LiveTypingStopReasonAsync(session) is { } reason)
        {
            MarkLiveTypingStopped(session, reason, thisTakeId);
            return LivePhraseTypingResult.Stopped;
        }
        // Cancelled or failed while the field was being read: nothing more goes in.
        if (session.IsAbandoned || takeId != thisTakeId) return LivePhraseTypingResult.Stopped;
        if (!LiveTypingModifierGuard.AllowsTyping(host.HeldModifiers(), Settings.DictationKey))
        {
            // Typed now, the letters could merge with the held key (Ctrl+M is Return). They stay
            // pending and go with the next phrase, or at release.
            return LivePhraseTypingResult.DeferredForHeldModifier;
        }
        var text = LivePhraseFormatter.TextToType(phraseWords, session.FormattingContext, session.TypedText);
        if (LivePhraseFormatter.ContainsControlCharacters(text))
        {
            MarkLiveTypingStopped(session, "a line break can't be typed safely", thisTakeId);
            return LivePhraseTypingResult.Stopped;
        }
        if (text.Length > 0)
        {
            if (!host.TypeText(text))
            {
                MarkLiveTypingStopped(session, "keystrokes could not be posted", thisTakeId);
                return LivePhraseTypingResult.Stopped;
            }
            session.TypedText += text;
            session.TypedPhraseCount += 1;
        }
        session.TypedRawWords.AddRange(phraseWords);
        return LivePhraseTypingResult.Typed;
    }

    private static void MarkLiveTypingStopped(LiveTypingSession session, string reason, Guid thisTakeId)
    {
        if (session.StoppedReason is not null) return;
        session.StoppedReason = reason;
        AppLog.Write($"take {Short(thisTakeId)} live typing stopped: {reason}");
    }

    private void StopLiveTyping(string reason, Guid thisTakeId)
    {
        if (liveTyping is { } session) MarkLiveTypingStopped(session, reason, thisTakeId);
    }

    private static void LogLiveTypedTotal(LiveTypingSession session, Guid thisTakeId) =>
        AppLog.Write($"take {Short(thisTakeId)} live-typed {session.TypedText.Length} chars in {session.TypedPhraseCount} phrases");

    /// <summary>
    /// The key came up on a take that was typed as it was spoken: only the words of the final
    /// transcript that were not typed yet are typed now. Never retyped, never deleted.
    /// </summary>
    private async Task FinishLiveTypedTakeAsync(string finalTranscript, LiveTypingSession session, double duration, Guid thisTakeId)
    {
        // Ctrl joined after words were typed: the final ends in the spoken edit instruction, which is
        // neither typed nor copied; the take is what was heard up to Ctrl.
        var raw = session.EditKeyJoinedAfterTyping ? session.LatestPartial : finalTranscript;
        var finalWords = StableWordCommitter.Words(raw);
        var alignment = TranscriptAlignment.Align(finalWords, session.TypedRawWords);
        if (!alignment.AlignedByWords)
        {
            // Where the typed words end in the final cannot be told: typing on could skip or repeat
            // words, so everything after the last match goes to the clipboard instead.
            MarkLiveTypingStopped(session, "the words were heard differently", thisTakeId);
        }
        var remainingWords = alignment.RemainingWords(finalWords);
        if (session.StoppedReason is null && remainingWords.Count > 0)
        {
            // The dictation key is up by now; a modifier still held is someone else's.
            if (await TypeLivePhraseAsync(remainingWords, session, thisTakeId) == LivePhraseTypingResult.DeferredForHeldModifier)
            {
                MarkLiveTypingStopped(session, "a modifier key was held", thisTakeId);
            }
            if (!IsCurrent(thisTakeId)) return;
        }

        var pasteOutcome = TakePasteOutcome.Posted;
        var message = "typed as you spoke";
        var untypedText = "";
        if (session.StoppedReason is { } reason)
        {
            // Typing stopped before the end: the rest waits on the clipboard for the person's Ctrl+V.
            untypedText = LivePhraseFormatter.TextToType(remainingWords, session.FormattingContext, session.TypedText);
            var untypedForClipboard = untypedText.Trim();
            if (untypedForClipboard.Length > 0)
            {
                host.CopyToClipboard(untypedForClipboard);
                pasteOutcome = TakePasteOutcome.LeftOnPasteboard;
                message = $"{reason}. the rest is copied, press Ctrl+V";
            }
        }
        LogLiveTypedTotal(session, thisTakeId);

        // What reached the app, plus (when typing had to stop) the words left on the clipboard.
        var formattedText = session.TypedText + untypedText;
        var record = MakeRecord(thisTakeId, takeStartedAt, Dictation.TakeMode.Dictate, TakeStatus.Complete,
            raw, formattedText, fieldAtPress.AppProcessName, fieldAtPress.AppName, duration, pasteOutcome);
        LastTake = record;
        if (!Settings.Incognito)
        {
            if (WriteToHistory("live-typed take", thisTakeId, store => store.Insert(record)) is { } historyNote) message += $" · {historyNote}";
            HistoryVersion += 1;
        }
        OnTakeFinished?.Invoke(record);

        var untypedForBox = untypedText.Trim();
        Orb.BoxText = untypedForBox.Length == 0 ? session.TypedText : untypedForBox;
        Orb.LiveTranscript = "";
        Orb.BoxReason = pasteOutcome == TakePasteOutcome.Posted ? "your last take" : message;
        Orb.QuickRewrites = QuickRewriteLabels(polisherForThisTake);
        // The rest is on the clipboard and the orb says so; the box opens only when asked for.
        Orb.IsBoxOpen = Settings.OrbRestsExpanded;
        ScheduleBoxClose();
        Play(pasteOutcome == TakePasteOutcome.Posted ? DictationEarcon.Complete : DictationEarcon.Notify);
        Orb.Phase = DictationOrbPhase.Done(message);
        AppLog.Write($"take {Short(thisTakeId)} done: {formattedText.Length} chars, paste={pasteOutcome}, engine={Settings.Engine.Id()}, live");
        EndTake();
        ScheduleIdle(TimeSpan.FromSeconds(1.8));
    }

    /// <summary>A cancelled or failed take whose first words were already typed: history keeps them. Returns the orb's note when that write failed.</summary>
    private string? RecordInterruptedLiveTypedTake(LiveTypingSession session, Guid thisTakeId, TakeStatus status, string reason)
    {
        if (Settings.Incognito) return null;
        var record = MakeRecord(thisTakeId, takeStartedAt, Dictation.TakeMode.Dictate, status,
            string.Join(" ", session.TypedRawWords), session.TypedText, fieldAtPress.AppProcessName, fieldAtPress.AppName,
            (Now - takeStartedAt).TotalSeconds, TakePasteOutcome.Posted, $"{reason} · words typed before it stay");
        var historyNote = WriteToHistory("interrupted take", thisTakeId, store => store.Insert(record));
        HistoryVersion += 1;
        return historyNote;
    }

    /// <summary>
    /// Writes to history. A failure (the file locked by another OpenClicky past the busy timeout, a
    /// full disk, a damaged database) is logged every time; the first one this run also returns the
    /// words the orb adds to its line, so a take missing from history is never a surprise. Without a
    /// store at all (it could not be opened) the History page says so instead.
    /// </summary>
    private string? WriteToHistory(string whatIsWritten, Guid writtenTakeId, Action<TakeStore> write)
    {
        if (TakeStore is not { } store) return null;
        try
        {
            write(store);
            return null;
        }
        catch (Exception error)
        {
            AppLog.Write($"take {Short(writtenTakeId)} not saved to history ({whatIsWritten}): {error.Message}");
            if (hasShownHistoryWriteFailure) return null;
            hasShownHistoryWriteFailure = true;
            return "not saved to history";
        }
    }

    // MARK: the box and the orb's rest

    /// <summary>A chip under the box: the last take is rewritten by the model and the box shows the result; history keeps the previous text as a revision.</summary>
    public async Task RewriteLastTakeAsync(string label)
    {
        if (Orb.IsRewriting || Orb.BoxText is not { Length: > 0 } text) return;
        var rewrite = QuickRewrites.FirstOrDefault(candidate => candidate.Label == label);
        if (rewrite.Label is null || host.MakePolisher() is not { } polisher) return;
        boxCloseCancellation?.Cancel();
        Orb.IsRewriting = true;
        try
        {
            var context = MakeFormattingContext(LastTake?.AppProcessName, LastTake?.AppName);
            var rewritten = await HeyClickyEditor.EditAsync(text, rewrite.Instruction, context, polisher);
            Orb.BoxText = rewritten;
            Orb.BoxReason = $"{label} · paste or copy";
            Orb.IsBoxOpen = true;
            if (LastTake is { } last && !Settings.Incognito)
            {
                if (WriteToHistory("quick rewrite", last.Id, store => store.Revise(last.Id, rewritten, $"quick-{label}")) is { } historyNote)
                {
                    Orb.BoxReason = $"{label} · paste or copy · {historyNote}";
                }
                LastTake = last with { FormattedText = rewritten };
                HistoryVersion += 1;
            }
            AppLog.Write($"quick rewrite ({label}): {rewritten.Length} chars");
        }
        catch (Exception error)
        {
            Orb.BoxReason = $"couldn't rewrite: {error.Message}";
        }
        finally
        {
            Orb.IsRewriting = false;
        }
        ScheduleBoxClose(TimeSpan.FromSeconds(20));
    }

    /// <summary>"paste" in the box: the words go to the app in front (the orb never takes focus).</summary>
    public void PasteFromBox()
    {
        if (Orb.BoxText is not { Length: > 0 } text) return;
        if (host.FrontAppProcessName() is { } front && IsOwnProcessName(front))
        {
            Orb.BoxReason = "click into the app you want it in, then paste";
            return;
        }
        Orb.BoxReason = host.Paste(text) == TextInsertionOutcome.Typed ? "pasted" : "copied, press Ctrl+V";
        if (LastTake is { PasteOutcome: TakePasteOutcome.LeftInOrb } last && !Settings.Incognito)
        {
            var pasted = last with { PasteOutcome = TakePasteOutcome.Posted };
            if (WriteToHistory("paste from the box", pasted.Id, store => store.Insert(pasted)) is { } historyNote)
            {
                Orb.BoxReason = $"{Orb.BoxReason} · {historyNote}";
            }
            LastTake = pasted;
            HistoryVersion += 1;
        }
        ScheduleBoxClose(TimeSpan.FromSeconds(3));
    }

    private bool IsOwnProcessName(string processName)
    {
        static string WithoutExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return string.Equals(WithoutExe(processName), WithoutExe(host.OwnProcessName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The box is for the moment the words had nowhere to go; it does not stay on screen.</summary>
    private void ScheduleBoxClose(TimeSpan? after = null)
    {
        boxCloseCancellation?.Cancel();
        if (!Orb.IsBoxOpen || Settings.OrbRestsExpanded) return;
        var closeCancellation = new CancellationTokenSource();
        boxCloseCancellation = closeCancellation;
        After(after ?? BoxLingerTime, () =>
        {
            if (IsTakeInProgress || Orb.IsRewriting || Settings.OrbRestsExpanded) return;
            Orb.IsBoxOpen = false;
        }, closeCancellation.Token);
    }

    private void ScheduleIdle(TimeSpan after)
    {
        var expectedPhase = Orb.Phase;
        After(after, () =>
        {
            if (IsTakeInProgress || Orb.Phase != expectedPhase) return;
            Orb.Phase = DictationOrbPhase.Idle;
            Orb.Hint = IdleHint;
        });
    }

    /// <summary>"inactivity timeout": a take with that long of silence ends on its own.</summary>
    private void StartInactivityTimer()
    {
        StopInactivityTimer();
        if (Settings.InactivityTimeoutMinutes <= 0) return;
        var limit = TimeSpan.FromMinutes(Settings.InactivityTimeoutMinutes);
        var checkEvery = TimeSpan.FromSeconds(5);
        inactivityTimer = timeProvider.CreateTimer(_ => mainThread.Post(() =>
        {
            if (State != TakeState.Listening) return;
            if (Now - lastLoudMomentAt >= limit)
            {
                AppLog.Write($"take {Short(takeId)} ended by the inactivity timeout");
                StopTake();
            }
        }), null, checkEvery, checkEvery);
    }

    private void StopInactivityTimer()
    {
        inactivityTimer?.Dispose();
        inactivityTimer = null;
    }

    // MARK: asking history

    /// <summary>The takes a history question is answered from: those containing every longer word of it (empty when none do).</summary>
    public IReadOnlyList<TakeRecord> HistoryMatches(string question)
    {
        var words = question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length > 3).ToList();
        if (words.Count == 0) return [];
        try { return TakeStore?.Recent(60, string.Join(" ", words)) ?? []; }
        catch (Exception) { return []; }
    }

    /// <summary>Asks the configured model a question over the takes that match it (history → "press enter to ask").</summary>
    public async Task<string> AskHistoryAsync(string question, CancellationToken cancellationToken = default)
    {
        if (host.MakePolisher() is not { } polisher) return $"asking needs a model: {DictationEngineResolver.HowToGetAModelSentence}.";
        var candidates = HistoryMatches(question);
        if (candidates.Count == 0)
        {
            try { candidates = TakeStore?.Recent(60) ?? []; }
            catch (Exception) { candidates = []; }
        }
        if (candidates.Count == 0) return "nothing in history to ask about yet.";
        var context = string.Join("\n", candidates.Take(60).Select(take =>
        {
            var when = take.CreatedAt.ToLocalTime().ToString("ddd d MMM, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            var words = take.DisplayText.Replace("\n", " ");
            if (words.Length > 400) words = words[..400];
            return $"[{when}] ({take.AppName ?? "?"}) {words}";
        }));
        const string system = "You answer questions about a person's own dictation history. Use only the takes given; quote the relevant words and say when they were said. If nothing matches, say so in one sentence. Answer in two or three sentences, plainly.";
        try
        {
            return await polisher.PolishAsync(system, $"Question: {question}\n\nTakes:\n{context}", cancellationToken);
        }
        catch (Exception error)
        {
            return $"couldn't ask right now: {error.Message}";
        }
    }

    // MARK: retrying a failed take

    public bool CanRetry(Guid id) => AudioStore.HasAudio(id);

    /// <summary>
    /// Hears a failed take's retained audio again with the current engine, formats it, and puts the
    /// words in the orb's box and in history (the cursor has moved on, so nothing is pasted).
    /// </summary>
    public async Task RetryAsync(Guid id)
    {
        if (IsTakeInProgress || IsRetrying || !AudioStore.HasAudio(id)) return;
        if (host.EngineUnavailableReason(Settings.Engine) is { } reason)
        {
            Orb.Phase = DictationOrbPhase.Failed(reason);
            ScheduleIdle(TimeSpan.FromSeconds(2.5));
            return;
        }
        IsRetrying = true;
        Orb.Phase = DictationOrbPhase.Working("retrying your recording");
        try
        {
            var provider = DictationEngineResolver.MakeProvider(Settings.Engine, Settings, providerFactory);
            TakeRecord? original = null;
            try { original = TakeStore?.Fetch(id); }
            catch (Exception) { }
            var raw = await RecordedAudioTranscription.TranscribeAsync(AudioStore.PathFor(id), provider,
                SpaceStore.Space.Dictionary.Select(term => term.Written).ToList(), pauseBetweenSlices: RetryPauseBetweenSlices);
            if (raw.Trim().Length == 0)
            {
                Orb.Phase = DictationOrbPhase.Failed("didn't catch that");
                ScheduleIdle(TimeSpan.FromSeconds(2.5));
                return;
            }
            var context = MakeFormattingContext(original?.AppProcessName, original?.AppName);
            var formatted = await TakeFormatter.FormatAsync(raw, context, host.MakePolisher(), DictationEngineResolver.WantsModelPolish(Settings));
            var record = MakeRecord(id, original?.CreatedAt ?? Now, Dictation.TakeMode.Dictate, TakeStatus.Complete,
                raw, formatted.Text, original?.AppProcessName, original?.AppName, original?.DurationSeconds ?? 0, TakePasteOutcome.LeftInOrb);
            string? historyNote = null;
            if (!Settings.Incognito) historyNote = WriteToHistory("retried take", id, store => store.Insert(record));
            HistoryVersion += 1;
            LastTake = record;
            AudioStore.Discard(id);
            if (RetryableTakeId == id) RetryableTakeId = null;
            Orb.BoxText = formatted.Text;
            Orb.BoxReason = "retried · copy from here";
            Orb.IsBoxOpen = true;
            ScheduleBoxClose();
            Play(DictationEarcon.Complete);
            Orb.Phase = DictationOrbPhase.Done("retried · copy from the box" + (historyNote is null ? "" : $" · {historyNote}"));
            AppLog.Write($"take {Short(id)} retried: {formatted.Text.Length} chars");
            ScheduleIdle(TimeSpan.FromSeconds(2.5));
        }
        catch (Exception error)
        {
            Orb.Phase = DictationOrbPhase.Failed($"couldn't retry: {error.Message}");
            AppLog.Write($"take {Short(id)} retry failed: {error.GetType().Name}");
            ScheduleIdle(TimeSpan.FromSeconds(3));
        }
        finally
        {
            IsRetrying = false;
        }
    }

    // MARK: history actions used by the window

    /// <summary>Pastes the last take into the app in front, after the window has given focus back.</summary>
    public void PasteLast()
    {
        var text = LastTake?.DisplayText;
        if (text is null)
        {
            try { text = TakeStore?.Recent(1, mode: Dictation.TakeMode.Dictate).FirstOrDefault()?.DisplayText; }
            catch (Exception) { text = null; }
        }
        if (text is null) return;
        HideOwnWindows?.Invoke();
        After(TimeSpan.FromSeconds(0.45), () => _ = host.Paste(text));
    }

    public void HistoryDidChange() => HistoryVersion += 1;

    // MARK: plumbing

    private void Play(DictationEarcon earcon)
    {
        if (Settings.Sounds) earcons?.Play(earcon);
    }

    /// <summary>Runs <paramref name="action"/> on the main thread after <paramref name="delay"/> (DispatchQueue.main.asyncAfter), unless cancelled first.</summary>
    private void After(TimeSpan delay, Action action, CancellationToken cancellationToken = default)
    {
        _ = Task.Delay(delay, timeProvider, cancellationToken).ContinueWith(delayed =>
        {
            if (delayed.IsCanceled || isDisposed) return;
            mainThread.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested || isDisposed) return;
                action();
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>A fire-and-forget step: its failure is logged, never lost silently.</summary>
    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception error) { AppLog.Write($"dictation: {error.GetType().Name}: {error.Message}"); }
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        Settings.PropertyChanged -= OnSettingsChanged;
        Capture.AudioPowerLevelChanged -= OnAudioPowerLevel;
        Capture.ErrorMessageChanged -= OnErrorMessage;
        Capture.SessionActivityChanged -= OnSessionActivity;
        StopInactivityTimer();
        pendingStartCancellation?.Cancel();
        pendingEditStart?.Cancel();
        boxCloseCancellation?.Cancel();
        liveTypingPauseCancellation?.Cancel();
    }
}
