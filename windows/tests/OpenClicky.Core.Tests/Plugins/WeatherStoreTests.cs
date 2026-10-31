// Ports WeatherStoreTests.swift: how a WMO code looks, the unit and its conversion, the Open-Meteo replies and the
// request URLs; and, new on Windows, the store's states with a fake location and network.

using System.Globalization;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Weather;

namespace OpenClicky.Core.Tests.Plugins;

public class WeatherStoreTests
{
    // MARK: - Codes

    [Fact]
    public void ClearAndPartlyCloudySkiesChangeTheirSymbolAtNight()
    {
        Assert.Equal(new WeatherCondition(WeatherSymbol.Sun, "Sunny", WeatherSky.ClearDay), WeatherCondition.ForWeatherCode(0, isDay: true));
        Assert.Equal(new WeatherCondition(WeatherSymbol.Moon, "Clear", WeatherSky.ClearNight), WeatherCondition.ForWeatherCode(0, isDay: false));
        Assert.Equal(WeatherSymbol.CloudSun, WeatherCondition.ForWeatherCode(2, isDay: true).Symbol);
        Assert.Equal(WeatherSky.PartlyCloudyDay, WeatherCondition.ForWeatherCode(2, isDay: true).Sky);
        Assert.Equal(WeatherSymbol.CloudMoon, WeatherCondition.ForWeatherCode(1, isDay: false).Symbol);
        Assert.Equal(WeatherSky.PartlyCloudyNight, WeatherCondition.ForWeatherCode(1, isDay: false).Sky);
    }

    [Theory]
    [InlineData(3, WeatherSymbol.Cloud, WeatherSky.OvercastDay, WeatherSky.OvercastNight)]
    [InlineData(45, WeatherSymbol.Fog, WeatherSky.OvercastDay, WeatherSky.OvercastNight)]
    [InlineData(48, WeatherSymbol.Fog, WeatherSky.OvercastDay, WeatherSky.OvercastNight)]
    [InlineData(51, WeatherSymbol.Drizzle, WeatherSky.RainDay, WeatherSky.RainNight)]
    [InlineData(57, WeatherSymbol.Drizzle, WeatherSky.RainDay, WeatherSky.RainNight)]
    [InlineData(61, WeatherSymbol.Rain, WeatherSky.RainDay, WeatherSky.RainNight)]
    [InlineData(67, WeatherSymbol.Rain, WeatherSky.RainDay, WeatherSky.RainNight)]
    [InlineData(82, WeatherSymbol.Rain, WeatherSky.RainDay, WeatherSky.RainNight)]
    [InlineData(71, WeatherSymbol.Snow, WeatherSky.OvercastDay, WeatherSky.OvercastNight)]
    [InlineData(86, WeatherSymbol.Snow, WeatherSky.OvercastDay, WeatherSky.OvercastNight)]
    [InlineData(95, WeatherSymbol.Thunderstorm, WeatherSky.Storm, WeatherSky.Storm)]
    [InlineData(99, WeatherSymbol.Thunderstorm, WeatherSky.Storm, WeatherSky.Storm)]
    public void EachFamilyOfCodesHasItsSymbolAndSky(int code, WeatherSymbol symbol, WeatherSky daySky, WeatherSky nightSky)
    {
        var day = WeatherCondition.ForWeatherCode(code, isDay: true);
        var night = WeatherCondition.ForWeatherCode(code, isDay: false);
        Assert.Equal(symbol, day.Symbol);
        Assert.Equal(symbol, night.Symbol);
        Assert.Equal(daySky, day.Sky);
        Assert.Equal(nightSky, night.Sky);
        Assert.NotEmpty(day.Word);
    }

    [Fact]
    public void AnUnknownCodeStillShowsACloud()
    {
        Assert.Equal(WeatherSymbol.Cloud, WeatherCondition.ForWeatherCode(42, isDay: true).Symbol);
        Assert.Equal(WeatherSky.OvercastNight, WeatherCondition.ForWeatherCode(-1, isDay: false).Sky);
    }

