//
//  NotchHUDPanels.swift
//  OpenClicky
//
//  The open island, one short panel (the pointer, the shortcuts, Settings and quit; no tabs), and
//  the floating result card (top-right) with "Follow up with agent". Drawn with the refinement
//  sheet's HUD tokens (`DS.HUD`).
//

import AppKit
import Combine
import SwiftUI

// MARK: - Full panel

/// The open island, one short panel (Claude Design, "one compact panel"): the band beside the
/// camera holds what the pointer is doing and the three round buttons (voice, Settings, quit); the
/// body is one row — dock or release the pointer, (i), and the four shortcuts as a 2 × 2 grid. A
/// side drawer has no camera to sit beside, so it stacks the same pieces. Hand-drawn throughout:
/// the island never becomes key, and system controls grey out in a panel that isn't.
struct NotchFullPanelView: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager

    /// The component in the slot right now.
    private var activeComponent: any IslandComponent {
        model.registry.first { $0.id == model.selection.active } ?? model.registry[0]
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            NotchBandRow(model: model, companionManager: companionManager)
                .padding(.top, 6)
                .frame(height: model.bandRowHeight)
            activeComponent.makeView(companionManager: companionManager, model: model)
                .padding(.top, model.placement.isSideDrawer ? 6 : 2)
                .padding(.horizontal, DS.HUD.bodySidePadding)
                .frame(maxWidth: .infinity, alignment: .topLeading)
                .frame(height: model.slotHeight, alignment: .top)
                .id(model.selection.active)
                .transition(.opacity)
        }
        .animation(.easeInOut(duration: 0.18), value: model.selection)
    }
}

// MARK: - Band

/// The band: the pointer's status on the left of the camera, the voice pill, Settings and quit on
/// the right. On a notch screen each side only has what the notch leaves free.
struct NotchBandRow: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager

    private var sideWidth: CGFloat {
        guard model.geometry.hasHardwareNotch, !model.placement.isSideDrawer else { return .infinity }
        return max(0, (model.fullWidth - model.geometry.notchWidth) / 2 - 12)
    }

    var body: some View {
        HStack(spacing: 8) {
            // The pointer's mark (its state on hover) and, when there is more than one component
            // to show, the capsule that flips them: no standing line of text, no row of its own.
            HStack(spacing: 10) {
                NotchPointerMark(isDimmed: false)
                    .help(NotchPointerStatus.statusText(model: model, companionManager: companionManager))
                    .accessibilityLabel(NotchPointerStatus.statusText(model: model, companionManager: companionManager))
                if model.showsCapsule {
                    IslandNavigationCapsule(model: model, inBand: true)
                }
            }
            .frame(maxWidth: sideWidth, alignment: .leading)
            Spacer(minLength: 8)
            HStack(spacing: 6) {
                VoiceDestinationPill(companionManager: companionManager, settings: companionManager.dictationSettings)
                // One round button per tool that is on: it shows that tool, and a second click
                // goes back to where the island was.
                ForEach(model.toolIDs, id: \.self) { toolID in
                    if let tool = model.registry.first(where: { $0.id == toolID }) {
                        let isActive = model.selection.active == toolID
                        Button(action: { model.selectComponent(isActive ? nil : toolID) }) {
                            Image(systemName: tool.symbolName)
                        }
                        .buttonStyle(IslandIconButtonStyle(size: 11, frameScale: 2, isActive: isActive))
                        .help(tool.title)
                        .accessibilityLabel(tool.title)
                        .accessibilityAddTraits(isActive ? .isSelected : [])
                    }
                }
                // Settings live in the window only: an island page of switches was a second,
                // smaller settings that stayed open behind the pointer.
                Button(action: {
                    model.dismiss()
                    companionManager.showSettings()
                }) { Image(systemName: "gearshape.fill") }
                    .buttonStyle(IslandIconButtonStyle(size: 12, frameScale: 2))
                    .help("settings")
                    .accessibilityLabel("settings")
                Button(action: { companionManager.quitWithFarewell() }) { Image(systemName: "power") }
                    .buttonStyle(IslandIconButtonStyle(size: 11, frameScale: 2))
                    .help("quit openclicky")
                    .accessibilityLabel("quit openclicky")
            }
            .frame(maxWidth: sideWidth, alignment: .trailing)
        }
        .padding(.horizontal, 16)
    }
}

