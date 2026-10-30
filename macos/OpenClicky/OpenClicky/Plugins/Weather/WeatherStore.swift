//
//  WeatherStore.swift
//  OpenClicky
//
//  The Weather plugin's live state: where the person is (CoreLocation, or a place they typed),
//  today's weather there from Open-Meteo, and the unit to show it in. It fetches only while the
//  plugin is on and a tile is on screen: every 30 minutes after a good reply, after 2 after a bad one.
//

import AppKit
import Combine
import CoreLocation
import Foundation

/// What the tile shows.
enum WeatherTileState: Equatable {
    /// Nothing fetched yet (or the plugin is off).
    case loading
    /// "Use my location" is on but CoreLocation may not be used. `canAsk` is true while macOS has
    /// not asked yet, so a click can ask; false once the person said no (or a policy forbids it).
    case needsLocationPermission(canAsk: Bool)
    /// "Use my location" is off and no place has been typed.
    case needsPlace
    case failed(message: String)
    case ready(report: WeatherReport, cityName: String, isAutomaticLocation: Bool)
}

@MainActor
final class WeatherStore: NSObject, ObservableObject {
    static let shared = WeatherStore()

    @Published private(set) var state: WeatherTileState = .loading
    @Published private(set) var unit: WeatherUnit

    private static let refreshInterval: TimeInterval = 30 * 60
    private static let retryInterval: TimeInterval = 2 * 60
    /// A reverse-geocoded city name is reused while the Mac stays within this many metres.
    private static let cityNameReuseDistance: CLLocationDistance = 5_000

    private let defaults: UserDefaults
    private let session: URLSession
    private var watchers = 0
    private var refreshLoopTask: Task<Void, Never>?
    private var lastSuccessfulRefreshDate: Date?
    private var settingsObservation: AnyCancellable?
    /// The settings that decide what is fetched; a change to any of them starts over.
    private var lastFetchSettings: FetchSettings

    /// Created on first use, on the main thread, so its delegate callbacks arrive there too.
    private lazy var locationManager: CLLocationManager = {
        let manager = CLLocationManager()
        manager.desiredAccuracy = kCLLocationAccuracyKilometer
        manager.delegate = self
        return manager
    }()
    private var pendingLocationContinuation: CheckedContinuation<CLLocation, Error>?
    private var lastReverseGeocodedLocation: CLLocation?
    private var lastReverseGeocodedCityName: String?
    private var resolvedPlacesByTypedName: [String: WeatherPlace] = [:]
    /// CoreLocation reports the status once when the manager is created; only a real change restarts.
    private var lastKnownAuthorizationStatus: CLAuthorizationStatus?

    private struct FetchSettings: Equatable {
        var isEnabled: Bool
        var useLocation: Bool
        var placeName: String

        init(defaults: UserDefaults) {
            isEnabled = defaults.bool(forKey: WeatherSettingsKeys.enabled)
            useLocation = defaults.object(forKey: WeatherSettingsKeys.useLocation) as? Bool ?? true
            placeName = (defaults.string(forKey: WeatherSettingsKeys.place) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }

    init(defaults: UserDefaults = .standard, session: URLSession = .shared) {
        self.defaults = defaults
        self.session = session
        self.unit = WeatherUnit.stored(in: defaults)
        self.lastFetchSettings = FetchSettings(defaults: defaults)
        super.init()
        // Settings → Widgets writes straight to UserDefaults; follow it from here so the tile and
        // the settings rows never disagree.
        settingsObservation = NotificationCenter.default
            .publisher(for: UserDefaults.didChangeNotification, object: defaults)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.settingsDidChange() }
    }

    var isEnabled: Bool { lastFetchSettings.isEnabled }

    // MARK: - watching

    /// A tile appeared: the first one fetches (unless a recent reply is still fresh) and starts the clock.
    func beginWatching() {
        watchers += 1
        guard watchers == 1 else { return }
        startRefreshLoop(forceImmediateRefresh: false)
    }

    /// A tile went away: the last one stops the clock. A request already in flight may finish.
    func endWatching() {
        watchers = max(0, watchers - 1)
        guard watchers == 0 else { return }
        refreshLoopTask?.cancel()
        refreshLoopTask = nil
    }

