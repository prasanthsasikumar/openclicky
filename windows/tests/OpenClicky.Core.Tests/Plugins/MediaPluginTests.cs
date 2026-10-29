// Ports MediaPluginTests.swift: where the track is now, the clock, the app's name, the peek line, and a store that
// follows the source, fetches artwork once per track, forwards commands and asks only while the plugin is on.

using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins;
using OpenClicky.Core.Plugins.Media;

namespace OpenClicky.Core.Tests.Plugins;

public class MediaPluginTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static MediaSnapshot Track(string title = "Blue Hero", string artist = "The Band", bool isPlaying = true, double position = 1, double duration = 200, string key = "k1") =>
        new("Spotify.exe", "spotify", isPlaying, title, artist, "", position, duration, key, Now);

    [Fact]
    public void PositionRunsOnWhilePlayingAndFreezesWhenPaused()
    {
        var snapshot = Track(position: 100, duration: 120);
        Assert.Equal(115, snapshot.PositionAt(Now.AddSeconds(15)));
        Assert.Equal(120, snapshot.PositionAt(Now.AddSeconds(50)));
        Assert.Equal(100, (snapshot with { IsPlaying = false }).PositionAt(Now.AddSeconds(15)));
    }

    [Fact]
    public void TheClockReadsLikeAPlayer()
    {
        Assert.Equal("0:05", MediaFormatter.Clock(5));
        Assert.Equal("1:23", MediaFormatter.Clock(83.7));
        Assert.Equal("1:02:03", MediaFormatter.Clock(3723));
    }

    [Fact]
    public void ThePlayerIsNamedByItsMediaSession()
    {
        Assert.Equal("spotify", MediaSnapshot.PlayerNameFor("Spotify.exe"));
        Assert.Equal("media player", MediaSnapshot.PlayerNameFor("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"));
        Assert.Equal("edge", MediaSnapshot.PlayerNameFor("MSEdge"));
        Assert.Equal("chrome", MediaSnapshot.PlayerNameFor("Chrome"));
        Assert.Equal("foobar2000", MediaSnapshot.PlayerNameFor("foobar2000.exe"));
    }

    [Fact]
    public void ThePeekLineNamesTheTrackAfterALiveSession()
    {
        Assert.Equal("♪ T · A", IslandTexts.PeekText("s", agentLine: null, mediaLine: "♪ T · A"));
        Assert.Equal("claude · thinking", IslandTexts.PeekText("s", agentLine: "claude · thinking", mediaLine: "♪ T · A"));
        Assert.Equal("s", IslandTexts.PeekText("s", agentLine: null, mediaLine: null));
    }

    [Fact]
    public void PlayingMusicIsTheMomentThatPicksNowPlaying()
    {
        var context = IslandContext.Idle with { IsMediaPlaying = true };
        Assert.Equal(IslandPluginRegistry.MediaId, context.ContextualKey);
        Assert.Contains(IslandPluginRegistry.All, plugin => plugin.Id == IslandPluginRegistry.MediaId);
    }

    private sealed class Source : IMediaSource
    {
        public MediaSnapshot? Reply;
        public readonly List<MediaCommand> Commands = [];
        public int ArtworkLoads;
        public Task<MediaSnapshot?> ReadNowPlayingAsync() => Task.FromResult(Reply);
        public Task PerformAsync(MediaCommand command, MediaSnapshot snapshot)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
        public Task<byte[]?> LoadArtworkAsync(MediaSnapshot snapshot)
        {
            ArtworkLoads++;
            return Task.FromResult<byte[]?>([1, 2, 3]);
        }
    }

    /// <summary>A delay that never ends until cancelled, so only the test's own reads run.</summary>
    private static Task NeverTick(TimeSpan _, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

    [Fact]
    public async Task TheStoreFollowsTheSourceFetchesArtworkOncePerTrackAndForwardsCommands()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var source = new Source();
        var store = new MediaStore(source, NeverTick);
        store.SetEnabled(true);
        Assert.True(store.IsPolling);
        Assert.Equal(MediaStore.BackgroundInterval, store.PollInterval);
        store.BeginWatching();
        Assert.Equal(MediaStore.WatchedInterval, store.PollInterval);
        await store.RefreshNowAsync();
        Assert.Null(store.Snapshot);
        Assert.Null(store.NowPlayingLine);

        source.Reply = Track();
        await store.RefreshNowAsync();
        await store.RefreshNowAsync();
        Assert.True(store.IsPlaying);
        Assert.Equal("♪ Blue Hero · The Band", store.NowPlayingLine);
        Assert.NotNull(store.Artwork);
        Assert.Equal(1, source.ArtworkLoads);

        source.Reply = Track(isPlaying: false);
        await store.RefreshNowAsync();
        Assert.False(store.IsPlaying);
        Assert.Null(store.NowPlayingLine);
        Assert.Equal(1, source.ArtworkLoads);

        await store.PlayPauseAsync();
        await store.SeekAsync(0.5);
        await store.NextAsync();
        Assert.Equal([MediaCommand.PlayPause, MediaCommand.SeekTo(100), MediaCommand.Next], source.Commands);

        source.Reply = null;
        await store.RefreshNowAsync();
        Assert.Null(store.Snapshot);
        Assert.Null(store.Artwork);
        store.EndWatching();
        Assert.Equal(MediaStore.BackgroundInterval, store.PollInterval);
        store.SetEnabled(false);
        Assert.False(store.IsPolling);
        Assert.Null(store.Snapshot);
    }

    [Fact]
    public async Task OffTheStoreNeverAsksAndForgetsTheTrack()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var source = new Source { Reply = Track() };
        var store = new MediaStore(source, NeverTick);
        await store.RefreshNowAsync();
        Assert.Null(store.Snapshot);
        store.BeginWatching();
        Assert.False(store.IsPolling);
        store.SetEnabled(true);
        await store.RefreshNowAsync();
        Assert.NotNull(store.Snapshot);
        store.SetEnabled(false);
        Assert.Null(store.Snapshot);
        Assert.Null(store.Artwork);
    }
}
