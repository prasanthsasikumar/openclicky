//
//  AppConnectPrompt.swift
//  OpenClicky
//
//  HeyClicky's "Connect Reddit to HeyClicky — Use HeyClicky to: …" card. When an app or site that
//  has an app-teaching skill comes to the front for the first time, the notch island opens with the
//  two app icons, No / Not now / Yes, and a marquee of example prompts taken from the skill.
//    Yes      remembers the app as connected and, when Composio is configured, asks the agent to
//             connect the account (the Composio toolkit named by the skill's `integration`); without
//             Composio the card explains what to set up instead of pretending
//    No       remembers the app as declined: its skill is left out of the voice prompts
//    Not now  hides the card until the app next launches
//  Only app skills with an `integration` (an account behind the app) get the card: Terminal, Finder
//  or Xcode have nothing to connect.
//

import AppKit
import Combine
import SwiftUI

// MARK: - Model

struct AppConnectPrompt: Equatable {
    enum Stage: Equatable {
        /// No / Not now / Yes.
        case ask
        /// The user said Yes but Composio is not configured: explain, offer to open the settings file.
        case composioMissing
    }

    let skillId: String
    /// What the card calls the app: the skill's name ("Gmail", "YouTube").
    let appName: String
    /// The Composio toolkit that connects the account ("youtube"), from the skill's `integration`.
    let integration: String
    /// The frontmost app when the card opened, for its icon (the browser for a site skill).
    let frontBundleIdentifier: String?
    let examplePrompts: [String]
    var stage: Stage = .ask
}

/// The example prompts a card shows for a skill, read out of the SKILL.md body.
enum AppSkillExamples {

    /// The quoted questions in "## Pointing hints" bullets (`- "How do I …?" — point at …`) first,
    /// then the lead phrase of every "## Common tasks" bullet (`- Compose: …` → "Compose"), up to
    /// `limit` entries and without duplicates. Skills with neither section yield an empty list.
    static func extract(from body: String, limit: Int = 6) -> [String] {
        var examples: [String] = []
        var currentSection = ""
        for rawLine in body.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("#") {
                currentSection = line.trimmingCharacters(in: CharacterSet(charactersIn: "# ")).lowercased()
                continue
            }
            guard line.hasPrefix("- ") else { continue }
            let bullet = String(line.dropFirst(2))
            if currentSection.contains("pointing"), let quoted = firstQuotedPhrase(in: bullet) {
                append(quoted, to: &examples)
            }
        }
        if examples.count < limit {
            currentSection = ""
            for rawLine in body.components(separatedBy: .newlines) {
                let line = rawLine.trimmingCharacters(in: .whitespaces)
                if line.hasPrefix("#") {
                    currentSection = line.trimmingCharacters(in: CharacterSet(charactersIn: "# ")).lowercased()
                    continue
                }
                guard currentSection.contains("common tasks"), line.hasPrefix("- ") else { continue }
                let bullet = String(line.dropFirst(2))
                if let colon = bullet.firstIndex(of: ":") {
                    let lead = bullet[..<colon].trimmingCharacters(in: .whitespaces)
                    if !lead.isEmpty, lead.count <= 48 { append(lead, to: &examples) }
                }
            }
        }
        return Array(examples.prefix(limit))
    }

    /// The text between the first pair of straight or curly double quotes.
    private static func firstQuotedPhrase(in text: String) -> String? {
        let quoteCharacters = CharacterSet(charactersIn: "\"“”")
        let scalars = Array(text.unicodeScalars)
        guard let open = scalars.firstIndex(where: { quoteCharacters.contains($0) }) else { return nil }
        guard let close = scalars[(open + 1)...].firstIndex(where: { quoteCharacters.contains($0) }) else { return nil }
        var phrase = ""
        phrase.unicodeScalars.append(contentsOf: scalars[(open + 1)..<close])
        let trimmed = phrase.trimmingCharacters(in: .whitespaces)
        return trimmed.isEmpty ? nil : trimmed
    }

    private static func append(_ example: String, to examples: inout [String]) {
        if !examples.contains(where: { $0.caseInsensitiveCompare(example) == .orderedSame }) {
            examples.append(example)
        }
    }
}

// MARK: - Controller