    // MARK: - user actions

    /// The tile was clicked: switch between Celsius and Fahrenheit and remember the choice.
    func toggleUnit() {
        let next = unit.toggled
        unit = next
        defaults.set(next.rawValue, forKey: WeatherSettingsKeys.unit)
    }

    /// Asks macOS for the location, but only ever from a person's action (a click on the tile, the
    /// "Use my location" or "Weather" switch turned on) so the prompt never appears out of nowhere.
    /// Once declined, macOS will not ask again, so this opens the Location Services pane instead.
    func requestLocationPermission() {
        switch locationManager.authorizationStatus {
        case .notDetermined:
            locationManager.requestWhenInUseAuthorization()
        case .denied, .restricted:
            if let paneURL = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices") {
                NSWorkspace.shared.open(paneURL)
            }
        default:
            break
        }
    }

    /// A click on a failed tile: try again now rather than waiting for the retry.
    func retryNow() {
        guard watchers > 0 else { return }
        startRefreshLoop(forceImmediateRefresh: true)
    }

    // MARK: - refreshing

    private func settingsDidChange() {
        let storedUnit = WeatherUnit.stored(in: defaults)
        if storedUnit != unit { unit = storedUnit }

        let next = FetchSettings(defaults: defaults)
        guard next != lastFetchSettings else { return }
        lastFetchSettings = next
        // Another place (or the plugin turned off) makes what is on screen stale.
        lastSuccessfulRefreshDate = nil
        state = .loading
        if watchers > 0 {
            startRefreshLoop(forceImmediateRefresh: true)
        }
    }

    private func startRefreshLoop(forceImmediateRefresh: Bool) {
        refreshLoopTask?.cancel()
        refreshLoopTask = nil
        guard lastFetchSettings.isEnabled else { return }

        // A reply younger than 30 minutes is still good: wait out the rest of the interval.
        var initialDelay: TimeInterval = 0
        if !forceImmediateRefresh, let lastSuccessfulRefreshDate {
            initialDelay = max(0, Self.refreshInterval - Date().timeIntervalSince(lastSuccessfulRefreshDate))
        }

        refreshLoopTask = Task { [weak self] in
            var delay = initialDelay
            while !Task.isCancelled {
                if delay > 0 {
                    try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
                    if Task.isCancelled { return }
                }
                guard let self else { return }
                // Nil means the store is waiting on the person (a permission, a place): stop until
                // they act, which restarts the loop.
                guard let nextDelay = await self.refreshOnce() else { return }
                delay = nextDelay
            }
        }
    }

    /// One fetch. Returns how long to wait before the next, or nil to wait for the person.
    private func refreshOnce() async -> TimeInterval? {
        let fetchSettings = lastFetchSettings
        do {
            let place: WeatherPlace
            let isAutomaticLocation: Bool
            if fetchSettings.useLocation {
                let status = locationManager.authorizationStatus
                lastKnownAuthorizationStatus = status
                guard Self.isAuthorized(status) else {
                    state = .needsLocationPermission(canAsk: status == .notDetermined)
                    return nil
                }
                place = try await currentLocationPlace()
                isAutomaticLocation = true
            } else {
                guard !fetchSettings.placeName.isEmpty else {
                    state = .needsPlace
                    return nil
                }
                place = try await resolvedPlace(named: fetchSettings.placeName)
                isAutomaticLocation = false
            }

            guard let forecastURL = WeatherAPI.forecastURL(latitude: place.latitude, longitude: place.longitude) else {
                throw WeatherAPIError.malformedReply
            }
            let report = try WeatherAPI.parseForecast(try await fetchData(from: forecastURL))
            // The settings may have changed while the request was out; that change started its own loop.
            guard !Task.isCancelled, fetchSettings == lastFetchSettings else { return nil }
            state = .ready(report: report, cityName: place.name, isAutomaticLocation: isAutomaticLocation)
            lastSuccessfulRefreshDate = Date()
            return Self.refreshInterval
        } catch {
            guard !Task.isCancelled, fetchSettings == lastFetchSettings else { return nil }
            // Keep showing an earlier reply through a passing failure; the retry will replace it.
            if case .ready = state {} else {
                state = .failed(message: Self.failureMessage(for: error))
            }
            return Self.retryInterval
        }
    }

