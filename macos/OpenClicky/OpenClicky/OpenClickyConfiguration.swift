//
//  OpenClickyConfiguration.swift
//  OpenClicky
//
//  OpenClicky integration: where the backend lives, the user's token, and how to
//  run the `openclicky` CLI. Read from ~/.openclicky/shell.json (shared with the
//  minimal OpenClickyShell) with environment overrides. The app never holds
//  provider keys — only the user's OpenClicky token, which the backend verifies.
//

import AppKit
import Foundation

struct OpenClickyShellSettings: Codable {
    /// Command used to run the CLI, e.g. ["openclicky"] or ["node", "/path/to/openclicky/agent/dist/cli.js"].
    var cliCommand: [String] = ["openclicky"]
    /// The hosted backend by default (invite accounts and bring-your-own-key both go there); a
    /// self-hosted backend is one line in shell.json (`http://localhost:8787`).
    var backendUrl: String = OpenClickyConfiguration.hostedBackendURL
    var token: String = ""
    /// Working directory for agent runs (created on demand).
    var workspace: String = NSString(string: "~/OpenClicky").expandingTildeInPath
    var model: String = ""
    var voiceSeconds: Int = 5
    /// "openai" (via the OpenClicky backend, default), "assemblyai" (needs ASSEMBLYAI_API_KEY on the backend), or "apple".
    var transcriptionProvider: String? = nil
    /// The language OpenClicky speaks and transcribes, as a language code ("en", "hi"). Unset is
    /// English. See ReplyLanguage.swift for why it is one fixed language and not "follow the user".
    var language: String? = nil
    /// Opt in to launching at login (upstream OpenClicky registered itself unconditionally).
    var registerAsLoginItem: Bool? = nil
    /// Optional MCP servers for the agent (rendered into the Codex config by the CLI).
    /// Composio: `composioMcpUrl` (https://connect.composio.dev/mcp) plus your `ck_…` key from
    /// dashboard.composio.dev, sent as the `x-consumer-api-key` header.
    /// Directories the agent's sandbox may write to outside the workspace. Unset leaves the agent's
    /// own default (the whole home folder); an empty list confines it to the workspace.
    var writableRoots: [String]? = nil
    var composioMcpUrl: String? = nil
    var composioApiKey: String? = nil
    var cuaDriverBin: String? = nil
    /// Directory of app-teaching skills (`app-skills/` in the repo). Defaults to the checkout the CLI runs from.
    var appSkillsPath: String? = nil
    /// Bring your own key: your OpenAI key (and optionally an Anthropic key for the Claude lanes).
    /// Sent to the backend with every request in `x-openclicky-*` headers; the backend runs the
    /// request on them instead of its own keys and meters nothing. Leave empty to use OpenClicky's
    /// keys under your plan.
    var openaiApiKey: String? = nil
    var anthropicApiKey: String? = nil
    /// Your Sarvam key (dashboard.sarvam.ai): Saaras transcribes takes and a Sarvam model polishes
    /// them, straight from this Mac. Never sent to the OpenClicky backend.
    var sarvamKey: String? = nil
    /// More keys that can answer questions (AnswerProvider.swift), each straight to its vendor:
    /// OpenRouter (one key for Claude, GPT, Gemini, Llama…), Google Gemini, Groq, Mistral, xAI.
    var openRouterApiKey: String? = nil
    var geminiApiKey: String? = nil
    var groqApiKey: String? = nil
    var mistralApiKey: String? = nil
    var xaiApiKey: String? = nil
    /// Answer questions through a plan the person already pays for: the Claude Code CLI signed in
    /// to their Claude plan, or Codex signed in with ChatGPT. Off until switched on in Settings.
    var useClaudeCodePlan: Bool? = nil
    var useCodexPlan: Bool? = nil
    /// Who answers questions: nil or "auto" picks in ProviderRouter's order; otherwise one of
    /// AnswerSource's raw values.
    var answerSource: String? = nil
    /// Model overrides per provider ("openrouter": "anthropic/claude-sonnet-4.6"); absent → default.
    var answerModels: [String: String]? = nil
    /// Who speaks answers (SpeechVoiceChoice): nil, "auto" or the older "cloud" picks in order (your
    /// keys, then the free voice); otherwise "account", "openai", "elevenlabs", "google", "polly",
    /// "sarvam", "kokoro" (downloaded on this Mac) or "mac" (this Mac's own voice).
    var speechVoice: String? = nil
    /// Voice-only keys, each straight to its vendor: ElevenLabs, Google Cloud Text-to-Speech (a
    /// Cloud API key with that API on), and Amazon Polly as "accessKeyId:secretKey[:region]".
    var elevenLabsApiKey: String? = nil
    var googleCloudApiKey: String? = nil
    var amazonPollyCredentials: String? = nil
    /// Which inbox "check my email" and "draft an email" use: "mail", "gmail", or nil for
    /// automatic (Apple Mail when it has accounts here, else Gmail). Settings → Voice.
    var emailApp: String? = nil
    /// The Kokoro speaker picked in Settings (its id in the model); nil → the language's default.
    var kokoroSpeaker: Int? = nil
    /// The Mac voice picked in Settings (an AVSpeechSynthesisVoice identifier); nil → the best one
    /// installed for the reply language.
    var macVoiceIdentifier: String? = nil
    /// Written by sign-in (OpenClickyAuthSession): the refresh token that keeps `token` fresh, when it
    /// expires (unix seconds), and whose account it is. Absent when the token was pasted by hand.
    var refreshToken: String? = nil
    var tokenExpiresAt: Double? = nil
    var accountEmail: String? = nil
}

