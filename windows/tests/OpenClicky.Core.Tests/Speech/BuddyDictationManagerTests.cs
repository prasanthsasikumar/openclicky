// BuddyDictationManager through a fake provider and a fake microphone: the take's lifecycle, the
// session identity on every callback (a late final from one take never reaches the next), the
// level meter, the keyterms, and the permission refusal.

using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class BuddyDictationManagerTests : IDisposable
{
    private readonly SerialMainThread mainThread = new();

    public void Dispose() => mainThread.Dispose();

    private sealed class FakeSession(BuddyTranscriptionSessionCallbacks callbacks) : IBuddyStreamingTranscriptionSession
    {
        public BuddyTranscriptionSessionCallbacks Callbacks { get; } = callbacks;
        public TimeSpan FinalTranscriptFallbackDelay { get; init; } = TimeSpan.FromSeconds(5);
        public int FramesHeard;
        public bool FinalRequested;
        public bool Cancelled;
        /// <summary>The Windows recogniser's CancelAsync on a session already stopped: a COMException (0x80131509).</summary>
        public bool ThrowsOnCancel;
        public void AppendAudio(AudioFrame frame) => Interlocked.Increment(ref FramesHeard);
        public void RequestFinalTranscript() => FinalRequested = true;
        public void Cancel()
        {
            Cancelled = true;
            if (ThrowsOnCancel) throw new System.Runtime.InteropServices.COMException("The text associated with this error code could not be found.", unchecked((int)0x80131509));
        }
    }

    private sealed class FakeProvider : IBuddyTranscriptionProvider
    {
        public List<FakeSession> Sessions { get; } = [];
        public IReadOnlyList<string>? LastKeyterms { get; private set; }
        public TimeSpan FallbackDelay { get; set; } = TimeSpan.FromSeconds(5);
        public string DisplayName => "Fake";
        public bool RequiresSpeechRecognitionPermission => false;
        public bool IsConfigured => true;
        public string? UnavailableExplanation => null;

        public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(IReadOnlyList<string> keyterms, Action<string> onTranscriptUpdate,
            Action<string> onFinalTranscriptReady, Action<Exception> onError, CancellationToken cancellationToken = default)
        {
            LastKeyterms = keyterms;
            var session = new FakeSession(new BuddyTranscriptionSessionCallbacks(onTranscriptUpdate, onFinalTranscriptReady, onError)) { FinalTranscriptFallbackDelay = FallbackDelay };
            lock (Sessions) Sessions.Add(session);
            return Task.FromResult<IBuddyStreamingTranscriptionSession>(session);
        }
    }

    private sealed record Harness(BuddyDictationManager Manager, FakeProvider Provider, FakeAudioCapture Capture, List<string> Drafts, List<string> Submitted);

    private async Task<Harness> StartedHarnessAsync(string currentDraft = "", MicrophoneConsent consent = MicrophoneConsent.Allowed, FakeAudioCapture? capture = null)
    {
        var provider = new FakeProvider();
        capture ??= new FakeAudioCapture();
        var manager = await mainThread.InvokeAsync(() => new BuddyDictationManager(provider, mainThread, () => capture, new FakeMicrophonePermission(consent)));
        var drafts = new List<string>();
        var submitted = new List<string>();
        await mainThread.RunAsync(() => manager.StartPushToTalkFromKeyboardShortcutAsync(currentDraft, drafts.Add, submitted.Add));
        return new Harness(manager, provider, capture, drafts, submitted);
    }

    [Fact]
    public async Task ATakeStreamsAudioShowsPartialsAndSubmitsTheFinalWords()
    {
        var harness = await StartedHarnessAsync();
        Assert.True(harness.Manager.IsRecordingFromKeyboardShortcut);
        Assert.Equal(16_000, harness.Capture.LastOptions!.SampleRate);
        var session = harness.Provider.Sessions.Single();
        harness.Capture.Push(TestAudio.Tone(320));
        Assert.Equal(1, session.FramesHeard);
        Assert.True(await Eventually.TrueAsync(() => harness.Manager.CurrentAudioPowerLevel > 0));

        session.Callbacks.OnTranscriptUpdate("hello");
        Assert.True(await Eventually.TrueAsync(() => harness.Drafts.Contains("hello")));

        await mainThread.InvokeAsync(harness.Manager.StopPushToTalkFromKeyboardShortcut);
        Assert.True(session.FinalRequested);
        Assert.False(harness.Capture.IsCapturing);
        session.Callbacks.OnFinalTranscriptReady("hello world");
        Assert.True(await Eventually.TrueAsync(() => harness.Submitted.Count == 1));
        Assert.Equal("hello world", harness.Submitted[0]);
        Assert.False(harness.Manager.IsDictationInProgress);
        Assert.False(harness.Capture.HasListeners);
    }

    /// <summary>Moonshine showed the words live, then read the whole take as nothing (2026-10-28): the live words are the take.</summary>
    [Fact]
    public async Task AnEmptyFinalAfterLiveWordsKeepsTheLiveWords()
    {
        var harness = await StartedHarnessAsync();
        var session = harness.Provider.Sessions.Single();
        session.Callbacks.OnTranscriptUpdate("hello openclicky this is my first take");
        Assert.True(await Eventually.TrueAsync(() => harness.Drafts.Count > 0));
        await mainThread.InvokeAsync(harness.Manager.StopPushToTalkFromKeyboardShortcut);
        session.Callbacks.OnFinalTranscriptReady("");
        Assert.True(await Eventually.TrueAsync(() => harness.Submitted.Count == 1));
        Assert.Equal("hello openclicky this is my first take", harness.Submitted[0]);
    }

    /// <summary>A tester's PC (2026-10-14): the Windows recogniser's cancel threw on every release, the
    /// exception left the take half finished (nothing pasted, "the microphone is still busy"), and the
    /// next take failed on the same cancel. A session that throws on cancel is let go of all the same.</summary>
    [Fact]
    public async Task ASessionWhoseCancelThrowsStillFinishesTheTakeAndTheNextOneStarts()
    {
        var harness = await StartedHarnessAsync();
        var session = harness.Provider.Sessions.Single();
        session.ThrowsOnCancel = true;
        await mainThread.InvokeAsync(harness.Manager.StopPushToTalkFromKeyboardShortcut);
        session.Callbacks.OnFinalTranscriptReady("hello world");
        Assert.True(await Eventually.TrueAsync(() => harness.Submitted.Count == 1));
        Assert.Equal("hello world", harness.Submitted[0]);
        Assert.True(session.Cancelled);
        Assert.False(harness.Manager.IsDictationInProgress);

        await mainThread.RunAsync(() => harness.Manager.StartPushToTalkFromKeyboardShortcutAsync("", harness.Drafts.Add, harness.Submitted.Add));
        Assert.Equal(2, harness.Provider.Sessions.Count);
        Assert.True(harness.Manager.IsRecordingFromKeyboardShortcut);
        Assert.Null(harness.Manager.LastErrorMessage);
    }

    [Fact]
    public async Task ALateFinalFromAnEarlierTakeNeverReachesTheNext()
    {
        var harness = await StartedHarnessAsync();
        var firstSession = harness.Provider.Sessions.Single();
        await mainThread.InvokeAsync(() => harness.Manager.CancelCurrentDictation(preserveDraftText: false));
        Assert.True(firstSession.Cancelled);

        await mainThread.RunAsync(() => harness.Manager.StartPushToTalkFromKeyboardShortcutAsync("", harness.Drafts.Add, harness.Submitted.Add));
        var secondSession = harness.Provider.Sessions[1];
        await mainThread.InvokeAsync(harness.Manager.StopPushToTalkFromKeyboardShortcut);

        firstSession.Callbacks.OnTranscriptUpdate("old words");
        firstSession.Callbacks.OnFinalTranscriptReady("old words");
        firstSession.Callbacks.OnError(new InvalidOperationException("old socket closed"));
        await mainThread.InvokeAsync(() => { });
        Assert.Empty(harness.Submitted);
        Assert.DoesNotContain("old words", harness.Drafts);
        Assert.Null(harness.Manager.LastErrorMessage);

        secondSession.Callbacks.OnFinalTranscriptReady("new words");
        Assert.True(await Eventually.TrueAsync(() => harness.Submitted.Count == 1));
        Assert.Equal("new words", harness.Submitted[0]);
    }

    [Fact]
    public async Task WordsAlreadyInTheDraftAreAddedToNotSent()
    {
        var harness = await StartedHarnessAsync(currentDraft: "Dear team,");
        var session = harness.Provider.Sessions.Single();
        await mainThread.InvokeAsync(harness.Manager.StopPushToTalkFromKeyboardShortcut);
        session.Callbacks.OnFinalTranscriptReady(" the build is green ");
        Assert.True(await Eventually.TrueAsync(() => harness.Drafts.Contains("Dear team, the build is green")));
        Assert.Empty(harness.Submitted);
    }

    [Fact]
    public async Task TheFallbackFinishesATakeWhoseEngineNeverAnswers()
    {
        var provider = new FakeProvider { FallbackDelay = TimeSpan.FromMilliseconds(150) };
        var capture = new FakeAudioCapture();
        var manager = await mainThread.InvokeAsync(() => new BuddyDictationManager(provider, mainThread, () => capture));
        var submitted = new List<string>();
        await mainThread.RunAsync(() => manager.StartPushToTalkFromKeyboardShortcutAsync("", _ => { }, submitted.Add));
        provider.Sessions.Single().Callbacks.OnTranscriptUpdate("heard so far");
        await mainThread.InvokeAsync(manager.StopPushToTalkFromKeyboardShortcut);
        Assert.True(await Eventually.TrueAsync(() => submitted.Count == 1));
        Assert.Equal("heard so far", submitted[0]);
    }

    [Fact]
    public async Task AnErrorBeforeAnyWordsCancelsTheTakeAndSaysWhy()
    {
        var harness = await StartedHarnessAsync();
        var session = harness.Provider.Sessions.Single();
        session.Callbacks.OnError(new TranscriptionProviderException("couldn't reach sarvam: offline"));
        Assert.True(await Eventually.TrueAsync(() => harness.Manager.LastErrorMessage is not null));
        // The error is set, then the take is cancelled, in the same main-thread turn: wait for the turn to end.
        await mainThread.InvokeAsync(() => { });
        Assert.Equal("couldn't reach sarvam: offline", harness.Manager.LastErrorMessage);
        Assert.True(session.Cancelled);
        Assert.False(harness.Manager.IsDictationInProgress);
    }

    [Fact]
    public async Task AMicrophoneThatWillNotStartTearsTheSessionDown()
    {
        var capture = new FakeAudioCapture { StartFailure = new AudioCaptureException(AudioCaptureFailure.DeviceInUse, "the microphone is in use by another app.") };
        var harness = await StartedHarnessAsync(capture: capture);
        Assert.True(harness.Provider.Sessions.Single().Cancelled);
        Assert.Equal("the microphone is in use by another app.", harness.Manager.LastErrorMessage);
        Assert.False(harness.Manager.IsDictationInProgress);
        Assert.Null(harness.Manager.ActiveSessionIdentity);
    }

    [Fact]
    public async Task AMicrophoneSwitchedOffInPrivacySettingsStopsTheTakeWithASentence()
    {
        var harness = await StartedHarnessAsync(consent: MicrophoneConsent.DeniedForDesktopApps);
        Assert.Empty(harness.Provider.Sessions);
        Assert.Equal(BuddyDictationManager.MicrophonePermissionRequiredSentence, harness.Manager.LastErrorMessage);
        Assert.False(harness.Manager.IsDictationInProgress);
    }

    [Fact]
    public async Task TheKeytermsAreTheBaseWordsThenTheCallersWithoutRepeats()
    {
        var provider = new FakeProvider();
        var manager = await mainThread.InvokeAsync(() => new BuddyDictationManager(provider, mainThread, () => new FakeAudioCapture()));
        await mainThread.RunAsync(async () =>
        {
            manager.UpdateContextualKeyterms([" openclicky ", "Saathi", "", "saathi"]);
            await manager.StartPushToTalkFromKeyboardShortcutAsync("", _ => { }, _ => { });
        });
        Assert.Equal(["OpenClicky", "Codex", "Claude", "Anthropic", "OpenAI", "SwiftUI", "Xcode", "Vercel", "Next.js", "localhost", "Saathi"], provider.LastKeyterms);
    }
}
