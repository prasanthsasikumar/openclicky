// Ports Kokoro/KokoroVoice.swift (with KokoroEngine.Request/wav and KokoroHelper.Reply).
//
// Kokoro: an open 82M-parameter voice model that runs on this PC, free and offline, through
// sherpa-onnx (the org.k2fsa.sherpa.onnx package ships the engine next to the exe); only the model
// (350 MB) is downloaded, when Kokoro is picked in Settings or on its own once the welcome tour is
// done, checked against a pinned SHA-256 and unpacked into %LOCALAPPDATA%\OpenClicky\Models.
//
// The loaded model takes about 400 MB of memory, so it lives in a helper process (OpenClicky.exe
// --openclicky-kokoro-helper, SherpaHelperMain) that runs only around a conversation:
// `PrepareForAnswer` starts and warms it the moment the ask shortcut is pressed, and it is ended
// after three minutes without an answer, or at once on memory pressure.

using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed partial class KokoroVoice : ObservableObject
{
    /// <summary>The app's Kokoro (assigned at startup).</summary>
    public static KokoroVoice? Shared { get; set; }

    /// <summary>The sherpa-onnx version the helper runs (the org.k2fsa.sherpa.onnx package).</summary>
    public const string RuntimeVersion = "1.13.8";

    public const string HelperFlag = "--openclicky-kokoro-helper";

    /// <summary>Kokoro v1.0, multilingual, full precision.</summary>
    public static ModelDownload ModelDownload { get; } = new(
        new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2"),
        "60703707bc05ccd48d60e1eb723f38b07d6e474795a0b6a588d7220400e0f7a4", 349_886_476);

    /// <summary>What Settings says before the download, rounded.</summary>
    public const string DownloadSizeText = "350 MB";

    public static string Folder => Path.Combine(OpenClickyPaths.ModelsDirectory, "Kokoro");
    public static string ModelFolder => Path.Combine(Folder, "kokoro-multi-lang-v1_0");

    /// <summary>Whether the engine is there (SherpaLibrary.isEmbedded on the Mac); the app sets it.</summary>
    public static Func<bool> EngineIsAvailable { get; set; } = () => true;

    public static bool IsInstalled => IsInstalledIn(ModelFolder);

    public static bool IsInstalledIn(string modelFolder) =>
        EngineIsAvailable()
        && File.Exists(Path.Combine(modelFolder, "model.onnx"))
        && File.Exists(Path.Combine(modelFolder, "voices.bin"))
        && File.Exists(Path.Combine(modelFolder, "tokens.txt"))
        && Directory.Exists(Path.Combine(modelFolder, "espeak-ng-data"));

    /// <summary>How long the model stays in memory after the last answer it spoke.</summary>
    public const int IdleUnloadSeconds = 180;

    /// <summary>Set when the person removed Kokoro or cancelled its download: it is never fetched again on
    /// its own (picking it in Settings clears this).</summary>
    public const string DeclinedKey = "openclicky.kokoro.declined";

    private readonly SherpaHelperClient engine;
    private readonly IProcessRunner processRunner;
    private readonly INetworkReachability network;
    private readonly Preferences preferences;
    private readonly IMainThread? mainThread;
    private CancellationTokenSource? downloadCancellation;
    private CancellationTokenSource? idleUnloadCancellation;
    private readonly object taskLock = new();

    [ObservableProperty]
    private ModelInstallState state;

    /// <summary>Whether Kokoro is the voice answers are spoken in now (SpeechVoiceChoice.current() == .kokoro); the app sets it.</summary>
    public Func<bool> IsCurrentVoice { get; set; } = () => false;

    /// <summary>The speaker picked in Settings (shell.json `kokoroSpeaker`).</summary>
    public Func<int?> PickedSpeaker { get; set; } = () => OpenClickyConfiguration.Settings.KokoroSpeaker;

    public KokoroVoice(IProcessRunner processRunner, INetworkReachability network, Preferences preferences, string helperExecutablePath, IMainThread? mainThread = null)
    {
        this.processRunner = processRunner;
        this.network = network;
        this.preferences = preferences;
        this.mainThread = mainThread;
        engine = new SherpaHelperClient(HelperFlag, "kokoro", processRunner, helperExecutablePath);
        state = IsInstalled ? ModelInstallState.Installed : ModelInstallState.NotInstalled;
    }

    private void SetState(ModelInstallState newState)
    {
        if (mainThread is null || mainThread.IsMainThread) State = newState;
        else mainThread.Post(() => State = newState);
    }

    // MARK: Memory

    /// <summary>Called when the ask shortcut is pressed or a typed question is sent: loads and warms the
    /// model while the question is still being heard, if Kokoro is the voice.</summary>
    public void PrepareForAnswer() => WarmUpIfSpeaking();

    /// <summary>Windows reports low memory: give the 400 MB back now; the next question loads it again.</summary>
    public void UnloadForMemoryPressure() => _ = UnloadAsync("memory pressure");

    private static TimeSpan IdleUnloadDelay
    {
        get
        {
#if DEBUG
            // Lets the voice smoke see the unload without waiting three minutes.
            if (int.TryParse(Environment.GetEnvironmentVariable("OPENCLICKY_KOKORO_IDLE_SECONDS"), out var overrideSeconds))
                return TimeSpan.FromSeconds(overrideSeconds);
#endif
            return TimeSpan.FromSeconds(IdleUnloadSeconds);
        }
    }

    /// <summary>Restarts the idle clock; when it runs out the model leaves memory.</summary>
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
            try { await Task.Delay(IdleUnloadDelay, cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await UnloadAsync("idle").ConfigureAwait(false);
        });
    }

    private async Task UnloadAsync(string reason)
    {
        lock (taskLock)
        {
            idleUnloadCancellation?.Cancel();
            idleUnloadCancellation = null;
        }
        if (await engine.UnloadAsync().ConfigureAwait(false)) AppLog.Write($"kokoro: unloaded ({reason})");
    }

    // MARK: Speaking

    /// <summary><paramref name="text"/> in Kokoro's voice, as WAV bytes. The first call loads the model (about 3 s).</summary>
    public async Task<byte[]> AudioAsync(string text, string? languageCode = null, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled) throw new KokoroException(KokoroException.NotInstalledMessage);
        languageCode ??= ReplyLanguage.CurrentCode;
        var speaker = SpeakerFor(languageCode, PickedSpeaker()) ?? throw new KokoroException(KokoroException.LanguageNotSpokenMessage);
        var request = new KokoroEngineRequest(text, speaker.Id, LanguageHint(languageCode), ModelFolder);
        try
        {
            var reply = await engine.SendAsync<KokoroEngineRequest, KokoroHelperReply>(request, cancellationToken).ConfigureAwait(false);
            try
            {
                var wav = Convert.FromBase64String(reply.Wav);
                if (wav.Length == 0) throw new KokoroException(KokoroException.NothingGeneratedMessage);
                return wav;
            }
            catch (FormatException)
            {
                throw new KokoroException(KokoroException.NothingGeneratedMessage);
            }
        }
        finally
        {
            ScheduleIdleUnload();
        }
    }

    /// <summary>Loads the model and speaks one throwaway word, so the first real answer starts at once.</summary>
    public void WarmUpIfSpeaking()
    {
        if (!IsInstalled || !IsCurrentVoice()) return;
        _ = Task.Run(async () =>
        {
            var startedAt = DateTime.UtcNow;
            try { await AudioAsync("ok").ConfigureAwait(false); }
            catch (Exception) { return; }
            AppLog.Write($"kokoro: warmed up in {(DateTime.UtcNow - startedAt).TotalSeconds:0.0} s");
        });
    }

    // MARK: Automatic setup

    /// <summary>
    /// At launch and when the welcome tour ends: once setup is done, Kokoro downloads in the
    /// background, and when it is ready it becomes the voice (if the voice was left on Automatic),
    /// so answers stop spending the free ElevenLabs characters. Nothing is loaded here: that waits
    /// for the first question (<see cref="PrepareForAnswer"/>).
    /// </summary>
    public void StartAutomaticSetup(bool tourIsFinished)
    {
        if (IsInstalled) return;
        bool downloading;
        lock (taskLock) downloading = downloadCancellation is not null;
        if (!tourIsFinished || downloading
            || preferences.GetBool(DeclinedKey)
            || SpeakerFor(ReplyLanguage.CurrentCode, null) is null
            || ModelDownload.FreeSpace() < ModelDownload.AutomaticDownloadFreeSpace) return;
        // Not on a phone hotspot or a metered connection: it tries again next launch.
        if (ModelDownload.NetworkIsExpensiveOrConstrained(network))
        {
            AppLog.Write("kokoro: automatic download waits for an unmetered network");
            return;
        }
        AppLog.Write("kokoro: automatic download started");
        Download(isAutomatic: true);
    }

    /// <summary>The raw values SpeechVoiceChoice knows; anything else (nil, "auto", the older "cloud") is Automatic.</summary>
    private static readonly HashSet<string> KnownVoiceRawValues =
        ["automatic", "account", "openai", "elevenlabs", "google", "gemini", "polly", "sarvam", "kokoro", "mac"];

    /// <summary>The decision once an automatic download finishes: Kokoro takes over from Automatic; a voice
    /// the person picked themselves is left alone.</summary>
    public static string? VoiceAfterAutomaticDownload(string? stored, string languageCode)
    {
        var isAutomatic = stored is null || stored == "automatic" || !KnownVoiceRawValues.Contains(stored);
        if (!isAutomatic || SpeakerFor(languageCode, null) is null) return stored;
        return "kokoro";
    }

    // MARK: Download

    /// <summary>Downloads, checks and unpacks Kokoro; does nothing while a download runs or once installed.
    /// Picking Kokoro in Settings calls this; so does the automatic setup.</summary>
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
                AppLog.Write("kokoro: installed");
                SetState(ModelInstallState.Installed);
                if (isAutomatic)
                {
                    var settings = OpenClickyConfiguration.Settings;
                    var voice = VoiceAfterAutomaticDownload(settings.SpeechVoice, ReplyLanguage.CurrentCode);
                    if (voice != settings.SpeechVoice)
                    {
                        OpenClickyConfiguration.Update(changed => changed.SpeechVoice = voice);
                        AppLog.Write("kokoro: now the voice");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SetState(ModelInstallState.NotInstalled);
            }
            catch (Exception error)
            {
                AppLog.Write($"kokoro: download failed: {error.Message}");
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

    /// <summary>Deletes the download and unloads the model.</summary>
    public async Task RemoveAsync()
    {
        preferences.Set(DeclinedKey, true);
        lock (taskLock) downloadCancellation?.Cancel();
        await engine.UnloadAsync().ConfigureAwait(false);
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"kokoro: could not remove the model: {error.Message}");
        }
        SetState(ModelInstallState.NotInstalled);
    }
}

