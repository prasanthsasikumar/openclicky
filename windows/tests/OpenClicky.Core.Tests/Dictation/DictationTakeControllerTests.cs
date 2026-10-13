// Ports DictationTakeControllerTests.swift (with the controller parts of LiveTypingTests.swift): the
// take state machine, driven through a fake capture side and a fake host on a test main thread:
// hold, tap, double tap, esc, the hands-free gesture, a cancel while a model is at work, where the
// words go, live typing, history writes, the clean-up switch, the engine rebuilt between takes, the
// retained audio and its retry, and the inactivity timeout.

using Microsoft.Data.Sqlite;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Shortcuts;
using OpenClicky.Core.Tests.Speech;
using static OpenClicky.Core.Dictation.DictationTakeController;

namespace OpenClicky.Core.Tests.Dictation;

/// <summary>A model that waits, then answers the same words whatever it is asked.</summary>
internal sealed class SlowPolisher(double seconds, string answer) : ITakePolisher
{
    public string DisplayName => "slow";

    public async Task<string> PolishAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), CancellationToken.None);
        return answer;
    }
}

/// <summary>A model that answers "Polished." and counts how often it was asked.</summary>
internal sealed class CountingPolisher : ITakePolisher
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public string DisplayName => "counting";

    public Task<string> PolishAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        return Task.FromResult("Polished.");
    }
}

/// <summary>Everything a take touches, faked; the controller lives on <see cref="MainThread"/>.</summary>
internal sealed class TakeHarness : IDisposable
{
    public const int NotesProcessId = 4242;
    public const int OwnProcessId = 99;

    public static readonly FocusedFieldSnapshot NotesBox = Field("notepad.exe", "Notepad", "Edit", editable: true, value: "");
    public static readonly FocusedFieldSnapshot ExplorerList = Field("explorer.exe", "File Explorer", "List", editable: false);

    public static FocusedFieldSnapshot Field(string? processName, string? appName, string? role, bool editable, bool secure = false,
                                             string? value = null, string? selectedText = null) =>
        new() { AppProcessName = processName, AppName = appName, Role = role, IsEditable = editable, IsSecure = secure, Value = value, SelectedText = selectedText };

    public static FocusedTextTarget Target(int processId, string runtimeId, string? role, bool secure = false, string? processName = null) =>
        new() { ProcessId = processId, RuntimeId = runtimeId, Role = role, IsSecure = secure, AppProcessName = processName };

    public SerialMainThread MainThread { get; } = new();
    public DictationSettings Settings { get; }
    public FakeCapture Capture { get; } = new();
    public FakeProviderFactory Factory { get; } = new();
    public TakeStore Store { get; }
    public DictationOrbState Orb { get; } = new();
    public DictationTakeController Controller { get; private set; } = null!;
    public TakeAudioStore AudioStore { get; }

    // What the host saw and what it reports (read and written on the main thread).
    public List<string> Pasted { get; } = [];
    /// <summary>What live typing posted as keystrokes, one entry per typing step.</summary>
    public List<string> Typed { get; } = [];
    /// <summary>What was put on the clipboard without a paste.</summary>
    public List<string> Copied { get; } = [];
    public string? FrontApp { get; set; } = "notepad.exe";
    /// <summary>The clock live typing measures word stability with; tests move it by hand.</summary>
    public DateTimeOffset LiveClock { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_000);
    /// <summary>The focused element as UI Automation reports it (live typing re-reads it every phrase).</summary>
    public FocusedTextTarget? FocusedTarget { get; set; }
    public LiveTypingModifiers HeldModifiers { get; set; } = LiveTypingModifiers.None;
    public PasteLandingResult Landing { get; set; } = PasteLandingResult.Verified;
    public int PolisherBuilds { get; private set; }
    /// <summary>The focused field as the host reads it now (a test moves focus by changing it).</summary>
    public FocusedFieldSnapshot FocusedField { get; set; }
    public List<string> PointerMessages { get; } = [];
    /// <summary>What the host says is missing for the engine (null: it can run).</summary>
    public string? UnavailableReason { get; set; }

    private readonly string scratchDirectory = Path.Combine(Path.GetTempPath(), $"openclicky-take-tests-{Guid.NewGuid():N}");

    public TakeHarness(ITakePolisher? polisher = null, FocusedFieldSnapshot? field = null, TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(scratchDirectory);
        Settings = new DictationSettings(Preferences.InMemory())
        {
            Sounds = false,
            Engine = DictationEngineChoice.Offline,
            ReadNearbyText = false,
            PolishWithModel = true,
            PolishOfflineTakes = polisher is not null,
        };
        Store = new TakeStore(Path.Combine(scratchDirectory, "takes.sqlite"));
        AudioStore = new TakeAudioStore(Path.Combine(scratchDirectory, "audio"));
        var snapshot = field ?? NotesBox;
        FocusedField = snapshot;
        FocusedTarget = Target(NotesProcessId, "notes-box", snapshot.Role, snapshot.IsSecure);
        var host = new DictationHost
        {
            ReadFocusedFieldAsync = _ => Task.FromResult(FocusedField),
            ReadNearbyTermsAsync = _ => Task.FromResult<IReadOnlyList<string>>([]),
            FrontAppProcessName = () => FrontApp,
            IsSecureInputOnAsync = _ => Task.FromResult(false),
            IsAccessibilityTrusted = () => true,
            Paste = text =>
            {
                Pasted.Add(text);
                return TextInsertionOutcome.Typed;
            },
            VerifyPasteAsync = (_, _) => Task.FromResult(Landing),
            TypeText = text =>
            {
                Typed.Add(text);
                return true;
            },
            CopyToClipboard = Copied.Add,
            Now = () => LiveClock,
            ReadFocusedTextTargetAsync = _ => Task.FromResult<FocusedTextTarget?>(FocusedTarget),
            HeldModifiers = () => HeldModifiers,
            MakePolisher = () =>
            {
                PolisherBuilds += 1;
                return polisher;
            },
            EngineUnavailableReason = _ => UnavailableReason,
            OwnProcessId = OwnProcessId,
        };
        MainThread.InvokeAsync(() =>
        {
            Controller = new DictationTakeController(Settings, new DictationSpaceStore(Path.Combine(scratchDirectory, "space")), Store, Orb, Capture,
                                                     host, Factory, MainThread, audioStore: AudioStore, timeProvider: timeProvider)
            {
                LiveTypingTiming = LiveTypingTestTiming.AlgorithmTests,
                // The real wait for a text box after release is 8 s; tests that never click wait less.
                TimeToWaitForATextBoxAfterRelease = TimeSpan.FromSeconds(0.4),
                OnNoTextBoxAtPress = PointerMessages.Add,
            };
        }).Wait();
    }

