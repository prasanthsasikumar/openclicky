//
//  HandheldListener.swift
//  OpenClicky
//
//  Hears a supported handheld: which one is plugged in, and its input reports decoded into
//  events. The IOKit side sits behind `HandheldReportSource` so everything above it is tested
//  with a fake. Nothing here exists until the plugin is switched on: the plugin makes the source
//  and the listener then, and drops both when it is switched off.
//
//  Listening only. The device is never seized (macOS keeps handling its wheel and touch pad as a
//  mouse) and nothing is ever written to it.
//

import Combine
import Foundation
import IOKit.hid

/// The IOKit side of listening, as the listener needs it.
protocol HandheldReportSource: AnyObject {
    var onDeviceConnected: ((_ vendorID: Int, _ productID: Int) -> Void)? { get set }
    var onDeviceDisconnected: ((_ vendorID: Int, _ productID: Int) -> Void)? { get set }
    /// `reportBytes` includes the leading report id byte.
    var onInputReport: ((_ vendorID: Int, _ productID: Int, _ reportID: UInt32, _ reportBytes: [UInt8]) -> Void)? { get set }
    func start(matching devices: [(vendorID: Int, productID: Int)])
    func stop()
}

@MainActor
final class HandheldListener: ObservableObject {
    @Published private(set) var connectedDevice: HandheldDevice?
    var onEvent: ((HandheldDevice, HandheldEvent) -> Void)?
    var onDisconnect: ((HandheldDevice) -> Void)?
    private(set) var isRunning = false

    private let source: HandheldReportSource
    private let catalog: [HandheldDevice]
    /// The last event's counter in this connection. A report delivered twice carries the same
    /// counter; a device that reconnects may start its counter again, so this is forgotten then.
    private var lastAcceptedSequence: UInt32?

    init(source: HandheldReportSource, catalog: [HandheldDevice] = HandheldDeviceCatalog.all) {
        self.source = source
        self.catalog = catalog
    }

    func start() {
        guard !isRunning else { return }
        isRunning = true
        source.onDeviceConnected = { [weak self] vendorID, productID in
            self?.deviceDidConnect(vendorID: vendorID, productID: productID)
        }
        source.onDeviceDisconnected = { [weak self] vendorID, productID in
            self?.deviceDidDisconnect(vendorID: vendorID, productID: productID)
        }
        source.onInputReport = { [weak self] vendorID, productID, reportID, reportBytes in
            self?.reportDidArrive(vendorID: vendorID, productID: productID, reportID: reportID, reportBytes: reportBytes)
        }
        source.start(matching: catalog.map { (vendorID: $0.vendorID, productID: $0.productID) })
    }

    /// Also reports the connected device as gone, so whatever a held control started is ended.
    func stop() {
        guard isRunning else { return }
        isRunning = false
        source.stop()
        source.onDeviceConnected = nil
        source.onDeviceDisconnected = nil
        source.onInputReport = nil
        forgetConnectedDevice()
    }

    private func deviceDidConnect(vendorID: Int, productID: Int) {
        guard let device = HandheldDeviceCatalog.device(vendorID: vendorID, productID: productID, in: catalog) else { return }
        lastAcceptedSequence = nil
        connectedDevice = device
    }

    private func deviceDidDisconnect(vendorID: Int, productID: Int) {
        guard let connectedDevice, connectedDevice.vendorID == vendorID, connectedDevice.productID == productID else { return }
        forgetConnectedDevice()
    }

    private func forgetConnectedDevice() {
        lastAcceptedSequence = nil
        guard let deviceThatWasConnected = connectedDevice else { return }
        connectedDevice = nil
        onDisconnect?(deviceThatWasConnected)
    }

    private func reportDidArrive(vendorID: Int, productID: Int, reportID: UInt32, reportBytes: [UInt8]) {
        guard let device = HandheldDeviceCatalog.device(vendorID: vendorID, productID: productID, in: catalog) else { return }
        // IOKit can hand over a first report before the matching callback was handled.
        if connectedDevice?.id != device.id {
            lastAcceptedSequence = nil
            connectedDevice = device
        }
        guard let event = device.decode(reportID, reportBytes) else { return }
        guard event.sequence != lastAcceptedSequence else { return }
        lastAcceptedSequence = event.sequence
        onEvent?(device, event)
    }
}

/// `HandheldReportSource` over `IOHIDManager`. The manager is made in `start` and released in
/// `stop`, so a plugin that is off holds no IOKit object at all.
final class IOKitHandheldReportSource: HandheldReportSource {
    var onDeviceConnected: ((_ vendorID: Int, _ productID: Int) -> Void)?
    var onDeviceDisconnected: ((_ vendorID: Int, _ productID: Int) -> Void)?
    var onInputReport: ((_ vendorID: Int, _ productID: Int, _ reportID: UInt32, _ reportBytes: [UInt8]) -> Void)?

    private var manager: IOHIDManager?
    /// One buffer per connected device for IOKit to write its input reports into, freed when the
    /// device goes or listening stops.
    private var reportBuffersByDevice: [IOHIDDevice: (buffer: UnsafeMutablePointer<UInt8>, capacity: Int)] = [:]
    private static let smallestReportBufferCapacity = 64

