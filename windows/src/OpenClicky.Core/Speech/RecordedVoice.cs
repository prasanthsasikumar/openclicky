// Ports RecordedVoice.swift: the lines OpenClicky says the same way every time, recorded once in
// its answering voice (OpenAI speech, marin) and shipped with the app (Assets\RecordedVoice\*.mp3,
// copied from the Mac bundle), so they sound good with no key and no network: the first-run tour's
// narration, and the notices that used to be read by the system voice ("i didn't get an
// answer…"). Anything not recorded here is spoken the way it was before.
//
// The recordings that name the Mac's keys ("hold the fn key", "control and option") are not played
// here: a tester's PC said "function" for a key it does not have (2026-10-14). For those lines the
// sentence is said the Windows way (WindowsSentence) by this PC's voice, unless a Windows recording
// (<line>-windows.mp3, made with the Mac's record-voice-lines.py) sits next to the Mac one.
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

    /// <summary>A recording made for Windows, which wins over the spoken sentence when it ships.</summary>
    public static string WindowsFileName(Line line) => $"{line.RawValue()}-windows.mp3";

    /// <summary>
    /// What a line says on Windows when its Mac recording names the Mac's keys, with the dictation
    /// key as the person has it ("right control", "caps lock"); null for every other line, whose
    /// recording is right on both.
    /// </summary>
    /// <param name="talkKeysName">Talk's keys, which follow the dictation key ("control and the windows key"
    /// when right Alt dictates).</param>
    public static string? WindowsSentence(Line line, string dictationKeyName, string talkKeysName = "right control and the windows key") => line switch
    {
        Line.TourDictate => $"hold {dictationKeyName}, say a sentence, then let go. your words land wherever your cursor is.",
        Line.TourTalk => $"your turn. hold {talkKeysName}, and ask me about anything on your screen.",
        Line.TourAct => $"now give me a task. hold {talkKeysName}, and tell me what to do.",
        Line.SettingsTourShortcuts => $"shortcuts shows every key at a glance. hold {talkKeysName} to talk to me.",
        _ => null,
    };

    private readonly IAudioPlayer player;
    private readonly string folder;
    private readonly Func<Line, string?>? windowsSentence;
    private readonly Func<string, CancellationToken, Task<byte[]>>? renderSentence;
    private readonly Func<bool>? dictationKeyIsDefault;

    /// <summary>The lines whose Windows recording names the dictation key (as "right control", the default),
    /// or talk's keys, which follow it.</summary>
    private static bool NamesTheDictationKey(Line line) =>
        line is Line.TourDictate or Line.TourTalk or Line.TourAct or Line.SettingsTourShortcuts;
    private readonly object stateLock = new();
    private CancellationTokenSource? currentLine;
    /// <summary>Tells whoever played the current line that it ended, exactly once.</summary>
    private Action? finishCurrentLine;

    /// <param name="windowsSentence">The Windows wording of a line, or null to play its recording.</param>
    /// <param name="renderSentence">This PC's voice, rendering a sentence to audio the player takes.</param>
    /// <param name="dictationKeyIsDefault">Whether the dictation key is still the default the dictate
    /// recording names; with another key the sentence (naming that key) is said instead. Null = default.</param>
    public RecordedVoice(IAudioPlayer player, string? folder = null,
                         Func<Line, string?>? windowsSentence = null,
                         Func<string, CancellationToken, Task<byte[]>>? renderSentence = null,
                         Func<bool>? dictationKeyIsDefault = null)
    {
        this.player = player;
        this.folder = folder ?? DefaultFolder;
        this.windowsSentence = windowsSentence;
        this.renderSentence = renderSentence;
        this.dictationKeyIsDefault = dictationKeyIsDefault;
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
        var windowsRecording = Path.Combine(folder, WindowsFileName(line));
        var windowsRecordingFits = !NamesTheDictationKey(line) || (dictationKeyIsDefault?.Invoke() ?? true);
        if (windowsRecordingFits && File.Exists(windowsRecording) && ReadRecording(windowsRecording) is { } windowsAudio)
        {
            StartLine(_ => Task.FromResult(windowsAudio), onFinished);
            return true;
        }
        if (windowsSentence?.Invoke(line) is { } sentence && renderSentence is { } render)
        {
            StartLine(cancellationToken => render(sentence, cancellationToken), onFinished);
            return true;
        }
        if (ReadRecording(Path.Combine(folder, FileName(line))) is not { } audio)
        {
            AppLog.Write($"recorded voice: {FileName(line)} is not in the bundle");
            return false;
        }
        StartLine(_ => Task.FromResult(audio), onFinished);
        return true;
    }

    private static byte[]? ReadRecording(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Cuts off whatever was playing and plays what <paramref name="audioSource"/> gives (a recording, or a sentence rendered now).</summary>
    private void StartLine(Func<CancellationToken, Task<byte[]>> audioSource, Action? onFinished)
    {
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
        _ = PlayAsync(audioSource, cancellation, FinishOnce);
    }

    private async Task PlayAsync(Func<CancellationToken, Task<byte[]>> audioSource, CancellationTokenSource cancellation, Action finishOnce)
    {
        try
        {
            var audio = await audioSource(cancellation.Token).ConfigureAwait(false);
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
