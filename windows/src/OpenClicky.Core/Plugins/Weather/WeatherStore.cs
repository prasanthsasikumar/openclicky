//
//  WeatherStore.cs
//  OpenClicky (Windows port of Plugins/Weather/WeatherStore.swift)
//
//  The weather tile's live state: where the person is (Windows' location, or a place they typed),
//  today's weather there from Open-Meteo, and the unit to show it in. It fetches only while the
//  weather is on and a tile is on screen: every 30 minutes after a good reply, after 2 after a bad
//  one. The location itself is the platform's (OpenClicky.Windows/Plugins/Weather).
//

using System.Net.Http;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.Weather;

/// <summary>What the tile shows.</summary>
public abstract record WeatherTileState
{
    private WeatherTileState() { }

    /// <summary>Nothing fetched yet (or the weather is off).</summary>
    public sealed record Loading : WeatherTileState;

    /// <summary>"Use my location" is on but the location may not be used. <c>CanAsk</c> is true while a click can still ask;
    /// false once location is off for desktop apps in Windows' privacy settings.</summary>
    public sealed record NeedsLocationPermission(bool CanAsk) : WeatherTileState;

    /// <summary>"Use my location" is off and no place has been typed.</summary>
    public sealed record NeedsPlace : WeatherTileState;

    public sealed record Failed(string Message) : WeatherTileState;

    public sealed record Ready(WeatherReport Report, string CityName, bool IsAutomaticLocation) : WeatherTileState;
}

public enum WeatherLocationAccess
{
    Allowed,
    /// <summary>Not answered yet: asking may still bring up Windows' prompt.</summary>
    NotDetermined,
    /// <summary>Location is off for this app (or every desktop app) in Settings → Privacy → Location.</summary>
    Denied,
}

/// <summary>Where the PC is: Windows' location service, or a test double.</summary>
public interface IWeatherLocationSource
{
    Task<WeatherLocationAccess> AccessAsync();

    /// <summary>Asks for access, only ever from a person's action; once refused, opens the privacy settings instead.</summary>
    Task RequestAccessAsync();

