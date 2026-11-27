//
//  HandheldDevice.swift
//  OpenClicky
//
//  A supported handheld push-to-talk device, described as data: how to recognise it on USB, what
//  its controls are called, which gestures each control reports, and how one of its input reports
//  is read as an event. Supporting another device means adding one description under `Devices/`
//  and listing it in the catalog; nothing else in the plugin names a device.
//

import Foundation

/// What a control can report. A device works gestures out by itself (how long a long press is,
/// how close two presses must be), so the plugin never times anything.
enum HandheldGesture: String, Codable, CaseIterable {
    /// The control went down. Reported at once, before the device knows which gesture it will be.
    case down
    case single
    case double
    case longStart
    case longEnd
}

struct HandheldControl: Equatable, Identifiable {
    /// Stable; stored in the bindings.
    let id: String
    let name: String
    /// Only the gestures this control really sends.
    let gestures: [HandheldGesture]
}

struct HandheldEvent: Equatable {
    let controlID: String
    let gesture: HandheldGesture
    /// The device's own event counter, used to drop a report delivered twice.
    let sequence: UInt32
}

struct HandheldDevice: Identifiable {
    /// Stable; the bindings are stored under it.
    let id: String
    let displayName: String
    let vendorID: Int
    let productID: Int
    /// The name macOS gives the device's microphone, nil when it has none.
    let microphoneName: String?
    let controls: [HandheldControl]
    let defaultBindings: HandheldBindings
    /// Reads one input report (its bytes include the leading report id) as an event, or nil for a
    /// report that is not a key event or that names a key or gesture this description does not list.
    let decode: (_ reportID: UInt32, _ reportBytes: [UInt8]) -> HandheldEvent?
    /// For a 3D model of the device (`HandheldDeviceView`): the name of the model's part that is
    /// each control, by control id. Empty for a device with no known model.
    var modelPartNameByControlID: [String: String] = [:]
    /// Where the 3D model can be downloaded from when the person asks for it; nil when there is none.
    var modelDownload: HandheldModelDownload?

    func control(withID controlID: String) -> HandheldControl? {
        controls.first { $0.id == controlID }
    }
}

enum HandheldDeviceCatalog {
    static let all: [HandheldDevice] = [WhisPromptHandheld.device]

    static func device(vendorID: Int, productID: Int, in catalog: [HandheldDevice] = all) -> HandheldDevice? {
        catalog.first { $0.vendorID == vendorID && $0.productID == productID }
    }
}
