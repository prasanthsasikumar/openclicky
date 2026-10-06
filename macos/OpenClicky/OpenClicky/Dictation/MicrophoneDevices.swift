//
//  MicrophoneDevices.swift
//  OpenClicky
//
//  The microphones this Mac has, by name and UID, and how a chosen one is handed to an
//  `AVAudioEngine`: the engine's input node is an AUHAL unit, and its current device can be set
//  directly, which is the one way to record from a microphone that is not the system default.
//

import AVFoundation
import CoreAudio
import Foundation

struct MicrophoneDevice: Identifiable, Equatable {
    let uid: String
    let name: String
    let isDefault: Bool
    var id: String { uid }
}

enum MicrophoneDevices {

    /// Every input device, the system default first.
    static func available() -> [MicrophoneDevice] {
        let defaultID = defaultInputDeviceID()
        return allDeviceIDs().compactMap { deviceID -> MicrophoneDevice? in
            guard inputChannelCount(of: deviceID) > 0, let uid = stringProperty(of: deviceID, kAudioDevicePropertyDeviceUID),
                  let name = stringProperty(of: deviceID, kAudioObjectPropertyName) else { return nil }
            return MicrophoneDevice(uid: uid, name: name, isDefault: deviceID == defaultID)
        }
        .sorted { ($0.isDefault ? 0 : 1, $0.name) < ($1.isDefault ? 0 : 1, $1.name) }
    }

    static func defaultInputName() -> String? {
        guard let deviceID = defaultInputDeviceID() else { return nil }
        return stringProperty(of: deviceID, kAudioObjectPropertyName)
    }

    /// Points the engine's input at the device with `uid`; nil or an unknown UID leaves the default.
    static func apply(preferredUID: String?, to engine: AVAudioEngine) {
        guard let preferredUID, !preferredUID.isEmpty,
              let deviceID = allDeviceIDs().first(where: { stringProperty(of: $0, kAudioDevicePropertyDeviceUID) == preferredUID }),
              let audioUnit = engine.inputNode.audioUnit else { return }
        var device = deviceID
        let status = AudioUnitSetProperty(audioUnit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &device, UInt32(MemoryLayout<AudioDeviceID>.size))
        if status != noErr { AppLog.append("microphone: could not select \(preferredUID) (\(status))") }
    }

    // MARK: CoreAudio

    private static func allDeviceIDs() -> [AudioDeviceID] {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    private static func defaultInputDeviceID() -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var deviceID: AudioDeviceID = 0
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &deviceID) == noErr else { return nil }
        return deviceID
    }

    private static func inputChannelCount(of deviceID: AudioDeviceID) -> Int {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreamConfiguration, mScope: kAudioDevicePropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &address, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let bufferList = UnsafeMutablePointer<AudioBufferList>.allocate(capacity: Int(size))
        defer { bufferList.deallocate() }
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, bufferList) == noErr else { return 0 }
        return UnsafeMutableAudioBufferListPointer(bufferList).reduce(0) { $0 + Int($1.mNumberChannels) }
    }

    private static func stringProperty(of deviceID: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }
}
