//
//  WeatherStoreTests.swift
//  OpenClickyTests
//
//  The Weather plugin's pure parts: how a WMO code looks, the unit and its conversion, the
//  Open-Meteo replies and the request URLs.
//

import Foundation
import Testing
@testable import OpenClicky

struct WeatherStoreTests {
    // MARK: - codes

    @Test func clearAndPartlyCloudySkiesChangeTheirSymbolAtNight() {
        #expect(WeatherCondition.forWeatherCode(0, isDay: true) == WeatherCondition(symbolName: "sun.max.fill", word: "Sunny", sky: .clearDay))
        #expect(WeatherCondition.forWeatherCode(0, isDay: false) == WeatherCondition(symbolName: "moon.stars.fill", word: "Clear", sky: .clearNight))
        #expect(WeatherCondition.forWeatherCode(2, isDay: true).symbolName == "cloud.sun.fill")
        #expect(WeatherCondition.forWeatherCode(2, isDay: true).sky == .partlyCloudyDay)
        #expect(WeatherCondition.forWeatherCode(1, isDay: false).symbolName == "cloud.moon.fill")
        #expect(WeatherCondition.forWeatherCode(1, isDay: false).sky == .partlyCloudyNight)
    }

    @Test func eachFamilyOfCodesHasItsSymbolAndSky() {
        let expectations: [(code: Int, symbol: String, daySky: WeatherSky, nightSky: WeatherSky)] = [
            (3, "cloud.fill", .overcastDay, .overcastNight),
            (45, "cloud.fog.fill", .overcastDay, .overcastNight),
            (48, "cloud.fog.fill", .overcastDay, .overcastNight),
            (51, "cloud.drizzle.fill", .rainDay, .rainNight),
            (57, "cloud.drizzle.fill", .rainDay, .rainNight),
            (61, "cloud.rain.fill", .rainDay, .rainNight),
            (67, "cloud.rain.fill", .rainDay, .rainNight),
            (82, "cloud.rain.fill", .rainDay, .rainNight),
            (71, "cloud.snow.fill", .overcastDay, .overcastNight),
            (86, "cloud.snow.fill", .overcastDay, .overcastNight),
            (95, "cloud.bolt.rain.fill", .storm, .storm),
            (99, "cloud.bolt.rain.fill", .storm, .storm),
        ]
        for expectation in expectations {
            let day = WeatherCondition.forWeatherCode(expectation.code, isDay: true)
            let night = WeatherCondition.forWeatherCode(expectation.code, isDay: false)
            #expect(day.symbolName == expectation.symbol, "code \(expectation.code)")
            #expect(night.symbolName == expectation.symbol, "code \(expectation.code)")
            #expect(day.sky == expectation.daySky, "code \(expectation.code)")
            #expect(night.sky == expectation.nightSky, "code \(expectation.code)")
            #expect(!day.word.isEmpty)
        }
    }

    @Test func anUnknownCodeStillShowsACloud() {
        #expect(WeatherCondition.forWeatherCode(42, isDay: true).symbolName == "cloud.fill")
        #expect(WeatherCondition.forWeatherCode(-1, isDay: false).sky == .overcastNight)
    }

    @Test func theSkyGradientsMatchTheDesign() {
        #expect(WeatherSky.clearDay.top == WeatherRGB(red: 40, green: 112, blue: 214))
        #expect(WeatherSky.clearDay.bottom == WeatherRGB(red: 98, green: 165, blue: 236))
        #expect(WeatherSky.storm.top == WeatherRGB(red: 36, green: 42, blue: 56))
        #expect(WeatherSky.storm.bottom == WeatherRGB(red: 72, green: 78, blue: 80))
        #expect(WeatherSky.rainNight.bottom == WeatherRGB(red: 52, green: 62, blue: 80))
    }

    // MARK: - unit

    @Test func celsiusConvertsToFahrenheitAndRoundsToWholeDegrees() {
        #expect(WeatherUnit.fahrenheit.convert(celsius: 0) == 32)
        #expect(WeatherUnit.fahrenheit.convert(celsius: 100) == 212)
        #expect(WeatherUnit.fahrenheit.convert(celsius: -40) == -40)
        #expect(WeatherUnit.celsius.convert(celsius: 21.4) == 21.4)
        #expect(WeatherUnit.fahrenheit.wholeDegrees(celsius: 22.8) == 73)
        #expect(WeatherUnit.celsius.wholeDegrees(celsius: 21.5) == 22)
        #expect(WeatherUnit.celsius.wholeDegrees(celsius: -2.5) == -3)
        #expect(WeatherUnit.celsius.toggled == .fahrenheit && WeatherUnit.fahrenheit.toggled == .celsius)
        #expect(WeatherUnit.celsius.letter == "C" && WeatherUnit.fahrenheit.letter == "F")
    }

    @Test func theLocaleDecidesTheDefaultUnit() {
        #expect(WeatherUnit.defaultUnit(for: Locale(identifier: "en_US")) == .fahrenheit)
        #expect(WeatherUnit.defaultUnit(for: Locale(identifier: "en_GB")) == .celsius)
        #expect(WeatherUnit.defaultUnit(for: Locale(identifier: "de_DE")) == .celsius)
    }

