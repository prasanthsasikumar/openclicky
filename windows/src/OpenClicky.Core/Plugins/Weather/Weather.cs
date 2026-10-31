//
//  Weather.cs
//  OpenClicky (Windows port of Plugins/Weather/WeatherModel.swift and WeatherSettings.swift's keys)
//
//  The weather tile's pure parts: the settings, the unit and its conversion, the WMO weather code
//  read as a symbol, a word and a sky, and the Open-Meteo requests and replies. Everything here is
//  free of I/O so the tests can pin it down; WeatherStore does the fetching. The weather is not a
//  plugin but a switch of home's (off by default: once on it asks the network, and maybe Windows
//  for the location).
//

using System.Globalization;
using System.Text.Json;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Plugins.Weather;

/// <summary>Where the weather keeps its choices, in the Mac's keys.</summary>
public static class WeatherSettingsKeys
{
    /// <summary>Off by default: the tile only asks the network (and maybe for the location) once turned on.</summary>
    public const string Enabled = IslandHomeLayout.WeatherEnabledKey;
    /// <summary>"celsius" or "fahrenheit"; when unset, the region's measurement system decides.</summary>
    public const string Unit = "openclicky.weather.unit";
    /// <summary>On by default; off means the typed place below is used instead.</summary>
    public const string UseLocation = "openclicky.weather.useLocation";
    /// <summary>A city typed by the person, used when "Use my location" is off.</summary>
    public const string Place = "openclicky.weather.place";

    public static IReadOnlyList<string> All { get; } = [Enabled, Unit, UseLocation, Place];
}

public enum WeatherUnit
{
    Celsius,
    Fahrenheit,
}

public static class WeatherUnits
{
    public static string RawValue(this WeatherUnit unit) => unit == WeatherUnit.Fahrenheit ? "fahrenheit" : "celsius";

    public static WeatherUnit? FromRawValue(string? raw) => raw switch
    {
        "celsius" => WeatherUnit.Celsius,
        "fahrenheit" => WeatherUnit.Fahrenheit,
        _ => null,
    };

    /// <summary>The letter after the degree sign on the tile.</summary>
    public static string Letter(this WeatherUnit unit) => unit == WeatherUnit.Fahrenheit ? "F" : "C";

    public static string Title(this WeatherUnit unit) => unit == WeatherUnit.Fahrenheit ? "Fahrenheit" : "Celsius";

    public static WeatherUnit Toggled(this WeatherUnit unit) => unit == WeatherUnit.Fahrenheit ? WeatherUnit.Celsius : WeatherUnit.Fahrenheit;

    /// <summary>Fahrenheit where the region does not measure in metric (the US and a few others), Celsius everywhere else.</summary>
    public static WeatherUnit DefaultFor(RegionInfo region) => region.IsMetric ? WeatherUnit.Celsius : WeatherUnit.Fahrenheit;

    public static WeatherUnit DefaultForCurrentRegion
    {
        get
        {
            try { return DefaultFor(RegionInfo.CurrentRegion); }
            catch (ArgumentException) { return WeatherUnit.Celsius; }
        }
    }

    /// <summary>The stored choice, or the region's default when nothing (or something unknown) is stored.</summary>
    public static WeatherUnit Stored(Preferences preferences) => FromRawValue(preferences.GetString(WeatherSettingsKeys.Unit)) ?? DefaultForCurrentRegion;

    /// <summary>Open-Meteo is always asked for Celsius; the conversion happens here so a unit switch on the tile is
    /// instant and needs no new request.</summary>
    public static double Convert(this WeatherUnit unit, double celsius) => unit == WeatherUnit.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    /// <summary>Whole degrees, rounded half away from zero ("-3", "21").</summary>
    public static int WholeDegrees(this WeatherUnit unit, double celsius) => (int)Math.Round(unit.Convert(celsius), MidpointRounding.AwayFromZero);
}

/// <summary>One sRGB colour in 0…255 components, as the design gives them.</summary>
public readonly record struct WeatherRgb(byte Red, byte Green, byte Blue);

/// <summary>The tile's background: one top-to-bottom gradient per kind of sky.</summary>
public enum WeatherSky
{
    ClearDay,
    ClearNight,
    PartlyCloudyDay,
    PartlyCloudyNight,
    OvercastDay,
    OvercastNight,
    RainDay,
    RainNight,
    Storm,
}