extension OpenClickyShellSettings {
    /// A field missing from the file takes its default: a short, hand-written shell.json is a
    /// normal file (the synthesised decoder refused one without every field, and the app then
    /// started from the defaults). A field of the wrong type still fails, so a mistake is reported
    /// instead of being quietly replaced.
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = OpenClickyShellSettings()
        cliCommand = try container.decodeIfPresent([String].self, forKey: .cliCommand) ?? defaults.cliCommand
        backendUrl = try container.decodeIfPresent(String.self, forKey: .backendUrl) ?? defaults.backendUrl
        token = try container.decodeIfPresent(String.self, forKey: .token) ?? defaults.token
        workspace = try container.decodeIfPresent(String.self, forKey: .workspace) ?? defaults.workspace
        model = try container.decodeIfPresent(String.self, forKey: .model) ?? defaults.model
        voiceSeconds = try container.decodeIfPresent(Int.self, forKey: .voiceSeconds) ?? defaults.voiceSeconds
        transcriptionProvider = try container.decodeIfPresent(String.self, forKey: .transcriptionProvider)
        language = try container.decodeIfPresent(String.self, forKey: .language)
        registerAsLoginItem = try container.decodeIfPresent(Bool.self, forKey: .registerAsLoginItem)
        writableRoots = try container.decodeIfPresent([String].self, forKey: .writableRoots)
        composioMcpUrl = try container.decodeIfPresent(String.self, forKey: .composioMcpUrl)
        composioApiKey = try container.decodeIfPresent(String.self, forKey: .composioApiKey)
        cuaDriverBin = try container.decodeIfPresent(String.self, forKey: .cuaDriverBin)
        appSkillsPath = try container.decodeIfPresent(String.self, forKey: .appSkillsPath)
        openaiApiKey = try container.decodeIfPresent(String.self, forKey: .openaiApiKey)
        anthropicApiKey = try container.decodeIfPresent(String.self, forKey: .anthropicApiKey)
        sarvamKey = try container.decodeIfPresent(String.self, forKey: .sarvamKey)
        openRouterApiKey = try container.decodeIfPresent(String.self, forKey: .openRouterApiKey)
        geminiApiKey = try container.decodeIfPresent(String.self, forKey: .geminiApiKey)
        groqApiKey = try container.decodeIfPresent(String.self, forKey: .groqApiKey)
        mistralApiKey = try container.decodeIfPresent(String.self, forKey: .mistralApiKey)
        xaiApiKey = try container.decodeIfPresent(String.self, forKey: .xaiApiKey)
        useClaudeCodePlan = try container.decodeIfPresent(Bool.self, forKey: .useClaudeCodePlan)
        useCodexPlan = try container.decodeIfPresent(Bool.self, forKey: .useCodexPlan)
        answerSource = try container.decodeIfPresent(String.self, forKey: .answerSource)
        answerModels = try container.decodeIfPresent([String: String].self, forKey: .answerModels)
        speechVoice = try container.decodeIfPresent(String.self, forKey: .speechVoice)
        macVoiceIdentifier = try container.decodeIfPresent(String.self, forKey: .macVoiceIdentifier)
        elevenLabsApiKey = try container.decodeIfPresent(String.self, forKey: .elevenLabsApiKey)
        googleCloudApiKey = try container.decodeIfPresent(String.self, forKey: .googleCloudApiKey)
        amazonPollyCredentials = try container.decodeIfPresent(String.self, forKey: .amazonPollyCredentials)
        emailApp = try container.decodeIfPresent(String.self, forKey: .emailApp)
        kokoroSpeaker = try container.decodeIfPresent(Int.self, forKey: .kokoroSpeaker)
        refreshToken = try container.decodeIfPresent(String.self, forKey: .refreshToken)
        tokenExpiresAt = try container.decodeIfPresent(Double.self, forKey: .tokenExpiresAt)
        accountEmail = try container.decodeIfPresent(String.self, forKey: .accountEmail)
    }
}

