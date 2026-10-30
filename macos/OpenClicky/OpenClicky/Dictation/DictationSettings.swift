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
    /// NVIDIA's Parakeet through sherpa-onnx, downloaded once. Nothing leaves the Mac.
    case parakeet
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
        case .parakeet: return "Parakeet"
        case .sarvam: return "Sarvam"
        case .openclicky: return "OpenClicky"
        case .assemblyai: return "AssemblyAI"
        }
    }

    /// The engines whose audio never leaves this Mac. Every rule that follows from "nothing leaves
    /// the Mac" reads this one property instead of naming the engines again: no model clean-up
    /// unless it was allowed by name, no failed take's audio kept for a retry (a local engine that
    /// failed has no key, credits or network to wait for), the "private · offline" tag.
    var isOnDevice: Bool {
        switch self {
        case .offline, .parakeet: return true
        case .sarvam, .openclicky, .assemblyai: return false
        }
    }
}

// MARK: - What each engine is called and where its audio goes

/// Every per-engine sentence the windows and the island show, in one place: adding an engine means
/// adding its words here, and the views read these properties rather than switching themselves.
extension DictationEngineChoice {
    /// Who hears the voice, lower case, for the island ("this mac", "sarvam").
    var listenerName: String {
        switch self {
        case .offline, .parakeet: return "this mac"
        case .sarvam: return "sarvam"
        case .openclicky: return "openai"
        case .assemblyai: return "assemblyai"
        }
    }

    /// The engine's name in Settings → Dictation's "Who hears you".
    var settingsTitle: String {
        switch self {
        case .offline: return "This Mac"
        case .parakeet: return "Parakeet, on this Mac"
        case .sarvam: return "Sarvam"
        case .openclicky: return "OpenAI, via OpenClicky"
        case .assemblyai: return "AssemblyAI, via OpenClicky"
        }
    }

    /// The title of the "how to dictate" row on the Your words page.
    var howToDictateTitle: String {
        switch self {
        case .offline: return "On this Mac"
        case .sarvam: return "Sarvam hears you"
        case .parakeet, .openclicky, .assemblyai: return settingsTitle
        }
    }

    /// The tag beside the engine's name in Settings: where the audio goes.
    var audioDestinationTag: String {
        switch self {
        case .offline, .parakeet: return "Private · offline"
        case .sarvam: return "Audio goes to Sarvam"
        case .openclicky: return "Audio goes to OpenAI"
        case .assemblyai: return "Audio goes to AssemblyAI"
        }
    }

    /// Where a take's audio goes, as one sentence for the Your words page.
    var audioDestinationSentence: String {
        switch self {
        case .offline, .parakeet: return "Your voice stays on this Mac."
        case .sarvam: return "Audio goes to Sarvam with your key."
        case .openclicky: return "Audio goes to OpenAI through the OpenClicky backend."
        case .assemblyai: return "Audio goes to AssemblyAI through the OpenClicky backend."
        }
    }

    /// What the engine does and what it needs, under its name in Settings.
    var settingsDetail: String {
        switch self {
        case .offline: return "Apple’s on-device recogniser. Nothing leaves this Mac. No key, no account."
        case .parakeet: return ParakeetDictation.isRecommendedForThisMac
            ? "Far more accurate than Apple’s recogniser on this Mac, and offline too. English and 24 European languages. A \(ParakeetDictation.downloadSizeText) download."
            : "NVIDIA’s open model, offline. English and 24 European languages. A \(ParakeetDictation.downloadSizeText) download."
        case .sarvam: return "Saaras hears eleven Indian languages. Billed by Sarvam to your key."
        case .openclicky: return "Needs an OpenClicky account or your own OpenAI key."
        case .assemblyai: return "Words appear as you speak. Needs the OpenClicky backend."
        }
    }

    /// The short note on the right of the engine in Settings while it can't be used yet.
    var unavailableBadge: String {
        switch self {
        case .offline: return ""
        case .parakeet: return "Not downloaded"
        case .sarvam: return "Needs a key"
        case .openclicky: return "Needs account"
        case .assemblyai: return "Needs backend"
        }
    }