/// One line saying what the pointer is doing right now, short enough for the side of the notch.
struct NotchPointerStatus: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager

    var body: some View {
        HStack(spacing: 8) {
            NotchPointerMark(isDimmed: false)
            Text(Self.statusText(model: model, companionManager: companionManager))
                .font(.system(size: 12))
                .foregroundColor(DS.HUD.text2)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .accessibilityElement(children: .combine)
    }

    /// From the "show the pointer" switch and the "the pointer shows" setting.
    static func statusText(model: NotchHUDModel, companionManager: CompanionManager) -> String {
        if !companionManager.isClickyCursorEnabled { return "pointer shows while you talk" }
        switch companionManager.pointerPresence {
        case .always: return "pointer follows your mouse"
        case .whileMoving: return "pointer follows while moving"
        case .onShake: return companionManager.isPointerAwake ? "pointer follows your mouse" : "shake the mouse for the pointer"
        }
    }
}

/// The peek: the pointer's status and that a click opens the panel, in one dim line.
struct NotchPeekLine: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var agentSessions: AgentSessionWatcher
    @ObservedObject var media: MediaStore

    /// A live coding session, then the playing track (each when its plugin is on), outrank the
    /// pointer's status.
    static func peekText(status: String, agentLine: String?, mediaLine: String? = nil) -> String {
        agentLine ?? mediaLine ?? status
    }

    private var text: String {
        let enabled = model.enabledComponentIDs
        let agentLine = enabled.contains(IslandPluginRegistry.agentsID) ? agentSessions.peekLine : nil
        let mediaLine = enabled.contains(IslandPluginRegistry.mediaID) ? media.nowPlayingLine : nil
        return Self.peekText(status: Self.shortStatusText(companionManager: companionManager), agentLine: agentLine, mediaLine: mediaLine)
    }

    /// The status in fewer words, so it fits beside "click to open" on the peek's one line.
    static func shortStatusText(companionManager: CompanionManager) -> String {
        if !companionManager.isClickyCursorEnabled { return "pointer while you talk" }
        switch companionManager.pointerPresence {
        case .always: return "pointer follows you"
        case .whileMoving: return "pointer while moving"
        case .onShake: return companionManager.isPointerAwake ? "pointer follows you" : "shake for the pointer"
        }
    }

    var body: some View {
        HStack(spacing: 6) {
            NotchPointerMark(isDimmed: false)
            Text(text)
                .foregroundColor(DS.HUD.text2)
            Text("· click to open")
                .foregroundColor(DS.HUD.text3)
        }
        .font(.system(size: 11))
        .lineLimit(1)
        .minimumScaleFactor(0.8)
        .padding(.horizontal, 10)
        .accessibilityElement(children: .combine)
    }
}

/// Where dictated words go, as the band's round pill shows it: a green microphone while the chosen
/// engine can run, an orange "!" when it can't (its hover text and spoken label say what is
/// missing). A tap opens the voice settings, or what the blocked engine needs.
private struct VoiceDestinationPill: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var settings: DictationSettings

    var body: some View {
        let engine = settings.engine
        let blockedLabel = NotchVoiceDestination.blockedPillLabel(for: engine)
        return Button(action: { NotchVoiceDestination.openSetup(for: engine, companionManager: companionManager) }) {
            HStack(spacing: 5) {
                if blockedLabel != nil {
                    Image(systemName: "exclamationmark").font(.system(size: 10, weight: .bold))
                } else {
                    Image(systemName: "mic.fill")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundColor(DS.HUD.live)
                }
            }
            .foregroundColor(blockedLabel == nil ? DS.HUD.text : .black)
            .frame(minWidth: DS.HUD.pillHeight, minHeight: DS.HUD.pillHeight, maxHeight: DS.HUD.pillHeight)
            .background(Capsule().fill(blockedLabel == nil ? DS.HUD.bandControl : DS.HUD.pointer))
            .fixedSize()
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help(blockedLabel == nil
              ? "dictation is heard by \(engine.displayName.lowercased()). click to change."
              : DictationEngineResolver.unavailableReason(for: engine) ?? "")
        // The pill shows only a glyph, so its words are what VoiceOver says.
        .accessibilityLabel(blockedLabel ?? "dictation is heard by \(engine.listenerName)")
    }
}