public static class WeatherSkies
{
    public static WeatherRgb Top(this WeatherSky sky) => sky switch
    {
        WeatherSky.ClearDay => new(40, 112, 214),
        WeatherSky.ClearNight => new(10, 22, 52),
        WeatherSky.PartlyCloudyDay => new(76, 116, 168),
        WeatherSky.PartlyCloudyNight => new(28, 38, 58),
        WeatherSky.OvercastDay => new(118, 140, 170),
        WeatherSky.OvercastNight => new(40, 52, 72),
        WeatherSky.RainDay => new(62, 84, 112),
        WeatherSky.RainNight => new(24, 32, 46),
        _ => new(36, 42, 56),
    };

    public static WeatherRgb Bottom(this WeatherSky sky) => sky switch
    {
        WeatherSky.ClearDay => new(98, 165, 236),
        WeatherSky.ClearNight => new(32, 54, 98),
        WeatherSky.PartlyCloudyDay => new(132, 162, 199),
        WeatherSky.PartlyCloudyNight => new(50, 72, 98),
        WeatherSky.OvercastDay => new(176, 192, 212),
        WeatherSky.OvercastNight => new(78, 92, 116),
        WeatherSky.RainDay => new(104, 124, 150),
        WeatherSky.RainNight => new(52, 62, 80),
        _ => new(72, 78, 80),
    };
}

/// <summary>The sky's picture, drawn by the platform (the Mac's SF Symbols, Segoe UI Symbol on Windows).</summary>
public enum WeatherSymbol
{
    Sun,
    Moon,
    CloudSun,
    CloudMoon,
    Cloud,
    Fog,
    Drizzle,
    Rain,
    Snow,
    Thunderstorm,
}

/// <summary>What a WMO weather code looks like on the tile.</summary>
/// <param name="Word">A short word or two that fits beside the symbol in a 118 tile.</param>
public sealed record WeatherCondition(WeatherSymbol Symbol, string Word, WeatherSky Sky)
{
    /// <summary>Open-Meteo reports the WMO 4677 code subset (0 clear … 99 thunderstorm with hail). Only clear and partly
    /// cloudy skies change their symbol at night; every kind of sky darkens.</summary>
    public static WeatherCondition ForWeatherCode(int weatherCode, bool isDay)
    {
        var overcastSky = isDay ? WeatherSky.OvercastDay : WeatherSky.OvercastNight;
        var rainSky = isDay ? WeatherSky.RainDay : WeatherSky.RainNight;
        return weatherCode switch
        {
            0 => isDay ? new(WeatherSymbol.Sun, "Sunny", WeatherSky.ClearDay) : new(WeatherSymbol.Moon, "Clear", WeatherSky.ClearNight),
            1 or 2 => isDay
                ? new(WeatherSymbol.CloudSun, weatherCode == 1 ? "Mostly Clear" : "Partly Cloudy", WeatherSky.PartlyCloudyDay)
                : new(WeatherSymbol.CloudMoon, weatherCode == 1 ? "Mostly Clear" : "Partly Cloudy", WeatherSky.PartlyCloudyNight),
            3 => new(WeatherSymbol.Cloud, "Cloudy", overcastSky),
            45 or 48 => new(WeatherSymbol.Fog, "Fog", overcastSky),
            51 or 53 or 55 or 56 or 57 => new(WeatherSymbol.Drizzle, "Drizzle", rainSky),
            61 or 63 or 65 => new(WeatherSymbol.Rain, "Rain", rainSky),
            66 or 67 => new(WeatherSymbol.Rain, "Sleet", rainSky),
            80 or 81 or 82 => new(WeatherSymbol.Rain, "Showers", rainSky),
            71 or 73 or 75 or 77 or 85 or 86 => new(WeatherSymbol.Snow, "Snow", overcastSky),
            95 or 96 or 99 => new(WeatherSymbol.Thunderstorm, "Thunderstorm", WeatherSky.Storm),
            // A code outside the documented set: show a plain cloud rather than nothing.
            _ => new(WeatherSymbol.Cloud, "Cloudy", overcastSky),
        };
    }
}

/// <summary>Today's weather at one place, in Celsius as Open-Meteo returns it.</summary>
/// <param name="HighCelsius">Null when the daily series came back empty or null.</param>
public sealed record WeatherReport(double TemperatureCelsius, int WeatherCode, bool IsDay, double? HighCelsius, double? LowCelsius)
{
    public WeatherCondition Condition => WeatherCondition.ForWeatherCode(WeatherCode, IsDay);
}

/// <summary>A place a typed name resolved to, or where the PC is.</summary>
public sealed record WeatherPlace(string Name, double Latitude, double Longitude);

public enum WeatherApiErrorKind
{
    MalformedReply,
    PlaceNotFound,
    BadStatus,
}

