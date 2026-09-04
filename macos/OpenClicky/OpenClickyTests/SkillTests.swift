//
//  SkillTests.swift
//  OpenClickyTests
//
//  SKILL.md parsing, frontmost-app matching, and prompt assembly for the talk lanes.
//

import Foundation
import Testing
@testable import OpenClicky

struct SkillTests {

    private func markdown(name: String, extra: String = "", body: String = "body") -> String {
        "---\nname: \(name)\ndescription: d \(name)\n\(extra)---\n\n# \(name)\n\(body)\n"
    }

    // MARK: - SkillFile.parse

    @Test func parsesFrontmatterWithInlineLists() throws {
        let skill = try #require(SkillFile.parse(
            markdown(name: "figma", extra: "apps: [com.figma.Desktop, \"com.figma.Beta\"]\nsites: [figma.com]\nsurfaces: [talk]\n"),
            id: "figma"
        ))
        #expect(skill.id == "figma")
        #expect(skill.name == "figma")
        #expect(skill.description == "d figma")
        #expect(skill.apps == ["com.figma.Desktop", "com.figma.Beta"])
        #expect(skill.sites == ["figma.com"])
        #expect(skill.surfaces == ["talk"])
        #expect(skill.body.contains("# figma"))
        #expect(skill.isForTalk)
    }

    @Test func defaultsSurfacesAndRejectsMissingFrontmatter() {
        let defaulted = SkillFile.parse(markdown(name: "x"), id: "x")
        #expect(defaulted?.surfaces == ["talk", "agent"])
        #expect(SkillFile.parse("# no frontmatter", id: "x") == nil)
        #expect(SkillFile.parse("---\nname: x\n---\n", id: "x") == nil) // description required
        let agentOnly = SkillFile.parse(markdown(name: "y", extra: "surfaces: [agent]\n"), id: "y")
        #expect(agentOnly?.isForTalk == false)
    }

    @Test func loadsEverySkillDirectory() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("skilltests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        for id in ["beta", "alpha"] {
            let dir = root.appendingPathComponent(id)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try markdown(name: id.capitalized).write(to: dir.appendingPathComponent("SKILL.md"), atomically: true, encoding: .utf8)
        }
        try FileManager.default.createDirectory(at: root.appendingPathComponent("empty"), withIntermediateDirectories: true)
        let loaded = SkillFile.load(directory: root)
        #expect(loaded.map(\.id) == ["alpha", "beta"])
        #expect(loaded.first?.name == "Alpha")
        #expect(SkillFile.load(directory: root.appendingPathComponent("missing")).isEmpty)
    }

    // MARK: - AppSkillMatcher

    private var gmail: SkillFile { SkillFile.parse(markdown(name: "Gmail", extra: "sites: [mail.google.com]\nsurfaces: [talk]\n"), id: "gmail")! }
    private var google: SkillFile { SkillFile.parse(markdown(name: "Google", extra: "sites: [google.com]\nsurfaces: [talk]\n"), id: "google")! }
    private var chrome: SkillFile { SkillFile.parse(markdown(name: "Google Chrome", extra: "apps: [com.google.Chrome]\nsites: [chrome.google.com]\nsurfaces: [talk]\n"), id: "google-chrome")! }
    private var xcode: SkillFile { SkillFile.parse(markdown(name: "Xcode", extra: "apps: [com.apple.dt.Xcode]\nsurfaces: [talk]\n"), id: "xcode")! }
    private var skills: [SkillFile] { [chrome, xcode, google, gmail] }

    @Test func prefersSiteSkillOverBrowserAppSkill() {
        let ctx = FrontAppContext(bundleIdentifier: "com.google.Chrome", appName: "Google Chrome", url: URL(string: "https://mail.google.com/mail/u/0/#inbox"), windowTitle: "Inbox")
        #expect(AppSkillMatcher.match(ctx, in: skills)?.id == "gmail")
    }

    @Test func hostSuffixMatchesSubdomainsOnly() {
        let sub = FrontAppContext(bundleIdentifier: "com.google.Chrome", appName: nil, url: URL(string: "https://docs.google.com/x"), windowTitle: nil)
        #expect(AppSkillMatcher.match(sub, in: [xcode, google])?.id == "google")
        let lookalike = FrontAppContext(bundleIdentifier: "com.other.Browser", appName: nil, url: URL(string: "https://notgoogle.com/"), windowTitle: nil)
        #expect(AppSkillMatcher.match(lookalike, in: [xcode, google]) == nil)
    }

    @Test func fallsBackToWindowTitleThenBundleIdentifier() {
        let titleOnly = FrontAppContext(bundleIdentifier: "com.google.Chrome", appName: nil, url: nil, windowTitle: "Inbox (3) - MAIL.GOOGLE.COM")
        #expect(AppSkillMatcher.match(titleOnly, in: skills)?.id == "gmail")
        let plainBrowser = FrontAppContext(bundleIdentifier: "com.google.Chrome", appName: nil, url: URL(string: "https://example.org/"), windowTitle: "Example")
        #expect(AppSkillMatcher.match(plainBrowser, in: skills)?.id == "google-chrome")
        let ide = FrontAppContext(bundleIdentifier: "com.apple.dt.Xcode", appName: "Xcode", url: nil, windowTitle: "Project")
        #expect(AppSkillMatcher.match(ide, in: skills)?.id == "xcode")
    }

    @Test func returnsNilWhenNothingMatches() {
        let ctx = FrontAppContext(bundleIdentifier: "com.apple.finder", appName: "Finder", url: nil, windowTitle: "Desktop")
        #expect(AppSkillMatcher.match(ctx, in: skills) == nil)
        #expect(AppSkillMatcher.match(FrontAppContext(), in: skills) == nil)
    }

    // MARK: - SkillPromptBuilder

    @Test func buildsSkillAndAppBlocks() {
        let style = SkillFile.parse(markdown(name: "Pirate", body: "talk like a pirate"), id: "pirate")!
        let ctx = FrontAppContext(bundleIdentifier: "com.google.Chrome", appName: "Google Chrome", url: URL(string: "https://mail.google.com/"), windowTitle: nil)
        let out = SkillPromptBuilder.build(activeSkills: [style], appSkill: gmail, front: ctx)
        #expect(out.hasPrefix("## Skill: Pirate\n# Pirate\ntalk like a pirate"))
        #expect(out.contains("\n\n## The app in front: Google Chrome (com.google.Chrome, mail.google.com)\n# Gmail\nbody"))
    }

    @Test func truncatesAppSkillToBudget() {
        let long = SkillFile.parse(markdown(name: "Big", body: String(repeating: "x", count: 500)), id: "big")!
        let out = SkillPromptBuilder.build(activeSkills: [], appSkill: long, front: FrontAppContext(bundleIdentifier: "com.big", appName: "Big"), appBudget: 100)
        let body = out.components(separatedBy: "\n").dropFirst().joined(separator: "\n")
        #expect(body.hasSuffix("…"))
        #expect(body.count <= 101)
        #expect(out.hasPrefix("## The app in front: Big (com.big)\n"))
    }

    @Test func dropsWholeActiveSkillsBeyondBudgetAndNonTalkSkills() {
        let skillA = SkillFile.parse(markdown(name: "A", body: String(repeating: "a", count: 60)), id: "a")!
        let skillB = SkillFile.parse(markdown(name: "B", body: String(repeating: "b", count: 60)), id: "b")!
        let agentOnly = SkillFile.parse(markdown(name: "Ops", extra: "surfaces: [agent]\n"), id: "ops")!
        let out = SkillPromptBuilder.build(activeSkills: [agentOnly, skillA, skillB], appSkill: nil, front: nil, activeBudget: 100)
        #expect(out.contains("## Skill: A"))
        #expect(!out.contains("## Skill: B"))
        #expect(!out.contains("## Skill: Ops"))
    }

    @Test func emptyWhenNothingToInject() {
        let agentOnly = SkillFile.parse(markdown(name: "Ops", extra: "surfaces: [agent]\n"), id: "ops")!
        #expect(SkillPromptBuilder.build(activeSkills: [], appSkill: nil, front: nil) == "")
        #expect(SkillPromptBuilder.build(activeSkills: [agentOnly], appSkill: nil, front: FrontAppContext(bundleIdentifier: "x")) == "")
    }
}
