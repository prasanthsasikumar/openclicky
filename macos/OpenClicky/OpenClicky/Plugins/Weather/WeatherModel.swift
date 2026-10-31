//
//  WeatherModel.swift
//  OpenClicky
//
//  The Weather plugin's pure parts: the settings keys, the unit and its conversion, the WMO weather
//  code read as a symbol, a word and a sky, and the Open-Meteo requests and replies. Everything
//  here is free of I/O so the tests can pin it down; `WeatherStore` does the fetching.
//

import Foundation
import SwiftUI

/// Where the Weather plugin keeps its choices in UserDefaults.
enum WeatherSettingsKeys {
    /// Off by default: the tile only asks the network (and maybe for the location) once turned on.
    static let enabled = "openclicky.weather.enabled"
    /// "celsius" or "fahrenheit"; when unset, the locale's measurement system decides.
    static let unit = "openclicky.weather.unit"
    /// On by default; off means the typed place below is used instead.
    static let useLocation = "openclicky.weather.useLocation"
    /// A city typed by the person, used when "Use my location" is off.
    static let place = "openclicky.weather.place"
}

// MARK: - unit

enum WeatherUnit: String, CaseIterable, Identifiable {
    case celsius
    case fahrenheit

    var id: String { rawValue }

    /// The letter after the degree sign on the tile.
    var letter: String { self == .celsius ? "C" : "F" }

    var title: String { self == .celsius ? "Celsius" : "Fahrenheit" }

    var toggled: WeatherUnit { self == .celsius ? .fahrenheit : .celsius }

    /// Fahrenheit where the locale measures in US units, Celsius everywhere else.
    static func defaultUnit(for locale: Locale) -> WeatherUnit {
        locale.measurementSystem == .us ? .fahrenheit : .celsius
    }

    static var defaultForCurrentLocale: WeatherUnit { defaultUnit(for: .current) }

    /// The stored choice, or the locale's default when nothing (or something unknown) is stored.
    static func stored(in defaults: UserDefaults) -> WeatherUnit {
        defaults.string(forKey: WeatherSettingsKeys.unit).flatMap(WeatherUnit.init(rawValue:)) ?? defaultForCurrentLocale
    }

    /// Open-Meteo is always asked for Celsius; the conversion happens here so a unit switch on the
    /// tile is instant and needs no new request.
    func convert(celsius: Double) -> Double {
        switch self {
        case .celsius: return celsius
        case .fahrenheit: return celsius * 9 / 5 + 32
        }
    }

    /// Whole degrees, rounded half away from zero ("-3", "21").
    func wholeDegrees(celsius: Double) -> Int {
        Int(convert(celsius: celsius).rounded())
    }
}

// MARK: - sky and condition

/// One sRGB colour given in 0…255 components, as the design specifies them.
struct WeatherRGB: Equatable {
    let red: Int
    let green: Int
    let blue: Int

    var color: Color {
        Color(.sRGB, red: Double(red) / 255, green: Double(green) / 255, blue: Double(blue) / 255, opacity: 1)
    }
}

/// The tile's background: one top-to-bottom gradient per kind of sky.
enum WeatherSky: CaseIterable {
    case clearDay, clearNight
    case partlyCloudyDay, partlyCloudyNight
    case overcastDay, overcastNight
    case rainDay, rainNight
    case storm

    var top: WeatherRGB {
        switch self {
        case .clearDay: return WeatherRGB(red: 40, green: 112, blue: 214)
        case .clearNight: return WeatherRGB(red: 10, green: 22, blue: 52)
        case .partlyCloudyDay: return WeatherRGB(red: 76, green: 116, blue: 168)
        case .partlyCloudyNight: return WeatherRGB(red: 28, green: 38, blue: 58)
        case .overcastDay: return WeatherRGB(red: 118, green: 140, blue: 170)
        case .overcastNight: return WeatherRGB(red: 40, green: 52, blue: 72)
        case .rainDay: return WeatherRGB(red: 62, green: 84, blue: 112)
        case .rainNight: return WeatherRGB(red: 24, green: 32, blue: 46)
        case .storm: return WeatherRGB(red: 36, green: 42, blue: 56)
        }
    }

