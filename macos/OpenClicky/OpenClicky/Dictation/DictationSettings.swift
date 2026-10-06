//
//  DictationSettings.swift
//  OpenClicky
//
//  Every dictation preference, in UserDefaults, as one observable object. Each property has a
//  default that matches a fresh install of the app this was modelled on: the orb shows at the
//  bottom of the screen as a pill, sounds and haptics are on, the key is fn, nothing leaves the
//  Mac until an engine that needs the network is chosen.
//

import AppKit
import Combine
import Foundation

/// Which speech engine turns a take into words.
enum DictationEngineChoice: String, CaseIterable, Identifiable {
    /// Apple's on-device recogniser. Nothing leaves the Mac.
    case offline
    /// Sarvam's Saaras, with the user's own key (`sarvamKey` in shell.json).
    case sarvam
    /// The OpenClicky backend (an account or the user's own OpenAI key).
    case openclicky
    /// AssemblyAI streaming through a backend-minted token.
    case assemblyai

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .offline: return "on this Mac"
        case .sarvam: return "Sarvam"
        case .openclicky: return "OpenClicky"
        case .assemblyai: return "AssemblyAI"
        }
    }

    var detail: String {
        switch self {
        case .offline: return "apple's recogniser, offline. your voice never leaves this mac."
        case .sarvam: return "saaras hears eleven indian languages. audio goes to sarvam with your key."
        case .openclicky: return "transcribed by the openclicky backend with your account or your openai key."
        case .assemblyai: return "streaming transcription through the openclicky backend."
        }
    }
}

enum OrbLook: String, CaseIterable, Identifiable {
    case pill, classic, pixel
    var id: String { rawValue }
}

enum OrbTheme: String, CaseIterable, Identifiable {
    case black, coral, mist
    var id: String { rawValue }
}

enum OrbSize: String, CaseIterable, Identifiable {
    case full, mini
    var id: String { rawValue }
}

enum DictationAppearance: String, CaseIterable, Identifiable {
    case light, dark, system
    var id: String { rawValue }

    var nsAppearance: NSAppearance? {
        switch self {
        case .light: return NSAppearance(named: .aqua)
        case .dark: return NSAppearance(named: .darkAqua)
        case .system: return nil
        }
    }
}

/// Native script or roman letters for Indian-language takes ("नमस्ते" or "namaste").
enum DictationScript: String, CaseIterable, Identifiable {
    case native, roman
    var id: String { rawValue }
}

/// The languages the engines can be pinned to. `auto` lets the engine detect.
struct DictationLanguage: Identifiable, Equatable {
    let code: String
    let name: String
    let nativeName: String
    var id: String { code }

    static let auto = DictationLanguage(code: "auto", name: "auto-detect", nativeName: "auto-detect")

    static let choices: [DictationLanguage] = [
        .auto,
        DictationLanguage(code: "en-IN", name: "English", nativeName: "English"),
        DictationLanguage(code: "hi-IN", name: "Hindi", nativeName: "हिन्दी"),
        DictationLanguage(code: "ta-IN", name: "Tamil", nativeName: "தமிழ்"),
        DictationLanguage(code: "te-IN", name: "Telugu", nativeName: "తెలుగు"),
        DictationLanguage(code: "kn-IN", name: "Kannada", nativeName: "ಕನ್ನಡ"),
        DictationLanguage(code: "ml-IN", name: "Malayalam", nativeName: "മലയാളം"),
        DictationLanguage(code: "bn-IN", name: "Bengali", nativeName: "বাংলা"),
        DictationLanguage(code: "mr-IN", name: "Marathi", nativeName: "मराठी"),
        DictationLanguage(code: "gu-IN", name: "Gujarati", nativeName: "ગુજરાતી"),
        DictationLanguage(code: "pa-IN", name: "Punjabi", nativeName: "ਪੰਜਾਬੀ"),
        DictationLanguage(code: "od-IN", name: "Odia", nativeName: "ଓଡ଼ିଆ"),
    ]

    static func named(_ code: String) -> DictationLanguage {
        choices.first { $0.code == code } ?? .auto
    }

    /// The bare language code Apple's recogniser and the prompts take ("hi" for "hi-IN").
    var bareCode: String? {
        guard code != "auto" else { return nil }
        return code.split(separator: "-").first.map(String.init)
    }
}

