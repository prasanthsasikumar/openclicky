// Ports RealtimeAudioEngine (RealtimeVoiceClient.swift): microphone → PCM16 mono 24 kHz frames out,
// PCM16 playback in. The Mac tried Apple's voice-processing unit (echo cancellation) first and fell
// back to plain capture; here the capture is opened as a communications stream (Windows applies its
// echo cancellation where the driver or Voice Clarity has it) with the same fallback inside
// IAudioCapture. A microphone the person chose that is not the default is recorded without echo
// cancellation, as on the Mac. Every operation is serialised (the Mac's audio queue); the capture
// thread only hands frames to the callback.

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class RealtimeAudio : IDisposable
{
    public const int SampleRate = 24_000;

    private readonly Func<IAudioCapture> makeCapture;
    private readonly Func<IStreamingPcmPlayer> makePlayer;
    private readonly IMicrophoneDevices? microphoneDevices;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    /// <summary>Used on the capture thread only, when a device delivers another rate.</summary>
    private readonly BuddyPCM16AudioConverter resampler = new(SampleRate);

    private Action<byte[], float>? onMicrophoneFrame;
    private Action<bool>? onPlaybackActiveChanged;
    private IAudioCapture? capture;
    private IStreamingPcmPlayer? player;
    private bool isRunning;
    private bool isPaused;
    private string? preferredMicrophoneId;
    private string? configuredMicrophoneId;
    // Statistics (for the smoke run and bug reports).
    private long tapCount;
    private long framesOut;
    private float peakLevel;
    private float peakOut;

    public RealtimeAudio(Func<IAudioCapture> makeCapture, Func<IStreamingPcmPlayer> makePlayer, IMicrophoneDevices? microphoneDevices = null)
    {
        this.makeCapture = makeCapture;
        this.makePlayer = makePlayer;
        this.microphoneDevices = microphoneDevices;
    }

    /// <summary>The device the next start uses (null = the default); a change rebuilds the capture at the next start.</summary>
    public string? PreferredMicrophoneId
    {
        get => Volatile.Read(ref preferredMicrophoneId);
        set => Volatile.Write(ref preferredMicrophoneId, value);
    }

    /// <summary>Installs the callbacks (frames come on the capture thread). Call before <see cref="StartAsync"/>.</summary>
    public void SetCallbacks(Action<byte[], float> onMicrophoneFrame, Action<bool> onPlaybackActiveChanged)
    {
        this.onMicrophoneFrame = onMicrophoneFrame;
        this.onPlaybackActiveChanged = onPlaybackActiveChanged;
    }

    public string DebugSummary() =>
        $"taps {Interlocked.Read(ref tapCount)}, frames out {Interlocked.Read(ref framesOut)}, peak in {peakLevel:0.000} out {peakOut:0.000}, engine running {isRunning && !isPaused}";

    /// <summary>Starts capture and playback; returns a one-line description of the configuration.</summary>
    public async Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A microphone chosen since the capture was set up: pause keeps the old device, so it is rebuilt.
            if (isRunning && configuredMicrophoneId != PreferredMicrophoneId) TearDown();
            if (isRunning)
            {
                if (isPaused)
                {
                    await capture!.StartAsync(CaptureOptions(configuredEchoCancellation), cancellationToken).ConfigureAwait(false);
                    player!.Start();
                    isPaused = false;
                }
                return "audio already running";
            }
            try
            {
                string description;
                if (microphoneDevices?.ChoiceDiffersFromDefault(PreferredMicrophoneId) == true)
                {
                    description = await ConfigureAsync(echoCancellation: false, cancellationToken).ConfigureAwait(false);
                    description += " (echo cancellation off: it only hears the default input)";
                }
                else
                {
                    description = await ConfigureAsync(echoCancellation: true, cancellationToken).ConfigureAwait(false);
                }
                isRunning = true;
                isPaused = false;
                return description;
            }
            catch
            {
                TearDown();
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    private bool configuredEchoCancellation;

    private AudioCaptureOptions CaptureOptions(bool echoCancellation) => new()
    {
        DeviceId = configuredMicrophoneId,
        SampleRate = SampleRate,
        Format = AudioSampleFormat.Pcm16,
        PreferEchoCancellation = echoCancellation,
        BufferMilliseconds = 100,
    };

    private async Task<string> ConfigureAsync(bool echoCancellation, CancellationToken cancellationToken)
    {
        configuredMicrophoneId = PreferredMicrophoneId;
        configuredEchoCancellation = echoCancellation;
        player = makePlayer();
        player.PlaybackActiveChanged += HandlePlaybackActiveChanged;
        player.Start();
        capture = makeCapture();
        capture.FramesAvailable += HandleMicrophoneFrame;
        var started = await capture.StartAsync(CaptureOptions(echoCancellation), cancellationToken).ConfigureAwait(false);
        var notes = new List<string> { $"microphone {(configuredMicrophoneId is null ? "default" : started.DeviceName)}" };
        if (echoCancellation) notes.Add(started.EchoCancellationApplied ? "communications capture" : "communications capture unavailable");
        return $"audio running: {started.Description}, {string.Join(", ", notes)}, voice processing {(started.EchoCancellationApplied ? "on" : "off")}";
    }

    /// <summary>Releases the microphone (the privacy indicator goes off) and the output, keeping the setup so the next start is fast.</summary>
    public void Pause() => RunSerially(() =>
    {
        if (!isRunning || isPaused) return;
        player?.Stop();
        capture?.Stop();
        isPaused = true;
    });

    /// <summary>Tears everything down so nothing of ours touches the audio system between turns.</summary>
    public void Release() => RunSerially(() =>
    {
        if (isRunning) TearDown();
    });

    /// <summary><see cref="Release"/>, returning once it is done (another capture is about to open the microphone).</summary>
    public async Task ReleaseNowAsync()
    {
        await operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isRunning) TearDown();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public void Enqueue(byte[] pcm16)
    {
        if (pcm16.Length == 0) return;
        var currentPlayer = Volatile.Read(ref player);
        if (currentPlayer is null || !isRunning) return;
        // The player starts itself when paused (the Mac restarted the engine for playback).
        currentPlayer.Enqueue(pcm16);
    }

    public void FlushPlayback()
    {
        var currentPlayer = Volatile.Read(ref player);
        if (currentPlayer is null) return;
        currentPlayer.Flush();
        onPlaybackActiveChanged?.Invoke(false);
    }

    private void RunSerially(Action action)
    {
        _ = Task.Run(async () =>
        {
            await operationLock.WaitAsync().ConfigureAwait(false);
            try { action(); }
            catch (Exception error) { Configuration.AppLog.Write($"realtime: audio operation failed: {error.Message}"); }
            finally { operationLock.Release(); }
        });
    }

    private void TearDown()
    {
        if (capture is not null)
        {
            capture.FramesAvailable -= HandleMicrophoneFrame;
            try { capture.Stop(); } catch (Exception) { }
            capture.Dispose();
            capture = null;
        }
        if (player is not null)
        {
            player.PlaybackActiveChanged -= HandlePlaybackActiveChanged;
            try { player.Stop(); } catch (Exception) { }
            player.Dispose();
            player = null;
        }
        configuredMicrophoneId = null;
        isRunning = false;
        isPaused = false;
    }

    private void HandlePlaybackActiveChanged(bool isActive) => onPlaybackActiveChanged?.Invoke(isActive);

    /// <summary>Capture thread: PCM16 24 kHz to the client, with the Mac's level (RMS × 6).</summary>
    private void HandleMicrophoneFrame(AudioFrame frame)
    {
        Interlocked.Increment(ref tapCount);
        var pcm16 = frame.SampleRate == SampleRate && frame.Pcm16 is { Length: > 0 } ready
            ? ready
            : BuddyPCM16AudioConverter.FloatToPcm16(resampler.ConvertSamples(frame.Samples, frame.SampleRate));
        if (pcm16.Length == 0) return;
        var level = RmsLevel(frame.Samples);
        var outPeak = PCM16SilenceGate.PeakMagnitude(pcm16) / 32768f;
        Interlocked.Add(ref framesOut, pcm16.Length / 2);
        peakLevel = Math.Max(peakLevel, level);
        peakOut = Math.Max(peakOut, outPeak);
        onMicrophoneFrame?.Invoke(pcm16, level);
    }

    public static float RmsLevel(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Min(1, Math.Sqrt(sum / samples.Length) * 6);
    }

    public void Dispose()
    {
        operationLock.Wait();
        try { TearDown(); }
        finally { operationLock.Release(); }
    }
}