    var bottom: WeatherRGB {
        switch self {
        case .clearDay: return WeatherRGB(red: 98, green: 165, blue: 236)
        case .clearNight: return WeatherRGB(red: 32, green: 54, blue: 98)
        case .partlyCloudyDay: return WeatherRGB(red: 132, green: 162, blue: 199)
        case .partlyCloudyNight: return WeatherRGB(red: 50, green: 72, blue: 98)
        case .overcastDay: return WeatherRGB(red: 176, green: 192, blue: 212)
        case .overcastNight: return WeatherRGB(red: 78, green: 92, blue: 116)
        case .rainDay: return WeatherRGB(red: 104, green: 124, blue: 150)
        case .rainNight: return WeatherRGB(red: 52, green: 62, blue: 80)
        case .storm: return WeatherRGB(red: 72, green: 78, blue: 80)
        }
    }

    var gradient: LinearGradient {
        LinearGradient(colors: [top.color, bottom.color], startPoint: .top, endPoint: .bottom)
    }
}

/// What a WMO weather code looks like on the tile.
struct WeatherCondition: Equatable {
    let symbolName: String
    /// A short word or two that fits beside the symbol in an 118 pt tile.
    let word: String
    let sky: WeatherSky

    /// Open-Meteo reports the WMO 4677 code subset (0 clear … 99 thunderstorm with hail). Only clear
    /// and partly cloudy skies change their symbol at night; every kind of sky darkens.
    static func forWeatherCode(_ weatherCode: Int, isDay: Bool) -> WeatherCondition {
        let overcastSky: WeatherSky = isDay ? .overcastDay : .overcastNight
        let rainSky: WeatherSky = isDay ? .rainDay : .rainNight
        switch weatherCode {
        case 0:
            return isDay
                ? WeatherCondition(symbolName: "sun.max.fill", word: "Sunny", sky: .clearDay)
                : WeatherCondition(symbolName: "moon.stars.fill", word: "Clear", sky: .clearNight)
        case 1, 2:
            let word = weatherCode == 1 ? "Mostly Clear" : "Partly Cloudy"
            return isDay
                ? WeatherCondition(symbolName: "cloud.sun.fill", word: word, sky: .partlyCloudyDay)
                : WeatherCondition(symbolName: "cloud.moon.fill", word: word, sky: .partlyCloudyNight)
        case 3:
            return WeatherCondition(symbolName: "cloud.fill", word: "Cloudy", sky: overcastSky)
        case 45, 48:
            return WeatherCondition(symbolName: "cloud.fog.fill", word: "Fog", sky: overcastSky)
        case 51, 53, 55, 56, 57:
            return WeatherCondition(symbolName: "cloud.drizzle.fill", word: "Drizzle", sky: rainSky)
        case 61, 63, 65:
            return WeatherCondition(symbolName: "cloud.rain.fill", word: "Rain", sky: rainSky)
        case 66, 67:
            return WeatherCondition(symbolName: "cloud.rain.fill", word: "Sleet", sky: rainSky)
        case 80, 81, 82:
            return WeatherCondition(symbolName: "cloud.rain.fill", word: "Showers", sky: rainSky)
        case 71, 73, 75, 77, 85, 86:
            return WeatherCondition(symbolName: "cloud.snow.fill", word: "Snow", sky: overcastSky)
        case 95, 96, 99:
            return WeatherCondition(symbolName: "cloud.bolt.rain.fill", word: "Thunderstorm", sky: .storm)
        default:
            // A code outside the documented set: show a plain cloud rather than nothing.
            return WeatherCondition(symbolName: "cloud.fill", word: "Cloudy", sky: overcastSky)
        }
    }
}

// MARK: - Open-Meteo

/// Today's weather at one place, in Celsius as Open-Meteo returns it.
struct WeatherReport: Equatable {
    var temperatureCelsius: Double
    var weatherCode: Int
    var isDay: Bool
    /// Nil when the daily series came back empty or null.
    var highCelsius: Double?
    var lowCelsius: Double?

    var condition: WeatherCondition { .forWeatherCode(weatherCode, isDay: isDay) }
}

/// A place a typed name resolved to.
struct WeatherPlace: Equatable {
    var name: String
    var latitude: Double
    var longitude: Double
}

