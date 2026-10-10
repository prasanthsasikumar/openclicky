//
//  NotchHUDPanels.swift
//  OpenClicky
//
//  The full notch app: tab bar + home / agents / settings, the agent thread store that feeds the
//  agents tab, and the floating result card (top-right) with "Follow up with agent". Drawn with
//  the refinement sheet's HUD tokens (`DS.HUD`).
//

import AppKit
import Combine
import SwiftUI

// MARK: - Full panel

struct NotchFullPanelView: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager
    @StateObject private var threadStore = AgentThreadStore()

    var body: some View {
        VStack(spacing: 0) {
            // The tab bar lives in the menu-bar band, on either side of the physical notch.
            NotchTabBar(model: model, companionManager: companionManager, threadStore: threadStore)
                .frame(height: model.topBandHeight)

            Group {
                switch model.activeTab {
                case .home:
                    NotchHomeView(companionManager: companionManager, model: model)
                case .agents:
                    NotchAgentsView(companionManager: companionManager, threadStore: threadStore)
                case .settings:
                    NotchSettingsView(companionManager: companionManager, settings: companionManager.dictationSettings)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .padding(.top, DS.HUD.bodyTopPadding)
        }
        .onAppear { threadStore.refresh() }
        .onChange(of: model.activeTab) { tab in
            if tab == .agents { threadStore.refresh() }
        }
        .onChange(of: model.expansion) { expansion in
            if expansion == .full { threadStore.refresh() }
        }
    }
}

// MARK: - Tab bar

struct NotchTabBar: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var threadStore: AgentThreadStore

    /// On a notch screen each side of the band only has what the notch leaves free; the right-hand
    /// cluster shortens the voice pill to fit instead of running under the notch.
    private var sideClusterWidth: CGFloat {
        guard model.geometry.hasHardwareNotch else { return .infinity }
        return max(0, (model.fullWidth - model.geometry.notchWidth) / 2 - 12)
    }

    var body: some View {
        HStack(spacing: 4) {
            tabPill(title: "home", tab: .home, badgeCount: 0)
            tabPill(title: "agents", tab: .agents, badgeCount: threadStore.attentionCount)

            Spacer(minLength: 8)

            HStack(spacing: 6) {
                Spacer(minLength: 0)
                ViewThatFits(in: .horizontal) {
                    VoiceDestinationPill(companionManager: companionManager, settings: companionManager.dictationSettings, length: .full)
                    VoiceDestinationPill(companionManager: companionManager, settings: companionManager.dictationSettings, length: .short)
                    VoiceDestinationPill(companionManager: companionManager, settings: companionManager.dictationSettings, length: .glyphOnly)
                }
                if model.activeTab == .agents {
                    bandIconButton(systemImage: "arrow.clockwise", help: "refresh agents", isSelected: false) { threadStore.refresh(force: true) }
                }
                bandIconButton(systemImage: "gearshape.fill", help: "settings", isSelected: model.activeTab == .settings) {
                    model.select(model.activeTab == .settings ? .home : .settings)
                }
            }
            .frame(maxWidth: sideClusterWidth, alignment: .trailing)
        }
        .padding(.horizontal, 12)
    }

    private func tabPill(title: String, tab: NotchHUDTab, badgeCount: Int) -> some View {
        let isSelected = model.activeTab == tab
        return Button(action: { model.select(tab) }) {
            HStack(spacing: 6) {
                Text(title)
                    .font(.system(size: 13, weight: isSelected ? .semibold : .medium))
                    .foregroundColor(isSelected ? DS.HUD.text : DS.HUD.text2)
                if badgeCount > 0 {
                    Text("\(badgeCount)")
                        .font(.system(size: 10, weight: .bold))
                        .foregroundColor(.black)
                        .padding(.horizontal, 5)
                        .frame(height: 14)
                        .background(Capsule().fill(DS.HUD.pointer))
                }
            }
            .padding(.horizontal, 10)
            .frame(height: DS.HUD.tabHeight)
            .background(Capsule().fill(isSelected ? DS.HUD.surfaceRaised : Color.clear))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .accessibilityLabel(badgeCount > 0 ? "\(title), \(badgeCount) need attention" : title)
    }

    private func bandIconButton(systemImage: String, help: String, isSelected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(isSelected ? .black : DS.HUD.text2)
                .frame(width: DS.HUD.pillHeight, height: DS.HUD.pillHeight)
                .background(Circle().fill(isSelected ? DS.HUD.text : DS.HUD.bandControl))
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help(help)
        .accessibilityLabel(help)
    }
}

/// Where dictated words go, as the band's pill says it: "voice stays on this mac", "sarvam hears
/// you", "openai hears you", "assemblyai hears you". When the chosen engine cannot run it says what
/// is missing in orange instead ("set up backend ›", "add sarvam key ›"). A tap opens the voice
/// settings, or what the blocked engine needs.
private struct VoiceDestinationPill: View {
    enum Length { case full, short, glyphOnly }

    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var settings: DictationSettings
    let length: Length

    var body: some View {
        let engine = settings.engine
        let blockedLabel = NotchVoiceDestination.blockedPillLabel(for: engine)
        return Button(action: { NotchVoiceDestination.openSetup(for: engine, companionManager: companionManager) }) {
            HStack(spacing: 5) {
                if let blockedLabel {
                    if length == .glyphOnly {
                        Image(systemName: "exclamationmark").font(.system(size: 10, weight: .bold))
                    } else {
                        Text(blockedLabel).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                    }
                } else {
                    Image(systemName: "mic.fill")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundColor(DS.HUD.live)
                    if length != .glyphOnly {
                        Text(length == .full ? NotchVoiceDestination.pillLabel(for: engine) : NotchVoiceDestination.shortPillLabel(for: engine))
                            .font(.system(size: 12, weight: .medium))
                            .foregroundColor(Color(hex: "#E5E5E5"))
                            .lineLimit(1)
                    }
                }
            }
            .foregroundColor(blockedLabel == nil ? DS.HUD.text : .black)
            .padding(.horizontal, length == .glyphOnly ? 0 : 10)
            .frame(minWidth: DS.HUD.pillHeight, minHeight: DS.HUD.pillHeight, maxHeight: DS.HUD.pillHeight)
            .background(Capsule().fill(blockedLabel == nil ? DS.HUD.bandControl : DS.HUD.pointer))
            .fixedSize()
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help(blockedLabel == nil
              ? "dictation is heard by \(engine.displayName.lowercased()). click to change."
              : DictationEngineResolver.unavailableReason(for: engine) ?? "")
    }
}

/// The words the HUD uses for where the voice goes, shared by the pill, the Home warning card and
/// the Settings summary.
enum NotchVoiceDestination {
    static func pillLabel(for engine: DictationEngineChoice) -> String {
        switch engine {
        case .offline: return "voice stays on this mac"
        case .sarvam: return "sarvam hears you"
        case .openclicky: return "openai hears you"
        case .assemblyai: return "assemblyai hears you"
        }
    }

    static func shortPillLabel(for engine: DictationEngineChoice) -> String {
        switch engine {
        case .offline: return "on this mac"
        case .sarvam: return "sarvam"
        case .openclicky: return "openai"
        case .assemblyai: return "assemblyai"
        }
    }

    /// Who hears the voice, for the Settings summary ("hears you: this mac").
    static func listenerName(for engine: DictationEngineChoice) -> String {
        switch engine {
        case .offline: return "this mac"
        case .sarvam: return "sarvam"
        case .openclicky: return "openai"
        case .assemblyai: return "assemblyai"
        }
    }

    /// The speech-to-text model behind the engine, for the Settings summary.
    static func speechToTextName(for engine: DictationEngineChoice) -> String {
        switch engine {
        case .offline: return "apple"
        case .sarvam: return "saaras v4"
        case .openclicky: return "openai, via openclicky"
        case .assemblyai: return "assemblyai streaming"
        }
    }

    /// Nil when the engine can run; otherwise the pill's orange call to action.
    static func blockedPillLabel(for engine: DictationEngineChoice) -> String? {
        guard DictationEngineResolver.unavailableReason(for: engine) != nil else { return nil }
        return engine == .sarvam ? "add sarvam key ›" : "set up backend ›"
    }

    /// Where the pill (and the Home warning card) sends the user: the voice page for Sarvam's key,
    /// shell.json for the backend the other engines need, the voice page when nothing is missing.
    @MainActor
    static func openSetup(for engine: DictationEngineChoice, companionManager: CompanionManager) {
        if DictationEngineResolver.unavailableReason(for: engine) != nil && engine != .sarvam {
            OpenClickyConfiguration.revealSettingsFile()
        } else {
            companionManager.showDictationWindow(settingsPage: .voice)
        }
    }
}

// MARK: - Home

/// Home answers three questions top to bottom: what the pointer is doing (and the button that
/// docks or releases it), how to call Clicky (the four shortcuts), and what it can use (skills and
/// integrations in one tile row). 512 × 232 pt including the menu-bar band.
struct NotchHomeView: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var model: NotchHUDModel

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 16) {
                NotchPointerCard(companionManager: companionManager, model: model)
                    .frame(width: 232)

                if let engineWarning = NotchEngineWarning(engine: companionManager.dictationSettings.engine) {
                    NotchEngineWarningView(warning: engineWarning, companionManager: companionManager)
                } else {
                    NotchShortcutList(companionManager: companionManager, settings: companionManager.dictationSettings)
                }
            }

            NotchSkillsAndIntegrationsRow(store: companionManager.skillLibraryStore)
        }
        .padding(.horizontal, DS.HUD.bodySidePadding)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

