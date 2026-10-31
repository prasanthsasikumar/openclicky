//
//  WindowsWeatherLocation.cs
//  OpenClicky (Windows port of WeatherStore.swift's CoreLocation half)
//
//  Where the PC is, from Windows' location service (Windows.Devices.Geolocation). An app installed
//  without a package has no prompt of its own: it may use the location when Settings → Privacy &
//  security → Location lets desktop apps, and is refused otherwise, so "asking" again opens that
//  page. Windows offers no free way to name the town (the Mac reverse-geocodes); the fix's civic
//  address is used when the location provider fills it in, "My Location" otherwise.
//

using System.Diagnostics;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Weather;
using Windows.Devices.Geolocation;

namespace OpenClicky.Windows.Plugins.Weather;

internal sealed class WindowsWeatherLocation : IWeatherLocationSource
{
    private const string PrivacySettingsUri = "ms-settings:privacy-location";

    public async Task<WeatherLocationAccess> AccessAsync()
    {
        try
        {
            return await Geolocator.RequestAccessAsync() switch
            {
                GeolocationAccessStatus.Allowed => WeatherLocationAccess.Allowed,
                GeolocationAccessStatus.Denied => WeatherLocationAccess.Denied,
                _ => WeatherLocationAccess.NotDetermined,
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // No location service at all (a policy, a stripped-down Windows): as good as refused.
            AppLog.Write($"weather: location access unreadable ({error.GetType().Name})");
            return WeatherLocationAccess.Denied;
        }
    }

    /// <summary>Asks once more; refused, it opens the location privacy page, the only place the answer can change.</summary>
    public async Task RequestAccessAsync()
    {
        if (await AccessAsync() != WeatherLocationAccess.Denied) return;
        try { Process.Start(new ProcessStartInfo(PrivacySettingsUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"weather: could not open the location settings ({error.Message})");
        }
    }

    /// <summary>One fix to about a kilometre, a cached one up to half an hour old being good enough for the weather.</summary>
    public async Task<WeatherPlace> CurrentPlaceAsync(CancellationToken cancellationToken)
    {
        Geoposition position;
        try
        {
            var locator = new Geolocator { DesiredAccuracyInMeters = 1000 };
            position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(15)).AsTask(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new WeatherLocationException("no location fix", error);
        }
        var point = position.Coordinate.Point.Position;
        return new WeatherPlace(TownName(position) ?? "My Location", point.Latitude, point.Longitude);
    }

    private static string? TownName(Geoposition position)
    {
        try
        {
            var city = position.CivicAddress?.City;
            return string.IsNullOrWhiteSpace(city) ? null : city.Trim();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }
}