/// Watches the frontmost app / browser site, opens the card the first time a skill matches, and
/// remembers the answers in UserDefaults (`appSkillConnectDecisions`: skill id → "connected" |
/// "declined"). The talk lanes ask `declinedSkillIds` before injecting an app skill.
@MainActor
final class AppConnectPromptController: ObservableObject {
    enum Decision: String {
        case connected
        case declined
    }

    enum Answer {
        case yes
        case no
        case notNow
        /// Dismisses the "Composio missing" notice; `openSettings` also reveals shell.json.
        case dismissNotice
        case openSettings
    }

    static let decisionsDefaultsKey = "appSkillConnectDecisions"

    @Published private(set) var decisions: [String: Decision]
    @Published private(set) var currentPrompt: AppConnectPrompt?

    var declinedSkillIds: Set<String> {
        Set(decisions.filter { $0.value == .declined }.map(\.key))
    }

    private let userDefaults: UserDefaults
    private weak var skillLibraryStore: SkillLibraryStore?
    private weak var notchHUDManager: NotchHUDManager?
    /// Whether Composio (the account integrations) is configured; `Yes` connects through it.
    var isComposioConfigured: () -> Bool = { OpenClickyConfiguration.settings.composioMcpUrl != nil }
    /// Hands a task to the agent lane (Codex with the Composio MCP server) and opens its result card.
    var submitToAgent: ((String) -> Void)?
    private var noticeDismissTask: Task<Void, Never>?
    /// Skills answered "Not now": hidden until the app they belong to is no longer in front, then
    /// launched again (tracked by the running app's process id).
    private var dismissedSkillIdsByProcessIdentifier: [String: pid_t] = [:]
    private var pollTimer: Timer?
    private var isPolling = false

    init(userDefaults: UserDefaults = .standard) {
        self.userDefaults = userDefaults
        let stored = userDefaults.dictionary(forKey: Self.decisionsDefaultsKey) as? [String: String] ?? [:]
        decisions = stored.compactMapValues(Decision.init(rawValue:))
    }

