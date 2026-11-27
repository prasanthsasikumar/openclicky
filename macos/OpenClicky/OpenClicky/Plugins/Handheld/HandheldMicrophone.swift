//
//  HandheldMicrophone.swift
//  OpenClicky
//
//  A handheld's own microphone, offered while the device is connected. It stands in for the
//  person's chosen microphone without ever replacing that choice: unplug the device, switch the
//  option or the plugin off, and takes are heard through their own microphone again.
//
//  For everything that records: dictation, questions, fast voice and hands-free. One microphone
//  for every lane, the same as the person's own choice in Settings (asked for on 2026-11-27: a
//  microphone that hears some lanes and not others is confusing). On fast voice any microphone
//  other than the Mac's default records without echo cancellation (`RealtimeVoiceClient`).
//

import Foundation

@MainActor
enum HandheldMicrophone {
    static let useDeviceMicrophoneKey = "openclicky.handheld.useDeviceMicrophone"
    static let rowTitle = "Use its microphone while it is connected"
    static let rowDetail = "For everything OpenClicky listens to: dictation, questions, fast voice and hands-free."

    /// The microphone takes and questions record from: the handheld's while the plugin is on, the
    /// option is on, the device is connected and its microphone is present; otherwise the
    /// person's own choice, untouched (nil means the Mac's default input).
    static func effectiveUID(personsChoiceUID: String?, pluginIsOn: Bool, optionIsOn: Bool,
                             connectedDeviceMicrophoneName: String?, availableMicrophones: [MicrophoneDevice]) -> String? {
        guard pluginIsOn, optionIsOn, let connectedDeviceMicrophoneName,
              let deviceMicrophone = availableMicrophones.first(where: { $0.name == connectedDeviceMicrophoneName }) else {
            return personsChoiceUID
        }
        return deviceMicrophone.uid
    }

    /// The same, from the plugin as it is now. With the plugin off the microphones are not even listed.
    static func currentUID(personsChoiceUID: String?, plugin: HandheldPlugin = .shared) -> String? {
        guard plugin.isEnabled, plugin.usesDeviceMicrophone, let microphoneName = plugin.connectedDevice?.microphoneName else {
            return personsChoiceUID
        }
        return effectiveUID(personsChoiceUID: personsChoiceUID, pluginIsOn: true, optionIsOn: true,
                            connectedDeviceMicrophoneName: microphoneName, availableMicrophones: MicrophoneDevices.available())
    }
}
