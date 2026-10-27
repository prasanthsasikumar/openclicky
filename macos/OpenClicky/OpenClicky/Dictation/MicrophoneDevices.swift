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

/// Plain CoreAudio calls and no app state, so the realtime client's audio queue can use it too.
nonisolated enum MicrophoneDevices {

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

    /// The name of the microphone a take would record from right now, for "Automatic (…)".
    static func effectiveInputName(preferredUID: String?) -> String? {
        let connected = allDeviceIDs().compactMap { deviceID in
            stringProperty(of: deviceID, kAudioDevicePropertyDeviceUID).map { ConnectedInput(deviceID: deviceID, uid: $0) }
        }
        let choice = deviceToSelect(preferredUID: preferredUID, connected: connected, defaultDeviceID: defaultInputDeviceID())
        guard let deviceID = choice.deviceID else { return nil }
        return stringProperty(of: deviceID, kAudioObjectPropertyName)
    }

    /// True when the built-in microphone is connected and is not what a take would use now.
    static func builtInMicrophoneIsAnAlternative(preferredUID: String?) -> Bool {
        let devices = available()
        guard let builtIn = devices.first(where: { $0.uid == builtInMicrophoneUID }) else { return false }
        return effectiveInputName(preferredUID: preferredUID) != builtIn.name
    }

    static func defaultInputName() -> String? {
        guard let deviceID = defaultInputDeviceID() else { return nil }
        return stringProperty(of: deviceID, kAudioObjectPropertyName)
    }

    /// Points the engine's input at the device with `uid` when that is not the system default, and
    /// otherwise leaves a fresh engine alone. A fresh engine's input is not the microphone itself but
    /// a device AVAudioEngine builds to follow the default; setting the default microphone on it
    /// anyway reconfigures the engine, and on a tester's Mac (2026-10-27, 44.1 kHz built-in mic) the
    /// engine then delivered two silent buffers per take and stopped, so every take and the mic test
    /// heard nothing. An engine that was once given a chosen device stays on it, so a caller keeping
    /// one across takes builds a new engine to go back to the default (`differsFromDefault`).
    /// Returns false when no device could be selected (logged).
    @discardableResult
    static func apply(preferredUID: String?, to engine: AVAudioEngine) -> Bool {
        let connected = allDeviceIDs().compactMap { deviceID in
            stringProperty(of: deviceID, kAudioDevicePropertyDeviceUID).map { ConnectedInput(deviceID: deviceID, uid: $0) }
        }
        let defaultDeviceID = defaultInputDeviceID()
        let choice = deviceToSelect(preferredUID: preferredUID, connected: connected, defaultDeviceID: defaultDeviceID)
        if choice.chosenUIDIsMissing, let preferredUID {
            AppLog.append("microphone: \(preferredUID) is not connected, using the mac default")
        }
        guard let deviceID = choice.deviceID else {
            AppLog.append("microphone: no input device to select")
            return false
        }
        guard choiceDiffersFromDefault(choice: choice, defaultDeviceID: defaultDeviceID) else { return true }
        guard let audioUnit = engine.inputNode.audioUnit else {
            AppLog.append("microphone: the engine has no input unit")
            return false
        }
        var device = deviceID
        let status = AudioUnitSetProperty(audioUnit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &device, UInt32(MemoryLayout<AudioDeviceID>.size))
        if status != noErr {
            AppLog.append("microphone: could not select \(preferredUID ?? "the mac default") (\(status))")
            return false
        }
        return true
    }

    /// True when a take records from a device other than the Mac's default input: a connected
    /// chosen one, or the built-in microphone standing in for the headphone jack. Echo cancellation
    /// can only record from the default, so the realtime lane asks this before turning it on.
    static func choiceDiffersFromDefault(preferredUID: String?) -> Bool {
        let connected = allDeviceIDs().compactMap { deviceID in
            stringProperty(of: deviceID, kAudioDevicePropertyDeviceUID).map { ConnectedInput(deviceID: deviceID, uid: $0) }
        }
        let defaultDeviceID = defaultInputDeviceID()
        let choice = deviceToSelect(preferredUID: preferredUID, connected: connected, defaultDeviceID: defaultDeviceID)
        return choiceDiffersFromDefault(choice: choice, defaultDeviceID: defaultDeviceID)
    }

    static func choiceDiffersFromDefault(choice: (deviceID: AudioDeviceID?, chosenUIDIsMissing: Bool),
                                         defaultDeviceID: AudioDeviceID?) -> Bool {
        guard let chosenDeviceID = choice.deviceID, !choice.chosenUIDIsMissing else { return false }
        return chosenDeviceID != defaultDeviceID
    }

    struct ConnectedInput: Equatable {
        let deviceID: AudioDeviceID
        let uid: String
    }

    /// The headphone jack's input. macOS lists it, and often makes it the default input, as soon as
    /// anything is plugged into the jack, speakers and plain headphones included, which have no
    /// microphone: it records silence, and the offline recogniser turned that silence into "no".
    static let headphoneJackInputUID = "BuiltInHeadphoneInputDevice"
    static let builtInMicrophoneUID = "BuiltInMicrophoneDevice"

    /// Which device a take records from: the chosen one when it is connected, else the default,
    /// except that a default which is the headphone jack gives way to the built-in microphone
    /// (choose the jack explicitly for a headset with a microphone).
    /// `chosenUIDIsMissing` is true when a device was chosen but is not among `connected`.
    static func deviceToSelect(preferredUID: String?, connected: [ConnectedInput],
                               defaultDeviceID: AudioDeviceID?) -> (deviceID: AudioDeviceID?, chosenUIDIsMissing: Bool) {
        guard let preferredUID, !preferredUID.isEmpty else {
            let defaultUID = connected.first(where: { $0.deviceID == defaultDeviceID })?.uid
            if defaultUID == headphoneJackInputUID, let builtIn = connected.first(where: { $0.uid == builtInMicrophoneUID }) {
                return (builtIn.deviceID, false)
            }
            return (defaultDeviceID, false)
        }
        if let match = connected.first(where: { $0.uid == preferredUID }) { return (match.deviceID, false) }
        return (defaultDeviceID, true)
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
