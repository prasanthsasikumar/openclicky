//
//  MediaPlugin.cs
//  OpenClicky (Windows port of Plugins/Media/MediaSnapshot.swift and MediaStore.swift)
//
//  The Now playing plugin's picture of the track (which app, playing or paused, title and artist,
//  where in the track it was at the last read and where it is now), and the store that asks for it:
//  every 2 s while a Now playing row is on screen, every 5 s while the plugin is merely on (so the
//  island can switch to it and the peek line can name the track), never while off. The Mac asks
//  Music and Spotify through their scripts; Windows asks its media sessions, which every player that
//  shows in the volume flyout reports (OpenClicky.Windows/Plugins/Media/WindowsMediaSource.cs).
//

using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.Media;

/// <summary>The plugin's component: what is playing, with controls.</summary>
public static class MediaPlugin
{
    public static IslandComponent Component { get; } = new(
        IslandPluginRegistry.MediaId, "Now playing", "", 84, IsPlugin: true,
        SettingsDetail: "Shows what Spotify, a browser or Media Player is playing, with artwork, progress and controls.");
}

/// <summary>One read of the track.</summary>
/// <param name="AppId">The media session's app (its AppUserModelId): commands go back to that session.</param>
/// <param name="PlayerName">The app as the island names it ("spotify", "chrome").</param>
/// <param name="Position">Seconds into the track at <paramref name="SampledAt"/>.</param>
/// <param name="ArtworkKey">The artwork is fetched once per key (the app and the track).</param>
public sealed record MediaSnapshot(string AppId, string PlayerName, bool IsPlaying, string Title, string Artist, string Album,
                                   double Position, double Duration, string ArtworkKey, DateTimeOffset SampledAt)
{
    /// <summary>Where the track is now: the last position plus the time since, while playing; frozen while paused; never
    /// past the end.</summary>
    public double PositionAt(DateTimeOffset now)
    {
        if (!IsPlaying) return Position;
        var position = Math.Max(Position + (now - SampledAt).TotalSeconds, 0);
        return Duration > 0 ? Math.Min(position, Duration) : position;
    }

    /// <summary>The app's name for the island, from its media session id: "Spotify.exe" → "spotify",
    /// "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "media player", "MSEdge" → "edge".</summary>
    public static string PlayerNameFor(string appId)
    {
        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "spotify";
        if (id.Contains("zunemusic") || id.Contains("zunevideo") || id.Contains("mediaplayer")) return "media player";
        if (id.Contains("msedge")) return "edge";
        if (id.Contains("chrome")) return "chrome";
        if (id.Contains("firefox") || id.StartsWith("308046b0af4a39cb", StringComparison.Ordinal)) return "firefox";
        if (id.Contains("vlc")) return "vlc";
        // Anything else: the exe or package name without its extension or publisher hash.
        var name = appId.Split('!')[0].Split('_')[0];
        var dot = name.LastIndexOf('.');
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        else if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length == 0 ? "player" : name.ToLowerInvariant();
    }
}