    private func fetchData(from url: URL) async throws -> Data {
        let (data, response) = try await session.data(from: url)
        if let httpResponse = response as? HTTPURLResponse, !(200..<300).contains(httpResponse.statusCode) {
            throw WeatherAPIError.badStatus(httpResponse.statusCode)
        }
        return data
    }

    private func resolvedPlace(named placeName: String) async throws -> WeatherPlace {
        if let cached = resolvedPlacesByTypedName[placeName] { return cached }
        guard let geocodingURL = WeatherAPI.geocodingURL(placeName: placeName) else {
            throw WeatherAPIError.placeNotFound(placeName)
        }
        let place = try WeatherAPI.parseGeocoding(try await fetchData(from: geocodingURL), placeName: placeName)
        resolvedPlacesByTypedName[placeName] = place
        return place
    }

    private func currentLocationPlace() async throws -> WeatherPlace {
        let location = try await requestOneLocation()
        let cityName = await cityName(for: location)
        return WeatherPlace(name: cityName, latitude: location.coordinate.latitude, longitude: location.coordinate.longitude)
    }

    private func requestOneLocation() async throws -> CLLocation {
        // Only one request is ever outstanding; an older waiter is let go rather than leaked.
        pendingLocationContinuation?.resume(throwing: CancellationError())
        pendingLocationContinuation = nil
        return try await withCheckedThrowingContinuation { continuation in
            pendingLocationContinuation = continuation
            locationManager.requestLocation()
        }
    }

    /// The city's name, reused while the Mac has not moved far; "My Location" if macOS cannot name it.
    private func cityName(for location: CLLocation) async -> String {
        if let lastReverseGeocodedLocation, let lastReverseGeocodedCityName,
           location.distance(from: lastReverseGeocodedLocation) < Self.cityNameReuseDistance {
            return lastReverseGeocodedCityName
        }
        let placemarks = try? await CLGeocoder().reverseGeocodeLocation(location)
        guard let placemark = placemarks?.first, let name = placemark.locality ?? placemark.name else {
            return "My Location"
        }
        lastReverseGeocodedLocation = location
        lastReverseGeocodedCityName = name
        return name
    }

    private static func failureMessage(for error: Error) -> String {
        if case WeatherAPIError.placeNotFound(let placeName) = error {
            return "Can't find \u{201C}\(placeName)\u{201D}"
        }
        if error is CLError {
            return "Can't find your location"
        }
        if let urlError = error as? URLError, urlError.code == .notConnectedToInternet {
            return "You're offline"
        }
        return "Weather unavailable"
    }

    // MARK: - location callbacks, on the main thread

    private func locationDidArrive(_ location: CLLocation) {
        pendingLocationContinuation?.resume(returning: location)
        pendingLocationContinuation = nil
    }

    private func locationDidFail(_ error: Error) {
        pendingLocationContinuation?.resume(throwing: error)
        pendingLocationContinuation = nil
    }

    /// On macOS a when-in-use grant is reported as `.authorizedAlways`; there is no separate case.
    private static func isAuthorized(_ status: CLAuthorizationStatus) -> Bool {
        status == .authorizedAlways
    }

    private func authorizationDidChange() {
        // The answer to the prompt (or a change in System Settings) decides what the tile can show.
        let status = locationManager.authorizationStatus
        guard status != lastKnownAuthorizationStatus else { return }
        lastKnownAuthorizationStatus = status
        guard lastFetchSettings.useLocation, watchers > 0 else { return }
        startRefreshLoop(forceImmediateRefresh: true)
    }
}

extension WeatherStore: CLLocationManagerDelegate {
    // The manager was created on the main thread, so these arrive there; hop onto the main actor
    // explicitly since the protocol itself is not isolated.
    nonisolated func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        guard let location = locations.last else { return }
        Task { @MainActor in self.locationDidArrive(location) }
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        Task { @MainActor in self.locationDidFail(error) }
    }

    nonisolated func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        Task { @MainActor in self.authorizationDidChange() }
    }
}