    /// The words the settings search finds the engine by, besides its title and detail.
    var searchKeywords: [String] {
        let common = ["engine", "provider", "transcription", "speech", "who hears you", audioDestinationTag]
        let onDeviceKeywords = isOnDevice ? ["offline", "local", "private", "on device"] : ["cloud"]
        switch self {
        case .offline: return common + onDeviceKeywords + ["apple"]
        case .parakeet: return common + onDeviceKeywords + ["parakeet", "nvidia", "download", "model"]
        case .sarvam: return common + ["sarvam", "saaras", "key", "api key", "indian", "hindi"]
        case .openclicky: return common + onDeviceKeywords + ["openai", "openclicky", "account", "backend"]
        case .assemblyai: return common + onDeviceKeywords + ["assemblyai", "streaming", "backend"]
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
        // Offered outside India (AppRegion): English as written there, and the European languages
        // Apple's recogniser, Parakeet and the online engines all hear.
        DictationLanguage(code: "en-US", name: "English (US)", nativeName: "English (US)"),
        DictationLanguage(code: "en-GB", name: "English (UK)", nativeName: "English (UK)"),
        DictationLanguage(code: "es-ES", name: "Spanish", nativeName: "Español"),
        DictationLanguage(code: "fr-FR", name: "French", nativeName: "Français"),
        DictationLanguage(code: "de-DE", name: "German", nativeName: "Deutsch"),
        DictationLanguage(code: "it-IT", name: "Italian", nativeName: "Italiano"),
        DictationLanguage(code: "pt-BR", name: "Portuguese", nativeName: "Português"),
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
    static let shared: DictationSettings = {
        let settings = DictationSettings()
        settings.followAccountProfile()
        return settings
    }()

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        engine = DictationEngineChoice(rawValue: defaults.string(forKey: Keys.engine) ?? "") ?? .offline
        languageCode = defaults.string(forKey: Keys.language) ?? DictationLanguage.auto.code
        script = DictationScript(rawValue: defaults.string(forKey: Keys.script) ?? "") ?? .native
        // On from the first take (a fresh install has nothing stored): a take is cleaned up by
        // whichever model this Mac has, and falls back to the local clean-up when none answers.
        // Live typing, which only runs when no model will polish, starts once it is switched off.
        polishWithModel = defaults.object(forKey: Keys.polishWithModel) == nil ? true : defaults.bool(forKey: Keys.polishWithModel)
        polishOfflineTakes = defaults.bool(forKey: Keys.polishOfflineTakes)
        // Before the explicit flag existed, a stored value could only have come from the toggle.
        if defaults.object(forKey: Keys.polishOfflineTakes) != nil, defaults.object(forKey: Keys.polishOfflineTakesChosen) == nil {
            defaults.set(true, forKey: Keys.polishOfflineTakesChosen)
        }
        dictationKey = DictationHotkey(rawValue: defaults.string(forKey: Keys.dictationKey) ?? "") ?? .fn
        orbVisible = defaults.object(forKey: Keys.orbVisible) == nil ? true : defaults.bool(forKey: Keys.orbVisible)
        orbLook = OrbLook(rawValue: defaults.string(forKey: Keys.orbLook) ?? "") ?? .pill
        orbTheme = OrbTheme(rawValue: defaults.string(forKey: Keys.orbTheme) ?? "") ?? .black
        orbSize = OrbSize(rawValue: defaults.string(forKey: Keys.orbSize) ?? "") ?? .full
        // Out of the way until a take starts: the pill resting on the Dock all day was the first
        // thing asked to go. Applied once, so a user who turns it off keeps it off.
        if !defaults.bool(forKey: Keys.orbHidesWhenIdleDefaultApplied) {
            defaults.set(true, forKey: Keys.orbHidesWhenIdleDefaultApplied)
            defaults.set(true, forKey: Keys.orbHidesWhenIdle)
        }
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
        pauseBeforeSendingScreen = defaults.object(forKey: Keys.pauseBeforeSendingScreen) == nil ? true : defaults.bool(forKey: Keys.pauseBeforeSendingScreen)
        answerSimpleRequestsOnThisMac = defaults.object(forKey: Keys.answerSimpleRequestsOnThisMac) == nil ? true : defaults.bool(forKey: Keys.answerSimpleRequestsOnThisMac)
        hasCompletedDictationOnboarding = defaults.bool(forKey: Keys.hasCompletedDictationOnboarding)
        preferredMicrophoneUID = defaults.string(forKey: Keys.preferredMicrophoneUID)
        orbPosition = Self.decodePoint(defaults.string(forKey: Keys.orbPosition))
    }

    private enum Keys {
        static let engine = "dictation.engine"
        static let language = "dictation.language"
        static let script = "dictation.script"
        static let polishWithModel = "dictation.polishWithModel"
        static let polishOfflineTakes = "dictation.polishOfflineTakes"
        /// Set once the person flips "also polish takes heard on this mac" themselves.
        static let polishOfflineTakesChosen = "dictation.polishOfflineTakesChosen"
        static let dictationKey = "dictation.key"
        static let orbVisible = "dictation.orb.visible"
        static let orbLook = "dictation.orb.look"
        static let orbTheme = "dictation.orb.theme"
        static let orbSize = "dictation.orb.size"
        static let orbHidesWhenIdle = "dictation.orb.hidesWhenIdle"
        static let orbHidesWhenIdleDefaultApplied = "dictation.orb.hidesWhenIdle.defaultApplied"
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
        static let pauseBeforeSendingScreen = "openclicky.ask.pauseBeforeSendingScreen"
        static let answerSimpleRequestsOnThisMac = "openclicky.ask.onThisMac"
        static let hasCompletedDictationOnboarding = "dictation.onboarded"
        static let preferredMicrophoneUID = "dictation.preferredMicrophoneUID"
    }

    // MARK: engine & language

    @Published var engine: DictationEngineChoice { didSet { defaults.set(engine.rawValue, forKey: Keys.engine) } }
    @Published var languageCode: String { didSet { defaults.set(languageCode, forKey: Keys.language) } }
    @Published var script: DictationScript { didSet { defaults.set(script.rawValue, forKey: Keys.script) } }
    /// Clean the raw transcript up with a model (style rules, punctuation) when one is configured.
    @Published var polishWithModel: Bool { didSet { defaults.set(polishWithModel, forKey: Keys.polishWithModel) } }
    /// The offline engine's promise is that nothing leaves the Mac; sending its words to a model
    /// for polish is off until asked for by name.
    @Published var polishOfflineTakes: Bool {
        didSet { if !isApplyingAccountDefault { defaults.set(polishOfflineTakes, forKey: Keys.polishOfflineTakes) } }
    }
    private var isApplyingAccountDefault = false
    private var accountObservers: [NSObjectProtocol] = []

    /// The settings toggle: the person's own choice, which the account default never overrides.
    func choosePolishOfflineTakes(_ isOn: Bool) {
        defaults.set(true, forKey: Keys.polishOfflineTakesChosen)
        polishOfflineTakes = isOn
    }

    /// A free account's takes go to its model anyway, so until the person chooses, polish of takes
    /// heard on this Mac is on for accounts and off otherwise. The default is not stored, so it
    /// follows the account as it changes.
    func applyAccountPolishDefault(kind: AccountKind) {
        guard !defaults.bool(forKey: Keys.polishOfflineTakesChosen) else { return }
        let wanted = kind == .account
        guard polishOfflineTakes != wanted else { return }
        isApplyingAccountDefault = true
        polishOfflineTakes = wanted
        isApplyingAccountDefault = false
    }

    /// Re-applies the default whenever credentials change or the backend's answer changes the account.
    private func followAccountProfile() {
        applyAccountPolishDefault(kind: AccountCapabilities.current().kind)
        for name in [OpenClickyConfiguration.credentialsChangedNotification, AccountProfileStore.profileChangedNotification] {
            accountObservers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { _ in
                Task { @MainActor in DictationSettings.shared.applyAccountPolishDefault(kind: AccountCapabilities.current().kind) }
            })
        }
    }