/// The pointer card: one line saying what the pointer is doing right now, the primary button that
/// docks or releases it, (i) "what can you do?", and a hint.
struct NotchPointerCard: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var model: NotchHUDModel

    var body: some View {
        let isDocked = companionManager.isCursorDocked
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                NotchPointerMark(isDimmed: isDocked)
                Text(pointerStateText)
                    .font(.system(size: 12))
                    .foregroundColor(DS.HUD.text2)
                    .lineLimit(1)
                    .minimumScaleFactor(0.85)
            }

            HStack(spacing: 8) {
                Button(action: { companionManager.setCursorDocked(!isDocked) }) {
                    Text(isDocked ? "release the pointer" : "dock in the notch")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundColor(.black)
                        .frame(maxWidth: .infinity)
                        .frame(height: DS.HUD.primaryButtonHeight)
                        .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(isDocked ? DS.HUD.pointer : DS.HUD.text))
                        .contentShape(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous))
                }
                .buttonStyle(.plain)
                .pointerCursor(isEnabled: companionManager.voiceState == .idle)
                .disabled(companionManager.voiceState != .idle)
                .opacity(companionManager.voiceState == .idle ? 1 : 0.5)

                // (i): the buddy types out what OpenClicky does, next to itself (or in this island
                // while it is docked). The panel closes so the buddy is in view.
                Button(action: {
                    model.close()
                    companionManager.explainWhatOpenClickyDoes()
                }) {
                    Text("i")
                        .font(.system(size: 17, design: .serif).italic())
                        .foregroundColor(DS.HUD.text)
                        .frame(width: DS.HUD.primaryButtonHeight, height: DS.HUD.primaryButtonHeight)
                        .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(DS.HUD.surfaceRaisedSoft))
                        .contentShape(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous))
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .help("what can you do?")
                .accessibilityLabel("what can you do?")
            }

            if let hintText {
                Text(hintText)
                    .font(.system(size: 11))
                    .foregroundColor(DS.HUD.text3)
                    .lineLimit(1)
            }
        }
        .padding(12)
        // Without a hint line the status and button sit in the middle rather than leave a gap below.
        .frame(height: 104, alignment: hintText == nil ? .center : .top)
        .background(RoundedRectangle(cornerRadius: DS.HUD.cardRadius, style: .continuous).fill(DS.HUD.surface))
    }

    /// What the pointer is doing now, from the dock state, the "show the pointer" switch and the
    /// "the pointer shows" setting.
    private var pointerStateText: String {
        if companionManager.isCursorDocked { return "pointer is docked under the notch" }
        if !companionManager.isClickyCursorEnabled { return "pointer shows only while you talk" }
        switch companionManager.pointerPresence {
        case .always: return "pointer is following your mouse"
        case .whileMoving: return "pointer follows while the mouse moves"
        case .onShake: return companionManager.isPointerAwake ? "pointer is following your mouse" : "pointer rests until you shake the mouse"
        }
    }

    private var hintText: String? {
        if companionManager.isCursorDocked { return "talk still works while docked" }
        if companionManager.isClickyCursorEnabled && companionManager.pointerPresence == .onShake {
            return "or shake the mouse"
        }
        return nil
    }
}

