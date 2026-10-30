//
//  IslandComponents.swift
//  OpenClicky
//
//  The island's slot: what the open panel shows under its band. A component declares when it is
//  available; the selector picks one for the moment (a live take, a working agent) or by the
//  user's choice (a default, an order, and a flip in the capsule). Pure: the views live in
//  `IslandComponentViews.swift`.
//

import Foundation
import SwiftUI

/// What the island knows about the moment, built by the HUD manager from what the app already
/// publishes.
struct IslandContext: Equatable {
    var isTakeLive = false
    /// When the last take landed (the live → idle flip), remembered by the HUD manager.
    var takeLandedAt: Date?
    var liveTranscript = ""
    /// The last take's words as they were pasted, for the moments after it lands.
    var landedTranscript = ""
    var agentActivityText: String?
    var isResultCardUp = false
    var engineCanRun = true
    var placement: HUDPlacement = .notch
    var now = Date()
    /// A drag from another app is over the island (the model keeps this per display).
    var isDragHovering = false
    /// Music or Spotify is playing (the Now playing plugin is on and found a playing track).
    var isMediaPlaying = false

    static let idle = IslandContext(now: Date(timeIntervalSince1970: 0))

    /// How long a landed take keeps its transcript in front.
    static let landedTranscriptWindow: TimeInterval = 8

    /// The component the moment asks for: the shelf while a drag hovers (an explicit act that
    /// outranks everything), the transcript while a take is live or just landed, the agent while
    /// one works or its result card is up, home (where the track is) while music plays, else nothing.
    var contextualKey: String? {
        if isDragHovering { return IslandPluginRegistry.shelfID }
        if isTakeLive { return IslandComponentRegistry.transcriptID }
        if let takeLandedAt, now.timeIntervalSince(takeLandedAt) < Self.landedTranscriptWindow { return IslandComponentRegistry.transcriptID }
        if agentActivityText != nil || isResultCardUp { return IslandComponentRegistry.agentID }
        if isMediaPlaying { return IslandComponentRegistry.homeID }
        return nil
    }
}

extension IslandContext {
    /// The context from what the app already publishes: the take controller's state and last
    /// take, the orb's live words, the companion's agent line, the result card's visibility.
    static func make(takeState: DictationTakeController.TakeState, lastTake: TakeRecord?, takeLandedAt: Date?, liveTranscript: String, agentActivityText: String?, isResultCardUp: Bool, engineCanRun: Bool, placement: HUDPlacement, now: Date) -> IslandContext {
        IslandContext(
            isTakeLive: takeState != .idle,
            takeLandedAt: takeLandedAt,
            liveTranscript: liveTranscript,
            landedTranscript: lastTake?.formattedText ?? "",
            agentActivityText: agentActivityText,
            isResultCardUp: isResultCardUp,
            engineCanRun: engineCanRun,
            placement: placement,
            now: now
        )
    }

    /// The landing time to carry: `now` at the live → idle flip, the previous one while the take
    /// stays landed, nothing while a take is live.
    static func landingTime(previous: IslandContext, next: IslandContext, now: Date) -> Date? {
        if next.isTakeLive { return nil }
        if previous.isTakeLive { return now }
        return previous.takeLandedAt
    }
}

/// One thing the island's slot can show.
protocol IslandComponent {
    /// Stable; stored in Settings.
    var id: String { get }
    /// For Settings and the capsule's help text.
    var title: String { get }
    /// The capsule's icon (an SF Symbol name).
    var symbolName: String { get }
    /// The slot's height for this component in the notch placement.
    var preferredHeight: CGFloat { get }
    /// The open island's width for this component under the notch; nil for the standard 512 pt.
    var preferredWidth: CGFloat? { get }
    /// A plugin: an optional extra, built for the Mac, that the user turns on in Settings; off,
    /// it is never available. The built-ins are not plugins. Plugins live under `Plugins/`.
    var isPlugin: Bool { get }
    /// A line under the plugin's switch in Settings (what it reads, what macOS may ask).
    var settingsDetail: String? { get }
    func isAvailable(in context: IslandContext) -> Bool
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView
}

extension IslandComponent {
    var preferredWidth: CGFloat? { nil }
    var isPlugin: Bool { false }
    var settingsDetail: String? { nil }
}

/// The selector's answer: the active component and every one the capsule may flip to.
struct IslandComponentSelection: Equatable {
    let active: String
    let available: [String]
}

enum IslandComponentSelector {
    /// Every component enabled: for callers and tests that have no switchboard.
    static func select(registry: [any IslandComponent], order: [String], defaultID: String, override: String?, context: IslandContext) -> IslandComponentSelection {
        select(registry: registry, order: order, defaultID: defaultID, override: override, context: context, enabled: Set(registry.map(\.id)))
    }

