//
//  HandheldPlugin.swift
//  OpenClicky
//
//  Handheld devices as a plugin: for the few people who own a supported push-to-talk handheld,
//  its buttons drive OpenClicky. Off unless switched on in Settings → Island › More, and while it
//  is off nothing of it exists: no HID manager, no timer, no prompt. Everything it needs lives in
//  this folder; the rest of the app knows it through `HandheldActionHost` and one line in
//  `CompanionManager.start`.
//

import AppKit
import Combine
import Foundation

@MainActor
final class HandheldPlugin: ObservableObject {
    static let enabledKey = "openclicky.handheld.enabled"
    private static let lastSeenDeviceKey = "openclicky.handheld.lastDevice"
    /// The handheld's own app. With it running too, each press is acted on twice.
    static let makerAppBundleIdentifier = "ai.whisprompt.desktop"

    static let shared = HandheldPlugin(
        defaults: .standard,
        makeSource: { IOKitHandheldReportSource() },
        makerAppIsRunning: { !NSRunningApplication.runningApplications(withBundleIdentifier: makerAppBundleIdentifier).isEmpty }
    )

    @Published private(set) var isEnabled: Bool
    @Published private(set) var connectedDevice: HandheldDevice?
    /// The device whose bindings the page shows while none is connected.
    @Published private(set) var lastSeenDevice: HandheldDevice?
    /// The bindings of the connected device, or of the last one seen.
    @Published private(set) var bindings = HandheldBindings()

    var makerAppIsAlsoRunning: Bool { makerAppIsRunning() }
    /// The device the page is about: the connected one, else the last one seen.
    var deviceShown: HandheldDevice? { connectedDevice ?? lastSeenDevice }

    private let defaults: UserDefaults
    private let bindingsStore: HandheldBindingsStore
    private let makeSource: () -> HandheldReportSource
    private let makerAppIsRunning: () -> Bool
    private weak var host: HandheldActionHost?
    private var listener: HandheldListener?
    private var actionRunner: HandheldActionRunner?

    init(defaults: UserDefaults, makeSource: @escaping () -> HandheldReportSource, makerAppIsRunning: @escaping () -> Bool) {
        self.defaults = defaults
        self.bindingsStore = HandheldBindingsStore(defaults: defaults)
        self.makeSource = makeSource
        self.makerAppIsRunning = makerAppIsRunning
        isEnabled = defaults.bool(forKey: Self.enabledKey)
        if let lastSeenDeviceID = defaults.string(forKey: Self.lastSeenDeviceKey),
           let device = HandheldDeviceCatalog.all.first(where: { $0.id == lastSeenDeviceID }) {
            lastSeenDevice = device
            bindings = bindingsStore.bindings(for: device)
        }
    }

    /// Called once as the app starts. Listening begins only if the plugin is switched on.
    func attach(to host: HandheldActionHost) {
        self.host = host
        if isEnabled { startListening() }
    }

    func setEnabled(_ enabled: Bool) {
        guard enabled != isEnabled else { return }
        isEnabled = enabled
        defaults.set(enabled, forKey: Self.enabledKey)
        if enabled {
            startListening()
        } else {
            stopListening()
        }
    }

    func setAction(_ action: HandheldAction, controlID: String, gesture: HandheldGesture) {
        guard let device = deviceShown else { return }
        bindings.set(action, controlID: controlID, gesture: gesture)
        bindingsStore.save(bindings, for: device)
    }

    func resetBindings() {
        guard let device = deviceShown else { return }
        bindingsStore.reset(device)
        bindings = bindingsStore.bindings(for: device)
    }

    private func startListening() {
        guard listener == nil, let host else { return }
        let newActionRunner = HandheldActionRunner(host: host)
        let newListener = HandheldListener(source: makeSource())
        newListener.onEvent = { [weak self] device, event in
            self?.deviceWasSeen(device)
            guard let self else { return }
            newActionRunner.handle(event, bindings: self.bindings)
        }
        newListener.onDisconnect = { [weak self] _ in
            newActionRunner.releaseEverythingHeld(reason: "device disconnected")
            self?.connectedDevice = nil
        }
        listener = newListener
        actionRunner = newActionRunner
        newListener.$connectedDevice
            .compactMap { $0 }
            .sink { [weak self] device in self?.deviceWasSeen(device) }
            .store(in: &listenerSubscriptions)
        newListener.start()
    }

    private var listenerSubscriptions: Set<AnyCancellable> = []

    private func stopListening() {
        actionRunner?.releaseEverythingHeld(reason: "plugin switched off")
        listener?.stop()
        listenerSubscriptions.removeAll()
        listener = nil
        actionRunner = nil
        connectedDevice = nil
    }

    private func deviceWasSeen(_ device: HandheldDevice) {
        guard connectedDevice?.id != device.id else { return }
        connectedDevice = device
        if lastSeenDevice?.id != device.id {
            lastSeenDevice = device
            defaults.set(device.id, forKey: Self.lastSeenDeviceKey)
        }
        bindings = bindingsStore.bindings(for: device)
    }
}