/// CALL CLICKY: the four shortcuts `CompanionShortcutRecognizer` knows.
struct NotchShortcutList: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var settings: DictationSettings

    var body: some View {
        let dictationKey = settings.dictationKey.keycapLabel
        VStack(alignment: .leading, spacing: 2) {
            NotchSectionHeader(title: "CALL CLICKY")
                .frame(height: 16, alignment: .top)
            shortcutRow(title: "talk", keys: ["⌃", "⌥"], gesture: "hold", isLive: false)
            shortcutRow(title: "type", keys: ["⌃"], gesture: "tap ×2", isLive: false)
            shortcutRow(title: "dictate", keys: [dictationKey], gesture: "hold", isLive: false)
            shortcutRow(title: "hands-free", keys: [dictationKey, "⌃"], gesture: "tap ×2", isLive: companionManager.isAlwaysListening)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func shortcutRow(title: String, keys: [String], gesture: String, isLive: Bool) -> some View {
        HStack(spacing: 6) {
            Text(title).font(.system(size: 13)).foregroundColor(DS.HUD.text)
            if isLive {
                Circle().fill(DS.HUD.live).frame(width: 6, height: 6).help("hands-free is on")
            }
            Spacer(minLength: 4)
            HStack(spacing: 4) {
                ForEach(keys, id: \.self) { key in NotchKeycap(text: key) }
                Text(gesture)
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundColor(DS.HUD.text3)
            }
        }
        .frame(height: 22)
        .accessibilityElement(children: .combine)
    }
}

/// A key as the HUD draws it: SF Mono 11 on a raised 5 pt keycap.
struct NotchKeycap: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 11, design: .monospaced))
            .foregroundColor(DS.HUD.text)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(DS.HUD.surfaceRaised))
    }
}

/// The small uppercase heading over a HUD group ("CALL CLICKY", "SKILLS & INTEGRATIONS").
struct NotchSectionHeader: View {
    let title: String

    var body: some View {
        Text(title)
            .font(.system(size: 11, weight: .semibold))
            .tracking(0.44)
            .foregroundColor(DS.HUD.text3)
            .lineLimit(1)
    }
}

/// Shown in place of the shortcut list while the chosen dictation engine cannot run, because
/// every shortcut that dictates would fail until it is fixed.
struct NotchEngineWarning: Equatable {
    let heading: String
    let message: String
    let buttonTitle: String
    let engine: DictationEngineChoice

    init?(engine: DictationEngineChoice) {
        guard DictationEngineResolver.unavailableReason(for: engine) != nil else { return nil }
        self.engine = engine
        if engine == .sarvam {
            heading = "SARVAM NEEDS A KEY"
            message = "you picked sarvam to hear you, but no key is saved. dictation stays off until it is."
            buttonTitle = "add key in settings"
        } else {
            let listener = NotchVoiceDestination.listenerName(for: engine)
            heading = "\(listener.uppercased()) NEEDS THE BACKEND"
            message = "you picked \(listener) to hear you, which goes through openclicky. sign in or add your own key."
            buttonTitle = "open shell.json"
        }
    }
}

struct NotchEngineWarningView: View {
    let warning: NotchEngineWarning
    @ObservedObject var companionManager: CompanionManager

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            NotchSectionHeader(title: warning.heading)
            Text(warning.message)
                .font(.system(size: 12))
                .foregroundColor(DS.HUD.text2)
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
            Button(action: { NotchVoiceDestination.openSetup(for: warning.engine, companionManager: companionManager) }) {
                Text(warning.buttonTitle)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(DS.HUD.text)
                    .padding(.horizontal, 10)
                    .frame(height: 26)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DS.HUD.surfaceRaised))
            }
            .buttonStyle(.plain)
            .pointerCursor()
        }
        .padding(.top, 2)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: - Skills & integrations (Home)

/// One tile row for everything Clicky can use: the two integrations (Composio, Computer Use), a
/// divider, one 40 pt tile per library skill (click toggles it; a switched-off skill is struck
/// through), and "+". With no skills yet the "+" is a wide "teach a skill" tile with an example,
/// so the row never ends in a lone tile. "+" swaps the row for the field that drafts a new
/// SKILL.md through the backend and activates it. App-teaching skills are automatic and have no
/// tile ("app know-how is built in").
struct NotchSkillsAndIntegrationsRow: View {
    @ObservedObject var store: SkillLibraryStore
    @State private var isComposing = false
    @State private var request = ""
    /// The skill tile under the mouse: the header line then says what it does and how to use it.
    @State private var hoveredSkillID: String?
    @FocusState private var isRequestFieldFocused: Bool

    private let tileSize = DS.HUD.tileSize
    private let exampleSkillRequest = "summarise any page i’m on"

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let hoveredSkill = store.librarySkills.first(where: { $0.id == hoveredSkillID }) {
                skillExplanation(hoveredSkill)
            } else {
                header
            }

