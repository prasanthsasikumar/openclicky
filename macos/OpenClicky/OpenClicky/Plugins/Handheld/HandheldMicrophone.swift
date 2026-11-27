//
//  HandheldMicrophone.swift
//  OpenClicky
//
//  A handheld's own microphone, offered while the device is connected. It stands in for the
//  person's chosen microphone without ever replacing that choice: unplug the device, switch the
//  option or the plugin off, and takes are heard through their own microphone again.
//
//  For dictation and for questions on the classic ⌃⌥ lane. Fast voice and hands-free keep the
//  person's own microphone: there another microphone switches echo cancellation off
//  (`RealtimeVoiceClient`), and a device held near the speakers would hear Clicky's own answers.
//

import Foundation

@MainActor
enum HandheldMicrophone {
    static let useDeviceMicrophoneKey = "openclicky.handheld.useDeviceMicrophone"
    static let rowTitle = "Use its microphone while it is connected"
    static let rowDetail = "For dictation and ⌃⌥ questions. Fast voice and hands-free keep your own microphone."

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