public static class MediaFormatter
{
    /// <summary>"0:05", "1:23", "1:02:03".</summary>
    public static string Clock(double seconds)
    {
        var total = (int)Math.Floor(Math.Max(0, seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }
}

public enum MediaCommandKind
{
    PlayPause,
    /// <summary>Pause and play as themselves, for a voice "stop that song" that must never start playback.</summary>
    Pause,
    Play,
    Next,
    Previous,
    Seek,
}

/// <param name="Seconds">Seek only: where in the track.</param>
public sealed record MediaCommand(MediaCommandKind Kind, double Seconds = 0)
{
    public static MediaCommand PlayPause { get; } = new(MediaCommandKind.PlayPause);
    public static MediaCommand Next { get; } = new(MediaCommandKind.Next);
    public static MediaCommand Previous { get; } = new(MediaCommandKind.Previous);
    public static MediaCommand SeekTo(double seconds) => new(MediaCommandKind.Seek, seconds);
}

/// <summary>Where the track comes from: Windows' media sessions, or a test double.</summary>
public interface IMediaSource
{
    /// <summary>The track, or null when nothing is up.</summary>
    Task<MediaSnapshot?> ReadNowPlayingAsync();
    Task PerformAsync(MediaCommand command, MediaSnapshot snapshot);
    /// <summary>The cover as encoded image bytes (PNG, JPEG), or null.</summary>
    Task<byte[]?> LoadArtworkAsync(MediaSnapshot snapshot);
}

/// <summary>
/// One per app. Reads one at a time; artwork is fetched once per track; commands go straight to the
/// player and the next read follows at once. Use it on the UI thread: reads come back on the thread
/// that started them, and <see cref="Changed"/> is raised there.
/// </summary>
public sealed class MediaStore(IMediaSource source, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan WatchedInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(5);

    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private bool isEnabled;
    private int watchers;
    private CancellationTokenSource? polling;
    private int generation;
    private string? artworkKey;
    /// <summary>One read at a time: a tick that lands mid-read asks for one more after it, never a pile.</summary>
    private bool isRefreshing;
    private bool refreshAgain;

    public MediaSnapshot? Snapshot { get; private set; }
    public byte[]? Artwork { get; private set; }

    /// <summary>The track, the artwork or whether it plays changed.</summary>
    public event Action? Changed;

    public bool IsEnabled => isEnabled;
    public bool IsPlaying => Snapshot?.IsPlaying ?? false;
    public bool IsPolling => polling is not null;
    public TimeSpan PollInterval => watchers > 0 ? WatchedInterval : BackgroundInterval;

    /// <summary>"♪ title · artist" while playing, for the island's peek line.</summary>
    public string? NowPlayingLine => Snapshot is { IsPlaying: true } snapshot
        ? snapshot.Artist.Length == 0 ? $"♪ {snapshot.Title}" : $"♪ {snapshot.Title} · {snapshot.Artist}"
        : null;

    /// <summary>The plugin's switch: off forgets the track and stops asking.</summary>
    public void SetEnabled(bool enabled)
    {
        if (isEnabled == enabled) return;
        isEnabled = enabled;
        if (enabled)
        {
            _ = RefreshNowAsync();
        }
        else
        {
            generation++;
            var hadSomething = Snapshot is not null || Artwork is not null;
            Snapshot = null;
            Artwork = null;
            artworkKey = null;
            if (hadSomething) Changed?.Invoke();
        }
        RestartPolling();
    }

    /// <summary>A Now playing row appeared: read now and every 2 s.</summary>
    public void BeginWatching()
    {
        watchers++;
        if (watchers != 1) return;
        if (isEnabled) _ = RefreshNowAsync();
        RestartPolling();
    }

    public void EndWatching()
    {
        watchers = Math.Max(0, watchers - 1);
        if (watchers == 0) RestartPolling();
    }

    /// <summary>One read, serialised: a call that lands mid-read returns at once and the running read goes round once more.
    /// A read that outlives the switch going off is dropped.</summary>
    public async Task RefreshNowAsync()
    {
        if (!isEnabled) return;
        if (isRefreshing)
        {
            refreshAgain = true;
            return;
        }
        isRefreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                await ReadOnceAsync();
            } while (refreshAgain && isEnabled);
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private async Task ReadOnceAsync()
    {
        var mine = generation;
        MediaSnapshot? next;
        try { next = await source.ReadNowPlayingAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"now playing: read failed ({error.GetType().Name})");
            return;
        }
        if (mine != generation || !isEnabled) return;
        if (next != Snapshot)
        {
            Snapshot = next;
            Changed?.Invoke();
        }
        if (next is null)
        {
            if (Artwork is not null) Changed?.Invoke();
            Artwork = null;
            artworkKey = null;
            return;
        }
        if (next.ArtworkKey == artworkKey) return;
        // A new track: the old cover goes at once, the new one lands when it arrives, and a load that outlives
        // another track change is dropped (that track's own load follows).
        artworkKey = next.ArtworkKey;
        Artwork = null;
        byte[]? image;
        try { image = await source.LoadArtworkAsync(next); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"now playing: artwork failed ({error.GetType().Name})");
            image = null;
        }
        if (mine != generation || !isEnabled || artworkKey != next.ArtworkKey) return;
        Artwork = image;
        Changed?.Invoke();
    }

    public Task PlayPauseAsync() => PerformAsync(MediaCommand.PlayPause);
    public Task NextAsync() => PerformAsync(MediaCommand.Next);
    public Task PreviousAsync() => PerformAsync(MediaCommand.Previous);

    /// <summary>Seeks to a fraction of the track.</summary>
    public Task SeekAsync(double fraction)
    {
        if (Snapshot is not { Duration: > 0 } snapshot) return Task.CompletedTask;
        return PerformAsync(MediaCommand.SeekTo(Math.Clamp(fraction, 0, 1) * snapshot.Duration));
    }

    private async Task PerformAsync(MediaCommand command)
    {
        if (Snapshot is not { } snapshot) return;
        try { await source.PerformAsync(command, snapshot); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"now playing: {command.Kind} failed ({error.GetType().Name})");
        }
        await RefreshNowAsync();
    }

    private void RestartPolling()
    {
        polling?.Cancel();
        polling = null;
        if (!isEnabled) return;
        var cancellation = new CancellationTokenSource();
        polling = cancellation;
        _ = PollAsync(PollInterval, cancellation.Token);
    }

    private async Task PollAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await delay(interval, cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (cancellationToken.IsCancellationRequested) return;
            await RefreshNowAsync();
        }
    }
}