    func start(skillLibraryStore: SkillLibraryStore,
               notchHUDManager: NotchHUDManager,
               submitToAgent: @escaping (String) -> Void,
               isComposioConfigured: (() -> Bool)? = nil) {
        self.skillLibraryStore = skillLibraryStore
        self.notchHUDManager = notchHUDManager
        self.submitToAgent = submitToAgent
        if let isComposioConfigured { self.isComposioConfigured = isComposioConfigured }
        pollTimer?.invalidate()
        // Browser tab changes fire no notification, so the front context is polled. The Accessibility
        // reads run off the main thread (they are bounded, but a busy browser can still take ~1 s).
        pollTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.pollFrontmostApp() }
        }
        pollFrontmostApp()
    }

    func stop() {
        pollTimer?.invalidate()
        pollTimer = nil
        setCurrentPrompt(nil)
    }

    func answer(_ answer: Answer) {
        guard let prompt = currentPrompt else { return }
        setCurrentPrompt(apply(answer, to: prompt))
    }

    /// Records one answer for a card and returns what the island should show next: nil to close, or
    /// the same card in its "Composio missing" stage. (The buttons call `answer`; tests call this.)
    @discardableResult
    func apply(_ answer: Answer, to prompt: AppConnectPrompt) -> AppConnectPrompt? {
        print("🔗 App connect: \(prompt.appName) → \(answer)")
        switch answer {
        case .yes:
            record(.connected, for: prompt.skillId)
            guard isComposioConfigured() else {
                var notice = prompt
                notice.stage = .composioMissing
                return notice
            }
            submitToAgent?(connectionTask(for: prompt))
            return nil
        case .no:
            record(.declined, for: prompt.skillId)
            return nil
        case .notNow:
            let frontProcessIdentifier = NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0
            dismissedSkillIdsByProcessIdentifier[prompt.skillId] = frontProcessIdentifier
            return nil
        case .openSettings:
            OpenClickyConfiguration.revealSettingsFile()
            return nil
        case .dismissNotice:
            return nil
        }
    }

    /// What the agent is asked to do when the user connects an app through Composio.
    func connectionTask(for prompt: AppConnectPrompt) -> String {
        "Connect my \(prompt.appName) account through the composio MCP tools (toolkit \"\(prompt.integration)\"). " +
        "Call COMPOSIO_MANAGE_CONNECTIONS to start the connection; if it returns an authorization link, run `open <link>` so it opens in my browser and also print it on its own line, " +
        "then call COMPOSIO_WAIT_FOR_CONNECTIONS. When the account is connected (or already was), use COMPOSIO_SEARCH_TOOLS to tell me in one short paragraph what you can now do in \(prompt.appName)."
    }

    /// The frontmost skill match, or nil. Exposed for tests; the poll uses the same rule.
    func prompt(for front: FrontAppContext, skills: [SkillFile]) -> AppConnectPrompt? {
        guard let skill = AppSkillMatcher.match(front, in: skills) else { return nil }
        // Only apps with an account to connect (Gmail, YouTube…); Terminal or Finder have none.
        guard let integration = skill.integration else { return nil }
        guard decisions[skill.id] == nil else { return nil }
        if let dismissedProcessIdentifier = dismissedSkillIdsByProcessIdentifier[skill.id] {
            let frontProcessIdentifier = NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0
            if dismissedProcessIdentifier == frontProcessIdentifier { return nil }
            dismissedSkillIdsByProcessIdentifier[skill.id] = nil
        }
        return AppConnectPrompt(
            skillId: skill.id,
            appName: skill.name,
            integration: integration,
            frontBundleIdentifier: front.bundleIdentifier,
            examplePrompts: AppSkillExamples.extract(from: skill.body)
        )
    }

    // MARK: - Private

    private func pollFrontmostApp() {
        guard !isPolling, let skillLibraryStore else { return }
        // A notice stays until dismissed (or its timer runs out); the poll must not replace it.
        if currentPrompt?.stage == .composioMissing { return }
        isPolling = true
        let skills = skillLibraryStore.appSkills.filter { $0.isForTalk }
        let ownBundleIdentifier = Bundle.main.bundleIdentifier
        Task.detached(priority: .utility) { [weak self] in
            let front = FrontmostAppObserver.current(excludingBundleIdentifier: ownBundleIdentifier)
            await MainActor.run { [weak self] in
                guard let self else { return }
                self.isPolling = false
                self.setCurrentPrompt(self.prompt(for: front, skills: skills))
            }
        }
    }

    private func setCurrentPrompt(_ prompt: AppConnectPrompt?) {
        guard currentPrompt != prompt else { return }
        currentPrompt = prompt
        notchHUDManager?.setConnectPrompt(prompt)
        noticeDismissTask?.cancel()
        if prompt?.stage == .composioMissing {
            noticeDismissTask = Task { [weak self] in
                try? await Task.sleep(nanoseconds: 15_000_000_000)
                guard !Task.isCancelled, let self, self.currentPrompt?.stage == .composioMissing else { return }
                self.setCurrentPrompt(nil)
            }
        }
    }

    private func record(_ decision: Decision, for skillId: String) {
        decisions[skillId] = decision
        userDefaults.set(decisions.mapValues(\.rawValue), forKey: Self.decisionsDefaultsKey)
    }
}

// MARK: - View

/// The card itself, drawn inside the notch island (596 × 103 pt including the menu-bar band).
struct AppConnectPromptView: View {
    let prompt: AppConnectPrompt
    @ObservedObject var controller: AppConnectPromptController
    /// On a hardware-notch screen the row sits under the notch band; elsewhere the island covers
    /// the menu bar and the row uses that height too.
    var topInset: CGFloat = 0