/// <summary>One request to the Kokoro helper (KokoroEngine.Request).</summary>
/// <param name="LanguageHint">sherpa-onnx's `lang` hint; English and Mandarin go by the model's lexicons instead.</param>
public sealed record KokoroEngineRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("speakerID")] int SpeakerId,
    [property: JsonPropertyName("languageHint")] string LanguageHint,
    [property: JsonPropertyName("modelFolder")] string ModelFolder);

/// <summary>The helper's answer: the spoken WAV, base64.</summary>
public sealed record KokoroHelperReply([property: JsonPropertyName("wav")] string Wav);

public static class KokoroEngine
{
    /// <summary>Mono float samples in [-1, 1] as a 16-bit WAV file (clipped, never wrapped).</summary>
    public static byte[] Wav(ReadOnlySpan<float> samples, int sampleRate) =>
        BuddyWAVFileBuilder.BuildWavData(BuddyPCM16AudioConverter.FloatToPcm16(samples), sampleRate);
}

public sealed class KokoroException(string message) : Exception(message)
{
    public const string NotInstalledMessage = "Kokoro isn't downloaded yet.";
    public const string LanguageNotSpokenMessage = "Kokoro doesn't speak this language.";
    public const string ModelNotLoadedMessage = "Kokoro's model didn't load.";
    public const string NothingGeneratedMessage = "Kokoro said nothing.";
}