    @Test func aStoredUnitWinsOverTheLocale() throws {
        let suiteName = "WeatherStoreTests.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        #expect(WeatherUnit.stored(in: defaults) == WeatherUnit.defaultForCurrentLocale)
        defaults.set("fahrenheit", forKey: WeatherSettingsKeys.unit)
        #expect(WeatherUnit.stored(in: defaults) == .fahrenheit)
        defaults.set("kelvin", forKey: WeatherSettingsKeys.unit)
        #expect(WeatherUnit.stored(in: defaults) == WeatherUnit.defaultForCurrentLocale)
    }

    // MARK: - replies

    @Test func aForecastReplyBecomesAReport() throws {
        let reply = """
        {"latitude":52.52,"longitude":13.419998,"generationtime_ms":0.05,"utc_offset_seconds":3600,
         "timezone":"Europe/Berlin","timezone_abbreviation":"GMT+1","elevation":38.0,
         "current_units":{"time":"iso8601","interval":"seconds","temperature_2m":"°C","weather_code":"wmo code","is_day":""},
         "current":{"time":"2026-10-30T14:15","interval":900,"temperature_2m":12.6,"weather_code":3,"is_day":1},
         "daily_units":{"time":"iso8601","temperature_2m_max":"°C","temperature_2m_min":"°C"},
         "daily":{"time":["2026-10-30"],"temperature_2m_max":[14.1],"temperature_2m_min":[6.8]}}
        """
        let report = try WeatherAPI.parseForecast(Data(reply.utf8))
        #expect(report == WeatherReport(temperatureCelsius: 12.6, weatherCode: 3, isDay: true, highCelsius: 14.1, lowCelsius: 6.8))
        #expect(report.condition.symbolName == "cloud.fill")
    }

    @Test func aNightReplyWithoutDailyFiguresStillParses() throws {
        let reply = """
        {"current":{"time":"2026-10-30T23:00","interval":900,"temperature_2m":-1.2,"weather_code":0,"is_day":0},
         "daily":{"time":["2026-10-30"],"temperature_2m_max":[null],"temperature_2m_min":[null]}}
        """
        let report = try WeatherAPI.parseForecast(Data(reply.utf8))
        #expect(!report.isDay && report.highCelsius == nil && report.lowCelsius == nil)
        #expect(report.condition.symbolName == "moon.stars.fill")
    }

    @Test func aMalformedForecastIsReportedAsSuch() {
        #expect(throws: WeatherAPIError.malformedReply) {
            try WeatherAPI.parseForecast(Data(#"{"error":true,"reason":"Latitude must be in range"}"#.utf8))
        }
    }

    @Test func aGeocodingReplyGivesThePlace() throws {
        let reply = """
        {"results":[{"id":2950159,"name":"Berlin","latitude":52.52437,"longitude":13.41053,"elevation":74.0,
          "feature_code":"PPLC","country_code":"DE","timezone":"Europe/Berlin","country":"Germany"}],
         "generationtime_ms":0.6}
        """
        let place = try WeatherAPI.parseGeocoding(Data(reply.utf8), placeName: "berlin")
        #expect(place == WeatherPlace(name: "Berlin", latitude: 52.52437, longitude: 13.41053))
    }

    @Test func aPlaceNobodyKnowsIsNotFound() {
        #expect(throws: WeatherAPIError.placeNotFound("Atlantis")) {
            try WeatherAPI.parseGeocoding(Data(#"{"generationtime_ms":0.3}"#.utf8), placeName: "Atlantis")
        }
    }

    // MARK: - URLs

    @Test func theForecastURLAsksForTodayInCelsius() throws {
        let url = try #require(WeatherAPI.forecastURL(latitude: 52.52437, longitude: -13.41053))
        let components = try #require(URLComponents(url: url, resolvingAgainstBaseURL: false))
        #expect(components.scheme == "https" && components.host == "api.open-meteo.com" && components.path == "/v1/forecast")
        let items = Dictionary(uniqueKeysWithValues: (components.queryItems ?? []).map { ($0.name, $0.value ?? "") })
        #expect(items == [
            "latitude": "52.5244",
            "longitude": "-13.4105",
            "current": "temperature_2m,weather_code,is_day",
            "daily": "temperature_2m_max,temperature_2m_min",
            "timezone": "auto",
            "forecast_days": "1",
        ])
    }

    @Test func theGeocodingURLEscapesThePlaceName() throws {
        let url = try #require(WeatherAPI.geocodingURL(placeName: "São Paulo & Co"))
        let components = try #require(URLComponents(url: url, resolvingAgainstBaseURL: false))
        #expect(components.host == "geocoding-api.open-meteo.com" && components.path == "/v1/search")
        #expect(components.queryItems == [URLQueryItem(name: "name", value: "São Paulo & Co"), URLQueryItem(name: "count", value: "1")])
        #expect(!url.absoluteString.contains(" "))
    }
}