    /// Disabled components are dropped first: never available, never the default, never pulled
    /// in by the moment. Then the override wins while it is available; else the moment's
    /// component; else the user's default (home when the stored one is disabled); else
    /// the first available in the user's order. `available` is the registry filtered by
    /// availability, in the user's order (unknown ids ignored, missing ids appended in
    /// registration order).
    static func select(registry: [any IslandComponent], order: [String], defaultID: String, override: String?, context: IslandContext, enabled: Set<String>) -> IslandComponentSelection {
        precondition(!registry.isEmpty, "the island needs at least one component")
        let availableIDs = registry.filter { enabled.contains($0.id) && $0.isAvailable(in: context) }.map(\.id)
        let ordered = order.filter { availableIDs.contains($0) } + availableIDs.filter { !order.contains($0) }
        let fallbackDefault = enabled.contains(defaultID) ? defaultID : IslandComponentRegistry.homeID
        let candidates = [override, context.contextualKey, fallbackDefault].compactMap { $0 }
        let active = candidates.first { ordered.contains($0) } ?? ordered.first ?? registry[0].id
        return IslandComponentSelection(active: active, available: ordered)
    }
}

/// The user's default component, order and switched-on plugins, in UserDefaults.
struct IslandComponentSettings {
    static let defaultKey = "openclicky.island.defaultComponent"
    static let orderKey = "openclicky.island.componentOrder"
    static let pluginsKey = "openclicky.island.plugins"

    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    var defaultID: String {
        get { defaults.string(forKey: Self.defaultKey) ?? IslandComponentRegistry.homeID }
        set { defaults.set(newValue, forKey: Self.defaultKey) }
    }

    var order: [String] {
        get { defaults.stringArray(forKey: Self.orderKey) ?? [] }
        set { defaults.set(newValue, forKey: Self.orderKey) }
    }

    /// The plugins the user turned on; unset means none.
    var enabledPlugins: [String] {
        get { defaults.stringArray(forKey: Self.pluginsKey) ?? [] }
        set { defaults.set(newValue, forKey: Self.pluginsKey) }
    }

    /// A built-in is always on; a plugin only when the user turned it on. Unknown ids are off.
    func isEnabled(_ id: String, in registry: [any IslandComponent]) -> Bool {
        guard let component = registry.first(where: { $0.id == id }) else { return false }
        return !component.isPlugin || enabledPlugins.contains(id)
    }

    /// The stored default when it is on; home when it has been switched off since.
    func resolvedDefaultID(in registry: [any IslandComponent]) -> String {
        isEnabled(defaultID, in: registry) ? defaultID : IslandComponentRegistry.homeID
    }

    /// The ids the selector may use, from the registry and the user's switches.
    static func enabledIDs(in registry: [any IslandComponent], settings: IslandComponentSettings) -> Set<String> {
        Set(registry.map(\.id).filter { settings.isEnabled($0, in: registry) })
    }
}

/// The built-in components, in registration order, then the plugins (`IslandPluginRegistry`).
enum IslandComponentRegistry {
    static let homeID = "home"
    static let shortcutsID = "shortcuts"
    static let transcriptID = "transcript"
    static let agentID = "agent"

    @MainActor static var builtIns: [any IslandComponent] {
        [HomeIslandComponent(), ShortcutsIslandComponent(), TranscriptIslandComponent(), AgentIslandComponent()]
    }

    /// The built-ins first, then the plugins (off until turned on in Settings).
    @MainActor static var all: [any IslandComponent] { builtIns + IslandPluginRegistry.all }
}

/// Today's body row: (i) and the shortcuts (or the engine warning).
struct ShortcutsIslandComponent: IslandComponent {
    let id = IslandComponentRegistry.shortcutsID
    let title = "Shortcuts"
    let symbolName = "keyboard"
    let preferredHeight: CGFloat = NotchPanelBody.shortcutsHeight
    func isAvailable(in context: IslandContext) -> Bool { true }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(NotchPanelBody(model: model, companionManager: companionManager))
    }
}

/// The take's words so far, while a take is live and for a moment after it lands.
struct TranscriptIslandComponent: IslandComponent {
    let id = IslandComponentRegistry.transcriptID
    let title = "Transcript"
    let symbolName = "text.quote"
    let preferredHeight: CGFloat = 52
    func isAvailable(in context: IslandContext) -> Bool { context.contextualKey == IslandComponentRegistry.transcriptID }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(TranscriptIslandView(model: model, companionManager: companionManager, orb: companionManager.orbModel, settings: companionManager.dictationSettings))
    }
}

/// The working agent's latest line, and its result card's follow-up.
struct AgentIslandComponent: IslandComponent {
    let id = IslandComponentRegistry.agentID
    let title = "Agent"
    let symbolName = "wand.and.sparkles"
    let preferredHeight: CGFloat = 52
    func isAvailable(in context: IslandContext) -> Bool { context.agentActivityText != nil || context.isResultCardUp }
    @MainActor func makeView(companionManager: CompanionManager, model: NotchHUDModel) -> AnyView {
        AnyView(AgentIslandView(model: model, companionManager: companionManager, result: companionManager.agentResultPanelManager.state))
    }
}