public sealed class WeatherApiException(WeatherApiErrorKind kind, string? placeName = null, int status = 0)
    : Exception($"weather: {kind}{(placeName is null ? "" : $" ({placeName})")}{(status == 0 ? "" : $" {status}")}")
{
    public WeatherApiErrorKind Kind { get; } = kind;
    public string? PlaceName { get; } = placeName;
    public int Status { get; } = status;
}

/// <summary>The two Open-Meteo endpoints the tile uses; neither needs a key.</summary>
public static class WeatherApi
{
    /// <summary>Current temperature, code and day flag plus today's high and low, in the place's own time zone so
    /// "today" is the place's today.</summary>
    public static Uri ForecastUri(double latitude, double longitude) => new(
        "https://api.open-meteo.com/v1/forecast" +
        $"?latitude={CoordinateText(latitude)}&longitude={CoordinateText(longitude)}" +
        "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1");

    /// <summary>The best match for a typed place name, escaped whole ("&amp;", "=" and "+" included).</summary>
    public static Uri GeocodingUri(string placeName) =>
        new($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(placeName)}&count=1");

    /// <summary>Four decimals is about 11 m: plenty for weather, and the invariant culture always writes a dot, whatever
    /// the person's region uses as a decimal separator.</summary>
    public static string CoordinateText(double coordinate) => coordinate.ToString("F4", CultureInfo.InvariantCulture);

    public static WeatherReport ParseForecast(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var current = root.GetProperty("current");
            double? high = null, low = null;
            if (root.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Object)
            {
                high = FirstNumber(daily, "temperature_2m_max");
                low = FirstNumber(daily, "temperature_2m_min");
            }
            return new WeatherReport(
                current.GetProperty("temperature_2m").GetDouble(),
                current.GetProperty("weather_code").GetInt32(),
                current.GetProperty("is_day").GetInt32() != 0,
                high,
                low);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new WeatherApiException(WeatherApiErrorKind.MalformedReply);
        }
    }

    /// <summary>The first result, or <see cref="WeatherApiErrorKind.PlaceNotFound"/> when Open-Meteo knows no such place
    /// (it then leaves out "results" altogether).</summary>
    public static WeatherPlace ParseGeocoding(string json, string placeName)
    {
        JsonElement first;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                throw new WeatherApiException(WeatherApiErrorKind.PlaceNotFound, placeName);
            }
            first = results[0].Clone();
        }
        catch (JsonException)
        {
            throw new WeatherApiException(WeatherApiErrorKind.MalformedReply);
        }
        try
        {
            return new WeatherPlace(first.GetProperty("name").GetString() ?? placeName, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new WeatherApiException(WeatherApiErrorKind.MalformedReply);
        }
    }

    private static double? FirstNumber(JsonElement daily, string name) =>
        daily.TryGetProperty(name, out var series) && series.ValueKind == JsonValueKind.Array && series.GetArrayLength() > 0
            && series[0].ValueKind == JsonValueKind.Number
            ? series[0].GetDouble()
            : null;
}

/// <summary>The weather's rows in Settings (Island › Weather on the Mac): the switch, the unit, and where the weather is
/// for, read and written in the preferences; <see cref="Observe"/> follows any of them changing.</summary>
public sealed class WeatherSettings(Preferences preferences)
{
    /// <summary>"Weather": the tile on home. Off by default.</summary>
    public bool IsEnabled
    {
        get => preferences.GetBool(WeatherSettingsKeys.Enabled, false);
        set => preferences.Set(WeatherSettingsKeys.Enabled, value);
    }

    /// <summary>"Temperature": the stored unit, else the region's.</summary>
    public WeatherUnit Unit
    {
        get => WeatherUnits.Stored(preferences);
        set => preferences.Set(WeatherSettingsKeys.Unit, value.RawValue());
    }

    /// <summary>"Use my location": on by default; off uses <see cref="Place"/>.</summary>
    public bool UsesLocation
    {
        get => preferences.GetBool(WeatherSettingsKeys.UseLocation, true);
        set => preferences.Set(WeatherSettingsKeys.UseLocation, value);
    }

    /// <summary>"Place": a typed city, trimmed when stored. Save it on Enter or when the field loses focus, so the
    /// store does not look up every half-typed name.</summary>
    public string Place
    {
        get => (preferences.GetString(WeatherSettingsKeys.Place) ?? "").Trim();
        set => preferences.Set(WeatherSettingsKeys.Place, value.Trim());
    }

    /// <summary>Calls <paramref name="changed"/> whenever a weather setting changes; dispose to stop.</summary>
    public IDisposable Observe(Action changed) => PreferenceObservations.ObserveAll(preferences, WeatherSettingsKeys.All, changed);
}
