//
//  HearingSettingsTests.swift
//  OpenClickyTests
//
//  One setting, every reader: the microphone chosen in settings reaches every engine that opens
//  the microphone (F-13, F-21), the styles page's language reaches every dictation engine (F-16),
//  and the hearing code logs its failures to the file the person can send (F-37) — the last is a
//  source rule in scripts/check-source-rules.sh, because a test reading ~/Documents prompts for access.
//

import CoreAudio
import Foundation
import Testing
@testable import OpenClicky

struct HearingSettingsTests {

    // MARK: microphone

    private let builtIn = MicrophoneDevices.ConnectedInput(deviceID: 41, uid: "BuiltInMicrophoneDevice")
    private let usb = MicrophoneDevices.ConnectedInput(deviceID: 77, uid: "AppleUSBAudioEngine:Blue:Yeti")

    @Test func aConnectedChosenMicrophoneIsSelected() {
        let choice = MicrophoneDevices.deviceToSelect(preferredUID: usb.uid, connected: [builtIn, usb], defaultDeviceID: builtIn.deviceID)
        #expect(choice.deviceID == usb.deviceID)
        #expect(!choice.chosenUIDIsMissing)
    }

    /// "mac default" after a chosen microphone: the default is selected explicitly, so an engine
    /// kept for the app's life leaves the old device.
    @Test func macDefaultSelectsTheDefaultDeviceExplicitly() {
        let choice = MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [builtIn, usb], defaultDeviceID: usb.deviceID)
        #expect(choice.deviceID == usb.deviceID)
        #expect(!choice.chosenUIDIsMissing)
        #expect(MicrophoneDevices.deviceToSelect(preferredUID: "", connected: [builtIn], defaultDeviceID: builtIn.deviceID).deviceID == builtIn.deviceID)
    }

    /// Speakers in the headphone jack make its input the Mac's default; it hears nothing, so
    /// "automatic" records from the built-in microphone instead. Chosen explicitly, the jack is used.
    @Test func automaticSkipsTheHeadphoneJackForTheBuiltInMicrophone() {
        let jack = MicrophoneDevices.ConnectedInput(deviceID: 91, uid: MicrophoneDevices.headphoneJackInputUID)
        let builtInMicrophone = MicrophoneDevices.ConnectedInput(deviceID: 92, uid: MicrophoneDevices.builtInMicrophoneUID)
        #expect(MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [jack, builtInMicrophone], defaultDeviceID: jack.deviceID).deviceID == builtInMicrophone.deviceID)
        #expect(MicrophoneDevices.deviceToSelect(preferredUID: jack.uid, connected: [jack, builtInMicrophone], defaultDeviceID: jack.deviceID).deviceID == jack.deviceID)
        // A Mac without a built-in microphone (a mini, a Studio) keeps the jack.
        #expect(MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [jack, usb], defaultDeviceID: jack.deviceID).deviceID == jack.deviceID)
    }

    /// A closed lid switches the built-in microphone off in hardware (Apple's microphone
    /// disconnect), and macOS still falls back to it when a headset drops: three silent takes on
    /// 2026-11-07 with a USB receiver sitting right there. "Automatic" then records from another
    /// real microphone when one is connected; a virtual device (a meeting app's loopback) is not one.
    @Test func automaticUsesAnotherMicrophoneWhileTheLidIsClosed() {
        let virtual = MicrophoneDevices.ConnectedInput(deviceID: 120, uid: "TeamsAudioDevice", isPhysicalMicrophone: false)
        let speakers = MicrophoneDevices.ConnectedInput(deviceID: 85, uid: "BuiltInSpeakerDevice", isPhysicalMicrophone: false)
        let closed = MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [speakers, virtual, builtIn, usb], defaultDeviceID: builtIn.deviceID, lidIsClosed: true)
        #expect(closed.deviceID == usb.deviceID)
        #expect(closed.standsInForClosedLid)
        // Lid open: the built-in microphone works and stays.
        let open = MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [builtIn, usb], defaultDeviceID: builtIn.deviceID, lidIsClosed: false)
        #expect(open.deviceID == builtIn.deviceID)
        #expect(!open.standsInForClosedLid)
        // Nothing real to stand in: the built-in microphone is kept, and the caller can say why it is silent.
        #expect(MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [speakers, virtual, builtIn], defaultDeviceID: builtIn.deviceID, lidIsClosed: true).deviceID == builtIn.deviceID)
        // The headphone jack hears nothing either, so it is never the stand-in.
        let jack = MicrophoneDevices.ConnectedInput(deviceID: 91, uid: MicrophoneDevices.headphoneJackInputUID)
        #expect(MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [jack, builtIn], defaultDeviceID: builtIn.deviceID, lidIsClosed: true).deviceID == builtIn.deviceID)
        // A chosen microphone is still the chosen one, lid or no lid.
        #expect(MicrophoneDevices.deviceToSelect(preferredUID: builtIn.uid, connected: [builtIn, usb], defaultDeviceID: usb.deviceID, lidIsClosed: true).deviceID == builtIn.deviceID)
        // Another default than the built-in microphone is unaffected by the lid.
        #expect(MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [builtIn, usb], defaultDeviceID: usb.deviceID, lidIsClosed: true).deviceID == usb.deviceID)
    }

    @Test func anUnpluggedChosenMicrophoneFallsBackToTheDefaultAndIsReported() {
        let choice = MicrophoneDevices.deviceToSelect(preferredUID: usb.uid, connected: [builtIn], defaultDeviceID: builtIn.deviceID)
        #expect(choice.deviceID == builtIn.deviceID)
        #expect(choice.chosenUIDIsMissing)
    }

    /// 2026-11-07: a headset unplugged between takes left dictation's long-lived engine reporting
    /// the headset's 8 kHz while the Mac's microphone runs at 48 kHz; installing the tap threw an
    /// Objective-C exception and the app crashed on the next main-actor check. An engine kept across
    /// takes is replaced when the device it was started on is gone or its sample rate no longer
    /// matches what the engine believes.
    @Test func aKeptEngineIsReplacedWhenTheInputDeviceOrItsSampleRateChanged() {
        let headset: AudioDeviceID = 161, builtInMicrophone: AudioDeviceID = 99
        // Same device, same rate: keep the warm engine.
        #expect(!MicrophoneDevices.keptEngineIsStale(lastDeviceID: headset, nextDeviceID: headset, engineSampleRate: 8_000, deviceSampleRate: 8_000))
        // The headset went away and the built-in microphone is the input now.
        #expect(MicrophoneDevices.keptEngineIsStale(lastDeviceID: headset, nextDeviceID: builtInMicrophone, engineSampleRate: 8_000, deviceSampleRate: 48_000))
        // A different device at the same rate still gets a new engine: the old one holds the gone device's ID.
        #expect(MicrophoneDevices.keptEngineIsStale(lastDeviceID: headset, nextDeviceID: builtInMicrophone, engineSampleRate: 48_000, deviceSampleRate: 48_000))
        // The same device re-set to another rate (Audio MIDI Setup) is a mismatch too.
        #expect(MicrophoneDevices.keptEngineIsStale(lastDeviceID: builtInMicrophone, nextDeviceID: builtInMicrophone, engineSampleRate: 44_100, deviceSampleRate: 48_000))
        // No device to record from, or a rate CoreAudio would not tell: judged on the device alone.
        #expect(MicrophoneDevices.keptEngineIsStale(lastDeviceID: headset, nextDeviceID: nil, engineSampleRate: 8_000, deviceSampleRate: nil))
        #expect(!MicrophoneDevices.keptEngineIsStale(lastDeviceID: headset, nextDeviceID: headset, engineSampleRate: 8_000, deviceSampleRate: nil))
    }

    /// F-13: the realtime lane's engine is handed the same microphone as dictation.
    /// Echo cancellation only hears the Mac's default input, so the realtime lane keeps it on
    /// unless the person chose a different, connected microphone.
    @Test func echoCancellationStaysOnUnlessAnotherMicrophoneIsChosen() {
        let defaultIsUSB = usb.deviceID
        #expect(!MicrophoneDevices.choiceDiffersFromDefault(choice: MicrophoneDevices.deviceToSelect(preferredUID: nil, connected: [builtIn, usb], defaultDeviceID: defaultIsUSB), defaultDeviceID: defaultIsUSB))
        #expect(!MicrophoneDevices.choiceDiffersFromDefault(choice: MicrophoneDevices.deviceToSelect(preferredUID: usb.uid, connected: [builtIn, usb], defaultDeviceID: defaultIsUSB), defaultDeviceID: defaultIsUSB))
        #expect(MicrophoneDevices.choiceDiffersFromDefault(choice: MicrophoneDevices.deviceToSelect(preferredUID: builtIn.uid, connected: [builtIn, usb], defaultDeviceID: defaultIsUSB), defaultDeviceID: defaultIsUSB))
        #expect(!MicrophoneDevices.choiceDiffersFromDefault(choice: MicrophoneDevices.deviceToSelect(preferredUID: "unplugged", connected: [builtIn, usb], defaultDeviceID: defaultIsUSB), defaultDeviceID: defaultIsUSB))
    }

    @Test @MainActor func theRealtimeClientPassesTheChosenMicrophoneToItsEngine() async {
        let client = RealtimeVoiceClient()
        client.preferredMicrophoneUID = usb.uid
        #expect(await client.audio.currentPreferredMicrophoneUID() == usb.uid)
        client.preferredMicrophoneUID = nil
        #expect(await client.audio.currentPreferredMicrophoneUID() == nil)
    }

    // MARK: language

    /// F-16: the styles page's language, not the HUD's, reaches the backend engines; auto sends none.
    @Test @MainActor func everyDictationEngineReceivesTheStylesPageLanguage() throws {
        let settings = DictationSettings(defaults: ScratchDefaults.make())
        settings.languageCode = "hi-IN"

        let openClicky = try #require(DictationEngineResolver.makeProvider(for: .openclicky, settings: settings) as? OpenAIAudioTranscriptionProvider)
        let assemblyAI = try #require(DictationEngineResolver.makeProvider(for: .assemblyai, settings: settings) as? AssemblyAIStreamingTranscriptionProvider)
        #expect(openClicky.language() == "hi")
        #expect(assemblyAI.language() == "hi")

        settings.languageCode = "auto"
        #expect(openClicky.language() == nil)
        #expect(assemblyAI.language() == nil)
    }

    /// The ⌃⌥ lane builds the provider without a dictation language and keeps the HUD's.
    @Test @MainActor func theAskLaneKeepsTheReplyLanguage() {
        #expect(OpenAIAudioTranscriptionProvider().language() == ReplyLanguage.currentCode)
    }

    /// AssemblyAI's streaming model hears Hindi and Marathi but not Tamil; the missing ones are logged.
    @Test func assemblyAIKnowsWhichLanguagesItHears() {
        #expect(AssemblyAIStreamingTranscriptionProvider.hears(languageCode: "hi"))
        #expect(AssemblyAIStreamingTranscriptionProvider.hears(languageCode: nil))
        #expect(!AssemblyAIStreamingTranscriptionProvider.hears(languageCode: "ta"))
    }
}