    var language: DictationLanguage { DictationLanguage.named(languageCode) }

    /// The stored dictation language, readable off the main actor: the polisher and the engine
    /// checks are built from background tasks too, and they read the same key `languageCode` writes.
    nonisolated static func storedLanguage(defaults: UserDefaults = .standard) -> DictationLanguage {
        DictationLanguage.named(defaults.string(forKey: Keys.language) ?? DictationLanguage.auto.code)
    }

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

    /// Which options the settings offer (AppRegion). Always the world since 2026-10-30: OpenClicky
    /// no longer offers India its own set, so the Region choice is gone. An option already in use
    /// (Sarvam, an Indian language) stays offered. The old `openclicky.region` value is ignored.
    var region: AppRegion { .world }
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
    /// A question to Clicky waits a moment (`AskSendPause`) before the screen is captured and sent,
    /// so esc or a click on the island can still call it off. The ⌃⌥ lane only; on by default.
    @Published var pauseBeforeSendingScreen: Bool { didSet { defaults.set(pauseBeforeSendingScreen, forKey: Keys.pauseBeforeSendingScreen) } }
    /// The ⌃⌥ lane tries Apple's on-device model first (`LocalAskRung`) for the fast-lane actions
    /// (open an app or a website, a folder, the volume, playback) and for rewrites of text it can
    /// read, so those never send a screenshot or spend answer credits. English, macOS 26 with
    /// Apple Intelligence on; elsewhere the switch does nothing. On by default.
    @Published var answerSimpleRequestsOnThisMac: Bool { didSet { defaults.set(answerSimpleRequestsOnThisMac, forKey: Keys.answerSimpleRequestsOnThisMac) } }
    @Published var hasCompletedDictationOnboarding: Bool { didSet { defaults.set(hasCompletedDictationOnboarding, forKey: Keys.hasCompletedDictationOnboarding) } }

    /// Settings → general → reset, and the orb page's reset: back to a fresh install's values.
    func resetOrbToDefaults() {
        orbVisible = true; orbLook = .pill; orbTheme = .black; orbSize = .full
        orbHidesWhenIdle = true; orbRestsExpanded = false; orbOpensBoxWhenPasteUnverified = false
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
