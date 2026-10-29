//
//  WindowsMediaSource.cs
//  OpenClicky (Windows port of Plugins/Media/AppleScriptMediaSource.swift)
//
//  The live source behind the Now playing plugin: Windows' media sessions
//  (GlobalSystemMediaTransportControlsSessionManager), the same ones the volume flyout shows, so
//  Spotify, browsers and Media Player all count without asking each player in its own words. No
//  permission to ask for, unlike the Mac's Automation prompt per player.
//

using OpenClicky.Core.Plugins.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OpenClicky.Windows.Plugins.Media;

internal sealed class WindowsMediaSource : IMediaSource
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;

    private async Task<GlobalSystemMediaTransportControlsSessionManager> ManagerAsync() =>
        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

    /// <summary>The playing session wins; else the one Windows calls current (the last one used).</summary>
    private static GlobalSystemMediaTransportControlsSession? PickSession(GlobalSystemMediaTransportControlsSessionManager sessionManager)
    {
        var sessions = sessionManager.GetSessions();
        foreach (var session in sessions)
        {
            if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return session;
        }
        return sessionManager.GetCurrentSession() ?? (sessions.Count > 0 ? sessions[0] : null);
    }

    public async Task<MediaSnapshot?> ReadNowPlayingAsync()
    {
        var sessionManager = await ManagerAsync();
        if (PickSession(sessionManager) is not { } session) return null;
        var status = session.GetPlaybackInfo()?.PlaybackStatus;
        if (status is not (GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)) return null;
        var properties = await session.TryGetMediaPropertiesAsync();
        var title = properties?.Title?.Trim() ?? "";
        if (title.Length == 0) return null;
        var artist = properties!.Artist?.Trim() ?? "";
        var album = properties.AlbumTitle?.Trim() ?? "";
        var now = DateTimeOffset.Now;
        var isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var timeline = session.GetTimelineProperties();
        var duration = timeline is null ? 0 : Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
        var position = timeline is null ? 0 : Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
        // The position is as of the player's last report, which some players only send on a change: carry it forward
        // to now while playing. A player that never reported says year 1.
        if (isPlaying && timeline is not null && timeline.LastUpdatedTime.Year > 2000)
        {
            position += Math.Max(0, (now - timeline.LastUpdatedTime).TotalSeconds);
        }
        if (duration > 0) position = Math.Min(position, duration);
        var appId = session.SourceAppUserModelId ?? "";
        return new MediaSnapshot(appId, MediaSnapshot.PlayerNameFor(appId), isPlaying, title, artist, album,
            position, duration, $"{appId}\u001E{title}\u001E{artist}\u001E{album}", now);
    }

    public async Task PerformAsync(MediaCommand command, MediaSnapshot snapshot)
    {
        var sessionManager = await ManagerAsync();
        // The session the row shows, by its app; it may have gone since the last read.
        var session = sessionManager.GetSessions().FirstOrDefault(candidate => candidate.SourceAppUserModelId == snapshot.AppId)
            ?? PickSession(sessionManager);
        if (session is null) return;
        _ = command.Kind switch
        {
            MediaCommandKind.PlayPause => await session.TryTogglePlayPauseAsync(),
            MediaCommandKind.Pause => await session.TryPauseAsync(),
            MediaCommandKind.Play => await session.TryPlayAsync(),
            MediaCommandKind.Next => await session.TrySkipNextAsync(),
            MediaCommandKind.Previous => await session.TrySkipPreviousAsync(),
            _ => await session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(command.Seconds).Ticks),
        };
    }

    public async Task<byte[]?> LoadArtworkAsync(MediaSnapshot snapshot)
    {
        var sessionManager = await ManagerAsync();
        var session = sessionManager.GetSessions().FirstOrDefault(candidate => candidate.SourceAppUserModelId == snapshot.AppId);
        if (session is null) return null;
        var properties = await session.TryGetMediaPropertiesAsync();
        if (properties?.Thumbnail is not { } thumbnail) return null;
        using var stream = await thumbnail.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > 8_000_000) return null;
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
