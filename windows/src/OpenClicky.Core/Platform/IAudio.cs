namespace OpenClicky.Core.Platform;

/// <summary>A capture endpoint (MicrophoneDevice). <see cref="Id"/> is the MMDevice endpoint id, stable across reboots.</summary>
public sealed record MicrophoneDevice(string Id, string Name, bool IsDefault, bool IsDefaultCommunications);

public interface IMicrophoneDevices : IDisposable
{
    /// <summary>Every active capture endpoint, the default first, then by name.</summary>
    IReadOnlyList<MicrophoneDevice> Available();

    MicrophoneDevice? Default();

    MicrophoneDevice? Find(string id);

    /// <summary>The device a take records from: the chosen one when connected, else the default. <c>ChosenIdIsMissing</c> when a choice was made but is not connected.</summary>
    (MicrophoneDevice? Device, bool ChosenIdIsMissing) DeviceToSelect(string? preferredId);

    /// <summary>The name of the microphone a take would use now, for "Automatic (…)".</summary>
    string? EffectiveInputName(string? preferredId);

    /// <summary>The person chose a connected microphone that is not the default (echo-cancelled capture is then skipped, as on the Mac).</summary>
    bool ChoiceDiffersFromDefault(string? preferredId);

    /// <summary>A device was added, removed, enabled/disabled or the default changed (raised on a COM thread).</summary>
    event Action? DevicesChanged;
}

public enum AudioSampleFormat
{
    Float32,
    Pcm16,
}

public sealed record AudioCaptureOptions
{
    /// <summary>Endpoint id; null = the default capture device (or the default communications device when echo cancellation is asked for).</summary>
    public string? DeviceId { get; init; }
    public int SampleRate { get; init; } = 16_000;
    /// <summary>Pcm16 also fills <see cref="AudioFrame.Pcm16"/>; samples are always provided as float.</summary>
    public AudioSampleFormat Format { get; init; } = AudioSampleFormat.Pcm16;
    /// <summary>
    /// Realtime voice: open the stream with AudioCategory_Communications so Windows applies its
    /// communications processing (echo cancellation where the driver or Windows Voice Clarity has it).
    /// Falls back to a plain stream when that fails.
    /// </summary>
    public bool PreferEchoCancellation { get; init; }
    /// <summary>WASAPI buffer length in milliseconds (frames arrive about this often).</summary>
    public int BufferMilliseconds { get; init; } = 20;
}

/// <summary>One block of captured audio, already mono and at the requested rate. The arrays belong to the receiver.</summary>
/// <param name="Level">This block's level: RMS × 10.2 clamped to 0…1 (the Mac's meter before smoothing).</param>
public sealed record AudioFrame(float[] Samples, byte[]? Pcm16, int SampleRate, float Level);

public sealed record AudioCaptureStartResult(string Description, string DeviceId, string DeviceName, bool EchoCancellationRequested, bool EchoCancellationApplied);

/// <summary>
/// Microphone capture (AVAudioEngine's input tap). <see cref="FramesAvailable"/> is raised on the
/// capture thread: handlers must not touch UI state (post to the main thread instead), the same
/// rule the Mac's AGENTS.md sets for audio taps.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Opens the device and starts delivering frames. Fast (WASAPI shared mode, tens of ms); throws <see cref="AudioCaptureException"/>.</summary>
    Task<AudioCaptureStartResult> StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default);

    void Stop();

    bool IsCapturing { get; }

    event Action<AudioFrame>? FramesAvailable;

    /// <summary>Capture ended by itself (device unplugged, access revoked): the exception, or null after <see cref="Stop"/>.</summary>
    event Action<Exception?>? CaptureStopped;

    /// <summary>The smoothed meter for the waveform: max(new level, previous × 0.72) — safe to read from any thread.</summary>
    float CurrentLevel { get; }
}

public enum AudioCaptureFailure
{
    NoDevice,
    AccessDenied,
    DeviceInUse,
    Other,
}

public sealed class AudioCaptureException(AudioCaptureFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public AudioCaptureFailure Failure { get; } = failure;
}

/// <summary>Plays one rendered answer (AVAudioPlayer): WAV, MP3, or anything Media Foundation decodes.</summary>
public interface IAudioPlayer
{
    /// <summary>Plays and returns when it has finished or was stopped. Cancelling stops the audio and throws OperationCanceledException.</summary>
    Task PlayToCompletionAsync(byte[] audio, CancellationToken cancellationToken = default);

    /// <summary>Raw PCM16 mono at <paramref name="sampleRate"/>.</summary>
    Task PlayPcm16ToCompletionAsync(byte[] pcm16, int sampleRate, CancellationToken cancellationToken = default);

