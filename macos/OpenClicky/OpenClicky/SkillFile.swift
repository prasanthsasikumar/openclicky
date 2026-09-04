//
//  SkillFile.swift
//  OpenClicky
//
//  A Hermes-style SKILL.md: YAML frontmatter (flat keys, inline `[a, b]` lists) followed by a
//  Markdown body. Mirrors agent/src/skillMarkdown.ts so the app, the CLI, and the backend agree on
//  what a skill is. Used for the app-teaching skills in `app-skills/` and the user library in
//  `~/.openclicky/skills/library`.
//

import Foundation

struct SkillFile: Equatable {
    let id: String
    let name: String
    let description: String
    /// Bundle identifiers this skill applies to (app-teaching skills only).
    let apps: [String]
    /// Browser host suffixes this skill applies to (app-teaching skills only).
    let sites: [String]
    /// Where the skill is injected: "talk" (voice / teacher prompts) and/or "agent" (Codex).
    let surfaces: Set<String>
    let body: String

    var isForTalk: Bool { surfaces.contains("talk") }

    private static let frontmatterPattern = try! NSRegularExpression(pattern: #"^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$"#)
    private static let keyValuePattern = try! NSRegularExpression(pattern: #"^([A-Za-z_][\w-]*):\s*(.*)$"#)

    /// Parses one SKILL.md. Returns nil without frontmatter or without `name` / `description`.
    static func parse(_ markdown: String, id: String) -> SkillFile? {
        let text = markdown.hasPrefix("\u{FEFF}") ? String(markdown.dropFirst()) : markdown
        let range = NSRange(text.startIndex..., in: text)
        guard let match = frontmatterPattern.firstMatch(in: text, range: range),
              let frontRange = Range(match.range(at: 1), in: text),
              let bodyRange = Range(match.range(at: 2), in: text) else { return nil }

        var fields: [String: String] = [:]
        for line in text[frontRange].components(separatedBy: .newlines) {
            let lineRange = NSRange(line.startIndex..., in: line)
            guard let keyValueMatch = keyValuePattern.firstMatch(in: line, range: lineRange),
                  let keyRange = Range(keyValueMatch.range(at: 1), in: line),
                  let valueRange = Range(keyValueMatch.range(at: 2), in: line) else { continue }
            fields[String(line[keyRange])] = stripQuotes(String(line[valueRange]).trimmingCharacters(in: .whitespaces))
        }
        guard let name = fields["name"], !name.isEmpty,
              let description = fields["description"], !description.isEmpty else { return nil }

        let surfaces = fields["surfaces"].map(parseList) ?? ["talk", "agent"]
        return SkillFile(
            id: id,
            name: name,
            description: description,
            apps: fields["apps"].map(parseList) ?? [],
            sites: fields["sites"].map(parseList) ?? [],
            surfaces: Set(surfaces),
            body: String(text[bodyRange]).trimmingCharacters(in: .whitespacesAndNewlines)
        )
    }

    /// Every `<directory>/<id>/SKILL.md`, sorted by id. Missing directory → empty.
    static func load(directory: URL) -> [SkillFile] {
        let fileManager = FileManager.default
        guard let entries = try? fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isDirectoryKey]) else { return [] }
        var skills: [SkillFile] = []
        for entry in entries {
            guard (try? entry.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else { continue }
            let file = entry.appendingPathComponent("SKILL.md")
            guard let markdown = try? String(contentsOf: file, encoding: .utf8),
                  let skill = parse(markdown, id: entry.lastPathComponent) else { continue }
            skills.append(skill)
        }
        return skills.sorted { $0.id < $1.id }
    }

    // MARK: - Helpers

    private static func stripQuotes(_ value: String) -> String {
        guard value.count >= 2, let first = value.first, let last = value.last,
              first == last, first == "\"" || first == "'" else { return value }
        return String(value.dropFirst().dropLast())
    }

    /// `[a, "b", 'c']` (or a bare comma list) → ["a", "b", "c"].
    private static func parseList(_ value: String) -> [String] {
        var inner = value.trimmingCharacters(in: .whitespaces)
        if inner.hasPrefix("[") { inner.removeFirst() }
        if inner.hasSuffix("]") { inner.removeLast() }
        return inner.split(separator: ",")
            .map { stripQuotes($0.trimmingCharacters(in: .whitespaces)) }
            .filter { !$0.isEmpty }
    }
}
