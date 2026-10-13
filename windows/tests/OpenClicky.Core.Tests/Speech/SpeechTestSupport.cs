using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

/// <summary>A main thread for tests: one dedicated thread with a SynchronizationContext, so awaits
/// resume on it as they do on the WPF dispatcher (the Mac's main actor).</summary>
public sealed class SerialMainThread : IMainThread, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly Thread thread;

    public SerialMainThread()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "test main thread" };
        thread.Start();
    }

    private sealed class Context(SerialMainThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            try { owner.queue.Add((d, state)); }
            catch (InvalidOperationException) { }
        }
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var (callback, state) in queue.GetConsumingEnumerable())
        {
            // An exception escaping a posted callback would end the test host; tests see failures through their own asserts.
            try { callback(state); }
            catch (Exception) { }
        }
    }

    public bool IsMainThread => Thread.CurrentThread == thread;

    public void Post(Action action)
    {
        // A timer firing after the test ended posts into a finished queue: dropped, never thrown on a pool thread.
        try { queue.Add((_ => action(), null)); }
        catch (InvalidOperationException) { }
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return Task.CompletedTask; });

    public Task<T> InvokeAsync<T>(Func<T> function)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { source.SetResult(function()); }
            catch (Exception error) { source.SetException(error); }
        });
        return source.Task;
    }

    public Task InvokeAsync(Func<Task> function)
    {
        if (IsMainThread) return function();
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await function();
                source.SetResult();
            }
            catch (Exception error)
            {
                source.SetException(error);
            }
        });
        return source.Task;
    }

    /// <summary>Runs an async function on the main thread and waits for it.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> function)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try { source.SetResult(await function()); }
            catch (Exception error) { source.SetException(error); }
        });
        return source.Task;
    }

    public Task RunAsync(Func<Task> function) => RunAsync(async () => { await function(); return true; });

    public Task RunAsync(Action action) => InvokeAsync(action);

    public void Dispose() => queue.CompleteAdding();
}

/// <summary>
/// Stands in for a websocket whose handshake has not finished: until <see cref="Open"/> every send
/// fails with "Socket is not connected", as the first session.update did in issue #1.
/// </summary>
public sealed class FakeSpeechWebSocket : ISpeechWebSocket
{
    private readonly object stateLock = new();
    private readonly Channel<Func<SpeechWebSocketMessage>> incoming = Channel.CreateUnbounded<Func<SpeechWebSocketMessage>>();
    private readonly List<string> sentTexts = [];
    private readonly List<byte[]> sentBinaries = [];
    private bool isOpen;
    private int pendingReceives;

    public Exception? ConnectFailure { get; set; }
    public Uri? ConnectedUrl { get; private set; }
    public IReadOnlyDictionary<string, string>? ConnectedHeaders { get; private set; }
    public int SendsBeforeOpen { get; private set; }
    public bool WasConnected { get; private set; }
    public bool WasClosed { get; private set; }
    public int? HandshakeStatusCode { get; private set; }
    /// <summary>Open as soon as the connect returns (an accepted handshake), for sessions that send right away.</summary>
    public bool OpensOnConnect { get; set; }

    public IReadOnlyList<string> SentTexts
    {
        get { lock (stateLock) return sentTexts.ToList(); }
    }

    public IReadOnlyList<byte[]> SentBinaries
    {
        get { lock (stateLock) return sentBinaries.ToList(); }
    }

    public bool IsWaitingToReceive
    {
        get { lock (stateLock) return pendingReceives > 0; }
    }

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        ConnectedUrl = uri;
        ConnectedHeaders = headers;
        WasConnected = true;
        if (ConnectFailure is { } failure) return Task.FromException(failure);
        if (OpensOnConnect)
        {
            lock (stateLock) isOpen = true;
        }
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            if (!isOpen)
            {
                SendsBeforeOpen += 1;
                return Task.FromException(new WebSocketException(WebSocketError.InvalidState, "Socket is not connected"));
            }
            sentTexts.Add(text);
        }
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            if (!isOpen)
            {
                SendsBeforeOpen += 1;
                return Task.FromException(new WebSocketException(WebSocketError.InvalidState, "Socket is not connected"));
            }
            sentBinaries.Add(data.ToArray());
        }
        return Task.CompletedTask;
    }

    public async Task<SpeechWebSocketMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        lock (stateLock) pendingReceives += 1;
        try
        {
            var next = await incoming.Reader.ReadAsync(cancellationToken);
            return next();
        }
        finally
        {
            lock (stateLock) pendingReceives -= 1;
        }
    }

    /// <summary>The handshake completes and the server's first event arrives.</summary>
    public void Open(string firstEvent)
    {
        lock (stateLock) isOpen = true;
        incoming.Writer.TryWrite(() => SpeechWebSocketMessage.FromText(firstEvent));
    }

    /// <summary>The server sends an event on an open socket.</summary>
    public void Receive(string serverEvent) => incoming.Writer.TryWrite(() => SpeechWebSocketMessage.FromText(serverEvent));

    /// <summary>The handshake is refused with <paramref name="statusCode"/>.</summary>
    public void Refuse(int statusCode)
    {
        HandshakeStatusCode = statusCode;
        incoming.Writer.TryWrite(() => throw new SpeechWebSocketHandshakeException(statusCode, "bad server response"));
    }

    public void CloseFromServer(int status = 1000, string reason = "") =>
        incoming.Writer.TryWrite(() => SpeechWebSocketMessage.Closed(status, reason));

    public Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string? reason = null)
    {
        WasClosed = true;
        return Task.CompletedTask;
    }

    public void Abort() => WasClosed = true;

    public void Dispose() => WasClosed = true;
}