/// What the island says about the dictation engine: the pill's call to action and where it leads.
enum NotchVoiceDestination {
    /// Nil when the engine can run; otherwise the pill's call to action, naming what is missing.
    static func blockedPillLabel(for engine: DictationEngineChoice) -> String? {
        guard let reason = DictationEngineResolver.unavailableReason(for: engine) else { return nil }
        switch engine {
        case .sarvam:
            return "add sarvam key ›"
        case .parakeet:
            // Downloaded but pinned to a language it doesn't hear: downloading again would not help.
            return reason == DictationEngineResolver.parakeetLanguageNotHeardReason ? "pick a language parakeet hears ›" : "download parakeet ›"
        case .offline, .openclicky, .assemblyai:
            return "set up backend ›"
        }
    }

    /// The place in the window that fixes the engine.
    enum SetupDestination: Equatable {
        /// Settings → Account: a sign-in is missing.
        case accountSettings
        /// Settings → Keys & Plans: the engine's own key (Sarvam) is missing.
        case keySettings
        /// Settings → Dictation: the engines, and Parakeet's download next to its option.
        case dictationSettings
        /// Settings → Dictation's language row.
        case languageSettings

        /// The warning's button, named for the page it really opens.
        var buttonTitle: String {
            switch self {
            case .accountSettings: return "open account settings"
            case .keySettings: return "add a key"
            case .dictationSettings: return "open dictation settings"
            case .languageSettings: return "open language settings"
            }
        }
    }

    /// Where the engine is fixed, given why it can't run (nil: it can, and the pill opens the
    /// engines). An engine on this Mac needs no key or account: what it can miss is its download
    /// (Settings → Dictation) or, for Parakeet, a language it hears.
    static func setupDestination(for engine: DictationEngineChoice, unavailableReason: String?) -> SetupDestination {
        guard let unavailableReason else { return .dictationSettings }
        if unavailableReason == DictationEngineResolver.parakeetLanguageNotHeardReason { return .languageSettings }
        if engine.isOnDevice { return .dictationSettings }
        // A missing key is added on Keys & Plans; a missing sign-in on Account.
        if engine == .sarvam || unavailableReason == "needs your own key" { return .keySettings }
        return .accountSettings
    }

    /// Where the pill (and the warning in the panel's body) sends the user.
    @MainActor
    static func openSetup(for engine: DictationEngineChoice, companionManager: CompanionManager) {
        switch setupDestination(for: engine, unavailableReason: DictationEngineResolver.unavailableReason(for: engine)) {
        case .accountSettings: companionManager.showSettings(.account)
        case .keySettings: companionManager.showSettings(.keys)
        case .dictationSettings, .languageSettings: companionManager.showSettings(.dictation)
        }
    }
}

// MARK: - Body

/// The body's one row: (i) "what can you do?" and the shortcuts (or, when the dictation engine
/// can't run, what is missing). The dock button that led the row is gone (2026-10-28).
struct NotchPanelBody: View {
    @ObservedObject var model: NotchHUDModel
    @ObservedObject var companionManager: CompanionManager

    /// The body row's height: the buttons, and the grid's two 14 pt rows.
    static let rowHeight: CGFloat = 30
    /// The shortcuts row's slot height under a notch.
    static let shortcutsHeight: CGFloat = 45

    var body: some View {
        if model.placement.isSideDrawer {
            VStack(alignment: .leading, spacing: 10) {
                infoButton
                shortcutsOrWarning
            }
        } else {
            HStack(alignment: .center, spacing: 10) {
                infoButton
                shortcutsOrWarning.padding(.leading, 2)
            }
        }
    }

    /// (i): the buddy types out what OpenClicky does, next to itself. The panel closes so the
    /// buddy is in view.
    private var infoButton: some View {
        Button(action: {
            model.close()
            companionManager.explainWhatOpenClickyDoes()
        }) {
            Text("i")
                .font(.system(size: 17, design: .serif).italic())
                .foregroundColor(DS.HUD.text)
                .frame(width: Self.rowHeight, height: Self.rowHeight)
                // On the glass band the tile is translucent white; on an all-black panel the raised grey.
                .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(model.look.hasGlass ? DS.HUD.glassTile : DS.HUD.surfaceRaisedSoft))
                .contentShape(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous))
        }
        .buttonStyle(.plain)
        .pointerCursor()
        .help("what can you do?")
        .accessibilityLabel("what can you do?")
    }

    @ViewBuilder
    private var shortcutsOrWarning: some View {
        if let engineWarning = NotchEngineWarning(engine: companionManager.dictationSettings.engine) {
            NotchEngineWarningView(warning: engineWarning, companionManager: companionManager)
        } else {
            NotchShortcutGrid(companionManager: companionManager, settings: companionManager.dictationSettings)
        }
    }
}