            HStack(spacing: 8) {
                integrationTile(
                    letters: "C", tint: DS.HUD.composio, letterColor: DS.HUD.text, title: "composio",
                    isConfigured: OpenClickyConfiguration.settings.composioMcpUrl != nil, settingName: "COMPOSIO_MCP_URL"
                )
                integrationTile(
                    letters: "CU", tint: DS.HUD.computerUse, letterColor: .black, title: "computer use",
                    isConfigured: OpenClickyConfiguration.resolvedCuaDriverBin != nil, settingName: "CUA_DRIVER_BIN"
                )
                Rectangle()
                    .fill(DS.HUD.surfaceRaisedSoft)
                    .frame(width: 1)
                    .padding(.vertical, 6)
                    .padding(.horizontal, 4)

                if isComposing || store.isCreating {
                    composer
                } else if store.librarySkills.isEmpty {
                    teachASkillTile
                } else {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 8) {
                            ForEach(store.librarySkills, id: \.id) { skill in
                                skillTile(skill)
                            }
                            addSkillTile
                        }
                    }
                }
            }
            .frame(height: tileSize)
        }
    }

    private var header: some View {
        HStack {
            NotchSectionHeader(title: "SKILLS & INTEGRATIONS")
            Spacer()
            if let error = store.lastError {
                Text(error)
                    .font(.system(size: 11))
                    .foregroundColor(DS.HUD.pointer)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .help(error)
            } else {
                Text("app know-how is built in")
                    .font(.system(size: 11))
                    .foregroundColor(DS.HUD.text3)
            }
        }
    }

    /// What a hovered skill tile is and how to use it, in the header's place: its name, how to
    /// call on it (or that it is off), then its description, cut to the line.
    private func skillExplanation(_ skill: SkillFile) -> some View {
        let isActive = store.activeIds.contains(skill.id)
        let howToUse: String
        if !isActive {
            howToUse = "off · click to turn on"
        } else if skill.isForTalk {
            howToUse = "hold ⌃ ⌥ and ask"
        } else {
            howToUse = "used when the agent works"
        }
        return (Text(skill.name.lowercased()).font(.system(size: 11, weight: .semibold)).foregroundColor(DS.HUD.text)
            + Text("  \(howToUse)").font(.system(size: 11, weight: .medium)).foregroundColor(isActive ? DS.HUD.live : DS.HUD.waiting)
            + Text("  \(skill.description)").font(.system(size: 11)).foregroundColor(DS.HUD.text3))
            .lineLimit(1)
            .truncationMode(.tail)
            .frame(height: 14, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// An integration lights up in its colour once configured; until then it is dimmed and a click
    /// reveals shell.json, where its setting goes.
    private func integrationTile(letters: String, tint: Color, letterColor: Color, title: String, isConfigured: Bool, settingName: String) -> some View {
        Button(action: { if !isConfigured { OpenClickyConfiguration.revealSettingsFile() } }) {
            Text(letters)
                .font(.system(size: 12, weight: .bold))
                .foregroundColor(isConfigured ? letterColor : DS.HUD.textOff)
                .frame(width: tileSize, height: tileSize)
                .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(isConfigured ? tint : Color(hex: "#141414")))
                .overlay(
                    RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous)
                        .strokeBorder(isConfigured ? Color.clear : DS.HUD.surfaceRaised, lineWidth: 1)
                )
        }
        .buttonStyle(.plain)
        .pointerCursor(isEnabled: !isConfigured)
        .help(isConfigured ? "\(title) connected" : "\(title) is not set up. click to set \(settingName) in shell.json")
        .accessibilityLabel(isConfigured ? "\(title), connected" : "\(title), not set up")
    }

    private var teachASkillTile: some View {
        Button(action: startComposing) {
            HStack(spacing: 10) {
                Text("+").font(.system(size: 18)).foregroundColor(DS.HUD.text2)
                Text("teach a skill").font(.system(size: 13, weight: .medium)).foregroundColor(DS.HUD.text)
                Text("“\(exampleSkillRequest)”")
                    .font(.system(size: 12))
                    .foregroundColor(DS.HUD.text3)
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 12)
            .frame(maxWidth: .infinity)
            .frame(height: tileSize)
            .background(dashedTileOutline)
            .contentShape(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous))
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help("teach clicky a skill: say what it should do, and it writes the skill")
        .contextMenu { openSkillsFolderMenuItem }
    }

    private var addSkillTile: some View {
        Button(action: startComposing) {
            Text("+")
                .font(.system(size: 18))
                .foregroundColor(DS.HUD.text2)
                .frame(width: tileSize, height: tileSize)
                .background(dashedTileOutline)
                .contentShape(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous))
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help("teach a skill, or open the skills folder from the tile's menu")
        .accessibilityLabel("teach a skill")
        .contextMenu { openSkillsFolderMenuItem }
    }

    private var dashedTileOutline: some View {
        RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous)
            .strokeBorder(DS.HUD.dashedLine, style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
    }

    private var openSkillsFolderMenuItem: some View {
        Button("open skills folder") { NSWorkspace.shared.open(store.userSkillsDirectory) }
    }

    private func skillTile(_ skill: SkillFile) -> some View {
        let isActive = store.activeIds.contains(skill.id)
        return Button(action: { store.setActive(skill.id, !isActive) }) {
            Text(String(skill.name.prefix(3)).lowercased())
                .font(.system(size: 11, weight: .semibold))
                .strikethrough(!isActive, color: DS.HUD.textOff)
                .foregroundColor(isActive ? DS.HUD.text : DS.HUD.textOff)
                .frame(width: tileSize, height: tileSize)
                .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(isActive ? DS.HUD.surfaceRaised : Color(hex: "#141414")))
                .overlay(
                    RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous)
                        .strokeBorder(isActive ? Color.clear : DS.HUD.surfaceRaised, lineWidth: 1)
                )
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .onHoverInPanel { isHovering in
            if isHovering {
                hoveredSkillID = skill.id
            } else if hoveredSkillID == skill.id {
                hoveredSkillID = nil
            }
        }
        .accessibilityLabel("\(skill.name), \(isActive ? "on" : "off")")
        .contextMenu {
            Button(isActive ? "turn off" : "turn on") { store.setActive(skill.id, !isActive) }
            Button("show in finder") { NSWorkspace.shared.activateFileViewerSelecting([store.libraryDirectory.appendingPathComponent(skill.id, isDirectory: true)]) }
            Divider()
            Button("remove from library", role: .destructive) { store.removeSkill(skill.id) }
        }
    }

    private var composer: some View {
        HStack(spacing: 6) {
            Text("+").font(.system(size: 15)).foregroundColor(DS.HUD.text2)
            TextField(exampleSkillRequest, text: $request)
                .textFieldStyle(.plain)
                .font(.system(size: 13))
                .foregroundColor(DS.HUD.text)
                .focused($isRequestFieldFocused)
                .onSubmit(create)
                .onExitCommand { isComposing = false }
                .disabled(store.isCreating)
            if store.isCreating {
                ProgressView().controlSize(.mini)
            } else {
                Button(action: { isComposing = false; request = "" }) {
                    Image(systemName: "xmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundColor(DS.HUD.text3)
                        .frame(width: DS.HUD.minimumHitSize, height: DS.HUD.minimumHitSize)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .pointerCursor()
                .help("cancel")
            }
        }
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity)
        .frame(height: tileSize)
        .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(DS.HUD.surface))
    }

    private func startComposing() {
        isComposing = true
        isRequestFieldFocused = true
    }

    private func create() {
        let text = request.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !store.isCreating else { return }
        Task { @MainActor in
            do {
                _ = try await store.createSkill(request: text)
                request = ""
                isComposing = false
            } catch {
                // The store publishes `lastError`; the composer stays open so the user can retry.
            }
        }
    }
}

// MARK: - Agents

/// Loads recent Codex threads through the CLI for the Agents tab.
@MainActor
final class AgentThreadStore: ObservableObject {
    @Published private(set) var threads: [OpenClickyThreadSummary] = []
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?
    private let agentClient = OpenClickyAgentClient()
    private var lastRefresh: Date?

    func refresh(force: Bool = false) {
        if !force, let lastRefresh, Date().timeIntervalSince(lastRefresh) < 10, !threads.isEmpty { return }
        guard !isLoading, OpenClickyConfiguration.isConfigured else { return }
        isLoading = true
        errorMessage = nil
        Task {
            do {
                threads = try await agentClient.listThreads(limit: 30)
                lastRefresh = Date()
            } catch {
                errorMessage = error.localizedDescription
            }
            isLoading = false
        }
    }

    /// Threads that are running or need the user, for the badge on the agents tab.
    var attentionCount: Int {
        threads.filter { AgentThreadStatus(codexStatus: $0.status) != .done }.count
    }

