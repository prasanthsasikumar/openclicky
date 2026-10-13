// Ports RecordedVoice.swift: the lines OpenClicky says the same way every time, recorded once in
// its answering voice (OpenAI speech, marin) and shipped with the app (Assets\RecordedVoice\*.mp3,
// copied from the Mac bundle), so they sound good with no key and no network: the first-run tour's
// narration, and the notices that used to be read by the system voice ("i didn't get an
// answer…"). Anything not recorded here is spoken the way it was before.
//
// Gap: the Mac plays these at volume 0.85; IAudioPlayer has no volume, so they play at full volume.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public sealed class RecordedVoice
{
    public enum Line
    {
        [RawValue("tour-hello")] TourHello,
        [RawValue("tour-dictate")] TourDictate,
        [RawValue("tour-dictate-done")] TourDictateDone,
        [RawValue("tour-talk")] TourTalk,
        [RawValue("tour-talk-done")] TourTalkDone,
        [RawValue("tour-act")] TourAct,
        [RawValue("tour-act-done")] TourActDone,
        [RawValue("tour-account")] TourAccount,
        [RawValue("tour-done")] TourDone,
        [RawValue("notice-empty-answer")] NoticeEmptyAnswer,
        [RawValue("notice-no-screen")] NoticeNoScreen,
        [RawValue("notice-screen-recording-off")] NoticeScreenRecordingOff,
        [RawValue("notice-no-internet")] NoticeNoInternet,
        [RawValue("notice-stopped-partway")] NoticeStoppedPartway,
        [RawValue("notice-went-wrong")] NoticeWentWrong,
        [RawValue("notice-realtime-unavailable")] NoticeRealtimeUnavailable,
        // The one-minute Settings tour (SettingsTour).
        [RawValue("settings-tour-intro")] SettingsTourIntro,
        [RawValue("settings-tour-dictation")] SettingsTourDictation,
        [RawValue("settings-tour-language")] SettingsTourLanguage,
        [RawValue("settings-tour-voice")] SettingsTourVoice,
        [RawValue("settings-tour-pointer")] SettingsTourPointer,
        [RawValue("settings-tour-words")] SettingsTourWords,
        [RawValue("settings-tour-shortcuts")] SettingsTourShortcuts,
        [RawValue("settings-tour-keys")] SettingsTourKeys,
        [RawValue("settings-tour-done")] SettingsTourDone,
    }

    /// <summary>The fixed sentences the app speaks, by their exact text (any case), and their recording.
    /// The screen-recording one names the Mac's setting; it is kept so the lookup matches the Mac's sentence.</summary>
    public static IReadOnlyDictionary<string, Line> Notices { get; } = new Dictionary<string, Line>(StringComparer.Ordinal)
    {
        ["i didn't get an answer. please ask again."] = Line.NoticeEmptyAnswer,
        ["i couldn't see your screen just now. please ask again."] = Line.NoticeNoScreen,
        ["screen recording is off for openclicky. turn it on in system settings, privacy and security, screen recording."] = Line.NoticeScreenRecordingOff,
        ["i couldn't reach openclicky. check your internet and ask again."] = Line.NoticeNoInternet,
        ["the answer stopped partway. please ask again."] = Line.NoticeStoppedPartway,
        ["something went wrong getting your answer. please ask again."] = Line.NoticeWentWrong,
        ["realtime voice is unavailable right now."] = Line.NoticeRealtimeUnavailable,
    };

    public static Line? LineFor(string spokenText) =>
        Notices.TryGetValue(spokenText.Trim().ToLowerInvariant(), out var line) ? line : null;

    /// <summary>Where the recordings are installed next to the exe.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "RecordedVoice");

    public static string FileName(Line line) => $"{line.RawValue()}.mp3";

    private readonly IAudioPlayer player;
    private readonly string folder;
    private readonly object stateLock = new();
    private CancellationTokenSource? currentLine;
    /// <summary>Tells whoever played the current line that it ended, exactly once.</summary>
    private Action? finishCurrentLine;

    public RecordedVoice(IAudioPlayer player, string? folder = null)
    {
        this.player = player;
        this.folder = folder ?? DefaultFolder;
    }

    public bool IsPlaying
    {
        get { lock (stateLock) return currentLine is not null; }
    }

    /// <summary>
    /// Plays <paramref name="line"/>, cutting off whatever recording was playing. False when it is not installed.
    /// <paramref name="onFinished"/> runs once when the line ends: played to the end, cut off by the next line,
    /// or stopped (the companion uses it to go back to idle after a spoken notice).
    /// </summary>
    public bool Play(Line line, Action? onFinished = null)
    {
        var path = Path.Combine(folder, FileName(line));
        byte[] audio;
        try
        {
            audio = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"recorded voice: {FileName(line)} is not in the bundle");
            return false;
        }
        StopCurrentLine();
        var cancellation = new CancellationTokenSource();
        var finished = 0;
        void FinishOnce()
        {
            if (Interlocked.Exchange(ref finished, 1) == 0) onFinished?.Invoke();
        }
        lock (stateLock)
        {
            currentLine = cancellation;
            finishCurrentLine = FinishOnce;
        }
        _ = PlayAsync(audio, cancellation, FinishOnce);
        return true;
    }

    private async Task PlayAsync(byte[] audio, CancellationTokenSource cancellation, Action finishOnce)
    {
        try
        {
            await player.PlayToCompletionAsync(audio, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AppLog.Write($"recorded voice: playback failed ({error.GetType().Name})");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (stateLock)
            {
                if (ReferenceEquals(currentLine, cancellation))
                {
                    currentLine = null;
                    finishCurrentLine = null;
                }
            }
            cancellation.Dispose();
            finishOnce();
        }
    }

    public void Stop() => StopCurrentLine();

    /// <summary>A stopped line is reported as finished here, at once.</summary>
    private void StopCurrentLine()
    {
        CancellationTokenSource? playing;
        Action? finish;
        lock (stateLock)
        {
            playing = currentLine;
            finish = finishCurrentLine;
            currentLine = null;
            finishCurrentLine = null;
        }
        try { playing?.Cancel(); }
        catch (ObjectDisposedException) { }
        finish?.Invoke();
    }
}