    [Fact]
    public void TheSkyGradientsMatchTheDesign()
    {
        Assert.Equal(new WeatherRgb(40, 112, 214), WeatherSky.ClearDay.Top());
        Assert.Equal(new WeatherRgb(98, 165, 236), WeatherSky.ClearDay.Bottom());
        Assert.Equal(new WeatherRgb(36, 42, 56), WeatherSky.Storm.Top());
        Assert.Equal(new WeatherRgb(72, 78, 80), WeatherSky.Storm.Bottom());
        Assert.Equal(new WeatherRgb(52, 62, 80), WeatherSky.RainNight.Bottom());
    }

    // MARK: - Unit

    [Fact]
    public void CelsiusConvertsToFahrenheitAndRoundsToWholeDegrees()
    {
        Assert.Equal(32, WeatherUnit.Fahrenheit.Convert(0));
        Assert.Equal(212, WeatherUnit.Fahrenheit.Convert(100));
        Assert.Equal(-40, WeatherUnit.Fahrenheit.Convert(-40));
        Assert.Equal(21.4, WeatherUnit.Celsius.Convert(21.4));
        Assert.Equal(73, WeatherUnit.Fahrenheit.WholeDegrees(22.8));
        Assert.Equal(22, WeatherUnit.Celsius.WholeDegrees(21.5));
        Assert.Equal(-3, WeatherUnit.Celsius.WholeDegrees(-2.5));
        Assert.Equal(WeatherUnit.Fahrenheit, WeatherUnit.Celsius.Toggled());
        Assert.Equal(WeatherUnit.Celsius, WeatherUnit.Fahrenheit.Toggled());
        Assert.Equal("C", WeatherUnit.Celsius.Letter());
        Assert.Equal("F", WeatherUnit.Fahrenheit.Letter());
    }

    [Fact]
    public void TheRegionDecidesTheDefaultUnit()
    {
        Assert.Equal(WeatherUnit.Fahrenheit, WeatherUnits.DefaultFor(new RegionInfo("US")));
        Assert.Equal(WeatherUnit.Celsius, WeatherUnits.DefaultFor(new RegionInfo("GB")));
        Assert.Equal(WeatherUnit.Celsius, WeatherUnits.DefaultFor(new RegionInfo("DE")));
    }

    [Fact]
    public void AStoredUnitWinsOverTheRegion()
    {
        var preferences = Preferences.InMemory();
        Assert.Equal(WeatherUnits.DefaultForCurrentRegion, WeatherUnits.Stored(preferences));
        preferences.Set(WeatherSettingsKeys.Unit, "fahrenheit");
        Assert.Equal(WeatherUnit.Fahrenheit, WeatherUnits.Stored(preferences));
        preferences.Set(WeatherSettingsKeys.Unit, "kelvin");
        Assert.Equal(WeatherUnits.DefaultForCurrentRegion, WeatherUnits.Stored(preferences));
    }

    [Fact]
    public void TheSettingsKeepTheMacsKeysAndDefaults()
    {
        var preferences = Preferences.InMemory();
        var settings = new WeatherSettings(preferences);
        Assert.False(settings.IsEnabled);
        Assert.True(settings.UsesLocation);
        Assert.Equal("", settings.Place);
        settings.Place = "  Lisbon ";
        Assert.Equal("Lisbon", preferences.GetString("openclicky.weather.place"));
        settings.IsEnabled = true;
        Assert.True(preferences.GetBool("openclicky.weather.enabled"));
    }

    // MARK: - Replies

    [Fact]
    public void AForecastReplyBecomesAReport()
    {
        const string reply = """
            {"latitude":52.52,"longitude":13.419998,"generationtime_ms":0.05,"utc_offset_seconds":3600,
             "timezone":"Europe/Berlin","timezone_abbreviation":"GMT+1","elevation":38.0,
             "current_units":{"time":"iso8601","interval":"seconds","temperature_2m":"°C","weather_code":"wmo code","is_day":""},
             "current":{"time":"2026-10-30T14:15","interval":900,"temperature_2m":12.6,"weather_code":3,"is_day":1},
             "daily_units":{"time":"iso8601","temperature_2m_max":"°C","temperature_2m_min":"°C"},
             "daily":{"time":["2026-10-30"],"temperature_2m_max":[14.1],"temperature_2m_min":[6.8]}}
            """;
        var report = WeatherApi.ParseForecast(reply);
        Assert.Equal(new WeatherReport(12.6, 3, true, 14.1, 6.8), report);
        Assert.Equal(WeatherSymbol.Cloud, report.Condition.Symbol);
    }

