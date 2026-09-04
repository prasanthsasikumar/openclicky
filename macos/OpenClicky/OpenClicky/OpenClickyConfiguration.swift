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
    var backendUrl: String = "http://localhost:8787"
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
    var composioMcpUrl: String? = nil
    var cuaDriverBin: String? = nil
    /// Directory of app-teaching skills (`app-skills/` in the repo). Defaults to the checkout the CLI runs from.
    var appSkillsPath: String? = nil
}

enum OpenClickyConfiguration {
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

    // MARK: - Derived values

    /// Backend base URL without a trailing slash.
    static var backendBaseURL: String {
        var url = settings.backendUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        while url.hasSuffix("/") { url.removeLast() }
        return url.isEmpty ? "http://localhost:8787" : url
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

    /// Adds the bearer token every backend request needs.
    static func authorize(_ request: inout URLRequest) {
        if let token {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
    }

    /// Environment for the `openclicky` CLI subprocess. Provider keys are stripped on purpose.
    static var cliProcessEnvironment: [String: String] {
        var environment = ProcessInfo.processInfo.environment
        environment["BACKEND_URL"] = backendBaseURL
        if let token { environment["OPENCLICKY_TOKEN"] = token }
        if let model = agentModelOverride { environment["OPENCLICKY_MODEL"] = model }
        environment["OPENCLICKY_WORKSPACE"] = workspacePath
        if let composioMcpUrl = settings.composioMcpUrl, !composioMcpUrl.isEmpty { environment["COMPOSIO_MCP_URL"] = composioMcpUrl }
        if let cuaDriverBin = settings.cuaDriverBin, !cuaDriverBin.isEmpty { environment["CUA_DRIVER_BIN"] = cuaDriverBin }
        environment.removeValue(forKey: "OPENAI_API_KEY")
        environment.removeValue(forKey: "ANTHROPIC_API_KEY")
        // GUI apps get a minimal PATH; add the usual CLI locations so `openclicky`, `codex`, `node`, `ffmpeg` resolve.
        let homeDirectory = NSHomeDirectory()
        let extraPathEntries = ["\(homeDirectory)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", "\(homeDirectory)/.npm-global/bin"]
        environment["PATH"] = (extraPathEntries + [environment["PATH"] ?? "/usr/bin:/bin"]).joined(separator: ":")
        return environment
    }
}
