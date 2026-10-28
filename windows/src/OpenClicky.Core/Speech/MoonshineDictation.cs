// Moonshine: the dictation engine that ships inside the app, so a fresh install dictates offline at
// once, with no download and without Windows' "online speech recognition" switch. Useful Sensors'
// Moonshine tiny (English, int8) through the same sherpa-onnx helper as Parakeet, under its own flag;
// the request and reply are Parakeet's (16 kHz PCM16 in, words out). Parakeet still downloads on its
// own after the tour and takes over (ParakeetDictation.EngineAfterAutomaticDownload).
//
// Source: https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-moonshine-tiny-en-int8.tar.bz2
// (pinned with its SHA-256 in windows/scripts/fetch-speech-model.ps1).

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class MoonshineDictation
{
    /// <summary>The app's Moonshine (assigned at startup).</summary>
    public static MoonshineDictation? Shared { get; set; }

    public const string HelperFlag = "--openclicky-moonshine-helper";

    public const int IdleUnloadSeconds = 180;

    public static string ModelFolder => MoonshineFacts.ModelFolder;
    public static bool IsInstalled => MoonshineFacts.IsInstalled;
    public static bool IsInstalledIn(string modelFolder) => MoonshineFacts.IsInstalledIn(modelFolder);

    private readonly SherpaHelperClient engine;
    private readonly object taskLock = new();
    private CancellationTokenSource? idleUnloadCancellation;

    public MoonshineDictation(IProcessRunner processRunner, string helperExecutablePath)
    {
        engine = new SherpaHelperClient(HelperFlag, "moonshine", processRunner, helperExecutablePath);
    }

    /// <summary>The dictation key went down: start the helper and load the model while the take is spoken.</summary>
    public void PrepareForTake()
    {
        if (!IsInstalled) return;
        _ = Task.Run(async () =>
        {
            var startedAt = DateTime.UtcNow;
            try { await TranscribeAsync([]).ConfigureAwait(false); }
            catch (Exception) { }
            AppLog.Write($"moonshine: ready in {(DateTime.UtcNow - startedAt).TotalSeconds:0.0} s");
        });
    }

    /// <summary>The words in 16 kHz mono PCM16 audio.</summary>
    public async Task<string> TranscribeAsync(byte[] pcm16, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled) throw new ParakeetException(ParakeetException.NotInstalledMessage);
        var request = new ParakeetEngineRequest(Convert.ToBase64String(TrimQuietEdges(pcm16)), ModelFolder);
        try
        {
            var reply = await engine.SendAsync<ParakeetEngineRequest, ParakeetEngineReply>(request, cancellationToken).ConfigureAwait(false);
            return reply.Text;
        }
        finally
        {
            ScheduleIdleUnload();
        }
    }

    /// <summary>Quiet kept before the first and after the last loud frame.</summary>
    public const double QuietEdgeSeconds = 0.3;
    private const int FrameSamples = ParakeetEngine.SampleRate / 50; // 20 ms

    /// <summary>
    /// The audio from just before the first loud frame to just after the last one. Moonshine tiny reads a
    /// take that opens with 1.7 s or more of quiet as no words at all (2026-10-28: the first take on a fresh
    /// install, held while the model loaded, failed with "didn't catch that" though the live words were
    /// right); with the edges trimmed to <see cref="QuietEdgeSeconds"/> the same audio reads correctly.
    /// Loud is a tenth of the take's loudest frame, so room noise at the edges goes too; audio with no
    /// frame clearly above the rest is returned as it is.
    /// </summary>
    public static byte[] TrimQuietEdges(byte[] pcm16)
    {
        var frameCount = pcm16.Length / 2 / FrameSamples;
        if (frameCount == 0) return pcm16;
        var levels = new double[frameCount];
        var loudest = 0.0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            double sumOfSquares = 0;
            for (var sample = 0; sample < FrameSamples; sample++)
            {
                var offset = (frame * FrameSamples + sample) * 2;
                double value = (short)(pcm16[offset] | (pcm16[offset + 1] << 8));
                sumOfSquares += value * value;
            }
            levels[frame] = Math.Sqrt(sumOfSquares / FrameSamples);
            loudest = Math.Max(loudest, levels[frame]);
        }
        // Below about -50 dBFS nothing in the take is a voice: leave it to the engine.
        if (loudest < 100) return pcm16;
        var threshold = loudest / 10;
        var first = Array.FindIndex(levels, level => level >= threshold);
        var last = Array.FindLastIndex(levels, level => level >= threshold);
        var edgeFrames = (int)(QuietEdgeSeconds * 50);
        var startByte = Math.Max(0, first - edgeFrames) * FrameSamples * 2;
        var endByte = Math.Min(pcm16.Length, (last + 1 + edgeFrames) * FrameSamples * 2);
        if (startByte == 0 && endByte == pcm16.Length) return pcm16;
        return pcm16[startByte..endByte];
    }

    private void ScheduleIdleUnload()
    {
        var cancellation = new CancellationTokenSource();
        lock (taskLock)
        {
            idleUnloadCancellation?.Cancel();
            idleUnloadCancellation = cancellation;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(IdleUnloadSeconds), cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await UnloadAsync("idle").ConfigureAwait(false);
        });
    }

    /// <summary>Windows reports low memory: the helper goes; the next take loads it again.</summary>
    public void UnloadForMemoryPressure() => _ = UnloadAsync("memory pressure");

    private async Task UnloadAsync(string reason)
    {
        lock (taskLock)
        {
            idleUnloadCancellation?.Cancel();
            idleUnloadCancellation = null;
        }
        if (await engine.UnloadAsync().ConfigureAwait(false)) AppLog.Write($"moonshine: unloaded ({reason})");
    }
}

/// <summary>Moonshine as a dictation provider: Parakeet's session (the take collected, read again about
/// once a second for the live words, read whole on release) over Moonshine's helper.</summary>
public sealed class MoonshineTranscriptionProvider : IBuddyTranscriptionProvider
{
    private readonly Func<MoonshineDictation?> dictation;
    private readonly Func<string?> unavailableReason;

    /// <param name="unavailableReason">The resolver's rule (in this build, and English or auto-detect); by
    /// default DictationEngineResolver.MoonshineUnavailableReason for the stored dictation language.</param>
    public MoonshineTranscriptionProvider(Func<string?>? unavailableReason = null, Func<MoonshineDictation?>? dictation = null)
    {
        this.dictation = dictation ?? (() => MoonshineDictation.Shared);
        this.unavailableReason = unavailableReason
            ?? (() => DictationEngineResolver.MoonshineUnavailableReason(MoonshineFacts.IsInstalled, DictationSettings.StoredLanguage().BareCode));
    }

    public string DisplayName => "Moonshine (on this PC)";
    public bool RequiresSpeechRecognitionPermission => false;
    public bool IsConfigured => UnavailableExplanation is null;
    public string? UnavailableExplanation => unavailableReason();

    public Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default)
    {
        if (!MoonshineDictation.IsInstalled || dictation() is not { } moonshine)
            throw new ParakeetException(ParakeetException.NotInstalledMessage);
        moonshine.PrepareForTake();
        IBuddyStreamingTranscriptionSession session = new ParakeetTranscriptionSession(
            onTranscriptUpdate, onFinalTranscriptReady, onError, moonshine.TranscribeAsync);
        return Task.FromResult(session);
    }
}
