//
//  WeatherTile.swift
//  OpenClicky
//
//  The weather tile on the open island's dashboard: the city, the temperature, the sky as a symbol
//  and a word, today's high and low, all on a gradient of the sky it describes. A click switches
//  between Celsius and Fahrenheit.
//

import SwiftUI

struct WeatherTile: View {
    @ObservedObject var store: WeatherStore = .shared

    var body: some View {
        Button(action: handleClick) {
            IslandTile(padding: 12) {
                background
            } content: {
                content
            }
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .accessibilityElement(children: .combine)
        .accessibilityHint(accessibilityHint)
        .onAppear { store.beginWatching() }
        .onDisappear { store.endWatching() }
    }

    /// The click does what the tile is asking for: switch units when it shows the weather, ask for
    /// the location when it needs it, try again after a failure.
    private func handleClick() {
        switch store.state {
        case .ready:
            store.toggleUnit()
        case .needsLocationPermission:
            store.requestLocationPermission()
        case .failed:
            store.retryNow()
        case .loading, .needsPlace:
            break
        }
    }

    private var accessibilityHint: String {
        switch store.state {
        case .ready: return "Switches between Celsius and Fahrenheit"
        case .needsLocationPermission: return "Asks to use your location"
        case .failed: return "Tries again"
        case .loading, .needsPlace: return ""
        }
    }

    @ViewBuilder
    private var background: some View {
        if case .ready(let report, _, _) = store.state {
            report.condition.sky.gradient
        } else {
            IslandDash.tileFill
        }
    }

    @ViewBuilder
    private var content: some View {
        switch store.state {
        case .ready(let report, let cityName, let isAutomaticLocation):
            readyContent(report: report, cityName: cityName, isAutomaticLocation: isAutomaticLocation)
        case .loading:
            placeholder("Loading weather…")
        case .needsLocationPermission(let canAsk):
            placeholder(canAsk ? "Click to use your location" : "Location is off in System Settings")
        case .needsPlace:
            placeholder("Add a place in Settings")
        case .failed(let message):
            placeholder(message)
        }
    }

    private func readyContent(report: WeatherReport, cityName: String, isAutomaticLocation: Bool) -> some View {
        let condition = report.condition
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 3) {
                Text(cityName)
                    .font(.system(size: 12, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.tail)
                if isAutomaticLocation {
                    Image(systemName: "location.fill")
                        .font(.system(size: 8))
                }
            }
            HStack(alignment: .firstTextBaseline, spacing: 1) {
                Text("\(store.unit.wholeDegrees(celsius: report.temperatureCelsius))°")
                    .font(.system(size: 34, weight: .regular))
                    .monospacedDigit()
                Text(store.unit.letter)
                    .font(.system(size: 13, weight: .semibold))
            }
            .lineLimit(1)
            .minimumScaleFactor(0.7)
            Spacer(minLength: 0)
            HStack(spacing: 4) {
                Image(systemName: condition.symbolName)
                    .symbolRenderingMode(.multicolor)
                    .font(.system(size: 13))
                Text(condition.word)
                    .font(.system(size: 11, weight: .semibold))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            if let highLowText = highLowText(report: report) {
                Text(highLowText)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundColor(.white.opacity(0.7))
                    .monospacedDigit()
                    .lineLimit(1)
                    .padding(.top, 2)
            }
        }
        .foregroundColor(.white)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    /// "H:73° L:52°", or nil when the daily series came back empty.
    private func highLowText(report: WeatherReport) -> String? {
        guard let highCelsius = report.highCelsius, let lowCelsius = report.lowCelsius else { return nil }
        return "H:\(store.unit.wholeDegrees(celsius: highCelsius))° L:\(store.unit.wholeDegrees(celsius: lowCelsius))°"
    }

    private func placeholder(_ line: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Image(systemName: "cloud.sun.fill")
                .symbolRenderingMode(.multicolor)
                .font(.system(size: 18))
            Spacer(minLength: 0)
            Text(line)
                .font(.system(size: 11, weight: .medium))
                .foregroundColor(.white.opacity(0.7))
                .fixedSize(horizontal: false, vertical: true)
                .lineLimit(3)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}