enum WeatherAPIError: Error, Equatable {
    case malformedReply
    case placeNotFound(String)
    case badStatus(Int)
}

/// The two Open-Meteo endpoints the plugin uses; neither needs a key.
enum WeatherAPI {
    /// Current temperature, code and day flag plus today's high and low, in the place's own time
    /// zone so "today" is the place's today.
    static func forecastURL(latitude: Double, longitude: Double) -> URL? {
        var components = URLComponents(string: "https://api.open-meteo.com/v1/forecast")
        components?.queryItems = [
            URLQueryItem(name: "latitude", value: coordinateText(latitude)),
            URLQueryItem(name: "longitude", value: coordinateText(longitude)),
            URLQueryItem(name: "current", value: "temperature_2m,weather_code,is_day"),
            URLQueryItem(name: "daily", value: "temperature_2m_max,temperature_2m_min"),
            URLQueryItem(name: "timezone", value: "auto"),
            URLQueryItem(name: "forecast_days", value: "1"),
        ]
        return components?.url
    }

    /// The best match for a typed place name.
    static func geocodingURL(placeName: String) -> URL? {
        var components = URLComponents(string: "https://geocoding-api.open-meteo.com/v1/search")
        // `queryItems` leaves "&", "=" and "+" as they are, which would cut a typed name like
        // "Trinidad & Tobago" short; encode those by hand.
        var allowedInQueryValue = CharacterSet.urlQueryAllowed
        allowedInQueryValue.remove(charactersIn: "&=+")
        guard let encodedPlaceName = placeName.addingPercentEncoding(withAllowedCharacters: allowedInQueryValue) else { return nil }
        components?.percentEncodedQueryItems = [
            URLQueryItem(name: "name", value: encodedPlaceName),
            URLQueryItem(name: "count", value: "1"),
        ]
        return components?.url
    }

    /// Four decimals is about 11 m: plenty for weather, and `String(format:)` always writes a dot,
    /// whatever the person's locale uses as a decimal separator.
    static func coordinateText(_ coordinate: Double) -> String {
        String(format: "%.4f", coordinate)
    }

    static func parseForecast(_ data: Data) throws -> WeatherReport {
        let reply: ForecastReply
        do {
            reply = try JSONDecoder().decode(ForecastReply.self, from: data)
        } catch {
            throw WeatherAPIError.malformedReply
        }
        return WeatherReport(
            temperatureCelsius: reply.current.temperature,
            weatherCode: reply.current.weatherCode,
            isDay: reply.current.isDay != 0,
            highCelsius: reply.daily?.maximumTemperatures.first ?? nil,
            lowCelsius: reply.daily?.minimumTemperatures.first ?? nil)
    }

    /// The first result, or `.placeNotFound` when Open-Meteo knows no such place (it then leaves
    /// out `results` altogether).
    static func parseGeocoding(_ data: Data, placeName: String) throws -> WeatherPlace {
        let reply: GeocodingReply
        do {
            reply = try JSONDecoder().decode(GeocodingReply.self, from: data)
        } catch {
            throw WeatherAPIError.malformedReply
        }
        guard let first = reply.results?.first else { throw WeatherAPIError.placeNotFound(placeName) }
        return WeatherPlace(name: first.name, latitude: first.latitude, longitude: first.longitude)
    }

    private struct ForecastReply: Decodable {
        struct Current: Decodable {
            let temperature: Double
            let weatherCode: Int
            let isDay: Int

            enum CodingKeys: String, CodingKey {
                case temperature = "temperature_2m"
                case weatherCode = "weather_code"
                case isDay = "is_day"
            }
        }

        struct Daily: Decodable {
            let maximumTemperatures: [Double?]
            let minimumTemperatures: [Double?]

            enum CodingKeys: String, CodingKey {
                case maximumTemperatures = "temperature_2m_max"
                case minimumTemperatures = "temperature_2m_min"
            }
        }

        let current: Current
        let daily: Daily?
    }

    private struct GeocodingReply: Decodable {
        struct Result: Decodable {
            let name: String
            let latitude: Double
            let longitude: Double
        }

        let results: [Result]?
    }
}
