import Foundation

/// `~/.openclicky/shell.json` — how the shell finds the CLI and the backend.
/// The shell never holds provider keys; it only forwards the user's OpenClicky token to the CLI.
struct ShellSettings: Codable {
    /// Command used to run the CLI, e.g. ["openclicky"] or ["node", "/path/to/openclicky/agent/dist/cli.js"].
    var cliCommand: [String] = ["openclicky"]
    var backendUrl: String = "http://localhost:8787"
    var token: String = ""
    /// Working directory for agent runs (defaults to ~/OpenClicky, created on demand).
    var workspace: String = NSString(string: "~/OpenClicky").expandingTildeInPath
    var model: String = ""
    var voiceSeconds: Int = 5

    static let fileURL = URL(fileURLWithPath: NSString(string: "~/.openclicky/shell.json").expandingTildeInPath)

    /// File settings, then environment overrides (handy when launching from a terminal or a test):
    /// OPENCLICKY_SHELL_CLI ("node /path/cli.js"), BACKEND_URL, OPENCLICKY_TOKEN, OPENCLICKY_WORKSPACE, OPENCLICKY_MODEL.
    static func load() -> ShellSettings {
        var s = ShellSettings()
        if let data = try? Data(contentsOf: fileURL), let decoded = try? JSONDecoder().decode(ShellSettings.self, from: data) { s = decoded }
        let env = ProcessInfo.processInfo.environment
        if let cli = env["OPENCLICKY_SHELL_CLI"], !cli.isEmpty { s.cliCommand = cli.split(separator: " ").map(String.init) }
        if let v = env["BACKEND_URL"], !v.isEmpty { s.backendUrl = v }
        if let v = env["OPENCLICKY_TOKEN"], !v.isEmpty { s.token = v }
        if let v = env["OPENCLICKY_WORKSPACE"], !v.isEmpty { s.workspace = v }
        if let v = env["OPENCLICKY_MODEL"], !v.isEmpty { s.model = v }
        return s
    }

    static func ensureFile() {
        let fm = FileManager.default
        try? fm.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !fm.fileExists(atPath: fileURL.path) {
            let enc = JSONEncoder()
            enc.outputFormatting = [.prettyPrinted, .sortedKeys]
            try? enc.encode(ShellSettings()).write(to: fileURL)
        }
    }

    var environment: [String: String] {
        var env = ProcessInfo.processInfo.environment
        env["BACKEND_URL"] = backendUrl
        if !token.isEmpty { env["OPENCLICKY_TOKEN"] = token }
        if !model.isEmpty { env["OPENCLICKY_MODEL"] = model }
        env["OPENCLICKY_WORKSPACE"] = workspace
        // Keys stay server-side: never let provider keys leak from the login shell into the agent.
        env.removeValue(forKey: "OPENAI_API_KEY")
        env.removeValue(forKey: "ANTHROPIC_API_KEY")
        // GUI apps get a minimal PATH; add the usual CLI locations so `openclicky`, `codex`, `ffmpeg` resolve.
        let home = NSHomeDirectory()
        let extra = ["\(home)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", "\(home)/.npm-global/bin"]
        env["PATH"] = (extra + [env["PATH"] ?? "/usr/bin:/bin"]).joined(separator: ":")
        return env
    }
}