/// CALL CLICKY as a 2 × 2 grid with no heading: the four shortcuts `CompanionShortcutRecognizer` knows.
struct NotchShortcutGrid: View {
    @ObservedObject var companionManager: CompanionManager
    @ObservedObject var settings: DictationSettings

    var body: some View {
        let dictationKey = settings.dictationKey.keycapLabel
        Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 2) {
            GridRow {
                shortcut(title: "talk", keys: ["⌃", "⌥"], gesture: "hold", isLive: false)
                shortcut(title: "type", keys: ["⌃"], gesture: "tap ×2", isLive: false)
            }
            GridRow {
                shortcut(title: "dictate", keys: [dictationKey], gesture: "hold", isLive: false)
                // Control goes down first: fn pressed first starts a dictation take.
                shortcut(title: "hands-free", heldKey: "⌃", keys: [dictationKey], gesture: "tap ×2", isLive: companionManager.isAlwaysListening)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// `heldKey`: a key held down before the others, drawn first with "hold".
    private func shortcut(title: String, heldKey: String? = nil, keys: [String], gesture: String, isLive: Bool) -> some View {
        HStack(spacing: 4) {
            Text(title).font(.system(size: 12)).foregroundColor(DS.HUD.text).lineLimit(1).fixedSize()
            if isLive {
                Circle().fill(DS.HUD.live).frame(width: 6, height: 6).help("hands-free is on")
            }
            Spacer(minLength: 4)
            HStack(spacing: 3) {
                if let heldKey {
                    NotchKeycap(text: heldKey, isSmall: true)
                    Text("hold")
                        .font(.system(size: 10, design: .monospaced))
                        .foregroundColor(DS.HUD.text3)
                        .lineLimit(1)
                        .fixedSize()
                }
                ForEach(keys, id: \.self) { key in NotchKeycap(text: key, isSmall: true) }
                Text(gesture)
                    .font(.system(size: 10, design: .monospaced))
                    .foregroundColor(DS.HUD.text3)
                    .lineLimit(1)
                    .fixedSize()
            }
        }
        .frame(height: 14)
        .accessibilityElement(children: .combine)
    }
}

/// A key as the HUD draws it: SF Mono 11 on a raised 5 pt keycap.
struct NotchKeycap: View {
    let text: String
    /// The panel's shortcut grid: smaller, so a row fits 14 pt.
    var isSmall = false

    var body: some View {
        Text(text)
            .font(.system(size: isSmall ? 9.5 : 11, design: .monospaced))
            .foregroundColor(DS.HUD.text)
            .fixedSize()
            .padding(.horizontal, isSmall ? 4 : 6)
            .padding(.vertical, isSmall ? 1 : 2)
            .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(DS.HUD.surfaceRaised))
    }
}

struct NotchEngineWarning: Equatable {
    let heading: String
    let message: String
    let buttonTitle: String
    let engine: DictationEngineChoice

    /// Nil when the engine can run. Otherwise the resolver's own reason, so the island says what is
    /// really missing (Parakeet's download or its language, a key, an account) rather than one
    /// "needs the backend" for everything, and a button named for the page it opens.
    init?(engine: DictationEngineChoice) {
        self.init(engine: engine, unavailableReason: DictationEngineResolver.unavailableReason(for: engine))
    }

    /// The same, for a reason already known (the tests give their own).
    init?(engine: DictationEngineChoice, unavailableReason: String?) {
        guard let unavailableReason else { return nil }
        self.engine = engine
        heading = "\(engine.displayName.uppercased()) CAN'T HEAR YOU YET"
        // The resolver's reasons are either "needs …" (said of the engine) or a whole sentence.
        if unavailableReason.hasPrefix("needs") {
            message = "\(engine.displayName.lowercased()) \(unavailableReason). dictation stays off until then."
        } else {
            message = "\(unavailableReason). dictation stays off until then."
        }
        buttonTitle = NotchVoiceDestination.setupDestination(for: engine, unavailableReason: unavailableReason).buttonTitle
    }
}

struct NotchEngineWarningView: View {
    let warning: NotchEngineWarning
    @ObservedObject var companionManager: CompanionManager

