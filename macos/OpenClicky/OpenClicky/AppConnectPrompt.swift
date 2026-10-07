//
//  AppConnectPrompt.swift
//  OpenClicky
//
//  The "connect <app> to openclicky?" card. When an app or site that has an app-teaching skill
//  comes to the front for the first time, the notch island opens with the app's icon, example
//  prompts taken from the skill, and never for <app> / not now / connect.
//    connect        remembers the app as connected and, when Composio is configured, asks the agent
//                   to connect the account (the Composio toolkit named by the skill's `integration`);
//                   without Composio the card explains what to set up instead of pretending
//    never for <app> remembers the app as declined: its skill is left out of the voice prompts
//    not now        hides the card until the app next launches
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

/// The card itself, drawn inside the notch island (512 pt wide, sized by `NotchHUDModel.connectHeight`).
struct AppConnectPromptView: View {
    let prompt: AppConnectPrompt
    @ObservedObject var controller: AppConnectPromptController
    /// On a hardware-notch screen the content starts under the notch band; elsewhere the island
    /// covers the menu bar and the content uses that height too.
    var topInset: CGFloat = 0

    var body: some View {
        // The refinement sheet's card: 40 pt app icon with the question and one line of what it
        // means, the example prompts as chips, then "never for <app>" on the left and
        // not now / connect on the right. 12 / 16 pt padding under the band.
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                frontAppIcon
                    .frame(width: DS.HUD.tileSize, height: DS.HUD.tileSize)
                VStack(alignment: .leading, spacing: 2) {
                    Text(headline)
                        .font(.system(size: 15, weight: .semibold))
                        .foregroundColor(DS.HUD.text)
                        .lineLimit(1)
                        .truncationMode(.tail)
                    Text(detail)
                        .font(.system(size: 12))
                        .foregroundColor(DS.HUD.text2)
                        .lineLimit(1)
                        .truncationMode(.tail)
                }
                Spacer(minLength: 0)
            }

            if !prompt.examplePrompts.isEmpty {
                exampleChips
            }

            HStack(spacing: 8) {
                switch prompt.stage {
                case .ask:
                    Button(action: { controller.answer(.no) }) {
                        Text("never for \(prompt.appName.lowercased())")
                            .font(.system(size: 13))
                            .foregroundColor(DS.HUD.text2)
                            .lineLimit(1)
                            .padding(.horizontal, 10)
                            .frame(height: 30)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .pointerCursor()
                    .help("don't ask again; \(prompt.appName)'s skill is left out of what clicky knows")
                    Spacer(minLength: 8)
                    answerButton(title: "not now", isPrimary: false) { controller.answer(.notNow) }
                    answerButton(title: "connect", isPrimary: true) { controller.answer(.yes) }
                case .composioMissing:
                    Spacer(minLength: 8)
                    answerButton(title: "ok", isPrimary: false) { controller.answer(.dismissNotice) }
                    answerButton(title: "open shell.json", isPrimary: true) { controller.answer(.openSettings) }
                }
            }
        }
        .padding(.top, topInset + 12)
        .padding(.horizontal, DS.HUD.bodySidePadding)
        .padding(.bottom, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    private var headline: String {
        switch prompt.stage {
        case .ask: return "connect \(prompt.appName.lowercased()) to openclicky?"
        case .composioMissing: return "\(prompt.appName.lowercased()) skill is on"
        }
    }

    private var detail: String {
        switch prompt.stage {
        case .ask: return "clicky could act in your \(prompt.appName.lowercased()) account when you ask."
        case .composioMissing: return "account actions need composio: set COMPOSIO_MCP_URL in shell.json."
        }
    }

    /// The skill's example prompts, as many whole chips as fit on one line.
    private var exampleChips: some View {
        WholeItemsRowLayout(spacing: 6) {
            ForEach(Array(prompt.examplePrompts.prefix(4).enumerated()), id: \.offset) { _, examplePrompt in
                Text("“\(examplePrompt)”")
                    .font(.system(size: 12))
                    .foregroundColor(Color(hex: "#D4D4D4"))
                    .lineLimit(1)
                    .fixedSize()
                    .padding(.horizontal, 9)
                    .padding(.vertical, 5)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DS.HUD.bandControl))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .frame(height: 26)
        .clipped()
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
                .font(.system(size: 18, weight: .medium))
                .foregroundColor(DS.HUD.text2)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(RoundedRectangle(cornerRadius: DS.HUD.tileRadius, style: .continuous).fill(DS.HUD.surfaceRaisedSoft))
        }
    }

    private func answerButton(title: String, isPrimary: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 13, weight: isPrimary ? .semibold : .regular))
                .foregroundColor(isPrimary ? .black : DS.HUD.text)
                .padding(.horizontal, 14)
                .frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(isPrimary ? DS.HUD.text : DS.HUD.surfaceRaised))
        }
        .buttonStyle(.plain)
        .pointerCursor()
    }
}


/// A row that lays its items out left to right and leaves out every item from the first one that
/// would cross the right edge, so a chip is never cut in half. A plain HStack of fixed-size chips
/// asks for their full width instead, which pushed the whole connect card wider than the island.
struct WholeItemsRowLayout: Layout {
    var spacing: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let sizes = subviews.map { $0.sizeThatFits(.unspecified) }
        let height = sizes.map(\.height).max() ?? 0
        let naturalWidth = sizes.map(\.width).reduce(0, +) + spacing * CGFloat(max(0, sizes.count - 1))
        return CGSize(width: min(proposal.width ?? naturalWidth, naturalWidth), height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX
        var rowIsFull = false
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if rowIsFull || x + size.width > bounds.maxX + 0.5 {
                // Out of room: this and every later item are placed out of sight (the row clips).
                rowIsFull = true
                subview.place(at: CGPoint(x: bounds.maxX + 10_000, y: bounds.minY), proposal: .zero)
                continue
            }
            subview.place(at: CGPoint(x: x, y: bounds.midY), anchor: .leading, proposal: ProposedViewSize(size))
            x += size.width + spacing
        }
    }
}