    bool IsPlaying { get; }

    void StopPlayback();
}

/// <summary>The Realtime voice's output: PCM16 mono 24 kHz chunks as they arrive, flushable for barge-in.</summary>
public interface IStreamingPcmPlayer : IDisposable
{
    int SampleRate { get; }

    void Start();

    void Enqueue(ReadOnlySpan<byte> pcm16);

    /// <summary>Drops everything queued and stops at once (the person started talking).</summary>
    void Flush();

    void Stop();

    bool IsPlaybackActive { get; }

    /// <summary>True when the first chunk starts, false when the queue runs dry or is flushed (raised on a background thread).</summary>
    event Action<bool>? PlaybackActiveChanged;
}

/// <summary>The short tones around a take.</summary>
public enum DictationEarcon
{
    Start,
    Stop,
    Complete,
    Blocked,
    Error,
    Notify,
    Goodbye,
}

public interface IEarconPlayer
{
    /// <summary>Settings → sounds; when false <see cref="Play"/> does nothing.</summary>
    bool Enabled { get; set; }

    void Play(DictationEarcon earcon);
}

/// <summary>The earcons' notes and their synthesis (a sine with an 8 ms attack and a linear release), as DictationEarcons.swift.</summary>
public static class DictationEarconSounds
{
    public const int SampleRate = 44_100;
    /// <summary>The player volume the Mac sets on each earcon.</summary>
    public const float Volume = 0.35f;

    public static IReadOnlyList<(double Frequency, double Seconds)> Notes(DictationEarcon earcon) => earcon switch
    {
        DictationEarcon.Start => [(660, 0.06), (880, 0.09)],
        DictationEarcon.Stop => [(880, 0.06), (660, 0.09)],
        DictationEarcon.Complete => [(784, 0.07), (1047, 0.11)],
        DictationEarcon.Blocked => [(330, 0.12)],
        DictationEarcon.Error => [(392, 0.1), (294, 0.14)],
        DictationEarcon.Notify => [(988, 0.09)],
        // Quitting: three falling notes, a "see you" rather than an alarm.
        DictationEarcon.Goodbye => [(1047, 0.08), (784, 0.08), (523, 0.18)],
        _ => [],
    };

    /// <summary>PCM16 mono samples of the earcon (before the player's volume).</summary>
    public static short[] Samples(DictationEarcon earcon, int sampleRate = SampleRate)
    {
        var samples = new List<short>();
        foreach (var (frequency, seconds) in Notes(earcon))
        {
            var count = (int)(seconds * sampleRate);
            var attack = (int)(0.008 * sampleRate);
            for (var index = 0; index < count; index++)
            {
                double envelope = index < attack
                    ? index / (double)attack
                    : 1 - (index - attack) / (double)Math.Max(1, count - attack);
                var value = Math.Sin(2 * Math.PI * frequency * index / sampleRate) * envelope * 0.8;
                samples.Add((short)(Math.Clamp(value, -1, 1) * short.MaxValue));
            }
        }
        return samples.ToArray();
    }

    /// <summary>The earcon as a 16-bit mono WAV file.</summary>
    public static byte[] Wav(DictationEarcon earcon, int sampleRate = SampleRate)
    {
        var samples = Samples(earcon, sampleRate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in samples) writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }
}

/// <summary>Microphone privacy state (Settings → Privacy &amp; security → Microphone).</summary>
public enum MicrophoneConsent
{
    Allowed,
    /// <summary>"Microphone access" is off for the whole device (HKLM).</summary>
    DeniedForDevice,
    /// <summary>"Let apps access your microphone" is off for this user.</summary>
    DeniedForAllApps,
    /// <summary>"Let desktop apps access your microphone" is off.</summary>
    DeniedForDesktopApps,
    Unknown,
}

public enum MicrophoneTestResult
{
    /// <summary>Audio arrived with signal in it.</summary>
    Works,
    /// <summary>The stream opened but only digital silence arrived (Windows feeds silence to apps it denies, or the mic is muted).</summary>
    Silent,
    AccessDenied,
    NoDevice,
    Failed,
}

public interface IMicrophonePermission
{
    /// <summary>The ConsentStore registry values (HKLM device switch, HKCU microphone, HKCU microphone\NonPackaged).</summary>
    MicrophoneConsent ReadConsent();

    /// <summary>Opens the device for ~0.4 s and reports whether real audio arrived.</summary>
    Task<MicrophoneTestResult> TestCaptureAsync(string? deviceId = null, CancellationToken cancellationToken = default);

    /// <summary>Opens ms-settings:privacy-microphone.</summary>
    void OpenPrivacySettings();
}
