// Fakes the take controller's tests drive it through: the capture side (a microphone and engine
// that the test speaks for), a provider factory, and a TimeProvider whose clock moves by hand.

using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Dictation;

/// <summary>A provider known by its name. Its session answers <see cref="FinalText"/> when asked for the final words.</summary>
internal sealed class NamedTranscriptionProvider(string displayName, string? finalText = null) : IBuddyTranscriptionProvider
{
    public string DisplayName { get; } = displayName;
    public string? FinalText { get; } = finalText;
    public bool RequiresSpeechRecognitionPermission => false;
    public bool IsConfigured => true;
    public string? UnavailableExplanation => null;
    public int AudioBytesHeard { get; private set; }

    public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(IReadOnlyList<string> keyterms, Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady, Action<Exception> onError, CancellationToken cancellationToken = default)
    {
        if (FinalText is null) throw new InvalidOperationException("this provider never opens a session");
        return Task.FromResult<IBuddyStreamingTranscriptionSession>(new Session(this, onFinalTranscriptReady));
    }

    private sealed class Session(NamedTranscriptionProvider owner, Action<string> onFinal) : IBuddyStreamingTranscriptionSession
    {
        public TimeSpan FinalTranscriptFallbackDelay => TimeSpan.FromSeconds(1);
        public void AppendAudio(AudioFrame frame) => owner.AudioBytesHeard += frame.Pcm16?.Length ?? 0;
        public void RequestFinalTranscript() => onFinal(owner.FinalText ?? "");
        public void Cancel() { }
    }
}

/// <summary>Builds named providers; a retry's provider says <see cref="RetryFinalText"/>.</summary>
internal sealed class FakeProviderFactory : IDictationProviderFactory
{
    public string? RetryFinalText { get; set; }
    public List<NamedTranscriptionProvider> Built { get; } = [];

    private NamedTranscriptionProvider Make(string name)
    {
        var provider = new NamedTranscriptionProvider(name, RetryFinalText);
        Built.Add(provider);
        return provider;
    }

    public IBuddyTranscriptionProvider MakeOffline(string? preferredLocale) => Make("offline");
    public IBuddyTranscriptionProvider MakeMoonshine(Func<string?> bareLanguageCode) => Make("moonshine");
    public IBuddyTranscriptionProvider MakeParakeet(Func<string?> bareLanguageCode) => Make("parakeet");
    public IBuddyTranscriptionProvider MakeSarvam(Func<DictationLanguage> language) => Make("sarvam");
    public IBuddyTranscriptionProvider MakeOpenClicky(Func<string?> bareLanguageCode) => Make("openclicky");
    public IBuddyTranscriptionProvider MakeAssemblyAI(Func<string?> bareLanguageCode) => Make("assemblyai");
}

/// <summary>
/// The microphone-and-engine side as a test drives it (the Mac's FakeCapture). Events are raised on
/// the caller's thread, which in these tests is the main thread.
/// </summary>
internal sealed class FakeCapture : IDictationCapturing
{
    public bool IsDictationInProgress { get; private set; }
    public string TranscriptionProviderDisplayName => "fake";
    public string? PreferredMicrophoneId { get; set; }
    public Action<AudioFrame>? AudioRetentionSink { get; set; }

    public event Action<double>? AudioPowerLevelChanged;
    public event Action<string?>? ErrorMessageChanged;
    public event Action<bool>? SessionActivityChanged;

    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public int Cancels { get; private set; }
    /// <summary>How many times the engine was rebuilt (a changed engine or language).</summary>
    public int ProviderReplacements { get; private set; }

    private Action<string>? onPartial;
    private Action<string>? onFinal;

    /// <summary>
    /// The next start returns without opening a session (microphone denied), publishing this message
    /// first when it is not null — as the real manager does on a refused permission.
    /// </summary>
    public bool NextStartFails { get; set; }
    public string? NextStartFailureMessage { get; set; }

    public Task StartPushToTalkFromKeyboardShortcutAsync(string currentDraftText, Action<string> updateDraftText, Action<string> submitDraftText,
                                                         CancellationToken cancellationToken = default)
    {
        Starts += 1;
        if (NextStartFails)
        {
            NextStartFails = false;
            if (NextStartFailureMessage is { } failure) ErrorMessageChanged?.Invoke(failure);
            NextStartFailureMessage = null;
            return Task.CompletedTask;
        }
        IsDictationInProgress = true;
        SessionActivityChanged?.Invoke(true);
        onPartial = updateDraftText;
        onFinal = submitDraftText;
        return Task.CompletedTask;
    }

    public void StopPushToTalkFromKeyboardShortcut() => Stops += 1;

    public void CancelCurrentDictation(bool preserveDraftText = true)
    {
        Cancels += 1;
        IsDictationInProgress = false;
        onFinal = null;
        SessionActivityChanged?.Invoke(false);
    }

    public void ReplaceTranscriptionProvider(IBuddyTranscriptionProvider provider) => ProviderReplacements += 1;

    public void UpdateContextualKeyterms(IReadOnlyList<string> contextualKeyterms) { }

    /// <summary>The engine's words arrive: the session ends, then the final text is delivered (as the real manager does: state first, callback second).</summary>
    public void DeliverFinal(string text)
    {
        var final = onFinal;
        IsDictationInProgress = false;
        onFinal = null;
        SessionActivityChanged?.Invoke(false);
        final?.Invoke(text);
    }

    public void DeliverPartial(string text) => onPartial?.Invoke(text);

    public void SendLevel(double level) => AudioPowerLevelChanged?.Invoke(level);

    public void SendError(string message) => ErrorMessageChanged?.Invoke(message);
}

/// <summary>A clock and timers that move only when the test says so.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object timersLock = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (timersLock) return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock and fires every timer that comes due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (timersLock) target = now + by;
        while (true)
        {
            ManualTimer? next;
            lock (timersLock)
            {
                next = timers.Where(timer => timer.DueAt is { } due && due <= target).OrderBy(timer => timer.DueAt).FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    return;
                }
                now = next.DueAt!.Value;
                next.DueAt = next.Period > TimeSpan.Zero ? now + next.Period : null;
                if (next.DueAt is null) timers.Remove(next);
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }
        public TimeSpan Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.timersLock)
            {
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    owner.timers.Remove(this);
                }
                else
                {
                    DueAt = owner.now + dueTime;
                    if (!owner.timers.Contains(this)) owner.timers.Add(this);
                }
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner.timersLock)
            {
                DueAt = null;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