    /// Fits the body's one row: the message in two lines at most, the way to fix it beside it.
    var body: some View {
        HStack(spacing: 10) {
            Text(warning.message)
                .font(.system(size: 11))
                .foregroundColor(DS.HUD.text2)
                .lineLimit(2)
                .help(warning.message)
            Button(action: { NotchVoiceDestination.openSetup(for: warning.engine, companionManager: companionManager) }) {
                Text(warning.buttonTitle)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(DS.HUD.text)
                    .padding(.horizontal, 10)
                    .frame(height: 26)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DS.HUD.surfaceRaised))
                    .fixedSize()
            }
            .buttonStyle(.plain)
            .pointerCursor()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityLabel(warning.heading)
    }
}

/// The microphone menu the island and the first-run tour open: the Mac's default, then every
/// input, the chosen one ticked.
@MainActor
enum MicrophoneMenu {
    static func currentName(settings: DictationSettings) -> String {
        if let uid = settings.preferredMicrophoneUID,
           let device = MicrophoneDevices.available().first(where: { $0.uid == uid }) {
            return device.name
        }
        return "automatic · \(MicrophoneDevices.effectiveInputName(preferredUID: nil) ?? "none found")"
    }

    /// Just the device's name ("External Microphone"), for a line with no room for more.
    static func deviceName(settings: DictationSettings) -> String {
        if let uid = settings.preferredMicrophoneUID,
           let device = MicrophoneDevices.available().first(where: { $0.uid == uid }) {
            return device.name
        }
        return MicrophoneDevices.effectiveInputName(preferredUID: nil) ?? "no microphone"
    }

    static func present(settings: DictationSettings) {
        let target = MicrophoneMenuTarget(settings: settings)
        let menu = NSMenu()
        let defaultItem = NSMenuItem(title: "Automatic (\(MicrophoneDevices.effectiveInputName(preferredUID: nil) ?? "none"))", action: #selector(MicrophoneMenuTarget.choose(_:)), keyEquivalent: "")
        defaultItem.target = target
        defaultItem.representedObject = ""
        defaultItem.state = settings.preferredMicrophoneUID == nil ? .on : .off
        menu.addItem(defaultItem)
        menu.addItem(.separator())
        for device in MicrophoneDevices.available() {
            let item = NSMenuItem(title: device.name, action: #selector(MicrophoneMenuTarget.choose(_:)), keyEquivalent: "")
            item.target = target
            item.representedObject = device.uid
            item.state = settings.preferredMicrophoneUID == device.uid ? .on : .off
            menu.addItem(item)
        }
        // popUp runs the menu to completion, so the target lives long enough.
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
        withExtendedLifetime(target) {}
    }
}

private final class MicrophoneMenuTarget: NSObject {
    let settings: DictationSettings

    init(settings: DictationSettings) { self.settings = settings }

    @MainActor @objc func choose(_ item: NSMenuItem) {
        let uid = item.representedObject as? String ?? ""
        settings.preferredMicrophoneUID = uid.isEmpty ? nil : uid
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
    /// The card's content, and whether it is up (the island's agent component reads both).
    let state = AgentResultPanelState()
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
                // Opening agent A then agent B starts two reads; if A's answer lands last it must
                // not overwrite the card that now shows B.
                guard state.threadId == threadId else { return }
                state.title = detail.thread.preview.split(separator: "\n").first.map(String.init) ?? "Agent"
                state.summary = detail.lastAgentMessage ?? "(no reply yet)"
                state.status = detail.turns.last?.status ?? "done"
            } catch {
                guard state.threadId == threadId else { return }
                state.summary = "Couldn't load this agent: \(error.localizedDescription)"
                state.status = "error"
            }
            state.isLoading = false
        }
    }

    func hide() {
        panel?.orderOut(nil)
        state.isVisible = false
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
        state.isVisible = true
    }
}

@MainActor
final class AgentResultPanelState: ObservableObject {
    /// True while the card is on screen.
    @Published var isVisible = false
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