enum OpenClickyConfiguration {
    /// Where the app talks to unless shell.json says otherwise.
    static let hostedBackendURL = "https://api.openclicky.flowsxr.com"
    /// Connectors (Composio app connections): the "connect <app> to openclicky?" card, the composio
    /// tile in the HUD, and the Composio tools handed to the agent. Off for 0.8.0; a later release
    /// turns them back on by flipping this.
    static let connectorsEnabled = false
    /// The Composio MCP URL from shell.json, or nil while connectors are off.
    static var activeComposioMcpUrl: String? {
        guard connectorsEnabled, let url = settings.composioMcpUrl, !url.isEmpty else { return nil }
        return url
    }
    static let settingsFileURL = URL(fileURLWithPath: NSString(string: "~/.openclicky/shell.json").expandingTildeInPath)

    private(set) static var settings: OpenClickyShellSettings = load()

    static func reload() {
        settings = load()
    }

    /// Posted when shell.json changed on disk in a way that matters to a live connection: the
    /// token, the backend, or one of the user's own keys.
    static let credentialsChangedNotification = Notification.Name("openClickyCredentialsChanged")

    /// What a live session was opened with. When this changes, the session is stale.
    nonisolated static func credentialFingerprint(of shellSettings: OpenClickyShellSettings) -> [String] {
        [shellSettings.backendUrl, shellSettings.token, shellSettings.openaiApiKey ?? "", shellSettings.anthropicApiKey ?? ""]
    }

    private static var settingsDirectoryWatcher: DispatchSourceFileSystemObject?
    private static var pendingSettingsReload: DispatchWorkItem?

