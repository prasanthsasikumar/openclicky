//
//  DeviceIdentity.swift
//  OpenClicky
//
//  One stable, anonymous id for this Mac, so the backend can give each Mac one starter allowance
//  however often the app is reinstalled. It is a salted SHA-256 of the hardware UUID: the UUID itself
//  never leaves the Mac. Apple-silicon and Intel Macs both have one.
//

import CryptoKit
import Foundation
import IOKit

enum DeviceIdentity {
    static func hash(platformUUID: String) -> String {
        SHA256.hash(data: Data("openclicky-device-v1:\(platformUUID)".utf8)).map { String(format: "%02x", $0) }.joined()
    }

    static var current: String? {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPlatformExpertDevice"))
        guard service != 0 else { return nil }
        defer { IOObjectRelease(service) }
        guard let uuid = IORegistryEntryCreateCFProperty(service, kIOPlatformUUIDKey as CFString, kCFAllocatorDefault, 0)?
            .takeRetainedValue() as? String, !uuid.isEmpty else { return nil }
        return hash(platformUUID: uuid)
    }
}