    /// <summary>Runs a test body on the main thread, as the Mac's tests run on the main actor.</summary>
    public static async Task Run(Func<TakeHarness, Task> body, ITakePolisher? polisher = null, FocusedFieldSnapshot? field = null, TimeProvider? timeProvider = null)
    {
        using var harness = new TakeHarness(polisher, field, timeProvider);
        await harness.MainThread.RunAsync(() => body(harness));
    }

    public DictationTakeController.TakeState State => Controller.State;

    public bool Handle(CompanionShortcutEvent shortcutEvent) => Controller.Handle(shortcutEvent);

    /// <summary>Lets the controller's start task and main-thread hops run.</summary>
    public static Task Settle(double seconds = 0.05) => Task.Delay(TimeSpan.FromSeconds(seconds));

    /// <summary>Waits for a condition the controller reaches asynchronously, up to a few seconds.</summary>
    public static async Task WaitUntil(Func<bool> condition, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
    }

    public Task WaitForState(DictationTakeController.TakeState wanted, double seconds = 5) => WaitUntil(() => State == wanted, seconds);

    /// <summary>Delivers a partial now and again after the stability threshold, so its settled words commit.</summary>
    public void Speak(string partial)
    {
        Capture.DeliverPartial(partial);
        LiveClock = LiveClock.AddSeconds(Controller.LiveTypingTiming.RequiredStableSeconds + 0.1);
        Capture.DeliverPartial(partial);
    }

    /// <summary>A whole held take: press, listen, release, the engine's words.</summary>
    public async Task HoldAndSay(string finalText)
    {
        Handle(CompanionShortcutEvent.DictationPressed);
        await WaitForState(DictationTakeController.TakeState.Listening);
        Handle(CompanionShortcutEvent.DictationReleased(wasTap: false));
        Capture.DeliverFinal(finalText);
        await WaitForState(DictationTakeController.TakeState.Idle);
    }

    public IReadOnlyList<TakeRecord> Rows() => Store.Recent();

    public void BreakTheStore()
    {
        using var connection = new SqliteConnection($"Data Source={Store.FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE IF EXISTS take_revisions; DROP TABLE takes;";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        MainThread.InvokeAsync(() => Controller.Dispose()).Wait(2_000);
        MainThread.Dispose();
        Store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(scratchDirectory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class DictationTakeControllerTests
{
    private static readonly CompanionShortcutEvent Pressed = CompanionShortcutEvent.DictationPressed;
    private static readonly CompanionShortcutEvent Held = CompanionShortcutEvent.DictationReleased(wasTap: false);
    private static readonly CompanionShortcutEvent Tapped = CompanionShortcutEvent.DictationReleased(wasTap: true);

    [Fact]
    public Task HoldingTheKeyPastesTheWordsAndRecordsTheTake() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        Assert.Equal(TakeState.Starting, h.State);
        await h.WaitForState(TakeState.Listening);
        Assert.Equal(TakeState.Listening, h.State);
        Assert.Equal(1, h.Capture.Starts);
        h.Handle(Held);
        Assert.Equal(TakeState.Finishing, h.State);
        Assert.Equal(1, h.Capture.Stops);
        h.Capture.DeliverFinal("hello there");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there"], h.Pasted);
        var rows = h.Rows();
        Assert.Single(rows);
        Assert.Equal("Hello there", rows[0].FormattedText);
        Assert.Equal(TakePasteOutcome.Verified, rows[0].PasteOutcome);
        Assert.Equal("notepad.exe", rows[0].AppProcessName);
        Assert.Equal("offline", rows[0].Engine);
        Assert.Equal(DictationOrbPhase.Done("moved to text box"), h.Orb.Phase);
    });

    [Fact]
    public Task ATapKeepsListeningUntilTheNextPress() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(Tapped);
        Assert.Equal(TakeState.Listening, h.State);
        Assert.Equal(0, h.Capture.Stops);
        Assert.Equal("say it, then tap right Ctrl", h.Orb.Hint);
        h.Handle(Pressed);
        Assert.Equal(TakeState.Finishing, h.State);
        Assert.Equal(1, h.Capture.Stops);
        h.Handle(Tapped);
        Assert.Equal(TakeState.Finishing, h.State);
        Assert.Equal(1, h.Capture.Stops);
    });