    /// Threads grouped by day, newest first.
    var sections: [(title: String, threads: [OpenClickyThreadSummary])] {
        let calendar = Calendar.current
        var groups: [(String, [OpenClickyThreadSummary])] = []
        for thread in threads.sorted(by: { $0.updatedAt > $1.updatedAt }) {
            let title: String
            if calendar.isDateInToday(thread.updatedDate) { title = "TODAY" }
            else if calendar.isDateInYesterday(thread.updatedDate) { title = "YESTERDAY" }
            else { title = thread.updatedDate.formatted(.dateTime.month(.abbreviated).day()).uppercased() }
            if let index = groups.firstIndex(where: { $0.0 == title }) { groups[index].1.append(thread) }
            else { groups.append((title, [thread])) }
        }
        return groups.map { (title: $0.0, threads: $0.1) }
    }
}

/// A thread's state as the list marks it. Codex reports a thread as `active` while a turn runs,
/// `systemError` when it broke, and `idle` / `notLoaded` once it is finished; a status naming
/// approval or waiting means the agent asked the user something.
enum AgentThreadStatus: Equatable {
    case running
    case needsYou
    case stopped
    case done

    init(codexStatus: String) {
        let status = codexStatus.lowercased()
        if status.contains("approval") || status.contains("waiting") { self = .needsYou }
        else if status == "active" || status == "running" || status == "inprogress" { self = .running }
        else if status.contains("error") || status == "failed" { self = .stopped }
        else { self = .done }
    }

    var mark: String {
        switch self {
        case .running: return "●"
        case .needsYou, .stopped: return "◆"
        case .done: return "✓"
        }
    }

    var color: Color {
        switch self {
        case .running: return DS.HUD.pointer
        case .needsYou, .stopped: return DS.HUD.waiting
        case .done: return DS.HUD.live
        }
    }

    var word: String {
        switch self {
        case .running: return "running"
        case .needsYou: return "needs you"
        case .stopped: return "stopped"
        case .done: return "done"
        }
    }
}

/// The Agents tab: a dense list (status mark, title, status line, time, open ↗) grouped by day, and
/// a composer under it that continues the selected thread or starts a new one. 512 × 392 pt.
struct NotchAgentsView: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var threadStore: AgentThreadStore
    /// The row the composer continues; nil starts a new thread.
    @State private var selectedThreadId: String?
    @State private var composerText = ""

    private var workspaceDisplayPath: String {
        OpenClickyConfiguration.workspacePath.replacingOccurrences(of: NSHomeDirectory(), with: "~")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if !OpenClickyConfiguration.isConfigured {
                emptyState("connect a backend to see your agents.", detail: "sign in from settings, or add a token to ~/.openclicky/shell.json.")
                Spacer(minLength: 0)
            } else if threadStore.threads.isEmpty && threadStore.isLoading {
                emptyState("loading agents…", detail: nil)
                Spacer(minLength: 0)
            } else if let errorMessage = threadStore.errorMessage, threadStore.threads.isEmpty {
                emptyState("couldn't load agents", detail: errorMessage)
                Spacer(minLength: 0)
            } else if threadStore.threads.isEmpty {
                emptyState("no agents yet", detail: "hold ⌃⌥ and ask clicky to do something, or type it below.")
                Spacer(minLength: 0)
            } else {
                ScrollView(showsIndicators: false) {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(Array(threadStore.sections.enumerated()), id: \.element.title) { sectionIndex, section in
                            HStack {
                                NotchSectionHeader(title: section.title)
                                Spacer()
                                if sectionIndex == 0 {
                                    Text("workspace \(workspaceDisplayPath)")
                                        .font(.system(size: 11))
                                        .foregroundColor(DS.HUD.text3)
                                        .lineLimit(1)
                                        .truncationMode(.middle)
                                }
                            }
                            VStack(spacing: 0) {
                                ForEach(Array(section.threads.enumerated()), id: \.element.id) { rowIndex, thread in
                                    AgentRowView(
                                        thread: thread,
                                        isSelected: selectedThreadId == thread.id,
                                        showsTopDivider: rowIndex > 0,
                                        workspacePath: OpenClickyConfiguration.workspacePath,
                                        onSelect: { selectedThreadId = selectedThreadId == thread.id ? nil : thread.id },
                                        onOpen: { companionManager.openAgentResultCard(threadId: thread.id) }
                                    )
                                }
                            }
                            .background(RoundedRectangle(cornerRadius: DS.HUD.cardRadius, style: .continuous).fill(DS.HUD.surface))
                            .clipShape(RoundedRectangle(cornerRadius: DS.HUD.cardRadius, style: .continuous))
                        }
                    }
                }
            }

            composer
            if let hint = AccountCapabilities.current().agentUnavailableHint {
                Text(hint).font(.system(size: 12)).foregroundColor(DS.HUD.text3)
            }
        }
        .padding(.horizontal, DS.HUD.bodySidePadding)
        .padding(.bottom, 16)
        .onAppear { threadStore.refresh() }
    }

    private var selectedThreadTitle: String? {
        guard let selectedThreadId, let thread = threadStore.threads.first(where: { $0.id == selectedThreadId }) else { return nil }
        return AgentRowView.title(for: thread)
    }

    private var isComposerEnabled: Bool {
        OpenClickyConfiguration.isConfigured && AccountCapabilities.current().usesAgent && companionManager.voiceState == .idle
    }

    private var composer: some View {
        HStack(spacing: 8) {
            NotchComposerTextField(
                text: $composerText,
                placeholder: selectedThreadTitle.map { "continue “\($0)”…" } ?? "continue the selected thread, or start a new one…",
                onSubmit: submitComposer,
                onEscape: { composerText = ""; selectedThreadId = nil }
            )
            .disabled(!isComposerEnabled)
            Text("↵")
                .font(.system(size: 11, design: .monospaced))
                .foregroundColor(DS.HUD.text3)
        }
        .padding(.horizontal, 12)
        .frame(height: 36)
        .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(DS.HUD.surface))
        .opacity(isComposerEnabled ? 1 : 0.5)
        .help(selectedThreadId == nil ? "↵ starts a new agent thread. select a row to continue it instead." : "↵ continues the selected thread. click the row again to start a new one.")
    }

    private func submitComposer() {
        let text = composerText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, isComposerEnabled else { return }
        composerText = ""
        companionManager.submitTextToAgent(text, threadId: selectedThreadId, startsNewThread: selectedThreadId == nil)
        // The new turn shows up as a running thread once the CLI has started it.
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { threadStore.refresh(force: true) }
    }

    private func emptyState(_ title: String, detail: String?) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 13, weight: .semibold)).foregroundColor(DS.HUD.text)
            if let detail { Text(detail).font(.system(size: 12)).foregroundColor(DS.HUD.text3) }
        }
        .padding(.top, 4)
    }
}

