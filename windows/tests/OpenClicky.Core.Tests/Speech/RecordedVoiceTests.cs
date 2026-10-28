// RecordedVoice: the fixed sentences find their recordings, every line ships an mp3 in the Windows
// assets, and a line reports its end exactly once whether it played out, was cut off or stopped.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class RecordedVoiceTests
{
    private sealed class ControlledPlayer : IAudioPlayer
    {
        public int PlayCount;
        public TaskCompletionSource CurrentPlayback = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PlayToCompletionAsync(byte[] audio, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PlayCount);
            var playback = CurrentPlayback;
            await playback.Task.WaitAsync(cancellationToken);
        }

        public Task PlayPcm16ToCompletionAsync(byte[] pcm16, int sampleRate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsPlaying => false;
        public void StopPlayback() { }
    }

    private static string AssetsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenClicky.Windows.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "OpenClicky.Windows", "Assets", "RecordedVoice");
    }

    [Fact]
    public void FixedSentencesFindTheirRecordingsInAnyCase()
    {
        Assert.Equal(RecordedVoice.Line.NoticeEmptyAnswer, RecordedVoice.LineFor("  I didn't get an answer. Please ask again.\n"));
        Assert.Equal(RecordedVoice.Line.NoticeRealtimeUnavailable, RecordedVoice.LineFor("realtime voice is unavailable right now."));
        Assert.Null(RecordedVoice.LineFor("something else entirely"));
        Assert.Equal("tour-dictate-done.mp3", RecordedVoice.FileName(RecordedVoice.Line.TourDictateDone));
    }

    [Fact]
    public void EveryLineShipsARecording()
    {
        var folder = AssetsFolder();
        foreach (var line in Enum.GetValues<RecordedVoice.Line>())
            Assert.True(File.Exists(Path.Combine(folder, RecordedVoice.FileName(line))), $"{line.RawValue()}.mp3 is missing");
    }

    [Fact]
    public void AMissingRecordingIsNotPlayed()
    {
        var player = new ControlledPlayer();
        var voice = new RecordedVoice(player, Path.Combine(Path.GetTempPath(), "openclicky-no-such-folder"));
        Assert.False(voice.Play(RecordedVoice.Line.TourHello));
        Assert.Equal(0, player.PlayCount);
    }

    /// <summary>The Mac recordings say "hold the fn key" and "control and option"; a tester's PC said
    /// "function" for a key it does not have. Those lines are said the Windows way by this PC's voice.</summary>
    [Fact]
    public void WindowsSentencesNameTheKeysAsWindowsHasThem()
    {
        var dictate = RecordedVoice.WindowsSentence(RecordedVoice.Line.TourDictate, "caps lock");
        Assert.Contains("caps lock", dictate);
        Assert.Contains("windows key", RecordedVoice.WindowsSentence(RecordedVoice.Line.TourTalk, "right control"));
        Assert.Contains("windows key", RecordedVoice.WindowsSentence(RecordedVoice.Line.TourAct, "right control"));
        Assert.Contains("windows key", RecordedVoice.WindowsSentence(RecordedVoice.Line.SettingsTourShortcuts, "right control"));
        Assert.Null(RecordedVoice.WindowsSentence(RecordedVoice.Line.TourHello, "right control"));
        Assert.Null(RecordedVoice.WindowsSentence(RecordedVoice.Line.NoticeEmptyAnswer, "right control"));
        foreach (var line in Enum.GetValues<RecordedVoice.Line>())
        {
            var sentence = RecordedVoice.WindowsSentence(line, "right control");
            if (sentence is null) continue;
            Assert.DoesNotContain("option", sentence);
            Assert.DoesNotContain("fn", sentence.Split(' '));
        }
    }

    /// <summary>A folder with the Mac recordings only: no Windows ones, so the sentences are what plays.</summary>
    private static string MacRecordingsOnlyFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"openclicky-mac-recordings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(AssetsFolder(), "*.mp3").Where(file => !file.EndsWith("-windows.mp3", StringComparison.Ordinal)))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        return folder;
    }

    [Fact]
    public void TheWindowsRecordingsShipForEveryLineWithAWindowsSentence()
    {
        foreach (var line in Enum.GetValues<RecordedVoice.Line>())
        {
            if (RecordedVoice.WindowsSentence(line, "right control") is null) continue;
            Assert.True(File.Exists(Path.Combine(AssetsFolder(), RecordedVoice.WindowsFileName(line))), $"{RecordedVoice.WindowsFileName(line)} is missing");
        }
    }

    /// <summary>The recordings say "right control", the default key, for dictating and as talk's Ctrl; with
    /// another key chosen (a laptop with no right Ctrl) the sentences name the keys as they are instead.</summary>
    [Fact]
    public async Task TheKeyRecordingsPlayOnlyWhileTheDefaultKeyIsChosen()
    {
        var player = new ControlledPlayer();
        var rendered = new List<string>();
        var voice = new RecordedVoice(player, AssetsFolder(),
            windowsSentence: line => RecordedVoice.WindowsSentence(line, "right alt", "control and the windows key"),
            renderSentence: (text, _) => { rendered.Add(text); return Task.FromResult(new byte[] { 1 }); },
            dictationKeyIsDefault: () => false);
        Assert.True(voice.Play(RecordedVoice.Line.TourDictate));
        await Task.Delay(50);
        Assert.Contains("right alt", Assert.Single(rendered));
        player.CurrentPlayback.SetResult();
        player.CurrentPlayback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(voice.Play(RecordedVoice.Line.TourTalk));
        await Task.Delay(50);
        Assert.Equal(2, rendered.Count);
        Assert.Contains("hold control and the windows key", rendered[1]);
        Assert.DoesNotContain("right control", rendered[1]);
        Assert.Equal(2, player.PlayCount);
    }

    [Fact]
    public async Task ALineThatNamesMacKeysIsSaidTheWindowsWayInsteadOfPlayed()
    {
        var player = new ControlledPlayer();
        var rendered = new List<string>();
        var voice = new RecordedVoice(player, MacRecordingsOnlyFolder(),
            windowsSentence: line => RecordedVoice.WindowsSentence(line, "right control"),
            renderSentence: (text, _) => { rendered.Add(text); return Task.FromResult(new byte[] { 1, 2, 3 }); });
        Assert.True(voice.Play(RecordedVoice.Line.TourDictate));
        await Task.Delay(50);
        Assert.Equal(1, player.PlayCount);
        Assert.Contains("right control", Assert.Single(rendered));
        // A line with no Windows sentence still plays its recording.
        player.CurrentPlayback.SetResult();
        player.CurrentPlayback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(voice.Play(RecordedVoice.Line.TourHello));
        await Task.Delay(50);
        Assert.Equal(2, player.PlayCount);
        Assert.Single(rendered);
    }

    [Fact]
    public async Task AWindowsRecordingWinsOverTheSpokenSentence()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"openclicky-recorded-voice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(AssetsFolder(), "tour-hello.mp3"), Path.Combine(folder, RecordedVoice.WindowsFileName(RecordedVoice.Line.TourDictate)));
        var player = new ControlledPlayer();
        var rendered = 0;
        var voice = new RecordedVoice(player, folder,
            windowsSentence: line => RecordedVoice.WindowsSentence(line, "right control"),
            renderSentence: (_, _) => { rendered++; return Task.FromResult(new byte[] { 1 }); });
        Assert.True(voice.Play(RecordedVoice.Line.TourDictate));
        await Task.Delay(50);
        Assert.Equal(1, player.PlayCount);
        Assert.Equal(0, rendered);
        Assert.Equal("tour-dictate-windows.mp3", RecordedVoice.WindowsFileName(RecordedVoice.Line.TourDictate));
    }

    [Fact]
    public async Task ALineReportsItsEndOnceWhetherItEndsIsCutOffOrStopped()
    {
        var player = new ControlledPlayer();
        var voice = new RecordedVoice(player, AssetsFolder());
        var firstEnds = 0;
        var secondEnds = 0;
        var thirdEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(voice.Play(RecordedVoice.Line.TourHello, () => Interlocked.Increment(ref firstEnds)));
        Assert.True(voice.IsPlaying);
        // The next line cuts the first off.
        player.CurrentPlayback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(voice.Play(RecordedVoice.Line.TourDone, () => Interlocked.Increment(ref secondEnds)));
        await Task.Delay(50);
        Assert.Equal(1, firstEnds);
        voice.Stop();
        await Task.Delay(50);
        Assert.Equal(1, secondEnds);
        Assert.False(voice.IsPlaying);
        // A line that plays to the end.
        player.CurrentPlayback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        voice.Play(RecordedVoice.Line.TourAct, () => thirdEnded.TrySetResult());
        player.CurrentPlayback.SetResult();
        await thirdEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(1, firstEnds);
        Assert.Equal(1, secondEnds);
    }
}