    [Fact]
    public void ANightReplyWithoutDailyFiguresStillParses()
    {
        const string reply = """
            {"current":{"time":"2026-10-30T23:00","interval":900,"temperature_2m":-1.2,"weather_code":0,"is_day":0},
             "daily":{"time":["2026-10-30"],"temperature_2m_max":[null],"temperature_2m_min":[null]}}
            """;
        var report = WeatherApi.ParseForecast(reply);
        Assert.False(report.IsDay);
        Assert.Null(report.HighCelsius);
        Assert.Null(report.LowCelsius);
        Assert.Equal(WeatherSymbol.Moon, report.Condition.Symbol);
    }

    [Fact]
    public void AMalformedForecastIsReportedAsSuch()
    {
        var error = Assert.Throws<WeatherApiException>(() => WeatherApi.ParseForecast("""{"error":true,"reason":"Latitude must be in range"}"""));
        Assert.Equal(WeatherApiErrorKind.MalformedReply, error.Kind);
        Assert.Equal(WeatherApiErrorKind.MalformedReply, Assert.Throws<WeatherApiException>(() => WeatherApi.ParseForecast("not json")).Kind);
    }

    [Fact]
    public void AGeocodingReplyGivesThePlace()
    {
        const string reply = """
            {"results":[{"id":2950159,"name":"Berlin","latitude":52.52437,"longitude":13.41053,"elevation":74.0,
              "feature_code":"PPLC","country_code":"DE","timezone":"Europe/Berlin","country":"Germany"}],
             "generationtime_ms":0.6}
            """;
        Assert.Equal(new WeatherPlace("Berlin", 52.52437, 13.41053), WeatherApi.ParseGeocoding(reply, "berlin"));
    }

    [Fact]
    public void APlaceNobodyKnowsIsNotFound()
    {
        var error = Assert.Throws<WeatherApiException>(() => WeatherApi.ParseGeocoding("""{"generationtime_ms":0.3}""", "Atlantis"));
        Assert.Equal(WeatherApiErrorKind.PlaceNotFound, error.Kind);
        Assert.Equal("Atlantis", error.PlaceName);
        Assert.Equal("Can't find “Atlantis”", WeatherStore.FailureMessage(error));
    }

    // MARK: - URLs