/// One agent thread in the list: 52 pt, status mark, title, status line, time, open ↗. A click on
/// the row selects it for the composer; "open ↗" opens the result card.
struct AgentRowView: View {
    let thread: OpenClickyThreadSummary
    let isSelected: Bool
    let showsTopDivider: Bool
    let workspacePath: String
    let onSelect: () -> Void
    let onOpen: () -> Void

    static func title(for thread: OpenClickyThreadSummary) -> String {
        let firstLine = thread.preview.split(separator: "\n").first.map(String.init) ?? thread.preview
        let trimmed = firstLine.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "untitled agent" : trimmed.prefix(1).uppercased() + trimmed.dropFirst()
    }

    private var status: AgentThreadStatus { AgentThreadStatus(codexStatus: thread.status) }

    /// "done", or "done · ~/elsewhere" when the thread ran outside the configured workspace.
    private var statusLine: String {
        guard !thread.cwd.isEmpty, thread.cwd != workspacePath else { return status.word }
        return "\(status.word) · \(thread.cwd.replacingOccurrences(of: NSHomeDirectory(), with: "~"))"
    }

    var body: some View {
        HStack(spacing: 12) {
            Text(status.mark)
                .font(.system(size: 12))
                .foregroundColor(status.color)
                .frame(width: 20)
            VStack(alignment: .leading, spacing: 2) {
                Text(Self.title(for: thread))
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundColor(DS.HUD.text)
                    .lineLimit(1)
                    .truncationMode(.tail)
                Text(statusLine)
                    .font(.system(size: 12))
                    .foregroundColor(DS.HUD.text3)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(thread.updatedDate.formatted(date: .omitted, time: .shortened).lowercased())
                .font(.system(size: 12).monospacedDigit())
                .foregroundColor(DS.HUD.text3)
            Button(action: onOpen) {
                Text("open ↗")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(DS.HUD.text)
                    .padding(.horizontal, 10)
                    .frame(height: 26)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DS.HUD.surfaceRaised))
            }
            .buttonStyle(.plain)
            .pointerCursor()
            .help("open this agent's result")
        }
        .padding(.horizontal, 12)
        .frame(height: 52)
        .background(isSelected ? Color(hex: "#202020") : Color.clear)
        .overlay(alignment: .leading) {
            if isSelected { Rectangle().fill(DS.HUD.pointer).frame(width: 2) }
        }
        .overlay(alignment: .top) {
            if showsTopDivider { Rectangle().fill(DS.HUD.line).frame(height: 1) }
        }
        .contentShape(Rectangle())
        .onTapGesture(perform: onSelect)
        .pointerCursor()
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(Self.title(for: thread)), \(status.word)")
        .accessibilityHint(isSelected ? "selected: the composer continues this thread" : "select to continue this thread")
    }
}

// MARK: - Settings

