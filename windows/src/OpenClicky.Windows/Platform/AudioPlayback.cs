using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// One answer at a time to the default output (AVAudioPlayer + AnswerVoicePlayer.playToCompletion):
/// WAV through WaveFileReader, MP3 through Mp3FileReader, anything else through Media Foundation.
/// </summary>
public sealed class AudioPlayer : IAudioPlayer
{
    private readonly object playbackLock = new();
    private WaveOutEvent? currentOutput;

    public bool IsPlaying
    {
        get { lock (playbackLock) return currentOutput?.PlaybackState == PlaybackState.Playing; }
    }

    public Task PlayToCompletionAsync(byte[] audio, CancellationToken cancellationToken = default) =>
        PlayAsync(() => OpenReader(audio), cancellationToken);

    public Task PlayPcm16ToCompletionAsync(byte[] pcm16, int sampleRate, CancellationToken cancellationToken = default) =>
        PlayAsync(() => new RawSourceWaveStream(new MemoryStream(pcm16), new WaveFormat(sampleRate, 16, 1)), cancellationToken);

    public void StopPlayback()
    {
        WaveOutEvent? output;
        lock (playbackLock) output = currentOutput;
        output?.Stop();
    }

    private async Task PlayAsync(Func<WaveStream> openReader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopPlayback();
        var reader = openReader();
        var output = AudioOutputs.Create(desiredLatency: 150);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => completion.TrySetResult();
        try
        {
            output.Init(reader);
            lock (playbackLock)
            {
                currentOutput = output;
            }
            output.Play();
            using (cancellationToken.Register(() => output.Stop()))
            {
                await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            lock (playbackLock)
            {
                if (ReferenceEquals(currentOutput, output))
                {
                    currentOutput = null;
                }
            }
            output.Dispose();
            reader.Dispose();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static WaveStream OpenReader(byte[] audio)
    {
        var stream = new MemoryStream(audio, writable: false);
        if (audio.Length >= 12 && audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F') return new WaveFileReader(stream);
        var looksLikeMp3 = audio.Length >= 3 && ((audio[0] == 'I' && audio[1] == 'D' && audio[2] == '3') || (audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0));
        if (looksLikeMp3) return new Mp3FileReader(stream);
        return new StreamMediaFoundationReader(stream);
    }
}

/// <summary>
/// The Realtime voice's output (RealtimeAudioEngine's player node): PCM16 mono 24 kHz chunks into a
/// buffered provider that plays silence when empty, so the device stays open and the next chunk starts at once.
/// </summary>
public sealed class StreamingPcmPlayer : IStreamingPcmPlayer
{
    private readonly object stateLock = new();
    private readonly WaveFormat format;
    private BufferedWaveProvider? buffer;
    private WaveOutEvent? output;
    private Timer? drainWatcher;
    private bool isPlaybackActive;

    public StreamingPcmPlayer(int sampleRate = 24_000)
    {
        SampleRate = sampleRate;
        format = new WaveFormat(sampleRate, 16, 1);
    }

    public int SampleRate { get; }

    public event Action<bool>? PlaybackActiveChanged;

    public bool IsPlaybackActive
    {
        get { lock (stateLock) return isPlaybackActive; }
    }

    public void Start()
    {
        lock (stateLock)
        {
            if (output is not null) return;
            buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMinutes(5),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            output = AudioOutputs.Create(desiredLatency: 100);
            output.Init(buffer);
            output.Play();
            drainWatcher = new Timer(_ => CheckDrained(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        }
    }

    public void Enqueue(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length == 0) return;
        Start(); // no-op when already started
        var becameActive = false;
        lock (stateLock)
        {
            if (buffer is null) return;
            var bytes = pcm16.ToArray();
            buffer.AddSamples(bytes, 0, bytes.Length);
            if (!isPlaybackActive)
            {
                isPlaybackActive = true;
                becameActive = true;
            }
        }
        if (becameActive) PlaybackActiveChanged?.Invoke(true);
    }

    public void Flush()
    {
        var becameIdle = false;
        lock (stateLock)
        {
            buffer?.ClearBuffer();
            if (isPlaybackActive)
            {
                isPlaybackActive = false;
                becameIdle = true;
            }
        }
        if (becameIdle) PlaybackActiveChanged?.Invoke(false);
    }

    public void Stop()
    {
        Flush();
        lock (stateLock)
        {
            drainWatcher?.Dispose();
            drainWatcher = null;
            output?.Stop();
            output?.Dispose();
            output = null;
            buffer = null;
        }
    }

    private void CheckDrained()
    {
        var becameIdle = false;
        lock (stateLock)
        {
            if (isPlaybackActive && buffer is not null && buffer.BufferedBytes == 0)
            {
                isPlaybackActive = false;
                becameIdle = true;
            }
        }
        if (becameIdle) PlaybackActiveChanged?.Invoke(false);
    }

    public void Dispose() => Stop();
}

/// <summary>The earcons (DictationEarconPlayer): synthesised once each, played at the Mac's 0.35 volume.</summary>
public sealed class EarconPlayer : IEarconPlayer
{
    private readonly Dictionary<DictationEarcon, byte[]> wavByEarcon = new();
    private readonly object cacheLock = new();

    public bool Enabled { get; set; } = true;

    public void Play(DictationEarcon earcon)
    {
        if (!Enabled) return;
        byte[] wav;
        lock (cacheLock)
        {
            if (!wavByEarcon.TryGetValue(earcon, out wav!))
            {
                wav = DictationEarconSounds.Wav(earcon);
                wavByEarcon[earcon] = wav;
            }
        }
        var reader = new WaveFileReader(new MemoryStream(wav, writable: false));
        var output = AudioOutputs.Create(desiredLatency: 60);
        var volume = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = DictationEarconSounds.Volume };
        output.PlaybackStopped += (_, _) =>
        {
            output.Dispose();
            reader.Dispose();
        };
        try
        {
            output.Init(volume);
            output.Play();
        }
        catch (Exception exception) when (exception is NAudio.MmException or InvalidOperationException)
        {
            output.Dispose();
            reader.Dispose();
        }
    }
}

internal static class AudioOutputs
{
    /// <summary>
    /// A WaveOutEvent created with no SynchronizationContext: NAudio captures the current one and
    /// raises PlaybackStopped through it, which would tie every playback's end to the UI thread.
    /// </summary>
    public static WaveOutEvent Create(int desiredLatency)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new WaveOutEvent { DesiredLatency = desiredLatency, NumberOfBuffers = 3 };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
