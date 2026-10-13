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