/// The Settings tab: the two live toggles (the orb, agent mode), a summary card per area
/// (dictation, backend & account, agent, pointer), and links to the full settings window,
/// shell.json and the own-key page. Under them, MORE keeps the island-only controls the window
/// does not have (language, realtime voice, always listening, the pointer, the menu bar icon,
/// signing in) so nothing that lived here is lost.
struct NotchSettingsView: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var settings: DictationSettings
    /// Mirrors `language` in shell.json; written back on change. See ReplyLanguage.swift.
    @State private var replyLanguageCode = ReplyLanguage.currentCode
    /// The account's allowance, for the plan row; refreshed whenever the tab appears.
    @StateObject private var billing = BillingStatusModel()

    private let summaryColumns = [GridItem(.flexible(), spacing: 8), GridItem(.flexible(), spacing: 8)]

    var body: some View {
        ScrollView(showsIndicators: false) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 8) {
                    liveToggle(title: "the orb", detail: "pill at the bottom", isOn: Binding(
                        get: { settings.orbVisible },
                        set: { settings.orbVisible = $0 }
                    ))
                    liveToggle(title: "agent mode", detail: "work goes to codex", isOn: Binding(
                        get: { companionManager.isAgentModeEnabled },
                        set: { companionManager.setAgentModeEnabled($0) }
                    ))
                }

                LazyVGrid(columns: summaryColumns, alignment: .leading, spacing: 8) {
                    summaryCard(title: "DICTATION", rows: [
                        ("hears you", NotchVoiceDestination.listenerName(for: settings.engine)),
                        ("speech-to-text", NotchVoiceDestination.speechToTextName(for: settings.engine)),
                        ("polish model", polishModelDescription),
                    ]) { companionManager.showDictationWindow(settingsPage: .voice) }
                    summaryCard(title: "BACKEND & ACCOUNT", rows: [
                        ("host", OpenClickyConfiguration.backendHostDescription),
                        ("token", OpenClickyConfiguration.isConfigured ? "configured" : "missing"),
                        ("plan", Self.planDescription(kind: AccountCapabilities.current().kind, summary: billing.summary)),
                    ]) { companionManager.showDictationWindow(settingsPage: .account) }
                    summaryCard(title: "AGENT", rows: [
                        ("workspace", OpenClickyConfiguration.workspacePath.replacingOccurrences(of: NSHomeDirectory(), with: "~")),
                        ("model", OpenClickyConfiguration.agentModelOverride ?? "codex"),
                    ]) { OpenClickyConfiguration.revealSettingsFile() }
                    summaryCard(title: "POINTER", rows: [
                        ("shows", companionManager.isClickyCursorEnabled ? companionManager.pointerPresence.label : "only while you talk"),
                        ("shortcuts", "see home"),
                    ]) { companionManager.showDictationWindow(settingsPage: .shortcuts) }
                }

                HStack(spacing: 8) {
                    Button(action: { companionManager.showDictationWindow(settingsPage: .general) }) {
                        Text("open all settings")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundColor(.black)
                            .frame(maxWidth: .infinity)
                            .frame(height: 32)
                            .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(DS.HUD.text))
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()
                    linkButton(title: "shell.json ↗", help: OpenClickyConfiguration.settingsFileURL.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) {
                        OpenClickyConfiguration.revealSettingsFile()
                    }
                    linkButton(title: "use my own key ↗", help: "use your own openai key instead of an account") {
                        companionManager.showDictationWindow(settingsPage: .account)
                    }
                }

                moreSection
            }
            .padding(.horizontal, DS.HUD.bodySidePadding)
            .padding(.bottom, 16)
        }
        .onAppear { billing.refresh() }
    }

    /// The model that polishes takes (`DictationEngineResolver.makePolisher`), or why none does.
    private var polishModelDescription: String {
        guard DictationEngineResolver.wantsModelPolish(settings: settings) else { return "off" }
        let sarvamKey = OpenClickyConfiguration.settings.sarvamKey?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !sarvamKey.isEmpty { return "sarvam-105b" }
        if OpenClickyConfiguration.isConfigured { return "openclicky" }
        return "none yet"
    }

    /// "account · 45% used" once the allowance has loaded, "account" until then.
    static func planDescription(kind: AccountKind, summary: BillingSummary?) -> String {
        switch kind {
        case .ownKeys: return "your own key"
        case .signedOut: return "not signed in"
        case .account:
            guard let summary, !summary.byok else { return "account" }
            return "account · \(Int((summary.fractionUsed * 100).rounded()))% used"
        }
    }

    private func liveToggle(title: String, detail: String, isOn: Binding<Bool>) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.system(size: 13, weight: .semibold)).foregroundColor(DS.HUD.text)
                Text(detail).font(.system(size: 11)).foregroundColor(DS.HUD.text3)
            }
            Spacer(minLength: 4)
            Toggle("", isOn: isOn)
                .toggleStyle(HUDSwitchToggleStyle())
                .labelsHidden()
                .pointerCursor()
                .accessibilityLabel(title)
        }
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity)
        .frame(height: 44)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DS.HUD.surface))
    }

    /// One area's summary; a click opens where that area is changed.
    private func summaryCard(title: String, rows: [(String, String)], onOpen: @escaping () -> Void) -> some View {
        Button(action: onOpen) {
            VStack(alignment: .leading, spacing: 6) {
                NotchSectionHeader(title: title)
                ForEach(rows, id: \.0) { row in
                    HStack(spacing: 6) {
                        Text(row.0).foregroundColor(DS.HUD.text2)
                        Spacer(minLength: 4)
                        Text(row.1).foregroundColor(DS.HUD.text).lineLimit(1).truncationMode(.middle)
                    }
                    .font(.system(size: 12))
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DS.HUD.surface))
            .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }

    private func linkButton(title: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 12))
                .foregroundColor(DS.HUD.text)
                .padding(.horizontal, 12)
                .frame(height: 32)
                .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(DS.HUD.surfaceRaised))
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help(help)
    }

    // MARK: More (the controls only the island has)

    private var moreSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            NotchSectionHeader(title: "MORE")
                .padding(.top, 6)
            VStack(spacing: 0) {
                languageRow
                moreToggleRow(title: "realtime voice", detail: "speech-to-speech over openai realtime (fast)", isOn: Binding(
                    get: { companionManager.isRealtimeVoiceEnabled },
                    set: { companionManager.setRealtimeVoiceEnabled($0) }
                ))
                moreToggleRow(title: "always listening", detail: "hands-free with barge-in (realtime only)", isOn: Binding(
                    get: { companionManager.isAlwaysListening },
                    set: { companionManager.setAlwaysListening($0) }
                ))
                moreToggleRow(title: "dock the pointer in the notch", detail: "the pointer lives in the hud", isOn: Binding(
                    get: { companionManager.isCursorDocked },
                    set: { companionManager.setCursorDocked($0) }
                ))
                moreToggleRow(title: "show the pointer", detail: "off: it appears only while you talk", isOn: Binding(
                    get: { companionManager.isClickyCursorEnabled },
                    set: { companionManager.setClickyCursorEnabled($0) }
                ))
                moreToggleRow(title: "menu bar icon", detail: "off by default: this hud is openclicky's home", isOn: Binding(
                    get: { companionManager.isMenuBarIconVisible },
                    set: { companionManager.setMenuBarIconVisible($0) }
                ))
            }
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DS.HUD.surface))

            // Sign in, or the plan and credits (BillingStatus.swift).
            VStack(spacing: 0) {
                NotchAccountSection(
                    row: { moreValueRow(systemImage: $0, title: $1, value: $2) },
                    action: { moreActionRow(systemImage: $0, title: $1, detail: $2, action: $3) },
                    openAccountPage: { companionManager.showDictationWindow(settingsPage: .account) }
                )
            }
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DS.HUD.surface))

            VStack(spacing: 0) {
                moreActionRow(systemImage: "macwindow", title: "open openclicky", detail: "history, dictionary, shortcuts, styles") {
                    companionManager.showDictationWindow()
                }
                moreActionRow(systemImage: "ladybug", title: "report a bug", detail: "opens the project's issue tracker") {
                    if let url = URL(string: "https://github.com/prasanthsasikumar/openclicky/issues") { NSWorkspace.shared.open(url) }
                }
                moreActionRow(systemImage: "power", title: "quit openclicky", detail: nil) { NSApp.terminate(nil) }
            }
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DS.HUD.surface))
        }
    }

    /// The one language OpenClicky speaks and listens in. Takes effect on the next turn: the
    /// Realtime session re-sends its instructions and transcription language when they change.
    private var languageRow: some View {
        HStack {
            VStack(alignment: .leading, spacing: 1) {
                Text("language").font(.system(size: 13, weight: .semibold)).foregroundColor(DS.HUD.text)
                Text("clicky speaks and listens in this").font(.system(size: 11)).foregroundColor(DS.HUD.text3)
            }
            Spacer()
            Picker("", selection: Binding(
                get: { replyLanguageCode },
                set: { chosenLanguageCode in
                    replyLanguageCode = chosenLanguageCode
                    OpenClickyConfiguration.update { $0.language = chosenLanguageCode }
                }
            )) {
                ForEach(ReplyLanguage.choices) { choice in Text(choice.name).tag(choice.code) }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .frame(width: 120)
            .pointerCursor()
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private func moreToggleRow(title: String, detail: String, isOn: Binding<Bool>) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.system(size: 13, weight: .semibold)).foregroundColor(DS.HUD.text)
                Text(detail).font(.system(size: 11)).foregroundColor(DS.HUD.text3)
            }
            Spacer()
            Toggle("", isOn: isOn)
                .toggleStyle(HUDSwitchToggleStyle())
                .labelsHidden()
                .pointerCursor()
                .accessibilityLabel(title)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .overlay(alignment: .top) { Rectangle().fill(DS.HUD.line).frame(height: 1) }
    }

    private func moreValueRow(systemImage: String, title: String, value: String) -> some View {
        HStack {
            Image(systemName: systemImage).font(.system(size: 12)).foregroundColor(DS.HUD.text3).frame(width: 18)
            Text(title.lowercased()).font(.system(size: 13, weight: .semibold)).foregroundColor(DS.HUD.text)
            Spacer()
            Text(value).font(.system(size: 12)).foregroundColor(DS.HUD.text2).lineLimit(1).truncationMode(.middle)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }

    private func moreActionRow(systemImage: String, title: String, detail: String?, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Image(systemName: systemImage).font(.system(size: 12)).foregroundColor(DS.HUD.text3).frame(width: 18)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title.lowercased()).font(.system(size: 13, weight: .semibold)).foregroundColor(DS.HUD.text)
                    if let detail { Text(detail).font(.system(size: 11)).foregroundColor(DS.HUD.text3).lineLimit(1).truncationMode(.middle) }
                }
                Spacer()
                Image(systemName: "chevron.right").font(.system(size: 10, weight: .semibold)).foregroundColor(DS.HUD.text3)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }
}