    /// Picks up shell.json edits made while the app runs — a key pasted in by hand used to need a
    /// relaunch, because nothing re-read the file except the app's own writes. The directory is
    /// watched, not the file: an editor's atomic save replaces the file, and a watch on the old
    /// file would go quiet after the first save.
    static func startWatchingSettingsFile() {
        guard settingsDirectoryWatcher == nil else { return }
        ensureSettingsFileExists()
        let directoryPath = settingsFileURL.deletingLastPathComponent().path
        let directoryDescriptor = open(directoryPath, O_EVTONLY)
        guard directoryDescriptor >= 0 else { return }
        let watcher = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: directoryDescriptor, eventMask: [.write, .rename, .delete], queue: .main)
        watcher.setEventHandler {
            // Editors write in bursts (temp file, rename, attributes); act once they are done.
            pendingSettingsReload?.cancel()
            let reloadWorkItem = DispatchWorkItem { reloadAfterExternalChange() }
            pendingSettingsReload = reloadWorkItem
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: reloadWorkItem)
        }
        watcher.setCancelHandler { close(directoryDescriptor) }
        watcher.resume()
        settingsDirectoryWatcher = watcher
    }

    private static func reloadAfterExternalChange() {
        let fingerprintBefore = credentialFingerprint(of: settings)
        reload()
        guard credentialFingerprint(of: settings) != fingerprintBefore else { return }
        NotificationCenter.default.post(name: credentialsChangedNotification, object: nil)
    }

    /// File settings, then environment overrides (handy when launching from a terminal):
    /// OPENCLICKY_SHELL_CLI ("node /path/cli.js"), BACKEND_URL, OPENCLICKY_TOKEN, OPENCLICKY_WORKSPACE, OPENCLICKY_MODEL.
    /// The overrides live only in memory: `update` starts from the file alone, so it never writes them.
    private static func load() -> OpenClickyShellSettings {
        load(fileURL: settingsFileURL, environment: ProcessInfo.processInfo.environment)
    }

    static func load(fileURL: URL, environment: [String: String]) -> OpenClickyShellSettings {
        var loadedSettings = OpenClickyShellSettings()
        switch readSettingsFile(at: fileURL) {
        case .readable(let fileSettings, _):
            loadedSettings = fileSettings
            if fileURL == settingsFileURL { settingsFileProblem = nil }
        case .missing:
            if fileURL == settingsFileURL { settingsFileProblem = nil }
        case .unreadable(let reason):
            // The defaults stand in for this launch; the file itself is left for the person to fix.
            noteUnreadableSettingsFile(reason, fileURL: fileURL)
        }
        if let cli = environment["OPENCLICKY_SHELL_CLI"], !cli.isEmpty { loadedSettings.cliCommand = cli.split(separator: " ").map(String.init) }
        if let value = environment["BACKEND_URL"], !value.isEmpty { loadedSettings.backendUrl = value }
        if let value = environment["OPENCLICKY_TOKEN"], !value.isEmpty { loadedSettings.token = value }
        if let value = environment["OPENCLICKY_WORKSPACE"], !value.isEmpty { loadedSettings.workspace = value }
        if let value = environment["OPENCLICKY_MODEL"], !value.isEmpty { loadedSettings.model = value }
        if let value = environment["OPENCLICKY_TRANSCRIPTION_PROVIDER"], !value.isEmpty { loadedSettings.transcriptionProvider = value }
        return loadedSettings
    }

    /// What is in shell.json: nothing yet, settings (with the file's own JSON object, so a write
    /// can keep what this build does not know), or a file that does not read, and why.
    enum SettingsFileReading {
        case missing
        case readable(OpenClickyShellSettings, jsonObject: [String: Any])
        case unreadable(String)
    }

    static func readSettingsFile(at fileURL: URL) -> SettingsFileReading {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return .missing }
        let data: Data
        do {
            data = try Data(contentsOf: fileURL)
        } catch {
            return .unreadable("it can't be opened (\(error.localizedDescription))")
        }
        // An empty file has nothing in it to lose.
        if String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return .readable(OpenClickyShellSettings(), jsonObject: [:])
        }
        guard let jsonObject = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            return .unreadable("it isn't valid json")
        }
        do {
            return .readable(try JSONDecoder().decode(OpenClickyShellSettings.self, from: data), jsonObject: jsonObject)
        } catch DecodingError.typeMismatch(_, let context) {
            return .unreadable("\"\(context.codingPath.map(\.stringValue).joined(separator: "."))\" has the wrong kind of value")
        } catch {
            return .unreadable("it doesn't read (\(error.localizedDescription))")
        }
    }

    /// The sentence saying why shell.json is neither used nor written, while it does not read.
    private(set) static var settingsFileProblem: String?
    /// Posted on the main actor when shell.json stops reading; the object is the sentence.
    static let settingsFileUnreadableNotification = Notification.Name("openClickySettingsFileUnreadable")

    nonisolated static func unreadableSettingsFileSentence(reason: String) -> String {
        "your settings file (~/.openclicky/shell.json) has a mistake in it: \(reason). openclicky won't change it until it's fixed."
    }

    /// Logs and shows the problem once per distinct problem (every refused write would repeat it).
    private static func noteUnreadableSettingsFile(_ reason: String, fileURL: URL) {
        AppLog.append("settings: \(fileURL.path) does not read, so nothing is written to it: \(reason)")
        // Only the real settings file is the person's to hear about; tests use temporary files.
        guard fileURL == settingsFileURL else { return }
        let sentence = unreadableSettingsFileSentence(reason: reason)
        guard settingsFileProblem != sentence else { return }
        settingsFileProblem = sentence
        Task { @MainActor in
            NotificationCenter.default.post(name: settingsFileUnreadableNotification, object: sentence)
            AccountProfileStore.shared.showNotice?(sentence)
        }
    }

    static func ensureSettingsFileExists() {
        let fileManager = FileManager.default
        guard !fileManager.fileExists(atPath: settingsFileURL.path) else {
            // The directory may already exist too, but from before this file learned to lock it
            // down (or created by some other process) — tighten it every time regardless.
            tightenSettingsDirectoryPermissionsIfNeeded(at: settingsFileURL.deletingLastPathComponent(), fileManager: fileManager)
            return
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(OpenClickyShellSettings()) else { return }
        try? writeShellSettingsData(data, toFileAt: settingsFileURL, fileManager: fileManager)
    }

    static func revealSettingsFile() {
        ensureSettingsFileExists()
        NSWorkspace.shared.open(settingsFileURL)
    }

    /// Changes shell.json in place (sign-in writes the session here) and reloads the settings.
    static func update(_ change: (inout OpenClickyShellSettings) -> Void) {
        update(fileAt: settingsFileURL, change)
        reload()
    }

    /// Starts from the file alone (never the environment overrides) and writes back only the
    /// fields the change touched, merged into the file's own JSON object: fields this build does
    /// not know, and values the change left alone, stay exactly as they were written. A file that
    /// exists but does not read is never written: starting from the defaults instead would throw
    /// away every key in it. Returns whether the file was written.
    @discardableResult
    static func update(fileAt fileURL: URL, _ change: (inout OpenClickyShellSettings) -> Void) -> Bool {
        var fileSettings = OpenClickyShellSettings()
        var fileJSONObject: [String: Any] = [:]
        switch readSettingsFile(at: fileURL) {
        case .missing:
            break
        case .readable(let readSettings, let readJSONObject):
            fileSettings = readSettings
            fileJSONObject = readJSONObject
        case .unreadable(let reason):
            noteUnreadableSettingsFile(reason, fileURL: fileURL)
            return false
        }
        var changedSettings = fileSettings
        change(&changedSettings)
        do {
            let encodedBefore = try encodedJSONObject(of: fileSettings)
            let encodedAfter = try encodedJSONObject(of: changedSettings)
            for fieldName in Set(encodedBefore.keys).union(encodedAfter.keys) {
                let valueBefore = encodedBefore[fieldName] as AnyObject?
                let valueAfter = encodedAfter[fieldName] as AnyObject?
                let fieldChanged = valueBefore.map { !$0.isEqual(valueAfter) } ?? (valueAfter != nil)
                // An optional set back to nil is not encoded at all, so it leaves the file too.
                if fieldChanged { fileJSONObject[fieldName] = encodedAfter[fieldName] }
            }
            let data = try JSONSerialization.data(withJSONObject: fileJSONObject, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
            try writeShellSettingsData(data, toFileAt: fileURL)
            return true
        } catch {
            AppLog.append("settings: could not write \(fileURL.path): \(error.localizedDescription)")
            return false
        }
    }

    private static func encodedJSONObject(of shellSettings: OpenClickyShellSettings) throws -> [String: Any] {
        (try JSONSerialization.jsonObject(with: JSONEncoder().encode(shellSettings)) as? [String: Any]) ?? [:]
    }

    /// shell.json holds the session token, the refresh token, and the user's own OpenAI/Anthropic
    /// keys, so it must never be readable by anyone but the owner. `Data.write(to:)` uses the
    /// process's default file permissions (0644 — world-readable) with no way to pass a mode, so the
    /// directory is created at 0700 and the file is chmod'd to 0600 unconditionally after every
    /// write. That "unconditionally" matters: an existing file from before this function existed
    /// (or one loosened by some other tool) gets tightened on its very next write, not only on
    /// first creation. Pulled out as a pure, injectable helper so it can be exercised against a
    /// temp directory in tests instead of the user's real `~/.openclicky`.
    static func writeShellSettingsData(
        _ data: Data,
        toFileAt fileURL: URL,
        fileManager: FileManager = .default
    ) throws {
        let directoryURL = fileURL.deletingLastPathComponent()
        try fileManager.createDirectory(
            at: directoryURL,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        // createDirectory only applies `attributes` when it actually creates the directory, so a
        // pre-existing directory (from an older build, or created some other way) is tightened here.
        tightenSettingsDirectoryPermissionsIfNeeded(at: directoryURL, fileManager: fileManager)
        try data.write(to: fileURL, options: .atomic)
        // The atomic write can replace the file via a temp-file swap, which does not preserve
        // whatever permissions the destination path had — set them explicitly every time.
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fileURL.path)
    }

    private static func tightenSettingsDirectoryPermissionsIfNeeded(at directoryURL: URL, fileManager: FileManager) {
        guard fileManager.fileExists(atPath: directoryURL.path) else { return }
        try? fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directoryURL.path)
    }

    // MARK: - Derived values

    /// Backend base URL without a trailing slash.
    static var backendBaseURL: String {
        var url = settings.backendUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        while url.hasSuffix("/") { url.removeLast() }
        return url.isEmpty ? hostedBackendURL : url
    }

    static var backendHostDescription: String {
        URL(string: backendBaseURL)?.host ?? backendBaseURL
    }

    static var token: String? {
        let trimmedToken = settings.token.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmedToken.isEmpty ? nil : trimmedToken
    }

    /// True when this Mac is signed in to an OpenClicky account (a token in shell.json). The account
    /// routes (/billing/me, /auth/*, AssemblyAI) need it; model calls do not (see `isConfigured`).
    static var hasAccountToken: Bool { token != nil }

    /// True when model calls can be made at all: an own OpenAI or Anthropic key (no account needed,
    /// the calls go straight to the vendor) or an account. Which route each call takes is
    /// `ProviderRouter`'s decision.
    static var isConfigured: Bool { isConfigured(settings) }

    nonisolated static func isConfigured(_ shellSettings: OpenClickyShellSettings) -> Bool {
        let credentials = ProviderCredentials(settings: shellSettings)
        // An account, an OpenAI key (it also covers speech and dictation), or anything that can answer.
        return credentials.accountToken != nil || credentials.openAIKey != nil || !credentials.answerSourcesInOrder.isEmpty
    }

    /// Where cua-driver is, mirroring the CLI's own precedence (`resolveCuaDriverBin` in
    /// `agent/src/config.ts`) so the app and the CLI agree on whether Computer Use exists. An
    /// explicit `settings.cuaDriverBin` — or, when that is unset, the `CUA_DRIVER_BIN` environment
    /// variable — wins outright, including an explicit empty string, which disables it. Only when
    /// neither is set does auto-discovery look for the well-known install paths. Before this, the
    /// HUD and the skill-capabilities list asked only "did shell.json spell it out", which said
    /// Computer Use was off on a Mac where the CLI had already found and attached it.
    static var resolvedCuaDriverBin: String? {
        resolvedCuaDriverBin(from: settings, environment: ProcessInfo.processInfo.environment)
    }

    static func resolvedCuaDriverBin(
        from settings: OpenClickyShellSettings,
        environment: [String: String],
        isExecutable: (String) -> Bool = { FileManager.default.isExecutableFile(atPath: $0) }
    ) -> String? {
        if let configured = settings.cuaDriverBin {
            let trimmed = configured.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        }
        if let envValue = environment["CUA_DRIVER_BIN"] {
            let trimmed = envValue.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        }
        let candidates = [
            NSString(string: "~/.local/bin/cua-driver").expandingTildeInPath,
            "/Applications/CuaDriver.app/Contents/MacOS/cua-driver",
        ]
        return candidates.first(where: isExecutable)
    }

    static var cliCommand: [String] { settings.cliCommand }
    static var workspacePath: String { NSString(string: settings.workspace).expandingTildeInPath }
    static var agentModelOverride: String? { settings.model.isEmpty ? nil : settings.model }

    /// The user's skill library: `library/<id>/SKILL.md`, `active/` symlinks, `activations.json`.
    /// Same layout as the CLI's `agent/src/skillsLibrary.ts`, so both sides see the same skills.
    static var userSkillsDirectory: URL {
        let environment = ProcessInfo.processInfo.environment
        if let override = environment["OPENCLICKY_USER_SKILLS_DIR"], !override.isEmpty {
            return URL(fileURLWithPath: NSString(string: override).expandingTildeInPath, isDirectory: true)
        }
        return URL(fileURLWithPath: NSString(string: "~/.openclicky/skills").expandingTildeInPath, isDirectory: true)
    }

    /// The app-teaching skills inside the app (`Contents/Resources/app-skills`), copied from the
    /// repository's `app-skills/` by the "Embed app skills" build phase, so an install from the dmg has them.
    static var bundledAppSkillsDirectory: URL? {
        Bundle.main.resourceURL?.appendingPathComponent("app-skills", isDirectory: true)
    }

    /// App-teaching skills (`app-skills/` in the checkout): `appSkillsPath` from shell.json, else derived from
    /// the CLI path (`…/agent/dist/cli.js` → `…/app-skills`), else `~/.openclicky/app-skills`.
    static var appSkillsDirectory: URL {
        if let configured = settings.appSkillsPath?.trimmingCharacters(in: .whitespacesAndNewlines), !configured.isEmpty {
            return URL(fileURLWithPath: NSString(string: configured).expandingTildeInPath, isDirectory: true)
        }
        if let cliPath = settings.cliCommand.last, cliPath.hasSuffix(".js") {
            let cliURL = URL(fileURLWithPath: NSString(string: cliPath).expandingTildeInPath)
            // agent/dist/cli.js → agent/dist → agent → repo root
            let root = cliURL.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            let candidate = root.appendingPathComponent("app-skills", isDirectory: true)
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate }
        }
        return URL(fileURLWithPath: NSString(string: "~/.openclicky/app-skills").expandingTildeInPath, isDirectory: true)
    }

    // MARK: - Bring your own key

    private static func cleaned(_ value: String?) -> String? {
        let trimmedValue = (value ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmedValue.isEmpty ? nil : trimmedValue
    }

    /// True when this Mac pays its own provider bills: an OpenAI key is set in shell.json.
    static func usesOwnKeys(_ settings: OpenClickyShellSettings = settings) -> Bool {
        cleaned(settings.openaiApiKey) != nil
    }
    static var usesOwnKeys: Bool { usesOwnKeys(settings) }

    /// Adds the account's bearer token to a backend request. Never the person's own provider keys:
    /// a capability they have a key for goes straight to its vendor (`ProviderRouter`), so the
    /// backend only sees calls that run on the account. No token, no header: the backend refuses
    /// an empty `Authorization: Bearer `.
    static func authorize(_ request: inout URLRequest) {
        if let token {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
    }

    /// Environment for the `openclicky` CLI subprocess. Provider keys are stripped on purpose; the
    /// user's own keys travel under OpenClicky names so the CLI forwards them as headers.
    static var cliProcessEnvironment: [String: String] { cliProcessEnvironment(from: settings) }

    static func cliProcessEnvironment(from settings: OpenClickyShellSettings) -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        var backendUrl = settings.backendUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        while backendUrl.hasSuffix("/") { backendUrl.removeLast() }
        environment["BACKEND_URL"] = backendUrl.isEmpty ? hostedBackendURL : backendUrl
        if let token = cleaned(settings.token) { environment["OPENCLICKY_TOKEN"] = token }
        if let model = cleaned(settings.model) { environment["OPENCLICKY_MODEL"] = model }
        environment["OPENCLICKY_WORKSPACE"] = NSString(string: settings.workspace).expandingTildeInPath
        if let writableRoots = settings.writableRoots { environment["OPENCLICKY_WRITABLE_ROOTS"] = writableRoots.joined(separator: ",") }
        if connectorsEnabled, let composioMcpUrl = settings.composioMcpUrl, !composioMcpUrl.isEmpty { environment["COMPOSIO_MCP_URL"] = composioMcpUrl }
        if connectorsEnabled, let composioApiKey = settings.composioApiKey, !composioApiKey.isEmpty { environment["COMPOSIO_API_KEY"] = composioApiKey }
        if let cuaDriverBin = settings.cuaDriverBin, !cuaDriverBin.isEmpty { environment["CUA_DRIVER_BIN"] = cuaDriverBin }
        if let openaiApiKey = cleaned(settings.openaiApiKey) { environment["OPENCLICKY_OPENAI_KEY"] = openaiApiKey }
        if let anthropicApiKey = cleaned(settings.anthropicApiKey) { environment["OPENCLICKY_ANTHROPIC_KEY"] = anthropicApiKey }
        environment.removeValue(forKey: "OPENAI_API_KEY")
        environment.removeValue(forKey: "ANTHROPIC_API_KEY")
        // GUI apps get a minimal PATH; add the usual CLI locations so `openclicky`, `codex`, `node`, `ffmpeg` resolve.
        let homeDirectory = NSHomeDirectory()
        let extraPathEntries = ["\(homeDirectory)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", "\(homeDirectory)/.npm-global/bin"]
        environment["PATH"] = (extraPathEntries + [environment["PATH"] ?? "/usr/bin:/bin"]).joined(separator: ":")
        return environment
    }
}