    var body: some View {
        // Measured from HeyClicky: the 32 pt icons sit right under the menu-bar band, the 25 pt
        // buttons are centered on them, and the 19 pt chips run along the bottom above a 12 pt gap.
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                appIcons
                VStack(alignment: .leading, spacing: 2) {
                    Text(prompt.stage == .ask ? "Connect \(prompt.appName) to OpenClicky" : "\(prompt.appName) skill is on")
                        .font(.system(size: 14, weight: .bold))
                        .foregroundColor(.white)
                        .lineLimit(1)
                        .truncationMode(.tail)
                    Text(prompt.stage == .ask ? "Use OpenClicky to:" : "Account actions need Composio: set COMPOSIO_MCP_URL in shell.json")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundColor(Color.white.opacity(0.6))
                        .lineLimit(1)
                }
                Spacer(minLength: 12)
                HStack(spacing: 9) {
                    switch prompt.stage {
                    case .ask:
                        answerButton(title: "No", systemImage: "xmark", isPrimary: false) { controller.answer(.no) }
                        answerButton(title: "Not now", systemImage: "clock", isPrimary: false) { controller.answer(.notNow) }
                        answerButton(title: "Yes", systemImage: "link", isPrimary: true) { controller.answer(.yes) }
                    case .composioMissing:
                        answerButton(title: "OK", systemImage: "checkmark", isPrimary: false) { controller.answer(.dismissNotice) }
                        answerButton(title: "Open settings", systemImage: "gearshape", isPrimary: true) { controller.answer(.openSettings) }
                    }
                }
                // The buttons keep their labels; a long app name truncates instead.
                .fixedSize()
            }
            .frame(height: 34)
            .padding(.leading, 21)
            .padding(.trailing, 17)

            Spacer(minLength: 6)

            if !prompt.examplePrompts.isEmpty {
                MarqueeChipsView(items: prompt.examplePrompts)
                    .frame(height: 19)
                    .padding(.bottom, 12)
            }
        }
        .padding(.top, topInset)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    private var appIcons: some View {
        HStack(spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(width: 32, height: 32)
            frontAppIcon
                .frame(width: 32, height: 32)
        }
    }

    @ViewBuilder
    private var frontAppIcon: some View {
        if let bundleIdentifier = prompt.frontBundleIdentifier,
           let application = NSWorkspace.shared.runningApplications.first(where: { $0.bundleIdentifier == bundleIdentifier }),
           let icon = application.icon {
            Image(nsImage: icon)
                .resizable()
                .aspectRatio(contentMode: .fit)
        } else {
            Image(systemName: "globe")
                .font(.system(size: 20, weight: .medium))
                .foregroundColor(.white)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white.opacity(0.12)))
        }
    }

    private func answerButton(title: String, systemImage: String, isPrimary: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 6) {
                Image(systemName: systemImage).font(.system(size: 11, weight: .bold))
                Text(title).font(.system(size: 13, weight: .semibold))
            }
            .foregroundColor(.white)
            .padding(.horizontal, 16)
            .frame(height: 25)
            .background(Capsule().fill(isPrimary ? DS.Colors.blue500 : Color.white.opacity(0.14)))
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }
}

/// Example prompts as gray pills drifting left, looping seamlessly (the row is drawn twice).
struct MarqueeChipsView: View {
    let items: [String]
    /// Points per second.
    var speed: CGFloat = 28

    @State private var rowWidth: CGFloat = 0

    var body: some View {
        // The moving row is an overlay on an empty, full-width strip so its (much wider) content
        // never counts toward the card's layout width; the strip clips it at the island's edge.
        Color.clear
            .frame(maxWidth: .infinity)
            .overlay(alignment: .leading) {
                TimelineView(.animation) { timeline in
                    let elapsed = timeline.date.timeIntervalSinceReferenceDate
                    let offset = rowWidth > 0 ? CGFloat(elapsed * Double(speed)).truncatingRemainder(dividingBy: rowWidth) : 0
                    HStack(spacing: 0) {
                        chipRow
                            .background(GeometryReader { proxy in
                                Color.clear.preference(key: MarqueeRowWidthKey.self, value: proxy.size.width)
                            })
                        chipRow
                    }
                    .fixedSize(horizontal: true, vertical: false)
                    .offset(x: -offset)
                }
            }
            .clipped()
            // The chips fade in at the island's left edge instead of being cut off.
            .mask(
                HStack(spacing: 0) {
                    LinearGradient(colors: [.clear, .black], startPoint: .leading, endPoint: .trailing).frame(width: 36)
                    Color.black
                }
            )
            .onPreferenceChange(MarqueeRowWidthKey.self) { rowWidth = $0 }
            .allowsHitTesting(false)
    }

    private var chipRow: some View {
        HStack(spacing: 10) {
            ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                Text(item)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(Color.white.opacity(0.92))
                    .lineLimit(1)
                    .padding(.horizontal, 12)
                    .frame(height: 19)
                    .background(Capsule().fill(Color.white.opacity(0.16)))
            }
        }
        .padding(.leading, 21)
    }
}

private struct MarqueeRowWidthKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}