public static class Eventually
{
    /// <summary>Waits (ten seconds at most: the full suite can starve the thread pool) for <paramref name="condition"/>.</summary>
    public static async Task<bool> TrueAsync(Func<bool> condition, int timeoutMilliseconds = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }
        return condition();
    }
}

/// <summary>What a session reported, from whichever thread it reported on.</summary>
public sealed class TranscriptLog
{
    private readonly object stateLock = new();
    private readonly List<string> finals = [];
    private readonly List<string> partials = [];
    private readonly List<Exception> errors = [];

    public IReadOnlyList<string> Finals { get { lock (stateLock) return finals.ToList(); } }
    public IReadOnlyList<string> Partials { get { lock (stateLock) return partials.ToList(); } }
    public IReadOnlyList<Exception> Errors { get { lock (stateLock) return errors.ToList(); } }

    public void Final(string text) { lock (stateLock) finals.Add(text); }
    public void Partial(string text) { lock (stateLock) partials.Add(text); }
    public void Error(Exception error) { lock (stateLock) errors.Add(error); }

    public async Task<string?> WaitForFinalAsync(int timeoutMilliseconds = 10_000)
    {
        await Eventually.TrueAsync(() => Finals.Count > 0, timeoutMilliseconds);
        return Finals.FirstOrDefault();
    }
}

/// <summary>A microphone that delivers only what the test pushes.</summary>
public sealed class FakeAudioCapture : IAudioCapture
{
    public Exception? StartFailure { get; set; }
    public AudioCaptureOptions? LastOptions { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool IsCapturing { get; private set; }
    public float CurrentLevel => 0;

    public event Action<AudioFrame>? FramesAvailable;
    public event Action<Exception?>? CaptureStopped;

    public Task<AudioCaptureStartResult> StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        StartCount += 1;
        if (StartFailure is { } failure) return Task.FromException<AudioCaptureStartResult>(failure);
        IsCapturing = true;
        return Task.FromResult(new AudioCaptureStartResult("fake capture", "fake", "Fake Microphone", options.PreferEchoCancellation, options.PreferEchoCancellation));
    }

    public void Stop()
    {
        StopCount += 1;
        IsCapturing = false;
        CaptureStopped?.Invoke(null);
    }

    public bool HasListeners => FramesAvailable is not null;

    public void Push(AudioFrame frame) => FramesAvailable?.Invoke(frame);

    public void Dispose() => IsCapturing = false;
}

public sealed class FakeStreamingPcmPlayer : IStreamingPcmPlayer
{
    public int SampleRate => 24_000;
    public List<byte[]> Enqueued { get; } = [];
    public int FlushCount { get; private set; }
    public bool IsPlaybackActive { get; private set; }
    public event Action<bool>? PlaybackActiveChanged;

    public void Start() { }

    public void Enqueue(ReadOnlySpan<byte> pcm16)
    {
        lock (Enqueued) Enqueued.Add(pcm16.ToArray());
        if (!IsPlaybackActive)
        {
            IsPlaybackActive = true;
            PlaybackActiveChanged?.Invoke(true);
        }
    }

    public void Flush()
    {
        FlushCount += 1;
        if (IsPlaybackActive)
        {
            IsPlaybackActive = false;
            PlaybackActiveChanged?.Invoke(false);
        }
    }

    public void Stop() => Flush();

    public void Dispose() { }
}

public sealed class FakeMicrophonePermission(MicrophoneConsent consent) : IMicrophonePermission
{
    public MicrophoneConsent ReadConsent() => consent;
    public Task<MicrophoneTestResult> TestCaptureAsync(string? deviceId = null, CancellationToken cancellationToken = default) => Task.FromResult(MicrophoneTestResult.Works);
    public void OpenPrivacySettings() { }
}

public static class TestAudio
{
    /// <summary>A frame of a 440 Hz tone at <paramref name="sampleRate"/>.</summary>
    public static AudioFrame Tone(int sampleCount, int sampleRate = 16_000, float amplitude = 0.5f)
    {
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++) samples[index] = (float)(Math.Sin(2 * Math.PI * 440 * index / sampleRate) * amplitude);
        return new AudioFrame(samples, BuddyPCM16AudioConverter.FloatToPcm16(samples), sampleRate, Math.Min(1, amplitude * 10.2f / MathF.Sqrt(2)));
    }

    public static AudioFrame Silence(int sampleCount, int sampleRate = 16_000) =>
        new(new float[sampleCount], new byte[sampleCount * 2], sampleRate, 0);
}
