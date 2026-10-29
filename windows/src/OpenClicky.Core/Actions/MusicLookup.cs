// Port of macos/OpenClicky/OpenClicky/MusicLookup.swift (the lookup half; the player is the
// Windows executor's).
//
// "Play Blinding Lights" in Spotify's desktop app. Spotify plays a track by URI but offers no search
// of its own, so the song is looked up first: on the person's own Spotify client credentials
// straight to Spotify, else through the OpenClicky backend's catalogue search (the words only), and
// when neither answers, Spotify's own search page opens for them to pick.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Actions;

public sealed record MusicTrack(string Uri, string Name, IReadOnlyList<string> Artists)
{
    public bool Equals(MusicTrack? other) =>
        other is not null && Uri == other.Uri && Name == other.Name && Artists.SequenceEqual(other.Artists);

    public override int GetHashCode() => HashCode.Combine(Uri, Name);
}

/// <summary>Where a song is looked up.</summary>
public abstract record MusicLookupRoute
{
    private MusicLookupRoute() { }

    public sealed record Spotify(string ClientId, string ClientSecret) : MusicLookupRoute;
    public sealed record Backend(Uri BaseUrl) : MusicLookupRoute;
    public sealed record None : MusicLookupRoute;

    /// <summary>Own credentials ("clientId:clientSecret" in shell.json) first; else the backend.</summary>
    public static MusicLookupRoute For(string? credentials, Uri? backendUrl)
    {
        var parts = (credentials ?? "").Trim().Split(':', 2);
        if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0) return new Spotify(parts[0], parts[1]);
        if (backendUrl is not null) return new Backend(backendUrl);
        return new None();
    }

    public string LogName => this switch
    {
        Spotify => "spotify",
        Backend => "backend",
        _ => "none",
    };
}

public static class MusicTrackPicker
{
    /// <summary>
    /// The track whose name and artists share the most words (three letters or more) with what was
    /// said ("blinding lights by the weeknd" picks The Weeknd's over a remix by someone else), else
    /// the first result. The confirmation names the pick, so a wrong one is heard.
    /// </summary>
    public static MusicTrack? Pick(IReadOnlyList<MusicTrack> tracks, string query)
    {
        var requestWords = Words(query).Where(word => word.Length >= 3).ToHashSet();
        MusicTrack? best = null;
        var bestShared = 0;
        foreach (var track in tracks)
        {
            var trackWords = Words(track.Name).Concat(track.Artists.SelectMany(Words)).ToHashSet();
            var shared = requestWords.Count(trackWords.Contains);
            if (shared > 0 && shared > bestShared)
            {
                best = track;
                bestShared = shared;
            }
        }
        return best ?? tracks.FirstOrDefault();
    }

    private static IEnumerable<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(word => word.Length > 0);
}

