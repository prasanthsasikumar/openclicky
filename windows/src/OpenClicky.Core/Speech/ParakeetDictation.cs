// Ports Parakeet/ParakeetDictation.swift.
//
// Parakeet: an offline dictation engine better than the one Windows has. Windows' own offline
// recogniser (Windows.Media.SpeechRecognition) is years behind, like SFSpeechRecognizer before
// macOS 26. NVIDIA's Parakeet TDT 0.6B v3 runs on any PC's CPU, writes its own punctuation and
// capitals, and hears 25 European languages. No Indian languages: those stay with the Windows
// recogniser or Sarvam.
//
// The engine is the sherpa-onnx package that already ships for Kokoro; only the model (465 MB) is
// downloaded. The loaded model lives in a helper process that starts the moment the dictation key
// goes down (it loads while the person speaks) and ends after three minutes without a take, or at
// once under memory pressure.

using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed partial class ParakeetDictation : ObservableObject
{
    /// <summary>The app's Parakeet (assigned at startup).</summary>
    public static ParakeetDictation? Shared { get; set; }

    public const string HelperFlag = "--openclicky-parakeet-helper";

    public static ModelDownload ModelDownload { get; } = new(
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"),
        "5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf", 487_170_055);

    /// <summary>What Settings says before the download, rounded.</summary>
    public const string DownloadSizeText = "465 MB";

    /// <summary>The languages Parakeet v3 hears (ISO 639-1).</summary>
    public static IReadOnlySet<string> LanguageCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt",
        "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk",
    };

    /// <summary>Whether it hears <paramref name="bareLanguageCode"/> (null: the dictation language is on
    /// auto-detect, which Parakeet does itself).</summary>
    public static bool Hears(string? bareLanguageCode) => bareLanguageCode is null || LanguageCodes.Contains(bareLanguageCode);

    public static string Folder => Path.Combine(OpenClickyPaths.ModelsDirectory, "Parakeet");
    public static string ModelFolder => Path.Combine(Folder, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8");

    public static bool IsInstalled => IsInstalledIn(ModelFolder);

    public static bool IsInstalledIn(string modelFolder) =>
        KokoroVoice.EngineIsAvailable()
        && new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" }
            .All(name => File.Exists(Path.Combine(modelFolder, name)));

    public const int IdleUnloadSeconds = 180;

    /// <summary>Set when the person removed the model or cancelled its download: it is never fetched again
    /// on its own (choosing it in Settings clears this).</summary>
    public const string DeclinedKey = "openclicky.parakeet.declined";

    /// <summary>
    /// Whether this PC should get Parakeet without being asked. On the Mac only before macOS 26,
    /// where the offline engine is the old recogniser; Windows' offline engine is the old Windows
    /// recogniser on every version, so always.
    /// </summary>
    public static bool IsRecommendedForThisPC => true;

    /// <summary>The dictation engine raw values (DictationEngineChoice in the dictation port).</summary>
    public const string OfflineEngineRawValue = "offline";
    public const string ParakeetEngineRawValue = "parakeet";

    /// <summary>The engine picked in Settings → Dictation, as its raw value.
    /// TODO(integration): DictationSettings.Shared.Engine (the dictation port's DictationEngineChoice).</summary>
    public Func<string>? CurrentDictationEngine { get; set; }

    /// <summary>Changes the engine picked in Settings → Dictation. TODO(integration): DictationSettings.Shared.Engine = …</summary>
    public Action<string>? SetDictationEngine { get; set; }

    private readonly SherpaHelperClient engine;
    private readonly IProcessRunner processRunner;
    private readonly INetworkReachability network;
    private readonly Preferences preferences;
    private readonly IMainThread? mainThread;
    private readonly object taskLock = new();
    private CancellationTokenSource? downloadCancellation;
    private CancellationTokenSource? idleUnloadCancellation;

    [ObservableProperty]
    private ModelInstallState state;

    public ParakeetDictation(IProcessRunner processRunner, INetworkReachability network, Preferences preferences, string helperExecutablePath, IMainThread? mainThread = null)
    {
        this.processRunner = processRunner;
        this.network = network;
        this.preferences = preferences;
        this.mainThread = mainThread;
        engine = new SherpaHelperClient(HelperFlag, "parakeet", processRunner, helperExecutablePath);
        state = IsInstalled ? ModelInstallState.Installed : ModelInstallState.NotInstalled;
    }

    private void SetState(ModelInstallState newState)
    {
        if (mainThread is null || mainThread.IsMainThread) State = newState;
        else mainThread.Post(() => State = newState);
    }

    // MARK: Hearing

    /// <summary>The dictation key went down: start the helper and load the model while the take is spoken.</summary>
    public void PrepareForTake()
    {
        if (!IsInstalled) return;
        _ = Task.Run(async () =>
        {
            var startedAt = DateTime.UtcNow;
            try { await TranscribeAsync([]).ConfigureAwait(false); }
            catch (Exception) { }
            AppLog.Write($"parakeet: ready in {(DateTime.UtcNow - startedAt).TotalSeconds:0.0} s");
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
        if (await engine.UnloadAsync().ConfigureAwait(false)) AppLog.Write($"parakeet: unloaded ({reason})");
    }

    // MARK: Automatic setup

    /// <summary>At launch and when the welcome tour ends: on a PC whose dictation is on the offline engine,
    /// the model downloads in the background and takes over when it is ready.</summary>
    public void StartAutomaticSetup(bool tourIsFinished, string currentEngineRawValue, string? bareLanguageCode)
    {
        bool downloading;
        lock (taskLock) downloading = downloadCancellation is not null;
        if (!IsRecommendedForThisPC || IsInstalled || !tourIsFinished || downloading
            || currentEngineRawValue != OfflineEngineRawValue || !Hears(bareLanguageCode)
            || preferences.GetBool(DeclinedKey)
            || ModelDownload.FreeSpace() < ModelDownload.AutomaticDownloadFreeSpace) return;
        if (ModelDownload.NetworkIsExpensiveOrConstrained(network))
        {
            AppLog.Write("parakeet: automatic download waits for an unmetered network");
            return;
        }
        AppLog.Write("parakeet: automatic download started");
        Download(isAutomatic: true);
    }

    /// <summary>The engine once an automatic download finishes: Parakeet replaces the old offline
    /// recogniser; an engine the person moved to themselves is left alone.</summary>
    public static string EngineAfterAutomaticDownload(string current) =>
        current == OfflineEngineRawValue ? ParakeetEngineRawValue : current;

    // MARK: Download

    public void Download(bool isAutomatic = false)
    {
        if (!isAutomatic) preferences.Set(DeclinedKey, false);
        CancellationTokenSource cancellation;
        lock (taskLock)
        {
            if (downloadCancellation is not null || IsInstalled)
            {
                if (IsInstalled) SetState(ModelInstallState.Installed);
                return;
            }
            cancellation = new CancellationTokenSource();
            downloadCancellation = cancellation;
        }
        SetState(ModelInstallState.Downloading(0));
        _ = Task.Run(async () =>
        {
            try
            {
                await ModelDownload.InstallAsync(Folder, fraction =>
                {
                    // A late progress report must not undo the installed state.
                    if (!State.IsBusy) return;
                    SetState(fraction >= 1 ? ModelInstallState.Unpacking : ModelInstallState.Downloading(fraction));
                }, processRunner, cancellation.Token).ConfigureAwait(false);
                if (!IsInstalled) throw ModelDownloadException.UnpackFailed("the files weren't where they should be");
                AppLog.Write("parakeet: installed");
                SetState(ModelInstallState.Installed);
                if (isAutomatic && CurrentDictationEngine?.Invoke() is { } current)
                {
                    var next = EngineAfterAutomaticDownload(current);
                    if (next != current)
                    {
                        SetDictationEngine?.Invoke(next);
                        AppLog.Write("parakeet: now the dictation engine");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SetState(ModelInstallState.NotInstalled);
            }
            catch (Exception error)
            {
                AppLog.Write($"parakeet: download failed: {error.Message}");
                SetState(ModelInstallState.Failed(error.Message));
            }
            finally
            {
                lock (taskLock)
                {
                    if (ReferenceEquals(downloadCancellation, cancellation)) downloadCancellation = null;
                }
                cancellation.Dispose();
            }
        });
    }

    /// <summary>A cancelled download is a no: it isn't started again on its own.</summary>
    public void CancelDownload()
    {
        preferences.Set(DeclinedKey, true);
        lock (taskLock) downloadCancellation?.Cancel();
    }

    /// <summary>Deletes the model and ends the helper; dictation goes back to the Windows recogniser.</summary>
    public async Task RemoveAsync()
    {
        preferences.Set(DeclinedKey, true);
        lock (taskLock) downloadCancellation?.Cancel();
        await engine.UnloadAsync().ConfigureAwait(false);
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"parakeet: could not remove the model: {error.Message}");
        }
        SetState(ModelInstallState.NotInstalled);
        if (CurrentDictationEngine?.Invoke() == ParakeetEngineRawValue) SetDictationEngine?.Invoke(OfflineEngineRawValue);
    }
}