    [Fact]
    public Task TwoQuickTapsCancelWithoutPasting() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(Tapped);
        h.Handle(Pressed);
        h.Handle(Tapped);
        h.Handle(CompanionShortcutEvent.DictationDoubleTapped);
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Equal(1, h.Capture.Cancels);
        await TakeHarness.Settle(0.2);
        Assert.Empty(h.Pasted);
        Assert.Empty(h.Rows());
    });

    [Fact]
    public Task EscapeCancelsAListeningTake() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        Assert.True(h.Handle(CompanionShortcutEvent.EscapePressed));
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Equal(1, h.Capture.Cancels);
        Assert.False(h.Handle(CompanionShortcutEvent.EscapePressed));
    });

    [Fact]
    public Task ACancelledTakeNeverPastesIntoTheNextOne() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(Held);
        h.Capture.DeliverFinal("old words");
        await TakeHarness.Settle();
        // esc while the model is at work, then a new take at once.
        h.Handle(CompanionShortcutEvent.EscapePressed);
        Assert.Equal(TakeState.Idle, h.State);
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        Assert.Equal(TakeState.Listening, h.State);
        Assert.Equal(2, h.Capture.Starts);
        await TakeHarness.Settle(0.6);
        // The old polish has finished by now; nothing was pasted and the new take still listens.
        Assert.Empty(h.Pasted);
        Assert.Equal(TakeState.Listening, h.State);
        h.Handle(Held);
        h.Capture.DeliverFinal("new words");
        await TakeHarness.WaitUntil(() => h.Pasted.Count > 0, 8);
        // The slow polisher answers the same for any input; what matters is that only the second take pasted.
        Assert.Equal(["Old words, polished."], h.Pasted);
    }, polisher: new SlowPolisher(0.4, "Old words, polished."));

    [Fact]
    public Task AQuickControlTapNeverOpensAnEditTake() => TakeHarness.Run(async h =>
    {
        // The dictation key + Ctrl tapped twice, as the recogniser reports it: no take, hands-free goes to the companion.
        h.Handle(CompanionShortcutEvent.DictationEditPressed);
        h.Handle(Tapped);
        h.Handle(CompanionShortcutEvent.DictationEditPressed);
        h.Handle(Tapped);
        Assert.False(h.Handle(CompanionShortcutEvent.HandsFreeToggleRequested));
        await TakeHarness.Settle(0.6);
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Equal(0, h.Capture.Starts);
    });

    [Fact]
    public Task ControlHeldPastTheTapWindowIsAnEditTake() => TakeHarness.Run(async h =>
    {
        // An edit needs a model and the clean-up switch on (see the F-18 tests below).
        h.Handle(CompanionShortcutEvent.DictationEditPressed);
        Assert.Equal(TakeState.Idle, h.State);
        await h.WaitForState(TakeState.Listening);
        Assert.Equal(TakeState.Listening, h.State);
        Assert.Equal(DictationOrbPhase.EditListening, h.Orb.Phase);
    }, polisher: new CountingPolisher());

    [Fact]
    public Task ControlJoiningAHeldKeyTurnsTheTakeIntoAnEdit() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(CompanionShortcutEvent.DictationEditModifierJoined);
        Assert.Equal(DictationOrbPhase.EditListening, h.Orb.Phase);
        Assert.Equal(1, h.Capture.Starts);
    });

    [Fact]
    public Task NothingHeardIsNotRecorded() => TakeHarness.Run(async h =>
    {
        await h.HoldAndSay("   ");
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Empty(h.Pasted);
        Assert.Empty(h.Rows());
        Assert.Equal(DictationOrbPhase.Failed("didn't catch that"), h.Orb.Phase);
    });

    /// <summary>The tour's practice take is heard and recorded but never typed or pasted into the app in front.</summary>
    [Fact]
    public Task APracticeTakeIsKeptOffTheAppInFront() => TakeHarness.Run(async h =>
    {
        h.Controller.HoldsTakesOnTheIsland = true;
        await h.HoldAndSay("this is a test");
        Assert.Empty(h.Pasted);
        Assert.Empty(h.Typed);
        Assert.Equal(TakePasteOutcome.LeftInOrb, h.Controller.LastTake?.PasteOutcome);
        Assert.Contains("this is a test", h.Controller.LastTake?.DisplayText.ToLowerInvariant());
    });

    /// <summary>
    /// A microphone that hears nothing: the offline recogniser wrote the silence down as "no", which
    /// was pasted. A take whose microphone never heard a voice pastes nothing; the same words from a
    /// microphone that did are pasted.
    /// </summary>
    [Fact]
    public Task WordsFromASilentMicrophoneAreNotPasted() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        for (var index = 0; index < 12; index++) h.Capture.SendLevel(0.004);
        await TakeHarness.Settle(0.15);
        h.Handle(Held);
        h.Capture.DeliverFinal("no");
        await h.WaitForState(TakeState.Idle);
        Assert.Empty(h.Pasted);
        Assert.Equal(DictationOrbPhase.Failed("didn't catch that. check the microphone"), h.Orb.Phase);

        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        foreach (var level in new[] { 0.02, 0.3, 0.5, 0.4, 0.2, 0.1, 0.3, 0.2, 0.1, 0.05, 0.02, 0.01 }) h.Capture.SendLevel(level);
        await TakeHarness.Settle(0.15);
        h.Handle(Held);
        h.Capture.DeliverFinal("no");
        await TakeHarness.WaitUntil(() => h.Pasted.Count > 0);
        Assert.NotEmpty(h.Pasted);
    });

    [Fact]
    public Task ASessionThatEndsWithoutWordsFailsTheTake() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(Held);
        // The engine gave up without a transcript: the manager's session flag drops.
        h.Capture.CancelCurrentDictation(preserveDraftText: false);
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Equal(DictationOrbPhase.Failed("didn't catch that"), h.Orb.Phase);
    });

    /// <summary>F-30: a start that came back with no session and no message left the orb saying "moving your words" until esc.</summary>
    [Fact]
    public Task AStartThatOpensNoSessionEndsTheTakeAndTheKeyWorksAgain() => TakeHarness.Run(async h =>
    {
        h.Capture.NextStartFails = true;
        h.Handle(Pressed);
        await TakeHarness.WaitUntil(() => h.State is TakeState.Listening or TakeState.Idle, 1);
        h.Handle(Held);
        await h.WaitForState(TakeState.Idle, 3);
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Equal(DictationOrbPhase.Failed("couldn't open the microphone"), h.Orb.Phase);
        Assert.Empty(h.Pasted);

        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        Assert.Equal(2, h.Capture.Starts);
        Assert.Equal(TakeState.Listening, h.State);
    });

    /// <summary>The manager's own reason (the permission sentence) is what the orb shows, not a generic one.</summary>
    [Fact]
    public Task ARefusedStartShowsTheManagersReason() => TakeHarness.Run(async h =>
    {
        h.Capture.NextStartFails = true;
        h.Capture.NextStartFailureMessage = "microphone permission is required for push to talk.";
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Idle, 3);
        await TakeHarness.Settle(0.3);
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Equal(DictationOrbPhase.Failed("microphone permission is required for push to talk."), h.Orb.Phase);
    });

    [Fact]
    public Task TheWordsAreCopiedWhenTheAppChanged() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(Held);
        h.FrontApp = "msedge.exe";
        h.Capture.DeliverFinal("hello");
        await h.WaitForState(TakeState.Idle);
        Assert.Empty(h.Pasted);
        // Nowhere to put them: on the clipboard, one line on the orb, no box.
        Assert.Equal(["Hello"], h.Copied);
        Assert.False(h.Orb.IsBoxOpen);
        Assert.Equal(DictationOrbPhase.Done("the app changed. copied, press Ctrl+V"), h.Orb.Phase);
        Assert.Equal(TakePasteOutcome.LeftOnPasteboard, h.Rows()[0].PasteOutcome);
    });

    [Fact]
    public Task WithTheCleanUpOffTheBoxOffersNoAIRewrites() => TakeHarness.Run(async h =>
    {
        h.Settings.PolishWithModel = false;
        h.Settings.ChoosePolishOfflineTakes(false);
        h.Settings.OrbRestsExpanded = true;
        await h.HoldAndSay("hello");
        Assert.Empty(h.Orb.QuickRewrites);
    });

    [Fact]
    public Task ASecureFieldIsNeverPastedInto() => TakeHarness.Run(async h =>
    {
        h.FrontApp = "msedge.exe";
        await h.HoldAndSay("my password");
        Assert.Empty(h.Pasted);
        Assert.Empty(h.Copied);
        Assert.Empty(h.Typed);
        Assert.False(h.Orb.IsBoxOpen);
        Assert.Equal(DictationOrbPhase.Done("not pasting into a password field"), h.Orb.Phase);
    }, field: TakeHarness.Field("msedge.exe", "Microsoft Edge", "Edit", editable: false, secure: true));

    // MARK: no text box at press (the pointer asks for one)

    [Fact]
    public Task ATakeWithNoTextBoxAsksThePointerToSaySo() => TakeHarness.Run(async h =>
    {
        h.FrontApp = "explorer.exe";
        h.Handle(Pressed);
        Assert.Equal([NoTextBoxPointerMessage], h.PointerMessages);
        Assert.Equal(NoTextBoxOrbHint, h.Orb.Hint);
        await h.WaitForState(TakeState.Listening);
        h.Handle(Held);
        h.Capture.DeliverFinal("hello");
        await h.WaitForState(TakeState.Idle);
        // Never clicked into one: the words are copied, as before.
        Assert.Empty(h.Pasted);
        Assert.Equal(["Hello"], h.Copied);
        Assert.Equal(DictationOrbPhase.Done("no text box. copied, press Ctrl+V"), h.Orb.Phase);
    }, field: TakeHarness.ExplorerList);

    [Fact]
    public Task ATextBoxClickedJustAfterLettingGoGetsTheWords() => TakeHarness.Run(async h =>
    {
        h.FrontApp = "explorer.exe";
        h.Controller.TimeToWaitForATextBoxAfterRelease = TimeSpan.FromSeconds(3);
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(Held);
        h.Capture.DeliverFinal("hello there");
        await TakeHarness.WaitUntil(() => h.Orb.Phase == DictationOrbPhase.Working(NoTextBoxOrbHint));
        Assert.Empty(h.Pasted);
        // A moment after letting go, the person clicks into a note.
        await TakeHarness.Settle(0.3);
        h.FrontApp = "notepad.exe";
        h.FocusedField = TakeHarness.NotesBox;
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there"], h.Pasted);
        Assert.Empty(h.Copied);
    }, field: TakeHarness.ExplorerList);

    [Fact]
    public Task ATextBoxClickedDuringTheTakeGetsTheWords() => TakeHarness.Run(async h =>
    {
        h.FrontApp = "explorer.exe";
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        // The person clicks into a note while still talking: another app, another element.
        h.FrontApp = "notepad.exe";
        h.FocusedField = TakeHarness.NotesBox;
        h.FocusedTarget = TakeHarness.Target(5151, "other-box", "Edit");
        h.Handle(Held);
        h.Capture.DeliverFinal("hello there");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there"], h.Pasted);
        Assert.Empty(h.Copied);
        Assert.Equal(DictationOrbPhase.Done("moved to text box"), h.Orb.Phase);
        Assert.Equal(TakePasteOutcome.Verified, h.Rows()[0].PasteOutcome);
    }, field: TakeHarness.ExplorerList);

    [Fact]
    public Task APasswordFieldClickedDuringTheTakeGetsNothing() => TakeHarness.Run(async h =>
    {
        h.FrontApp = "explorer.exe";
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.FrontApp = "msedge.exe";
        h.FocusedField = TakeHarness.Field("msedge.exe", "Microsoft Edge", "Edit", editable: false, secure: true);
        h.Handle(Held);
        h.Capture.DeliverFinal("my password");
        await h.WaitForState(TakeState.Idle);
        Assert.Empty(h.Pasted);
        Assert.Empty(h.Copied);
        Assert.Equal(DictationOrbPhase.Done("not pasting into a password field"), h.Orb.Phase);
    }, field: TakeHarness.ExplorerList);

    [Fact]
    public Task ATakeInATextBoxDoesNotAskThePointer() => TakeHarness.Run(h =>
    {
        h.Handle(Pressed);
        Assert.Empty(h.PointerMessages);
        Assert.Equal("tap / release to transcribe", h.Orb.Hint);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AnUnknownFieldDoesNotAskThePointer() => TakeHarness.Run(h =>
    {
        // An app that exposes nothing (no role): the paste is tried, so nothing is said.
        h.Handle(Pressed);
        Assert.Empty(h.PointerMessages);
        return Task.CompletedTask;
    }, field: TakeHarness.Field("example.exe", "Example", null, editable: false));

    [Fact]
    public Task TheToursPracticeTakeDoesNotAskThePointer() => TakeHarness.Run(h =>
    {
        h.Controller.HoldsTakesOnTheIsland = true;
        h.Handle(Pressed);
        Assert.Empty(h.PointerMessages);
        return Task.CompletedTask;
    }, field: TakeHarness.ExplorerList);

    [Fact]
    public void NoTextBoxIsOnlySaidWhenAccessibilityCanTell()
    {
        Assert.True(HasNoTextBoxToPasteInto(TakeHarness.ExplorerList, isAccessibilityTrusted: true));
        Assert.False(HasNoTextBoxToPasteInto(TakeHarness.ExplorerList, isAccessibilityTrusted: false));
        Assert.False(HasNoTextBoxToPasteInto(TakeHarness.NotesBox, isAccessibilityTrusted: true));
        Assert.False(HasNoTextBoxToPasteInto(FocusedFieldSnapshot.Unknown, isAccessibilityTrusted: true));
        Assert.False(HasNoTextBoxToPasteInto(TakeHarness.Field("msedge.exe", "Microsoft Edge", "Edit", editable: false, secure: true), isAccessibilityTrusted: true));
    }

    [Fact]
    public Task IncognitoPastesButRecordsNothing() => TakeHarness.Run(async h =>
    {
        h.Settings.Incognito = true;
        await h.HoldAndSay("secret plans");
        Assert.Equal(["Secret plans"], h.Pasted);
        Assert.Empty(h.Rows());
    });

    // MARK: live typing (no model polish: the words go in as they settle)

    [Fact]
    public Task WhenThePersonPausesTheLastWordIsTypedBeforeRelease() => TakeHarness.Run(async h =>
    {
        h.Controller.LiveTypingTiming = LiveTypingTestTiming.AlgorithmTests with { PauseSeconds = 0.05 };
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("this is a test message");
        h.Speak("this is a test message");
        // These tests hold back two words: "test message" waits while the person is still talking.
        Assert.Equal("This is a", string.Concat(h.Typed));
        // No new partial: after the pause the held-back words go in while the key is still held.
        await TakeHarness.WaitUntil(() => string.Concat(h.Typed) == "This is a test message");
        Assert.Equal("This is a test message", string.Concat(h.Typed));
        Assert.Equal(TakeState.Listening, h.State);
    });

    [Fact]
    public Task WithoutPolishTheSettledWordsAreTypedWhileSpeakingAndTheRestAtRelease() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Equal(["Hello there"], h.Typed);
        // The words are appearing in the app, so the orb shows only the listening pill.
        Assert.Equal("", h.Orb.LiveTranscript);
        h.Speak("hello there my friend how are you");
        // "my friend" had already settled and is now clear of the tail; "how" settles a moment later.
        Assert.Equal(["Hello there", " my friend", " how"], h.Typed);
        h.Handle(Held);
        h.Capture.DeliverFinal("Hello there, my friend, how are you?");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there", " my friend", " how", " are you?"], h.Typed);
        Assert.Empty(h.Pasted);
        var record = h.Rows()[0];
        Assert.Equal("Hello there, my friend, how are you?", record.RawText);
        Assert.Equal("Hello there my friend how are you?", record.FormattedText);
        Assert.Equal(TakePasteOutcome.Posted, record.PasteOutcome);
        Assert.Equal(TakeStatus.Complete, record.Status);
        Assert.Equal(DictationOrbPhase.Done("typed as you spoke"), h.Orb.Phase);
    });

    [Fact]
    public Task ATakeThatWillBePolishedIsNotTypedLive() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Empty(h.Typed);
        Assert.Equal("hello there my friend", h.Orb.LiveTranscript);
        h.Handle(Held);
        h.Capture.DeliverFinal("hello there my friend");
        await h.WaitForState(TakeState.Idle);
        Assert.Empty(h.Typed);
        Assert.Equal(["Hello there, my friend."], h.Pasted);
    }, polisher: new SlowPolisher(0.05, "Hello there, my friend."));

    [Fact]
    public Task AFieldThatIsNotEditableIsNotTypedInto() => TakeHarness.Run(async h =>
    {
        h.FrontApp = "explorer.exe";
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Empty(h.Typed);
    }, field: TakeHarness.Field("explorer.exe", "File Explorer", "Text", editable: false));

    [Fact]
    public Task WhenTheAppChangesTypingStopsAndTheRestGoesToTheClipboard() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Equal(["Hello there"], h.Typed);
        // Edge's text field has focus now (UI Automation's pid decides, not the foreground window).
        var notesTarget = h.FocusedTarget;
        h.FocusedTarget = TakeHarness.Target(5151, "edge-box", "Edit");
        h.Speak("hello there my friend and more words");
        Assert.Equal(["Hello there"], h.Typed);
        // Back in the original field: typing stays stopped for this take.
        h.FocusedTarget = notesTarget;
        h.Speak("hello there my friend and more words to come");
        Assert.Equal(["Hello there"], h.Typed);
        Assert.Equal("my friend and more words to come", h.Orb.LiveTranscript);
        h.Handle(Held);
        h.Capture.DeliverFinal("hello there my friend and more words to come");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there"], h.Typed);
        Assert.Empty(h.Pasted);
        Assert.Equal(["my friend and more words to come"], h.Copied);
        Assert.Equal("my friend and more words to come", h.Orb.BoxText);
        Assert.Equal(TakePasteOutcome.LeftOnPasteboard, h.Rows()[0].PasteOutcome);
        Assert.Equal(DictationOrbPhase.Done("the app changed. the rest is copied, press Ctrl+V"), h.Orb.Phase);
    });

    [Fact]
    public Task FocusMovingInsideTheSameAppStopsTyping() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Equal(["Hello there"], h.Typed);
        // Same app, another element (one tab's box → another's, the compose box → the message list).
        h.FocusedTarget = TakeHarness.Target(TakeHarness.NotesProcessId, "another-element", "Edit");
        h.Speak("hello there my friend and more words");
        Assert.Equal(["Hello there"], h.Typed);
        h.Handle(Held);
        h.Capture.DeliverFinal("hello there my friend and more words");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there"], h.Typed);
        Assert.Equal(["my friend and more words"], h.Copied);
    });

    [Fact]
    public Task AWebPageWithFocusIsNotTypedInto() => TakeHarness.Run(async h =>
    {
        h.FrontApp = "chrome.exe";
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("archive this one please");
        Assert.Empty(h.Typed);
        h.Handle(Held);
        h.Capture.DeliverFinal("archive this one please");
        await h.WaitForState(TakeState.Idle);
        // The old path: one paste of the whole take.
        Assert.Equal(["Archive this one please"], h.Pasted);
    }, field: TakeHarness.Field("chrome.exe", "Google Chrome", "Document", editable: true));

    [Fact]
    public Task ALineBreakInADictionaryTermIsNeverTypedAndTheRestIsCopied() => TakeHarness.Run(async h =>
    {
        h.Controller.SpaceStore.Update(space => space.Dictionary = [new DictionaryTerm("first\nsecond", ["two lines"])]);
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Equal(["Hello there"], h.Typed);
        h.Speak("hello there my friend two lines and more");
        // "my friend" settles first and is typed; "two lines" becomes "first\nsecond" and stops typing.
        Assert.Equal(["Hello there", " my friend"], h.Typed);
        h.Handle(Held);
        h.Capture.DeliverFinal("hello there my friend two lines and more");
        await h.WaitForState(TakeState.Idle);
        Assert.All(h.Typed, typed => Assert.DoesNotContain("\n", typed));
        Assert.Equal(["Hello there", " my friend"], h.Typed);
        // Formatted as a finished take would be: a new line starts a sentence.
        Assert.Equal(["first\nSecond and more"], h.Copied);
    });

    [Fact]
    public Task AHeldModifierDefersTypingUntilItIsReleased() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        // The dictation key (right Ctrl) and Alt: Alt is someone else's.
        h.HeldModifiers = LiveTypingModifiers.Control | LiveTypingModifiers.Alt;
        h.Speak("hello there my friend");
        Assert.Empty(h.Typed);
        h.HeldModifiers = LiveTypingModifiers.Control;
        h.Speak("hello there my friend how are you");
        // The deferred words go first, with the ones that settled since.
        Assert.Equal("Hello there my friend", h.Typed[0]);
        Assert.Equal("Hello there my friend how", string.Concat(h.Typed));
    });

    [Fact]
    public Task ALatePartialAfterReleaseIsNeverTyped() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        h.Handle(Held);
        Assert.Equal(TakeState.Finishing, h.State);
        h.Speak("hello there my friend how are you today");
        Assert.Equal(["Hello there"], h.Typed);
        h.Capture.DeliverFinal("hello there my friend how are you today");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there", " my friend how are you today"], h.Typed);
        Assert.Empty(h.Pasted);
    });

    /// <summary>Ctrl after words were typed: typing stops there and the spoken edit is never typed (F-24).</summary>
    [Fact]
    public Task ControlJoiningAfterWordsWereTypedStopsTypingAndNeverTypesTheEdit() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Equal(["Hello there"], h.Typed);
        h.Handle(CompanionShortcutEvent.DictationEditModifierJoined);
        Assert.Equal(DictationOrbPhase.Listening, h.Orb.Phase);
        Assert.Equal("already typing, stopped here. let go, then start an edit", h.Orb.Hint);
        // The edit instruction is heard but never typed.
        h.Speak("hello there my friend make it formal");
        Assert.Equal(["Hello there"], h.Typed);
        h.Handle(Held);
        h.Capture.DeliverFinal("hello there my friend make it formal");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there"], h.Typed);
        Assert.Empty(h.Pasted);
        // What was dictated before Ctrl and not typed yet is copied; the instruction is not.
        Assert.Equal(["my friend"], h.Copied);
        var record = h.Rows()[0];
        Assert.Equal(OpenClicky.Core.Dictation.TakeMode.Dictate, record.Mode);
        Assert.Equal("hello there my friend", record.RawText);
        Assert.DoesNotContain("formal", record.FormattedText);
    });

    // MARK: where the words may go

    /// <summary>A terminal's focused view is an Edit, but letters are commands there (F-07).</summary>
    [Fact]
    public Task ATerminalIsNeverTypedIntoLiveAndGetsThePaste() => TakeHarness.Run(async h =>
    {
        h.FrontApp = "WindowsTerminal.exe";
        h.FocusedTarget = TakeHarness.Target(6060, "terminal-view", "Edit", processName: "WindowsTerminal.exe");
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("quit now and type hello world");
        Assert.Empty(h.Typed);
        h.Handle(Held);
        h.Capture.DeliverFinal("quit now and type hello world");
        await h.WaitForState(TakeState.Idle);
        Assert.Empty(h.Typed);
        Assert.Equal(["Quit now and type hello world"], h.Pasted);
    }, field: TakeHarness.Field("WindowsTerminal.exe", "Terminal", "Edit", editable: true, value: ""));

    /// <summary>The record page's box is OpenClicky's own: typed live it would get the words twice (F-23).</summary>
    [Fact]
    public Task OpenClickysOwnWindowIsNeverTypedIntoLive() => TakeHarness.Run(async h =>
    {
        h.FocusedTarget = TakeHarness.Target(TakeHarness.OwnProcessId, "record-box", "Edit");
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Empty(h.Typed);
        h.Handle(Held);
        h.Capture.DeliverFinal("hello there my friend");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there my friend"], h.Pasted);
    });

    /// <summary>The paste goes only into the element the key went down in (F-28).</summary>
    [Fact]
    public Task FocusMovingInsideTheSameAppRefusesThePaste() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(Held);
        // Same app, another element: the search box, not the document.
        h.FocusedTarget = TakeHarness.Target(TakeHarness.NotesProcessId, "search-box", "Edit");
        h.Capture.DeliverFinal("hello");
        await h.WaitForState(TakeState.Idle);
        Assert.Empty(h.Pasted);
        Assert.Equal(["Hello"], h.Copied);
        Assert.Equal(DictationOrbPhase.Done("the focus moved. copied, press Ctrl+V"), h.Orb.Phase);
        Assert.Equal(TakePasteOutcome.LeftOnPasteboard, h.Rows()[0].PasteOutcome);
    });

    /// <summary>A paste that cannot be read back is not reported as landed (F-29).</summary>
    [Fact]
    public Task APasteThatCannotBeSeenLandingIsLeftOnTheClipboard() => TakeHarness.Run(async h =>
    {
        h.Landing = PasteLandingResult.NoTarget;
        await h.HoldAndSay("hello");
        Assert.Equal(["Hello"], h.Pasted);
        Assert.Equal(["Hello"], h.Copied);
        Assert.Equal(DictationOrbPhase.Done("couldn't see it land. copied, press Ctrl+V"), h.Orb.Phase);
        Assert.Equal(TakePasteOutcome.LeftOnPasteboard, h.Rows()[0].PasteOutcome);
    });

    // MARK: history writes

    /// <summary>A write history refuses is said once on the orb and logged, never silently lost (F-33).</summary>
    [Fact]
    public Task AFailedHistoryWriteIsShownOnceOnTheOrb() => TakeHarness.Run(async h =>
    {
        // Break the store underneath: every insert from now on fails.
        h.BreakTheStore();
        foreach (var expectedMessage in new[] { "moved to text box · not saved to history", "moved to text box" })
        {
            await h.HoldAndSay("hello");
            Assert.Equal(DictationOrbPhase.Done(expectedMessage), h.Orb.Phase);
            await TakeHarness.Settle(0.1);
        }
    });

    [Fact]
    public Task AFailedTakeCopiesTheHeardButUntypedWords() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        h.Capture.SendError("the socket closed");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello there"], h.Typed);
        Assert.Equal(["my friend"], h.Copied);
        var record = h.Rows()[0];
        Assert.Equal(TakeStatus.Failed, record.Status);
        Assert.Equal("Hello there", record.FormattedText);
        Assert.Equal(DictationOrbPhase.Failed("the socket closed. the rest is copied, press Ctrl+V"), h.Orb.Phase);
    });

    [Fact]
    public Task CancellingLeavesTheTypedWordsAndRecordsThem() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Speak("hello there my friend");
        Assert.Equal(["Hello there"], h.Typed);
        Assert.True(h.Handle(CompanionShortcutEvent.EscapePressed));
        Assert.Equal(TakeState.Idle, h.State);
        await TakeHarness.Settle(0.2);
        Assert.Equal(["Hello there"], h.Typed);
        Assert.Empty(h.Pasted);
        Assert.Empty(h.Copied);
        var record = h.Rows()[0];
        Assert.Equal(TakeStatus.Cancelled, record.Status);
        Assert.Equal("Hello there", record.FormattedText);
        Assert.Equal(TakePasteOutcome.Posted, record.PasteOutcome);
    });

    // MARK: the dictation key used for a shortcut (F-22)

    [Fact]
    public Task AShortcutTypedWithTheKeyHeldThrowsTheTakeAway() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(CompanionShortcutEvent.DictationReleased(wasTap: false, keyWasPressedDuringHold: true));
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Equal(1, h.Capture.Cancels);
        Assert.Equal(0, h.Capture.Stops);
        await TakeHarness.Settle(0.2);
        Assert.Empty(h.Pasted);
        Assert.Empty(h.Rows());
        Assert.Equal(DictationOrbPhase.Failed("cancelled, that was a shortcut"), h.Orb.Phase);
    });

    // MARK: hey clicky and the clean-up switch (F-18)

    private static readonly FocusedFieldSnapshot SelectedTypo =
        TakeHarness.Field("notepad.exe", "Notepad", "Edit", editable: true, value: "see you tomorow", selectedText: "see you tomorow");

    [Fact]
    public async Task WithTheCleanUpOffAHeyClickyEditSendsNothingToAModel()
    {
        var polisher = new CountingPolisher();
        await TakeHarness.Run(async h =>
        {
            h.Settings.PolishWithModel = false;
            h.Handle(Pressed);
            await h.WaitForState(TakeState.Listening);
            h.Handle(CompanionShortcutEvent.DictationEditModifierJoined);
            Assert.Equal(HeyClickyNeedsCleanUpMessage, h.Orb.Hint);
            h.Handle(Held);
            h.Capture.DeliverFinal("make it formal");
            await h.WaitForState(TakeState.Idle);
            Assert.Empty(h.Pasted);
            Assert.Equal(DictationOrbPhase.Failed(HeyClickyNeedsCleanUpMessage), h.Orb.Phase);
        }, polisher: polisher, field: SelectedTypo);
        Assert.Equal(0, polisher.Calls);
    }

    [Fact]
    public Task WithTheCleanUpOffAControlFirstEditNeverOpensTheMicrophone() => TakeHarness.Run(async h =>
    {
        h.Settings.PolishWithModel = false;
        h.Handle(CompanionShortcutEvent.DictationEditPressed);
        await TakeHarness.Settle(0.6);
        Assert.Equal(0, h.Capture.Starts);
        Assert.Equal(TakeState.Idle, h.State);
        Assert.Equal(DictationOrbPhase.Failed(HeyClickyNeedsCleanUpMessage), h.Orb.Phase);
    }, polisher: new CountingPolisher());

    [Fact]
    public async Task WithTheCleanUpOnAHeyClickyEditStillGoesToTheModel()
    {
        var polisher = new CountingPolisher();
        await TakeHarness.Run(async h =>
        {
            h.Handle(Pressed);
            await h.WaitForState(TakeState.Listening);
            h.Handle(CompanionShortcutEvent.DictationEditModifierJoined);
            h.Handle(Held);
            h.Capture.DeliverFinal("make it formal");
            await h.WaitForState(TakeState.Idle);
            Assert.Equal(["Polished."], h.Pasted);
            Assert.Equal(OpenClicky.Core.Dictation.TakeMode.Edit, h.Rows()[0].Mode);
        }, polisher: polisher, field: SelectedTypo);
        Assert.Equal(1, polisher.Calls);
    }

    // MARK: engine or language changed during a take (F-19)

    [Fact]
    public Task ALanguageChangedDuringATakeIsUsedByTheNextTake() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Settings.LanguageCode = "hi-IN";
        await TakeHarness.Settle(0.1);
        // Not under the take that is listening.
        Assert.Equal(0, h.Capture.ProviderReplacements);
        Assert.True(h.Handle(CompanionShortcutEvent.EscapePressed));
        await TakeHarness.Settle(0.1);
        h.Handle(Pressed);
        Assert.Equal(1, h.Capture.ProviderReplacements);
        await h.WaitForState(TakeState.Listening);
        Assert.True(h.Handle(CompanionShortcutEvent.EscapePressed));
        // Nothing changed since: the next take keeps the engine it has.
        h.Handle(Pressed);
        Assert.Equal(1, h.Capture.ProviderReplacements);
    });

    [Fact]
    public Task ALanguageChangedWhileIdleRebuildsTheEngineOnce() => TakeHarness.Run(async h =>
    {
        h.Settings.LanguageCode = "ta-IN";
        await TakeHarness.WaitUntil(() => h.Capture.ProviderReplacements > 0);
        Assert.Equal(1, h.Capture.ProviderReplacements);
        Assert.Equal("offline", h.Factory.Built[^1].DisplayName);
        h.Handle(Pressed);
        Assert.Equal(1, h.Capture.ProviderReplacements);
    });

    /// <summary>Live typing's decision, the clean-up and the box's chips all read one polisher per take.</summary>
    [Fact]
    public Task ATakeBuildsItsPolisherOnce() => TakeHarness.Run(async h =>
    {
        await h.HoldAndSay("hello there");
        Assert.Equal(["Polished."], h.Pasted);
        Assert.Equal(1, h.PolisherBuilds);
        Assert.Equal(["formal", "casual", "shorter"], h.Orb.QuickRewrites);
    }, polisher: new CountingPolisher());

    // MARK: retained audio and its retry

    /// <summary>One second of a quiet tone at 16 kHz, as the microphone capture hands it over.</summary>
    private static AudioFrame OneSecondOfSound()
    {
        var samples = new float[16_000];
        for (var index = 0; index < samples.Length; index++) samples[index] = 0.1f * MathF.Sin(index * 0.05f);
        return new AudioFrame(samples, Core.Speech.BuddyPCM16AudioConverter.FloatToPcm16(samples), 16_000, 0.1f);
    }

    /// <summary>
    /// A failed take's audio is kept for a retry only when a network engine heard it (the rule in
    /// TakeAudioStore's header): an engine on this PC has no key, credits or network to wait for.
    /// </summary>
    [Theory]
    [InlineData(DictationEngineChoice.Offline)]
    [InlineData(DictationEngineChoice.Parakeet)]
    [InlineData(DictationEngineChoice.Sarvam)]
    public Task OnlyANetworkEnginesFailedTakeKeepsItsAudio(DictationEngineChoice engine) => TakeHarness.Run(async h =>
    {
        h.Settings.Engine = engine;
        await TakeHarness.Settle();
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Capture.AudioRetentionSink?.Invoke(OneSecondOfSound());
        h.Capture.SendError("the network went away");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(!engine.IsOnDevice(), h.Controller.RetryableTakeId is not null);
        if (!engine.IsOnDevice()) Assert.Equal(DictationOrbPhase.Failed("the network went away · take saved, retry from history"), h.Orb.Phase);
    });

    [Fact]
    public Task ARetriedTakeIsHeardAgainAndLandsInTheBox() => TakeHarness.Run(async h =>
    {
        h.Settings.Engine = DictationEngineChoice.Sarvam;
        h.Factory.RetryFinalText = "hello again";
        h.Controller.RetryPauseBetweenSlices = TimeSpan.Zero;
        await TakeHarness.Settle();
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Capture.AudioRetentionSink?.Invoke(OneSecondOfSound());
        h.Capture.SendError("the network went away");
        await h.WaitForState(TakeState.Idle);
        var failedTakeId = Assert.IsType<Guid>(h.Controller.RetryableTakeId);
        Assert.True(h.Controller.CanRetry(failedTakeId));
        Assert.Equal(TakeStatus.Failed, h.Rows()[0].Status);

        await h.Controller.RetryAsync(failedTakeId);
        Assert.Null(h.Controller.RetryableTakeId);
        Assert.False(h.Controller.CanRetry(failedTakeId));
        Assert.Equal(32_000, h.Factory.Built[^1].AudioBytesHeard);
        var retried = h.Rows().Single();
        Assert.Equal(failedTakeId, retried.Id);
        Assert.Equal(TakeStatus.Complete, retried.Status);
        Assert.Equal("Hello again", retried.FormattedText);
        Assert.Equal(TakePasteOutcome.LeftInOrb, retried.PasteOutcome);
        // The cursor has moved on: nothing is pasted, the words wait in the box.
        Assert.Empty(h.Pasted);
        Assert.Equal("Hello again", h.Orb.BoxText);
        Assert.True(h.Orb.IsBoxOpen);
        Assert.Equal(DictationOrbPhase.Done("retried · copy from the box"), h.Orb.Phase);
    });

    // MARK: the inactivity timeout

    [Fact]
    public Task ATakeWithTooLongASilenceEndsOnItsOwn()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_000));
        return TakeHarness.Run(async h =>
        {
            h.Settings.InactivityTimeoutMinutes = 1;
            h.Handle(Pressed);
            await h.WaitForState(TakeState.Listening);
            clock.Advance(TimeSpan.FromSeconds(30));
            await TakeHarness.Settle();
            Assert.Equal(TakeState.Listening, h.State);
            clock.Advance(TimeSpan.FromSeconds(35));
            await TakeHarness.WaitUntil(() => h.State == TakeState.Finishing);
            Assert.Equal(TakeState.Finishing, h.State);
            Assert.Equal(1, h.Capture.Stops);
        }, timeProvider: clock);
    }

    // MARK: the orb

    [Fact]
    public Task TheOrbIsLikeATapOfTheKey() => TakeHarness.Run(async h =>
    {
        h.Controller.OrbClicked();
        Assert.Equal("say it, then tap the orb", h.Orb.Hint);
        await h.WaitForState(TakeState.Listening);
        // Released keys do not end a take the orb started.
        h.Handle(Held);
        Assert.Equal(TakeState.Listening, h.State);
        h.Controller.OrbClicked();
        Assert.Equal(TakeState.Finishing, h.State);
        h.Capture.DeliverFinal("hello");
        await h.WaitForState(TakeState.Idle);
        Assert.Equal(["Hello"], h.Pasted);
    });

    [Fact]
    public Task TheIdleOrbSaysHowToTalk() => TakeHarness.Run(async h =>
    {
        h.Handle(Pressed);
        await h.WaitForState(TakeState.Listening);
        h.Handle(CompanionShortcutEvent.EscapePressed);
        await TakeHarness.WaitUntil(() => h.Orb.Phase == DictationOrbPhase.Idle, 4);
        Assert.Equal(DictationOrbPhase.Idle, h.Orb.Phase);
        Assert.Equal("tap / hold right Ctrl to talk", h.Orb.Hint);
    });

    [Fact]
    public Task AnUnavailableEngineNeverOpensTheMicrophone() => TakeHarness.Run(async h =>
    {
        // Parakeet without its model: the resolver's own reason, on the orb, and no take.
        h.UnavailableReason = "needs its 465 MB download";
        h.Handle(Pressed);
        Assert.Equal(TakeState.Idle, h.State);
        await TakeHarness.Settle(0.1);
        Assert.Equal(0, h.Capture.Starts);
        Assert.Equal(DictationOrbPhase.Failed("needs its 465 MB download"), h.Orb.Phase);
    });
}