/// <summary>
/// The catalogue search, on whichever route applies. Every request waits at most four seconds; any
/// failure is an empty list, and the caller falls back to Spotify's own search.
/// </summary>
public sealed class MusicLookup(MusicLookupRoute route, HttpClient? client = null)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private readonly HttpClient client = client ?? HttpClients.Shared;

    /// <summary>
    /// The client-credentials tokens, one per client id, each kept until a minute before Spotify says
    /// it expires. Per client id so that new credentials are really tested, not answered from the old
    /// pair's token.
    /// </summary>
    private static readonly Dictionary<string, (string Value, DateTimeOffset ExpiresAt)> Tokens = [];
    private static readonly object TokenLock = new();

    public MusicLookupRoute Route => route;

    /// <summary>The route the app's settings give now.</summary>
    public static MusicLookupRoute CurrentRoute() =>
        MusicLookupRoute.For(OpenClickyConfiguration.Settings.SpotifyCredentials,
                             Uri.TryCreate(OpenClickyConfiguration.BackendBaseUrl, UriKind.Absolute, out var backend) ? backend : null);

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (route)
            {
                case MusicLookupRoute.Spotify spotify:
                {
                    var token = await SpotifyTokenAsync(spotify.ClientId, spotify.ClientSecret, cancellationToken).ConfigureAwait(false);
                    var market = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                    var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=5&market={Uri.EscapeDataString(market)}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return DecodeSpotifyResults(await ReadAsync(request, cancellationToken).ConfigureAwait(false));
                }
                case MusicLookupRoute.Backend backend:
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{backend.BaseUrl.AbsoluteUri.TrimEnd('/')}/music/search?q={Uri.EscapeDataString(query)}");
                    return DecodeBackendResults(await ReadAsync(request, cancellationToken).ConfigureAwait(false));
                }
                default:
                    return [];
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            AppLog.Write($"music lookup failed ({route.LogName}): {error.GetType().Name}");
            return [];
        }
    }

    private async Task<string> SpotifyTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        lock (TokenLock)
        {
            if (Tokens.TryGetValue(clientId, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt.AddSeconds(-60)) return cached.Value;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        var reply = JsonNode.Parse(await ReadAsync(request, cancellationToken).ConfigureAwait(false)) as JsonObject
            ?? throw new JsonException("no token reply");
        var token = reply["access_token"]?.GetValue<string>() ?? throw new JsonException("no access_token");
        var expiresIn = reply["expires_in"]?.GetValue<double>() ?? 3600;
        lock (TokenLock) Tokens[clientId] = (token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        return token;
    }

    private async Task<string> ReadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode >= 300) throw new HttpRequestException($"status {(int)response.StatusCode}");
        return body;
    }

    /// <summary>The backend's <c>{tracks:[{uri,name,artists:[String],…}]}</c>.</summary>
    public static IReadOnlyList<MusicTrack> DecodeBackendResults(string json)
    {
        var tracks = (JsonNode.Parse(json) as JsonObject)?["tracks"] as JsonArray ?? throw new JsonException("no tracks");
        return tracks.OfType<JsonObject>().Select(track => new MusicTrack(
            track["uri"]?.GetValue<string>() ?? throw new JsonException("no uri"),
            track["name"]?.GetValue<string>() ?? "",
            (track["artists"] as JsonArray)?.Select(artist => artist?.GetValue<string>() ?? "").ToList() ?? [])).ToList();
    }

    /// <summary>Spotify's own <c>{tracks:{items:[{uri,name,artists:[{name}],…}]}}</c>.</summary>
    public static IReadOnlyList<MusicTrack> DecodeSpotifyResults(string json)
    {
        var items = ((JsonNode.Parse(json) as JsonObject)?["tracks"] as JsonObject)?["items"] as JsonArray ?? throw new JsonException("no tracks");
        return items.OfType<JsonObject>().Select(track => new MusicTrack(
            track["uri"]?.GetValue<string>() ?? throw new JsonException("no uri"),
            track["name"]?.GetValue<string>() ?? "",
            (track["artists"] as JsonArray)?.OfType<JsonObject>().Select(artist => artist["name"]?.GetValue<string>() ?? "").ToList() ?? [])).ToList();
    }

    /// <summary>Keys &amp; Plans' Test for own Spotify credentials: one song looked up on them alone (never the backend).</summary>
    public static async Task<string> TestCredentialsAsync(string? credentials, HttpClient? client = null)
    {
        if (MusicLookupRoute.For(credentials, backendUrl: null) is not MusicLookupRoute.Spotify route)
        {
            return "✗ Paste client id:client secret from developer.spotify.com.";
        }
        var tracks = await new MusicLookup(route, client).SearchAsync("test").ConfigureAwait(false);
        return tracks.Count > 0 ? $"✓ Works. Found {tracks[0].Name}." : "✗ Spotify refused the credentials.";
    }

    /// <summary><c>spotify:search:&lt;query&gt;</c>: Spotify's own search page, for when the lookup found nothing.</summary>
    public static string SpotifySearchUri(string query) => "spotify:search:" + Uri.EscapeDataString(query);
}
