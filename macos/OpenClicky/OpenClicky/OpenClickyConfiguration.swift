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
    /// Written by sign-in (OpenClickyAuthSession): the refresh token that keeps `token` fresh, when it
    /// expires (unix seconds), and whose account it is. Absent when the token was pasted by hand.
    var refreshToken: String? = nil
    var tokenExpiresAt: Double? = nil
    var accountEmail: String? = nil
}

enum OpenClickyConfiguration {
    /// Where the app talks to unless shell.json says otherwise.
    static let hostedBackendURL = "https://api.openclicky.flowsxr.com"
    static let settingsFileURL = URL(fileURLWithPath: NSString(string: "~/.openclicky/shell.json").expandingTildeInPath)

    private(set) static var settings: OpenClickyShellSettings = load()

    static func reload() {
        settings = load()
    }

    /// File settings, then environment overrides (handy when launching from a terminal):
    /// OPENCLICKY_SHELL_CLI ("node /path/cli.js"), BACKEND_URL, OPENCLICKY_TOKEN, OPENCLICKY_WORKSPACE, OPENCLICKY_MODEL.
    private static func load() -> OpenClickyShellSettings {
        var loadedSettings = OpenClickyShellSettings()
        if let data = try? Data(contentsOf: settingsFileURL),
           let decoded = try? JSONDecoder().decode(OpenClickyShellSettings.self, from: data) {
            loadedSettings = decoded
        }
        let environment = ProcessInfo.processInfo.environment
        if let cli = environment["OPENCLICKY_SHELL_CLI"], !cli.isEmpty { loadedSettings.cliCommand = cli.split(separator: " ").map(String.init) }
        if let value = environment["BACKEND_URL"], !value.isEmpty { loadedSettings.backendUrl = value }
        if let value = environment["OPENCLICKY_TOKEN"], !value.isEmpty { loadedSettings.token = value }
        if let value = environment["OPENCLICKY_WORKSPACE"], !value.isEmpty { loadedSettings.workspace = value }
        if let value = environment["OPENCLICKY_MODEL"], !value.isEmpty { loadedSettings.model = value }
        if let value = environment["OPENCLICKY_TRANSCRIPTION_PROVIDER"], !value.isEmpty { loadedSettings.transcriptionProvider = value }
        return loadedSettings
    }

    static func ensureSettingsFileExists() {
        let fileManager = FileManager.default
        try? fileManager.createDirectory(at: settingsFileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard !fileManager.fileExists(atPath: settingsFileURL.path) else { return }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? encoder.encode(OpenClickyShellSettings()).write(to: settingsFileURL)
    }

    static func revealSettingsFile() {
        ensureSettingsFileExists()
        NSWorkspace.shared.open(settingsFileURL)
    }

    /// Changes shell.json in place (sign-in writes the session here) and reloads the settings.
    static func update(_ change: (inout OpenClickyShellSettings) -> Void) {
        var changedSettings = load()
        change(&changedSettings)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        do {
            try FileManager.default.createDirectory(at: settingsFileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try encoder.encode(changedSettings).write(to: settingsFileURL, options: .atomic)
        } catch {
            print("⚠️ Could not write \(settingsFileURL.path): \(error.localizedDescription)")
        }
        reload()
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

    /// True when the user has a token: the backend rejects every model call without one.
    static var isConfigured: Bool { token != nil }

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

    /// The `x-openclicky-*` headers carrying the user's own provider keys (empty when they use OpenClicky's).
    static func providerKeyHeaders(from settings: OpenClickyShellSettings = settings) -> [String: String] {
        var headers: [String: String] = [:]
        if let openaiApiKey = cleaned(settings.openaiApiKey) { headers["x-openclicky-openai-key"] = openaiApiKey }
        if let anthropicApiKey = cleaned(settings.anthropicApiKey) { headers["x-openclicky-anthropic-key"] = anthropicApiKey }
        return headers
    }

    /// True when this Mac pays its own provider bills: an OpenAI key is set in shell.json.
    static func usesOwnKeys(_ settings: OpenClickyShellSettings = settings) -> Bool {
        cleaned(settings.openaiApiKey) != nil
    }
    static var usesOwnKeys: Bool { usesOwnKeys(settings) }

    /// Adds the bearer token every backend request needs, plus the user's own provider keys if any.
    static func authorize(_ request: inout URLRequest) {
        if let token {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        for (headerName, headerValue) in providerKeyHeaders() {
            request.setValue(headerValue, forHTTPHeaderField: headerName)
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
        if let composioMcpUrl = settings.composioMcpUrl, !composioMcpUrl.isEmpty { environment["COMPOSIO_MCP_URL"] = composioMcpUrl }
        if let composioApiKey = settings.composioApiKey, !composioApiKey.isEmpty { environment["COMPOSIO_API_KEY"] = composioApiKey }
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