    [Fact]
    public void TheForecastUrlAsksForTodayInCelsius()
    {
        var previous = CultureInfo.CurrentCulture;
        // A comma for the decimal point must not leak into the coordinates.
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var uri = WeatherApi.ForecastUri(52.52437, -13.41053);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("api.open-meteo.com", uri.Host);
            Assert.Equal("/v1/forecast", uri.AbsolutePath);
            var items = Query(uri);
            Assert.Equal(new Dictionary<string, string>
            {
                ["latitude"] = "52.5244",
                ["longitude"] = "-13.4105",
                ["current"] = "temperature_2m,weather_code,is_day",
                ["daily"] = "temperature_2m_max,temperature_2m_min",
                ["timezone"] = "auto",
                ["forecast_days"] = "1",
            }, items);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TheGeocodingUrlEscapesThePlaceName()
    {
        var uri = WeatherApi.GeocodingUri("São Paulo & Co");
        Assert.Equal("geocoding-api.open-meteo.com", uri.Host);
        Assert.Equal("/v1/search", uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["name"] = "São Paulo & Co", ["count"] = "1" }, Query(uri));
        Assert.DoesNotContain(" ", uri.AbsoluteUri);
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

    // MARK: - The store

    [Fact]
    public async Task TheStoreAsksNothingWhileOffAndWaitsForThePersonWhenItNeedsThem()
    {
        var preferences = Preferences.InMemory();
        var location = new FakeLocation { Access = WeatherLocationAccess.NotDetermined };
        var fetched = new List<Uri>();
        using var store = new WeatherStore(preferences, location, (uri, _) => { fetched.Add(uri); return Task.FromResult(""); }, NeverDelay);
        store.BeginWatching();
        await Task.Yield();
        Assert.False(store.IsRefreshing);
        Assert.IsType<WeatherTileState.Loading>(store.State);

        new WeatherSettings(preferences).IsEnabled = true;
        await Settle();
        Assert.Equal(new WeatherTileState.NeedsLocationPermission(CanAsk: true), store.State);
        location.Access = WeatherLocationAccess.Denied;
        store.RetryNow();
        await Settle();
        Assert.Equal(new WeatherTileState.NeedsLocationPermission(CanAsk: false), store.State);

        new WeatherSettings(preferences).UsesLocation = false;
        await Settle();
        Assert.IsType<WeatherTileState.NeedsPlace>(store.State);
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task ATypedPlaceIsLookedUpOnceAndItsWeatherShown()
    {
        var preferences = Preferences.InMemory();
        var settings = new WeatherSettings(preferences) { IsEnabled = true, UsesLocation = false, Place = "Berlin", Unit = WeatherUnit.Celsius };
        var fetched = new List<string>();
        Task<string> Fetch(Uri uri, CancellationToken cancellationToken)
        {
            fetched.Add(uri.Host);
            return Task.FromResult(uri.Host.StartsWith("geocoding", StringComparison.Ordinal)
                ? """{"results":[{"name":"Berlin","latitude":52.52,"longitude":13.41}]}"""
                : """{"current":{"temperature_2m":21.5,"weather_code":0,"is_day":1},"daily":{"temperature_2m_max":[24],"temperature_2m_min":[12]}}""");
        }
        using var store = new WeatherStore(preferences, new FakeLocation(), Fetch, NeverDelay);
        store.BeginWatching();
        await Settle();
        var ready = Assert.IsType<WeatherTileState.Ready>(store.State);
        Assert.Equal("Berlin", ready.CityName);
        Assert.False(ready.IsAutomaticLocation);
        Assert.Equal(22, store.Unit.WholeDegrees(ready.Report.TemperatureCelsius));
        Assert.Equal(["geocoding-api.open-meteo.com", "api.open-meteo.com"], fetched);

        store.ToggleUnit();
        Assert.Equal(WeatherUnit.Fahrenheit, store.Unit);
        Assert.Equal(WeatherUnit.Fahrenheit, settings.Unit);
        store.RetryNow();
        await Settle();
        // The place was resolved already: only the forecast is asked again.
        Assert.Equal(["geocoding-api.open-meteo.com", "api.open-meteo.com", "api.open-meteo.com"], fetched);
    }

    [Fact]
    public async Task TheLocationIsUsedWhenAllowedAndAFailureSaysWhy()
    {
        var preferences = Preferences.InMemory();
        _ = new WeatherSettings(preferences) { IsEnabled = true };
        var location = new FakeLocation { Access = WeatherLocationAccess.Allowed };
        var fails = true;
        using var store = new WeatherStore(preferences, location, (_, _) => fails
            ? throw new WeatherApiException(WeatherApiErrorKind.BadStatus, status: 503)
            : Task.FromResult("""{"current":{"temperature_2m":3,"weather_code":71,"is_day":0}}"""), NeverDelay);
        store.BeginWatching();
        await Settle();
        Assert.Equal(new WeatherTileState.Failed("Weather unavailable"), store.State);
        fails = false;
        store.RetryNow();
        await Settle();
        var ready = Assert.IsType<WeatherTileState.Ready>(store.State);
        Assert.Equal("Auckland", ready.CityName);
        Assert.True(ready.IsAutomaticLocation);
        Assert.Equal(WeatherSymbol.Snow, ready.Report.Condition.Symbol);
    }

    private static Task NeverDelay(TimeSpan interval, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

    private static async Task Settle()
    {
        for (var turn = 0; turn < 10; turn++) await Task.Yield();
    }

    private sealed class FakeLocation : IWeatherLocationSource
    {
        public WeatherLocationAccess Access { get; set; } = WeatherLocationAccess.Allowed;
        public Task<WeatherLocationAccess> AccessAsync() => Task.FromResult(Access);
        public Task RequestAccessAsync() => Task.CompletedTask;
        public Task<WeatherPlace> CurrentPlaceAsync(CancellationToken cancellationToken) => Task.FromResult(new WeatherPlace("Auckland", -36.85, 174.76));
    }
}