@MainActor
final class DictationSettings: ObservableObject {
    static let shared = DictationSettings()

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        engine = DictationEngineChoice(rawValue: defaults.string(forKey: Keys.engine) ?? "") ?? .offline
        languageCode = defaults.string(forKey: Keys.language) ?? DictationLanguage.auto.code
        script = DictationScript(rawValue: defaults.string(forKey: Keys.script) ?? "") ?? .native
        polishWithModel = defaults.object(forKey: Keys.polishWithModel) == nil ? true : defaults.bool(forKey: Keys.polishWithModel)
        dictationKey = DictationHotkey(rawValue: defaults.string(forKey: Keys.dictationKey) ?? "") ?? .fn
        orbVisible = defaults.object(forKey: Keys.orbVisible) == nil ? true : defaults.bool(forKey: Keys.orbVisible)
        orbLook = OrbLook(rawValue: defaults.string(forKey: Keys.orbLook) ?? "") ?? .pill
        orbTheme = OrbTheme(rawValue: defaults.string(forKey: Keys.orbTheme) ?? "") ?? .black
        orbSize = OrbSize(rawValue: defaults.string(forKey: Keys.orbSize) ?? "") ?? .full
        orbHidesWhenIdle = defaults.bool(forKey: Keys.orbHidesWhenIdle)
        orbRestsExpanded = defaults.bool(forKey: Keys.orbRestsExpanded)
        orbOpensBoxWhenPasteUnverified = defaults.bool(forKey: Keys.orbOpensBoxWhenPasteUnverified)
        orbIsDraggable = defaults.object(forKey: Keys.orbIsDraggable) == nil ? true : defaults.bool(forKey: Keys.orbIsDraggable)
        tooltips = defaults.bool(forKey: Keys.tooltips)
        sounds = defaults.object(forKey: Keys.sounds) == nil ? true : defaults.bool(forKey: Keys.sounds)
        haptics = defaults.object(forKey: Keys.haptics) == nil ? true : defaults.bool(forKey: Keys.haptics)
        reduceAnimation = defaults.bool(forKey: Keys.reduceAnimation)
        appearance = DictationAppearance(rawValue: defaults.string(forKey: Keys.appearance) ?? "") ?? .system
        inactivityTimeoutMinutes = defaults.object(forKey: Keys.inactivityTimeoutMinutes) == nil ? 3 : defaults.integer(forKey: Keys.inactivityTimeoutMinutes)
        clipboardHistoryEnabled = defaults.bool(forKey: Keys.clipboardHistoryEnabled)
        incognito = defaults.bool(forKey: Keys.incognito)
        keepMemoryOnThisMac = defaults.bool(forKey: Keys.keepMemoryOnThisMac)
        retainFailedTakeAudio = defaults.object(forKey: Keys.retainFailedTakeAudio) == nil ? true : defaults.bool(forKey: Keys.retainFailedTakeAudio)
        readNearbyText = defaults.object(forKey: Keys.readNearbyText) == nil ? true : defaults.bool(forKey: Keys.readNearbyText)
        hasCompletedDictationOnboarding = defaults.bool(forKey: Keys.hasCompletedDictationOnboarding)
        preferredMicrophoneUID = defaults.string(forKey: Keys.preferredMicrophoneUID)
        orbPosition = Self.decodePoint(defaults.string(forKey: Keys.orbPosition))
    }

    private enum Keys {
        static let engine = "dictation.engine"
        static let language = "dictation.language"
        static let script = "dictation.script"
        static let polishWithModel = "dictation.polishWithModel"
        static let dictationKey = "dictation.key"
        static let orbVisible = "dictation.orb.visible"
        static let orbLook = "dictation.orb.look"
        static let orbTheme = "dictation.orb.theme"
        static let orbSize = "dictation.orb.size"
        static let orbHidesWhenIdle = "dictation.orb.hidesWhenIdle"
        static let orbRestsExpanded = "dictation.orb.restsExpanded"
        static let orbOpensBoxWhenPasteUnverified = "dictation.orb.opensBoxWhenPasteUnverified"
        static let orbIsDraggable = "dictation.orb.draggable"
        static let orbPosition = "dictation.orb.position"
        static let tooltips = "dictation.tooltips"
        static let sounds = "dictation.sounds"
        static let haptics = "dictation.haptics"
        static let reduceAnimation = "dictation.reduceAnimation"
        static let appearance = "dictation.appearance"
        static let inactivityTimeoutMinutes = "dictation.inactivityTimeoutMinutes"
        static let clipboardHistoryEnabled = "dictation.clipboardHistoryEnabled"
        static let incognito = "dictation.incognito"
        static let keepMemoryOnThisMac = "dictation.keepMemoryOnThisMac"
        static let retainFailedTakeAudio = "dictation.retainFailedTakeAudio"
        static let readNearbyText = "dictation.readNearbyText"
        static let hasCompletedDictationOnboarding = "dictation.onboarded"
        static let preferredMicrophoneUID = "dictation.preferredMicrophoneUID"
    }

    // MARK: engine & language

    @Published var engine: DictationEngineChoice { didSet { defaults.set(engine.rawValue, forKey: Keys.engine) } }
    @Published var languageCode: String { didSet { defaults.set(languageCode, forKey: Keys.language) } }
    @Published var script: DictationScript { didSet { defaults.set(script.rawValue, forKey: Keys.script) } }
    /// Clean the raw transcript up with a model (style rules, punctuation) when one is configured.
    @Published var polishWithModel: Bool { didSet { defaults.set(polishWithModel, forKey: Keys.polishWithModel) } }

    var language: DictationLanguage { DictationLanguage.named(languageCode) }

    // MARK: keys

    @Published var dictationKey: DictationHotkey { didSet { defaults.set(dictationKey.rawValue, forKey: Keys.dictationKey) } }

    // MARK: the orb

    @Published var orbVisible: Bool { didSet { defaults.set(orbVisible, forKey: Keys.orbVisible) } }
    @Published var orbLook: OrbLook { didSet { defaults.set(orbLook.rawValue, forKey: Keys.orbLook) } }
    @Published var orbTheme: OrbTheme { didSet { defaults.set(orbTheme.rawValue, forKey: Keys.orbTheme) } }
    @Published var orbSize: OrbSize { didSet { defaults.set(orbSize.rawValue, forKey: Keys.orbSize) } }
    /// "hide when not in use": appears when a take starts and hides when it is done.
    @Published var orbHidesWhenIdle: Bool { didSet { defaults.set(orbHidesWhenIdle, forKey: Keys.orbHidesWhenIdle) } }
    /// "rest with the box open": the transcript box stays open between takes.
    @Published var orbRestsExpanded: Bool { didSet { defaults.set(orbRestsExpanded, forKey: Keys.orbRestsExpanded) } }
    /// "paste visibility · orb box": open the box when a paste could not be confirmed.
    @Published var orbOpensBoxWhenPasteUnverified: Bool { didSet { defaults.set(orbOpensBoxWhenPasteUnverified, forKey: Keys.orbOpensBoxWhenPasteUnverified) } }
    @Published var orbIsDraggable: Bool { didSet { defaults.set(orbIsDraggable, forKey: Keys.orbIsDraggable) } }
    /// Where the orb was last dragged to, in screen points; nil = bottom centre of the main screen.
    @Published var orbPosition: CGPoint? { didSet { defaults.set(Self.encodePoint(orbPosition), forKey: Keys.orbPosition) } }
    @Published var tooltips: Bool { didSet { defaults.set(tooltips, forKey: Keys.tooltips) } }
    @Published var sounds: Bool { didSet { defaults.set(sounds, forKey: Keys.sounds) } }
    @Published var haptics: Bool { didSet { defaults.set(haptics, forKey: Keys.haptics) } }
    @Published var reduceAnimation: Bool { didSet { defaults.set(reduceAnimation, forKey: Keys.reduceAnimation) } }

    // MARK: general

    @Published var appearance: DictationAppearance { didSet { defaults.set(appearance.rawValue, forKey: Keys.appearance) } }
    /// A take with this long of silence ends on its own. 0 = never.
    @Published var inactivityTimeoutMinutes: Int { didSet { defaults.set(inactivityTimeoutMinutes, forKey: Keys.inactivityTimeoutMinutes) } }
    @Published var preferredMicrophoneUID: String? {
        didSet {
            defaults.set(preferredMicrophoneUID, forKey: Keys.preferredMicrophoneUID)
            preferredMicrophoneUIDDidChange?(preferredMicrophoneUID)
        }
    }
    /// The dictation manager records from the chosen device; it is told here when it changes.
    var preferredMicrophoneUIDDidChange: ((String?) -> Void)?

    // MARK: privacy

    @Published var clipboardHistoryEnabled: Bool { didSet { defaults.set(clipboardHistoryEnabled, forKey: Keys.clipboardHistoryEnabled) } }
    /// Takes still paste, but nothing is saved to history.
    @Published var incognito: Bool { didSet { defaults.set(incognito, forKey: Keys.incognito) } }
    /// History and learned terms never leave this Mac (no backend sync of either).
    @Published var keepMemoryOnThisMac: Bool { didSet { defaults.set(keepMemoryOnThisMac, forKey: Keys.keepMemoryOnThisMac) } }
    @Published var retainFailedTakeAudio: Bool { didSet { defaults.set(retainFailedTakeAudio, forKey: Keys.retainFailedTakeAudio) } }
    /// Read captions near the cursor through Accessibility so names on screen are spelled right.
    @Published var readNearbyText: Bool { didSet { defaults.set(readNearbyText, forKey: Keys.readNearbyText) } }
    @Published var hasCompletedDictationOnboarding: Bool { didSet { defaults.set(hasCompletedDictationOnboarding, forKey: Keys.hasCompletedDictationOnboarding) } }

    /// Settings → general → reset, and the orb page's reset: back to a fresh install's values.
    func resetOrbToDefaults() {
        orbVisible = true; orbLook = .pill; orbTheme = .black; orbSize = .full
        orbHidesWhenIdle = false; orbRestsExpanded = false; orbOpensBoxWhenPasteUnverified = false
        orbIsDraggable = true; orbPosition = nil; tooltips = false; sounds = true; haptics = true
    }

    func resetGeneralToDefaults() {
        appearance = .system; inactivityTimeoutMinutes = 3; reduceAnimation = false
    }

    func resetShortcutsToDefaults() {
        dictationKey = .fn
    }

    private static func encodePoint(_ point: CGPoint?) -> String? {
        guard let point else { return nil }
        return "\(point.x),\(point.y)"
    }

    private static func decodePoint(_ encoded: String?) -> CGPoint? {
        guard let encoded else { return nil }
        let parts = encoded.split(separator: ",").compactMap { Double($0) }
        guard parts.count == 2 else { return nil }
        return CGPoint(x: parts[0], y: parts[1])
    }
}
