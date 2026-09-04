//
//  SkillLibraryStoreTests.swift
//  OpenClickyTests
//
//  The on-disk skill library (library/, active/ symlinks, activations.json) shared with the CLI.
//

import Foundation
import Testing
@testable import OpenClicky

@MainActor
struct SkillLibraryStoreTests {

    private func markdown(name: String, surfaces: String? = nil) -> String {
        let surfacesLine = surfaces.map { "surfaces: \($0)\n" } ?? ""
        return "---\nname: \(name)\ndescription: d \(name)\n\(surfacesLine)---\n\n# \(name)\nbody\n"
    }

    private func makeStore() throws -> (SkillLibraryStore, URL) {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("oc-skills-\(UUID().uuidString)", isDirectory: true)
        let appSkills = root.appendingPathComponent("app-skills", isDirectory: true)
        try FileManager.default.createDirectory(at: appSkills, withIntermediateDirectories: true)
        let store = SkillLibraryStore(userSkillsDirectory: root.appendingPathComponent("skills", isDirectory: true),
                                      appSkillsDirectory: appSkills,
                                      watch: false)
        return (store, root)
    }

    private func isSymlink(_ url: URL) -> Bool {
        (try? FileManager.default.destinationOfSymbolicLink(atPath: url.path)) != nil
    }

    @Test func importWritesFileActivatesAndLinks() throws {
        let (store, _) = try makeStore()
        let skill = try store.importSkill(markdown: markdown(name: "Write Like Me"))
        #expect(skill.id == "write-like-me")
        #expect(FileManager.default.fileExists(atPath: store.libraryDirectory.appendingPathComponent("write-like-me/SKILL.md").path))
        #expect(store.librarySkills.map(\.id) == ["write-like-me"])
        #expect(store.activeIds == ["write-like-me"])
        #expect(isSymlink(store.activeDirectory.appendingPathComponent("write-like-me")))

        let data = try Data(contentsOf: store.activationsURL)
        let object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(object["active"] as? [String] == ["write-like-me"])
        #expect((object["updatedAt"] as? String)?.isEmpty == false)
    }

    @Test func deactivatingRemovesTheLinkAndKeepsTheFile() throws {
        let (store, _) = try makeStore()
        try store.importSkill(markdown: markdown(name: "Pirate"))
        store.setActive("pirate", false)
        #expect(store.activeIds.isEmpty)
        #expect(!isSymlink(store.activeDirectory.appendingPathComponent("pirate")))
        #expect(store.librarySkills.map(\.id) == ["pirate"])
        store.setActive("pirate", true)
        #expect(isSymlink(store.activeDirectory.appendingPathComponent("pirate")))
    }

    @Test func staleLinksAndUnknownActivationsAreDropped() throws {
        let (store, _) = try makeStore()
        try FileManager.default.createSymbolicLink(atPath: store.activeDirectory.appendingPathComponent("ghost").path,
                                                   withDestinationPath: "/nowhere")
        try "{ \"active\": [\"gone\", \"pirate\"] }".write(to: store.activationsURL, atomically: true, encoding: .utf8)
        try store.importSkill(markdown: markdown(name: "Pirate"))
        store.reload()
        #expect(!isSymlink(store.activeDirectory.appendingPathComponent("ghost")))
        #expect(store.activeIds == ["pirate"])
    }

    @Test func activeTalkSkillsExcludesAgentOnlySkills() throws {
        let (store, _) = try makeStore()
        try store.importSkill(markdown: markdown(name: "Deploy Flow", surfaces: "[agent]"))
        try store.importSkill(markdown: markdown(name: "Warm Tone", surfaces: "[talk]"))
        try store.importSkill(markdown: markdown(name: "Both"))
        #expect(store.activeTalkSkills.map(\.id) == ["both", "warm-tone"])
    }

    @Test func idsAreDedupedAndSlugified() throws {
        let (store, _) = try makeStore()
        #expect(try store.importSkill(markdown: markdown(name: "Same")).id == "same")
        #expect(try store.importSkill(markdown: markdown(name: "Same")).id == "same-2")
        #expect(try store.importSkill(markdown: markdown(name: "Same")).id == "same-3")
        #expect(SkillLibraryStore.slugify("  Hello, World! ") == "hello-world")
        #expect(SkillLibraryStore.slugify("日本語") == "skill")
    }

    @Test func invalidMarkdownIsRejected() throws {
        let (store, _) = try makeStore()
        #expect(throws: SkillLibraryError.self) { try store.importSkill(markdown: "# no frontmatter") }
        #expect(store.librarySkills.isEmpty)
    }

    @Test func appSkillsLoadFromTheirDirectory() throws {
        let (store, root) = try makeStore()
        let figma = root.appendingPathComponent("app-skills/figma", isDirectory: true)
        try FileManager.default.createDirectory(at: figma, withIntermediateDirectories: true)
        try "---\nname: Figma\ndescription: d\napps: [com.figma.Desktop]\nsurfaces: [talk]\n---\nbody\n"
            .write(to: figma.appendingPathComponent("SKILL.md"), atomically: true, encoding: .utf8)
        store.reload()
        #expect(store.appSkills.map(\.id) == ["figma"])
        #expect(store.appSkills.first?.apps == ["com.figma.Desktop"])
    }
}
