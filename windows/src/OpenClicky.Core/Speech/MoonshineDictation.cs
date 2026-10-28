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
        var request = new ParakeetEngineRequest(Convert.ToBase64String(pcm16), ModelFolder);
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