    /// <summary>One fix, named by its town when Windows can tell ("My Location" otherwise).</summary>
    Task<WeatherPlace> CurrentPlaceAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One per app. Use it on the UI thread: fetches come back on the thread that started them, and <see cref="Changed"/> is
/// raised there.
/// </summary>
public sealed class WeatherStore : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    private static readonly Lazy<HttpClient> SharedHttpClient = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenClicky");
        return client;
    });

    private readonly Preferences preferences;
    private readonly IWeatherLocationSource location;
    private readonly Func<Uri, CancellationToken, Task<string>> fetch;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<DateTimeOffset> clock;
    private readonly IDisposable settingsObservation;
    private readonly Dictionary<string, WeatherPlace> resolvedPlacesByTypedName = new(StringComparer.Ordinal);
    private int watchers;
    private CancellationTokenSource? refreshLoop;
    private DateTimeOffset? lastSuccessfulRefresh;
    /// <summary>The settings that decide what is fetched; a change to any of them starts over.</summary>
    private FetchSettings lastFetchSettings;

    private sealed record FetchSettings(bool IsEnabled, bool UsesLocation, string PlaceName)
    {
        public static FetchSettings From(WeatherSettings settings) => new(settings.IsEnabled, settings.UsesLocation, settings.Place);
    }

    /// <param name="fetch">GETs a URL and returns the body; throws on a failed status. Defaults to a shared HttpClient.</param>
    public WeatherStore(Preferences preferences, IWeatherLocationSource location, Func<Uri, CancellationToken, Task<string>>? fetch = null,
                        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null)
    {
        this.preferences = preferences;
        this.location = location;
        this.fetch = fetch ?? FetchWithHttpClientAsync;
        this.delay = delay ?? Task.Delay;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        var settings = new WeatherSettings(preferences);
        Unit = settings.Unit;
        lastFetchSettings = FetchSettings.From(settings);
        // Settings writes straight to the preferences; follow them from here so the tile and the rows never disagree.
        settingsObservation = settings.Observe(SettingsDidChange);
    }

    public WeatherTileState State { get; private set; } = new WeatherTileState.Loading();
    public WeatherUnit Unit { get; private set; }

    /// <summary>The state or the unit changed.</summary>
    public event Action? Changed;

    public bool IsEnabled => lastFetchSettings.IsEnabled;
    public bool IsRefreshing => refreshLoop is not null;

    // MARK: - Watching

    /// <summary>A tile appeared: the first one fetches (unless a recent reply is still fresh) and starts the clock.</summary>
    public void BeginWatching()
    {
        watchers++;
        if (watchers != 1) return;
        StartRefreshLoop(forceImmediateRefresh: false);
    }

    /// <summary>A tile went away: the last one stops the clock. A request already in flight may finish.</summary>
    public void EndWatching()
    {
        watchers = Math.Max(0, watchers - 1);
        if (watchers > 0) return;
        refreshLoop?.Cancel();
        refreshLoop = null;
    }

    // MARK: - The person's actions

    /// <summary>The tile was clicked: switch between Celsius and Fahrenheit and remember the choice.</summary>
    public void ToggleUnit()
    {
        Unit = Unit.Toggled();
        preferences.Set(WeatherSettingsKeys.Unit, Unit.RawValue());
        Changed?.Invoke();
    }

    /// <summary>Asks for the location, but only ever from a person's action (a click on the tile, or the weather or
    /// "Use my location" switch turned on), then tries again.</summary>
    public async Task RequestLocationPermissionAsync()
    {
        try { await location.RequestAccessAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"weather: location request failed ({error.GetType().Name})");
        }
        if (watchers > 0 && lastFetchSettings.UsesLocation) StartRefreshLoop(forceImmediateRefresh: true);
    }

    /// <summary>A click on a failed tile: try again now rather than waiting for the retry.</summary>
    public void RetryNow()
    {
        if (watchers == 0) return;
        StartRefreshLoop(forceImmediateRefresh: true);
    }

    // MARK: - Refreshing

    private void SettingsDidChange()
    {
        var settings = new WeatherSettings(preferences);
        var storedUnit = settings.Unit;
        if (storedUnit != Unit)
        {
            Unit = storedUnit;
            Changed?.Invoke();
        }
        var next = FetchSettings.From(settings);
        if (next == lastFetchSettings) return;
        lastFetchSettings = next;
        // Another place (or the weather turned off) makes what is on screen stale.
        lastSuccessfulRefresh = null;
        SetState(new WeatherTileState.Loading());
        if (watchers > 0) StartRefreshLoop(forceImmediateRefresh: true);
    }

    private void StartRefreshLoop(bool forceImmediateRefresh)
    {
        refreshLoop?.Cancel();
        refreshLoop = null;
        if (!lastFetchSettings.IsEnabled) return;
        // A reply younger than 30 minutes is still good: wait out the rest of the interval.
        var initialDelay = TimeSpan.Zero;
        if (!forceImmediateRefresh && lastSuccessfulRefresh is { } lastRefresh)
        {
            initialDelay = RefreshInterval - (clock() - lastRefresh);
            if (initialDelay < TimeSpan.Zero) initialDelay = TimeSpan.Zero;
        }
        var cancellation = new CancellationTokenSource();
        refreshLoop = cancellation;
        _ = RefreshLoopAsync(initialDelay, cancellation.Token);
    }

    private async Task RefreshLoopAsync(TimeSpan initialDelay, CancellationToken cancellationToken)
    {
        var wait = initialDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (wait > TimeSpan.Zero)
            {
                try { await delay(wait, cancellationToken); }
                catch (OperationCanceledException) { return; }
                if (cancellationToken.IsCancellationRequested) return;
            }
            // Null means the store is waiting on the person (a permission, a place): stop until they act, which restarts.
            if (await RefreshOnceAsync(cancellationToken) is not { } next) return;
            wait = next;
        }
    }

    /// <summary>One fetch. Returns how long to wait before the next, or null to wait for the person.</summary>
    private async Task<TimeSpan?> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var fetchSettings = lastFetchSettings;
        try
        {
            WeatherPlace place;
            bool isAutomaticLocation;
            if (fetchSettings.UsesLocation)
            {
                var access = await location.AccessAsync();
                if (cancellationToken.IsCancellationRequested) return null;
                if (access != WeatherLocationAccess.Allowed)
                {
                    SetState(new WeatherTileState.NeedsLocationPermission(CanAsk: access == WeatherLocationAccess.NotDetermined));
                    return null;
                }
                place = await location.CurrentPlaceAsync(cancellationToken);
                isAutomaticLocation = true;
            }
            else
            {
                if (fetchSettings.PlaceName.Length == 0)
                {
                    SetState(new WeatherTileState.NeedsPlace());
                    return null;
                }
                place = await ResolvedPlaceAsync(fetchSettings.PlaceName, cancellationToken);
                isAutomaticLocation = false;
            }
            var report = WeatherApi.ParseForecast(await fetch(WeatherApi.ForecastUri(place.Latitude, place.Longitude), cancellationToken));
            // The settings may have changed while the request was out; that change started its own loop.
            if (cancellationToken.IsCancellationRequested || fetchSettings != lastFetchSettings) return null;
            SetState(new WeatherTileState.Ready(report, place.Name, isAutomaticLocation));
            lastSuccessfulRefresh = clock();
            return RefreshInterval;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (cancellationToken.IsCancellationRequested || fetchSettings != lastFetchSettings) return null;
            AppLog.Write($"weather: refresh failed ({error.GetType().Name})");
            // Keep showing an earlier reply through a passing failure; the retry will replace it.
            if (State is not WeatherTileState.Ready) SetState(new WeatherTileState.Failed(FailureMessage(error)));
            return RetryInterval;
        }
    }

    private async Task<WeatherPlace> ResolvedPlaceAsync(string placeName, CancellationToken cancellationToken)
    {
        if (resolvedPlacesByTypedName.TryGetValue(placeName, out var cached)) return cached;
        var place = WeatherApi.ParseGeocoding(await fetch(WeatherApi.GeocodingUri(placeName), cancellationToken), placeName);
        resolvedPlacesByTypedName[placeName] = place;
        return place;
    }

    /// <summary>The tile's words for a failure.</summary>
    public static string FailureMessage(Exception error) => error switch
    {
        WeatherApiException { Kind: WeatherApiErrorKind.PlaceNotFound, PlaceName: var placeName } => $"Can't find “{placeName}”",
        WeatherLocationException => "Can't find your location",
        HttpRequestException when !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() => "You're offline",
        _ => "Weather unavailable",
    };

    private void SetState(WeatherTileState state)
    {
        if (state == State) return;
        State = state;
        Changed?.Invoke();
    }

    private static async Task<string> FetchWithHttpClientAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await SharedHttpClient.Value.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new WeatherApiException(WeatherApiErrorKind.BadStatus, status: (int)response.StatusCode);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Stops following the settings (tests; the app's one store lives as long as the app).</summary>
    public void Dispose()
    {
        settingsObservation.Dispose();
        refreshLoop?.Cancel();
        refreshLoop = null;
    }
}

/// <summary>The location service could not give a fix (timed out, turned off mid-way, no sensor or network to go on).</summary>
public sealed class WeatherLocationException(string message, Exception? inner = null) : Exception(message, inner);
