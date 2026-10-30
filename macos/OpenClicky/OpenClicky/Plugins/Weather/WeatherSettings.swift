//
//  WeatherSettings.swift
//  OpenClicky
//
//  The Weather plugin's rows in Settings: the switch, the unit, and where the weather is for.
//

import SwiftUI

extension SettingsCatalog {
    func weatherSettingsItems() -> [SettingsItem] {
        [
            SettingsItem(
                id: "weather.enabled", page: .widgets, section: "",
                title: "Weather", detail: "Today's weather on the island, from Open-Meteo.",
                keywords: ["weather", "temperature", "forecast", "widget", "tile"],
                view: AnyView(WeatherEnabledRow())),
            SettingsItem(
                id: "weather.unit", page: .widgets, section: "Weather",
                title: "Temperature", detail: "Clicking the tile switches it too.",
                keywords: ["weather", "unit", "celsius", "fahrenheit", "degrees"],
                view: AnyView(WeatherUnitRow())),
            // The place field lives in the same item as the location switch so it can appear the
            // moment the switch goes off; a separate item would only show after the window redraws.
            SettingsItem(
                id: "weather.location", page: .widgets, section: "Weather",
                title: "Use my location", detail: "Or type the place you want the weather for.",
                keywords: ["weather", "location", "city", "place", "town", "where"],
                view: AnyView(WeatherLocationRows())),
        ]
    }
}

private struct WeatherEnabledRow: View {
    @AppStorage(WeatherSettingsKeys.enabled) private var isEnabled = false
    @AppStorage(WeatherSettingsKeys.useLocation) private var useLocation = true

    var body: some View {
        SettingsRow(title: "Weather", detail: "Today's weather on the island, from Open-Meteo.") {
            SettingsSwitch(isOn: Binding(get: { isEnabled }, set: { newValue in
                isEnabled = newValue
                // Turning it on is the person's action, so this is the moment to ask for the location.
                if newValue && useLocation { WeatherStore.shared.requestLocationPermission() }
            }))
            .accessibilityLabel("Weather")
        }
    }
}

private struct WeatherUnitRow: View {
    @AppStorage(WeatherSettingsKeys.unit) private var unit: WeatherUnit = .defaultForCurrentLocale

    var body: some View {
        SettingsRow(title: "Temperature", detail: "Clicking the tile switches it too.") {
            Picker("", selection: $unit) {
                Text(WeatherUnit.fahrenheit.title).tag(WeatherUnit.fahrenheit)
                Text(WeatherUnit.celsius.title).tag(WeatherUnit.celsius)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .tint(SettingsLook.accent)
            .pointerCursor()
            .accessibilityLabel("Temperature unit")
        }
    }
}

private struct WeatherLocationRows: View {
    @AppStorage(WeatherSettingsKeys.useLocation) private var useLocation = true
    @AppStorage(WeatherSettingsKeys.place) private var storedPlace = ""
    @AppStorage(WeatherSettingsKeys.enabled) private var isEnabled = false
    /// What is being typed; saved on Return or when the field loses focus, so the store does not
    /// look up every half-typed name.
    @State private var placeDraft = ""
    @FocusState private var isPlaceFieldFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            SettingsRow(title: "Use my location", detail: "Or type the place you want the weather for.") {
                SettingsSwitch(isOn: Binding(get: { useLocation }, set: { newValue in
                    useLocation = newValue
                    if newValue && isEnabled { WeatherStore.shared.requestLocationPermission() }
                }))
                .accessibilityLabel("Use my location")
            }
            if !useLocation {
                Divider()
                SettingsRow(title: "Place", detail: "A city or town, like Lisbon or Austin.") {
                    TextField("City", text: $placeDraft, prompt: Text("City"))
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 180)
                        .focused($isPlaceFieldFocused)
                        .onSubmit(savePlace)
                        .accessibilityLabel("Place")
                }
            }
        }
        .onAppear { placeDraft = storedPlace }
        .onChange(of: isPlaceFieldFocused) { _, isFocused in
            if !isFocused { savePlace() }
        }
        .onDisappear(perform: savePlace)
    }

    private func savePlace() {
        let trimmed = placeDraft.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != storedPlace { storedPlace = trimmed }
    }
}