    func start(matching devices: [(vendorID: Int, productID: Int)]) {
        guard manager == nil else { return }
        let newManager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
        let matchingDictionaries = devices.map { [kIOHIDVendorIDKey: $0.vendorID, kIOHIDProductIDKey: $0.productID] }
        IOHIDManagerSetDeviceMatchingMultiple(newManager, matchingDictionaries as CFArray)

        let unretainedSelf = Unmanaged.passUnretained(self).toOpaque()
        IOHIDManagerRegisterDeviceMatchingCallback(newManager, { context, _, _, device in
            guard let context else { return }
            Unmanaged<IOKitHandheldReportSource>.fromOpaque(context).takeUnretainedValue().deviceDidAppear(device)
        }, unretainedSelf)
        IOHIDManagerRegisterDeviceRemovalCallback(newManager, { context, _, _, device in
            guard let context else { return }
            Unmanaged<IOKitHandheldReportSource>.fromOpaque(context).takeUnretainedValue().deviceDidGo(device)
        }, unretainedSelf)

        // The main run loop: every callback below then arrives on the main thread, where the
        // listener lives. The common modes, like the keyboard's event tap: in the default mode
        // alone a key's release would wait out an open menu or a drag, with the microphone open.
        IOHIDManagerScheduleWithRunLoop(newManager, CFRunLoopGetMain(), CFRunLoopMode.commonModes.rawValue)
        let openResult = IOHIDManagerOpen(newManager, IOOptionBits(kIOHIDOptionsTypeNone))
        manager = newManager
        if openResult == kIOReturnSuccess {
            AppLog.append("handheld: listening")
        } else {
            AppLog.append("handheld: could not open the device (0x\(String(UInt32(bitPattern: openResult), radix: 16)))")
        }
    }

    func stop() {
        guard let manager else { return }
        IOHIDManagerUnscheduleFromRunLoop(manager, CFRunLoopGetMain(), CFRunLoopMode.commonModes.rawValue)
        IOHIDManagerClose(manager, IOOptionBits(kIOHIDOptionsTypeNone))
        for (_, reportBuffer) in reportBuffersByDevice {
            reportBuffer.buffer.deallocate()
        }
        reportBuffersByDevice = [:]
        self.manager = nil
        AppLog.append("handheld: stopped")
    }

    deinit {
        stop()
    }

    private func identifiers(of device: IOHIDDevice) -> (vendorID: Int, productID: Int)? {
        guard let vendorID = IOHIDDeviceGetProperty(device, kIOHIDVendorIDKey as CFString) as? Int,
              let productID = IOHIDDeviceGetProperty(device, kIOHIDProductIDKey as CFString) as? Int else { return nil }
        return (vendorID, productID)
    }

    private func deviceDidAppear(_ device: IOHIDDevice) {
        guard let deviceIdentifiers = identifiers(of: device), reportBuffersByDevice[device] == nil else { return }
        let largestInputReportSize = (IOHIDDeviceGetProperty(device, kIOHIDMaxInputReportSizeKey as CFString) as? Int) ?? 0
        let bufferCapacity = max(Self.smallestReportBufferCapacity, largestInputReportSize)
        let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: bufferCapacity)
        reportBuffersByDevice[device] = (buffer, bufferCapacity)

        IOHIDDeviceRegisterInputReportCallback(device, buffer, bufferCapacity, { context, _, sender, _, reportID, report, reportLength in
            guard let context, let sender else { return }
            let source = Unmanaged<IOKitHandheldReportSource>.fromOpaque(context).takeUnretainedValue()
            let device = Unmanaged<IOHIDDevice>.fromOpaque(sender).takeUnretainedValue()
            let reportBytes = Array(UnsafeBufferPointer(start: report, count: reportLength))
            source.reportDidArrive(from: device, reportID: reportID, reportBytes: reportBytes)
        }, Unmanaged.passUnretained(self).toOpaque())

        let productName = (IOHIDDeviceGetProperty(device, kIOHIDProductKey as CFString) as? String) ?? "device"
        AppLog.append("handheld: \(productName) connected")
        onDeviceConnected?(deviceIdentifiers.vendorID, deviceIdentifiers.productID)
    }

    private func deviceDidGo(_ device: IOHIDDevice) {
        guard let reportBuffer = reportBuffersByDevice.removeValue(forKey: device) else { return }
        reportBuffer.buffer.deallocate()
        guard let deviceIdentifiers = identifiers(of: device) else { return }
        AppLog.append("handheld: device disconnected")
        onDeviceDisconnected?(deviceIdentifiers.vendorID, deviceIdentifiers.productID)
    }

    private func reportDidArrive(from device: IOHIDDevice, reportID: UInt32, reportBytes: [UInt8]) {
        guard let deviceIdentifiers = identifiers(of: device) else { return }
        onInputReport?(deviceIdentifiers.vendorID, deviceIdentifiers.productID, reportID, reportBytes)
    }
}