// MARK: - Agent result card (top-right)

private final class AgentResultPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

/// The floating card HeyClicky shows top-right when an agent finishes or you open one:
/// title, status, the agent's summary, Copy, and a "Follow up with agent" field.
@MainActor
final class AgentResultPanelManager {
    private var panel: AgentResultPanel?
    private let state = AgentResultPanelState()
    private let agentClient = OpenClickyAgentClient()

    func show(threadId: String, companionManager: CompanionManager) {
        state.threadId = threadId
        state.title = "Agent"
        state.summary = ""
        state.status = "loading"
        state.isLoading = true
        presentPanel(companionManager: companionManager)
        Task {
            do {
                let detail = try await agentClient.readThread(threadId)
                state.title = detail.thread.preview.split(separator: "\n").first.map(String.init) ?? "Agent"
                state.summary = detail.lastAgentMessage ?? "(no reply yet)"
                state.status = detail.turns.last?.status ?? "done"
            } catch {
                state.summary = "Couldn't load this agent: \(error.localizedDescription)"
                state.status = "error"
            }
            state.isLoading = false
        }
    }

    func hide() {
        panel?.orderOut(nil)
    }

    private func presentPanel(companionManager: CompanionManager) {
        if panel == nil {
            let newPanel = AgentResultPanel(
                contentRect: NSRect(x: 0, y: 0, width: 400, height: 240),
                styleMask: [.borderless, .nonactivatingPanel],
                backing: .buffered,
                defer: false
            )
            newPanel.isOpaque = false
            newPanel.backgroundColor = .clear
            newPanel.hasShadow = true
            newPanel.level = .floating
            newPanel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            newPanel.isReleasedWhenClosed = false
            newPanel.hidesOnDeactivate = false
            newPanel.contentView = NSHostingView(rootView: AgentResultView(state: state, companionManager: companionManager, onClose: { [weak self] in self?.hide() }))
            panel = newPanel
        }
        guard let panel, let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let visibleFrame = screen.visibleFrame
        let origin = NSPoint(x: visibleFrame.maxX - panel.frame.width - 20, y: visibleFrame.maxY - panel.frame.height - 12)
        panel.setFrameOrigin(origin)
        panel.makeKeyAndOrderFront(nil)
    }
}

@MainActor
final class AgentResultPanelState: ObservableObject {
    @Published var threadId: String = ""
    @Published var title: String = ""
    @Published var summary: String = ""
    @Published var status: String = ""
    @Published var isLoading = false
}

struct AgentResultView: View {
    @ObservedObject var state: AgentResultPanelState
    @ObservedObject var companionManager: CompanionManager
    var onClose: () -> Void
    @State private var followUpText = ""
    @State private var didCopy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Text(state.title)
                    .font(.system(size: 12, weight: .bold))
                    .foregroundColor(.white)
                    .lineLimit(1)
                Spacer()
                Text(statusLabel)
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundColor(.white)
                    .padding(.horizontal, 7)
                    .padding(.vertical, 3)
                    .background(Capsule().fill(statusColor))
                Button(action: onClose) {
                    Image(systemName: "xmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundColor(Color.white.opacity(0.7))
                        .frame(width: 18, height: 18)
                        .background(Circle().fill(Color.white.opacity(0.12)))
                }
                .buttonStyle(.plain)
                .pointerCursor()
            }

            if state.isLoading {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Loading…").font(.system(size: 11)).foregroundColor(Color.white.opacity(0.6))
                }
            } else {
                ScrollView(showsIndicators: false) {
                    Text(liveSummary)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundColor(.white)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 90)

                Button(action: copySummary) {
                    HStack(spacing: 5) {
                        Image(systemName: didCopy ? "checkmark" : "doc.on.doc").font(.system(size: 10, weight: .semibold))
                        Text(didCopy ? "Copied" : "Copy").font(.system(size: 11, weight: .semibold))
                    }
                    .foregroundColor(.white)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .background(Capsule().fill(Color.white.opacity(0.14)))
                }
                .buttonStyle(.plain)
                .pointerCursor()
            }

            HStack(spacing: 8) {
                Image(systemName: "mic.fill").font(.system(size: 11)).foregroundColor(Color.white.opacity(0.7))
                TextField("Follow up with agent", text: $followUpText)
                    .textFieldStyle(.plain)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(.white)
                    .onSubmit(sendFollowUp)
                    .disabled(companionManager.voiceState != .idle)
                Image(systemName: "keyboard").font(.system(size: 11)).foregroundColor(Color.white.opacity(0.35))
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.white.opacity(0.10)))

            if let agentActivityText = companionManager.agentActivityText, companionManager.voiceState == .processing {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.mini)
                    Text(agentActivityText).font(.system(size: 10)).foregroundColor(Color.white.opacity(0.6)).lineLimit(1)
                }
            }
        }
        .padding(16)
        .frame(width: 400)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Color(hex: "#2B2D31").opacity(0.96)))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Color.white.opacity(0.12), lineWidth: 1))
    }

    /// Prefer the freshest result for this thread over the loaded history.
    private var liveSummary: String {
        if let lastResult = companionManager.lastAgentResult, lastResult.threadId == state.threadId, lastResult.finishedAt > Date().addingTimeInterval(-3600) {
            return lastResult.text.isEmpty ? state.summary : lastResult.text
        }
        return state.summary
    }

    private var statusLabel: String {
        if companionManager.voiceState == .processing { return "Working" }
        switch state.status {
        case "completed", "done": return "Done"
        case "loading": return "…"
        case "error": return "Error"
        default: return state.status.capitalized
        }
    }

    private var statusColor: Color {
        if companionManager.voiceState == .processing { return DS.Colors.warning.opacity(0.8) }
        return state.status == "error" ? DS.Colors.overlayCursorColor.opacity(0.8) : DS.Colors.accent.opacity(0.8)
    }

    private func copySummary() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(liveSummary, forType: .string)
        didCopy = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { didCopy = false }
    }

    private func sendFollowUp() {
        let text = followUpText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        followUpText = ""
        companionManager.submitTextToAgent(text, threadId: state.threadId)
    }
}
